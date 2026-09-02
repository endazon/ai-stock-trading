using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Fx;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-10, FR-11, FR-09, #381, ADR-0022 決定2・決定5, IADR-0196, IADR-0198: 為替の情報源の状態の**実発行**。
//
// 🔴 **抑止の規則は 2 系統ある。** 状態（フォールバック・鮮度）は `FxSourceStatusTracker` が判定するが、
// **鮮度切れでの決済は tracker を通らない**（IADR-0198 決定3 で「抑止しない」と決めたため）。
// **通らない経路は、tracker のテストでは 1 行も守られない**——本クラスで実測する。
public class PublishingFxSourceStatusNotifierTests
{
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private const string ServiceName = "ai-stock-trading.trade-decision-service";
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 1, 0, 0, TimeSpan.Zero);

    private static Task<IHost> StartHostAsync() =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // 本番と同じ配線を用い、送信先だけ stub へ倒す（ルーティングが無いと送信そのものが起きない）。
                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static PublishingFxSourceStatusNotifier NewNotifier(IHost host) =>
        new(host.Services.GetRequiredService<IWolverineRuntime>(), new FixedClock(Now),
            NullLogger<PublishingFxSourceStatusNotifier>.Instance);

    // IADR-0198 決定3: 鮮度切れのレートで決済した事実を発行する。**観測日を運ぶ。**
    [Fact]
    public async Task 鮮度切れでの決済は_観測日つきで発行する()
    {
        using var host = await StartHostAsync();
        var notifier = NewNotifier(host);

        var asOf = Now.AddDays(-31);
        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(_ =>
            notifier.ReportClosedWithStaleRateAsync(
                "7203", Market.Japan, "JPY", 300, 0.0067m, asOf, TimeSpan.FromDays(31)));

        var e = session.Sent.MessagesOf<PositionClosedWithStaleFxRate>().Should().ContainSingle().Subject;
        e.Symbol.Should().Be("7203");
        e.Quantity.Should().Be(300);
        e.FxRateToBase.Should().Be(0.0067m);
        // 🔴 これが載らなければ、台帳に観測日が残らない（本イベントを足した意味が無い）。
        e.RateAsOf.Should().Be(asOf);
        e.AgeDays.Should().Be(31);

        await host.StopAsync();
    }

    // 🔴 **否定形（最重要）。** 状態の通知と違い、**取引は抑止しない**——
    // **1 件ずつ残さなければ、後から件数も金額も復元できない**（IADR-0198 決定3）。
    // tracker を通らない経路であるため、tracker の抑止テストはここを 1 行も守っていない。
    [Fact]
    public async Task 鮮度切れでの決済は_同じ日に何件でも発行する()
    {
        using var host = await StartHostAsync();
        var notifier = NewNotifier(host);

        var session = await host.TrackActivityForTest()
            .ExecuteAndWaitAsync(_ => ReportTwoClosesAsync(notifier));

        session.Sent.MessagesOf<PositionClosedWithStaleFxRate>().Should()
            .HaveCount(2, "取引は状態ではない（抑止すると件数も金額も復元できなくなる）");

        await host.StopAsync();
    }

    // 対（IADR-0196 決定1）: **状態のほうは抑止する。** 同じ日・同じ状態なら 1 件だけ。
    [Fact]
    public async Task 鮮度警告は_同じ日に1件だけ発行する()
    {
        using var host = await StartHostAsync();
        var notifier = NewNotifier(host);

        var session = await host.TrackActivityForTest()
            .ExecuteAndWaitAsync(_ => ReportStaleTwiceAsync(notifier, secondIsBlocked: false));

        session.Sent.MessagesOf<FxRateStale>().Should().ContainSingle().Which.EntryBlocked.Should().BeFalse();

        await host.StopAsync();
    }

    // 🔴 IADR-0198 決定2: **昇格は同じ日でも発行する**（状態を鍵に含めているため）。
    [Fact]
    public async Task 警告から停止への昇格は_同じ日でも発行する()
    {
        using var host = await StartHostAsync();
        var notifier = NewNotifier(host);

        var session = await host.TrackActivityForTest()
            .ExecuteAndWaitAsync(_ => ReportStaleTwiceAsync(notifier, secondIsBlocked: true));

        var sent = session.Sent.MessagesOf<FxRateStale>().ToList();
        sent.Should().HaveCount(2);
        sent.Should().ContainSingle(e => e.EntryBlocked, "停止への昇格が 1 件出る");

        await host.StopAsync();
    }

    // 追跡ブロック内で 2 回呼ぶための補助（`async` ラムダは Task/ValueTask 版の解決が曖昧になる）。
    private static async Task ReportTwoClosesAsync(PublishingFxSourceStatusNotifier notifier)
    {
        var asOf = Now.AddDays(-31);
        await notifier.ReportClosedWithStaleRateAsync(
            "7203", Market.Japan, "JPY", 300, 0.0067m, asOf, TimeSpan.FromDays(31));
        await notifier.ReportClosedWithStaleRateAsync(
            "7203", Market.Japan, "JPY", 100, 0.0067m, asOf, TimeSpan.FromDays(31));
    }

    private static async Task ReportStaleTwiceAsync(
        PublishingFxSourceStatusNotifier notifier, bool secondIsBlocked)
    {
        var asOf = Now.AddDays(secondIsBlocked ? -31 : -7);
        await notifier.ReportStaleAsync(
            "USD", asOf, TimeSpan.FromDays(7), TimeSpan.FromDays(5), TimeSpan.FromDays(30));
        await notifier.ReportStaleAsync(
            "USD", asOf, TimeSpan.FromDays(secondIsBlocked ? 31 : 7),
            TimeSpan.FromDays(5), TimeSpan.FromDays(30), entryBlocked: secondIsBlocked);
    }
}
