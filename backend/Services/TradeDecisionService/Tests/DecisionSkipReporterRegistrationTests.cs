using System.Reflection;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace TradeDecisionService.Tests;

// T-10-671, FR-04, FR-10, NFR-07, #891, IADR-0374（PR #919 監査の指摘 M3）:
// 見送り理由の計上ポート（IDecisionSkipReporter）が composition root で **ちょうど 1 つ・計上実装へ**
// 登録され、判断サービスへ実際に届いていることを固定する。
//
// 🔴 **本テストの存在理由は、判断サービス側の依存が省略可能（既定 NoOp）だからである。**
//   `TradeDecisionAppService` は構築点の多さ（テスト 30 か所）から `skipReporter` を省略可能引数で受ける。
//   その代償として、Program.cs の `AddSingleton<IDecisionSkipReporter, MetricsDecisionSkipReporter>()` を
//   消しても **コンパイルは通り、DI は既定値（NoOp）で判断サービスを組み、既存テストは全緑のまま**になる。
//   そのとき `ast.trade_cycle.decision_skips` は 1 件も出ず、ダッシュボードのパネルは空になり、
//   保有不明のアラートは**エラーを出さずに永久に鳴らない**（IADR-0163 決定2 が禁じる形。
//   PriceMovementDetectedHandler の冒頭注記と同じ規律）。
//
// 変異注入の実測（PR #919 監査の是正）: Program.cs の登録行を消すと本スイートの 3 件が赤になり、
// 既存のテストはすべて緑のままだった。
public class DecisionSkipReporterRegistrationTests
{
    // 🔴 本体。登録を消すと 0 件、二重に登録すると 2 件になって落ちる。
    [Fact]
    public void 見送り理由の計上ポートの登録はちょうど一つである()
    {
        using var factory = new Factory();
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetServices<IDecisionSkipReporter>().Should().ContainSingle();
    }

    // 対の肯定形。個数だけを見ると「1 つだが NoOp へ落ちている」壊れ方を見逃す。
    [Fact]
    public void 見送り理由の計上ポートはメトリクス計上の実装へ結線される()
    {
        using var factory = new Factory();
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IDecisionSkipReporter>()
            .Should().BeOfType<MetricsDecisionSkipReporter>()
            .And.NotBeOfType<NoOpDecisionSkipReporter>();
    }

    // 🔴 登録があっても判断サービスへ届かなければ同じ症状になる（省略可能引数は DI が解決できないとき既定値へ
    // 黙って落ちる）。DI が組んだ判断サービスが保持する実体を直接確かめる。
    [Fact]
    public void DIが組んだ判断サービスは計上実装を保持する()
    {
        using var factory = new Factory();
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TradeDecisionAppService>();

        var field = typeof(TradeDecisionAppService).GetField(
            "_skipReporter", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull("判断サービスは見送り理由の計上ポートをフィールドで保持する");

        field!.GetValue(service).Should().BeOfType<MetricsDecisionSkipReporter>();
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost");
            builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");

            builder.ConfigureServices(services =>
            {
                // ADR-0013, IADR-0129, #354: 実 RabbitMQ を避けて Wolverine の外部トランスポートを無効化する。
                services.DisableAllExternalWolverineTransports();
            });
        }
    }
}
