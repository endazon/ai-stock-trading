using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// NFR, ADR-0013, IADR-0129 決定 11, #354: **ハンドラの生成コードが実際に組み立てられること**を固定する。
//
// なぜ要るのか（実測に基づく回帰）: Wolverine はハンドラの実行コードを実行時に生成し、依存を
// 「生成コード内で new する」か「IServiceProvider から解決する（service location）」かを、依存の
// **具象型が public かどうか**で決める。本ユニットの永続アダプタは `internal sealed`（25 型）であり、
// 生成コードからは new できないため必ず service location になる。Wolverine 6 の既定
// （ServiceLocationPolicy.NotAllowed）はこれを拒否して例外を投げる。
//
// この失敗は**起動時ではなく 1 通目の受信時**に起きる。したがって次がすべて成功したまま、
// メッセージだけが無言で処理されない:
//   - ホスト起動・ヘルスチェック（ready）
//   - キュー／DLQ の宣言（AutoProvision）
//   - consumer の接続（ブローカ上で consumer 数 ≥ 1）
//   - 発行（exchange まで届き、binding 経由でキューにも入る）
// 実 RabbitMQ の E2E では「発注が一件も執行されない」形で現れた（#354 第 3 段階の実走）。
//
// 本テストはブローカを必要とせず（外部トランスポートは stub）、**ハンドラを実際に起動して**
// この生成を通す。ハンドラ配線の検証を「型名・キュー名の照合」で済ませると、生成コードは一度も
// 組み立てられないため本欠陥をすり抜ける（実際にすり抜けた）。
public class WolverineHandlerCodegenTests
{
    private const string ServiceName = "ai-stock-trading.codegen-probe-service";

    [Fact]
    public async Task 内部実装に依存するハンドラも生成コードから実行できる()
    {
        InternalDependencyProbeHandler.Handled.Clear();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
                // 具象型が internal な登録（本ユニットの永続アダプタと同じ形）。生成コードは new できず
                // service location に倒れるため、方針が NotAllowed のままだとここで組み立てが失敗する。
                services.AddScoped<IInternalDependencyProbe, InternalDependencyProbe>())
            .UseWolverine(opts =>
            {
                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                // 実ブローカへ接続しない（ローカル・CI ともに RabbitMQ を要求しない）。
                opts.StubAllExternalTransports();
            })
            .StartAsync();

        // InvokeAsync は受信時と同じ経路でハンドラの実行コードを組み立てる（ブローカを介さずに固定できる）。
        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMessageBus>()
                .InvokeAsync(new InternalDependencyProbeEvent("AAPL"));
        }

        InternalDependencyProbeHandler.Handled.Should().Contain("AAPL",
            "internal な具象型に依存するハンドラでも生成コードが組み立てられ、実際に処理されること"
            + "（失敗すると、起動・キュー宣言・consumer 接続・発行がすべて成功したままメッセージだけが処理されない）");

        await host.StopAsync();
    }
}

// 検証用のイベント・ポート・ハンドラ（Shared.Contracts へ依存させないため本テストプロジェクトに置く）。
public record InternalDependencyProbeEvent(string Symbol);

public interface IInternalDependencyProbe
{
    void Record(string symbol);
}

// **internal**（本ユニットの永続アダプタ EfExecutedOrderStore 等と同じ可視性）。
internal sealed class InternalDependencyProbe : IInternalDependencyProbe
{
    public void Record(string symbol) => InternalDependencyProbeHandler.Handled.Add(symbol);
}

// IADR-0129 決定 9: ハンドラ型は public sealed。
public sealed class InternalDependencyProbeHandler(IInternalDependencyProbe probe)
{
    public static System.Collections.Concurrent.ConcurrentBag<string> Handled { get; } = [];

    public void Handle(InternalDependencyProbeEvent message)
    {
        ArgumentNullException.ThrowIfNull(message);
        probe.Record(message.Symbol);
    }
}
