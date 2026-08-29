using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Runtime;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// NFR, ADR-0013, IADR-0129, #354: Wolverine 共通配線が定めるブローカのトポロジを固定する。
//
// ここで固定するのは「pub/sub が pub/sub のまま保たれること」である。#258 では、キュー名の導出規則に対する
// 思い込みのせいで pub/sub が competing consumer へ退行し、取引判断が無言で消えた。同じ種類の事故が
// Wolverine では**構造的に**起こり得る（既定はキュー名をメッセージ型名だけから導き、発行元にハンドラが
// あれば発行をプロセス内へ閉じる）。よってキュー名の導出と経路の向き先をテストで固定する。
public class WolverineTopologyTests
{
    private const string CostControl = "ai-stock-trading.cost-control-service";
    private const string MarketMonitor = "ai-stock-trading.market-monitor-service";

    // 検証用のイベント型（Shared.Contracts へ依存させないため本テスト内に置く）。
    public record TradeDecisionMade(string Symbol);

    // Wolverine は public なハンドラ型しか発見しない（実測）。
    public class TradeDecisionMadeBaselineHandler
    {
        public void Handle(TradeDecisionMade message) => _ = message;
    }

    // IADR-0129 決定 1: キュー名は <ServiceName>.<メッセージ型名>。
    [Fact]
    public void キュー名はサービス名とメッセージ型名から導かれる()
    {
        WolverineExtensions.QueueNameFor(CostControl, typeof(TradeDecisionMade))
            .Should().Be("ai-stock-trading.cost-control-service.TradeDecisionMade");
    }

    // IADR-0129 決定 1 / #258 の回帰: 同じイベントを購読しても、サービスが違えばキューは別になる。
    // MassTransit 時代はこれを consumer クラス名の一意性（IADR-0106）に頼っていたが、Wolverine では
    // クラス名がキュー名に一切関与しないため、サービス名の前置だけがキューを分離する。
    [Fact]
    public void 同じイベントでもサービスが違えばキュー名は衝突しない()
    {
        var risk = WolverineExtensions.QueueNameFor(MarketMonitor, typeof(TradeDecisionMade));
        var monitor = WolverineExtensions.QueueNameFor(CostControl, typeof(TradeDecisionMade));

        risk.Should().NotBe(monitor);
    }

    // IADR-0129 決定 5: DLQ は MassTransit 時代と同じ <queue>_error（既定の共有 DLQ には倒さない）。
    [Fact]
    public void デッドレターキュー名はキュー単位で分かれる()
    {
        WolverineExtensions.DeadLetterQueueNameFor("ai-stock-trading.cost-control-service.LlmCostIncurred")
            .Should().Be("ai-stock-trading.cost-control-service.LlmCostIncurred_error");
    }

    // IADR-0129 決定 2/3: 自プロセス内にハンドラがある型を発行しても、経路はローカルへ閉じず、
    // メッセージ型名の共有 exchange へ向かう（＝他サービスへ届く）。
    // これが崩れると、OrderApproved を発行しつつ購読する RiskManagementService で発注が執行されなくなる。
    [Fact]
    public async Task 自分が購読している型の発行もブローカの共有_exchange_へ向かう()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAiStockTradingRabbitMq(CostControl, "amqp://guest:guest@localhost:5672");
                // 実ブローカへ接続しない（ローカル・CI ともに RabbitMQ を要求しない）。
                opts.StubAllExternalTransports();
            })
            .StartAsync();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var routes = runtime.RoutingFor(typeof(TradeDecisionMade)).Routes;

        routes.Should().NotBeEmpty();
        routes.Select(r => r.ToString()).Should().AllSatisfy(route =>
            route.Should().NotContain("local://", "発行がプロセス内へ閉じると他サービスへ届かない（IADR-0129 決定 3）"));

        await host.StopAsync();
    }

    // 🔴 NFR, IADR-0268 の回帰: **ハンドラ探索の範囲は呼び出し元サービスのアセンブリに閉じる。**
    //
    // Wolverine の既定はハンドラを application assembly から走査し、明示しなければ実行時に推論する。
    // **同一プロセスで 2 つ目以降に起動したホストは、先に起動したホストが確定させたものを引き継ぐ。**
    // 1 サービス 1 プロセスの本番では表に出ないが、複数サービスの Worker を同居させる実基盤 E2E では
    // 後発のサービスが他サービスのハンドラを取り込み、依存が DI に無いためチェーンの組み立てが失敗する。
    // 失敗するのは**起動時ではなく 1 通目の受信時**であり、起動・ヘルスチェック・キュー宣言・
    // consumer 接続がすべて成功したまま**メッセージだけが無言で処理されない**（IADR-0129 決定 11 と同型）。
    //
    // 実測（#600 の VSA 移送後）: リスク管理ホストを先に起動したプロセスで発注執行ホストが
    // RiskManagementService のハンドラを拾い、OrderApproved が一通も執行されなくなった。
    // **この欠陥は Docker を要する nightly の E2E でしか観測できなかった。** 同型の再発を
    // 既定 CI（Docker 不要）で止めるため、不変条件をここで固定する。
    [Fact]
    public void ハンドラ探索の範囲は先に起動した別サービスのアセンブリを引き継がない()
    {
        // 同居プロセスで「先に起動した別サービスのホスト」が application assembly を
        // 確定させてしまった状態を模す（Wolverine 本体のアセンブリを他サービスの代役に使う）。
        var options = new WolverineOptions { ApplicationAssembly = typeof(WolverineOptions).Assembly };

        options.UseAiStockTradingRabbitMq(CostControl, "amqp://guest:guest@localhost:5672");

        options.ApplicationAssembly.Should().BeSameAs(
            typeof(WolverineTopologyTests).Assembly,
            "共通配線は探索範囲を呼び出し元サービスのアセンブリへ固定し、"
            + "同居プロセスで先に起動した別サービスの範囲を引き継がないこと（IADR-0268 決定 1）");
    }

    // 上の固定が「呼び出し元」を指していることまで確かめる（shim 自身のアセンブリへ固定してしまうと、
    // どのサービスも他サービスのハンドラを拾わない代わりに**自分のハンドラも拾わなくなる**）。
    [Fact]
    public void 固定先は共通配線ではなく呼び出し元サービスのアセンブリである()
    {
        var options = new WolverineOptions();

        options.UseAiStockTradingRabbitMq(MarketMonitor, "amqp://guest:guest@localhost:5672");

        options.ApplicationAssembly.Should().NotBeSameAs(
            typeof(WolverineExtensions).Assembly,
            "shim 自身へ固定すると、各サービスが自分のハンドラを発見できなくなる");
        options.ApplicationAssembly.Should().BeSameAs(typeof(WolverineTopologyTests).Assembly);
    }
}
