using AiStockTrading.TestSupport.Messaging;
using TradeDecisionService.Application.Ports;
using TradeDecisionService.Infrastructure.Adapters;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace TradeDecisionService.Infrastructure.Tests;

// UC-01, FR-09, FR-11, IADR-0096, #210: 日報未確定通知の実発行と営業日単位の重複抑止（dedup）を検証する。
//
// ADR-0013, IADR-0129, #354: MassTransit のテストハーネス（harness.Bus + harness.Published）から
// Wolverine.Tracking（IWolverineRuntime + session.Sent）へ移行した。本アダプタは singleton であり、
// scoped な IMessageBus を注入できないため singleton の IWolverineRuntime を受け取る形になった。
public class PublishingDailyPolicyUnconfirmedNotifierTests
{
    // UtcNow を差し替え可能にするテスト用クロック。
    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private const string ServiceName = "ai-stock-trading.trade-decision-service";
    private static readonly DateTimeOffset Day1 = new(2026, 7, 20, 1, 0, 0, TimeSpan.Zero);

    private static Task<IHost> StartHostAsync() =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // 本番と同じ配線（キュー名・fan-out・再試行・DLQ）を用い、送信先だけ stub へ倒す。
                // ルーティングを入れないと発行先が 1 つも無く、送信そのものが起きない。
                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static PublishingDailyPolicyUnconfirmedNotifier NewNotifier(IHost host, IClock clock) =>
        new(host.Services.GetRequiredService<IWolverineRuntime>(), clock,
            NullLogger<PublishingDailyPolicyUnconfirmedNotifier>.Instance);

    // 基準1: 日報未確定の見送りで DailyPolicyUnconfirmed が発行される（当日初回）。
    [Fact]
    public async Task 初回は営業日つきで発行する()
    {
        using var host = await StartHostAsync();
        var clock = new MutableClock(Day1);
        var notifier = NewNotifier(host, clock);

        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(_ => notifier.NotifyAsync());

        session.Sent.MessagesOf<DailyPolicyUnconfirmed>().Should().NotBeEmpty();
        var e = session.Sent.MessagesOf<DailyPolicyUnconfirmed>().First();
        e.BusinessDay.Should().Be(new DateOnly(2026, 7, 20));
        e.OccurredAt.Should().Be(Day1);

        await host.StopAsync();
    }

    // 基準2: 同一営業日内の 2 回目以降は抑止される（1 巡回で全銘柄が policy-null でも 1 件）。
    [Fact]
    public async Task 同一営業日の再通知は抑止する()
    {
        using var host = await StartHostAsync();
        var clock = new MutableClock(Day1);
        var notifier = NewNotifier(host, clock);

        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(_ => NotifyTwiceAsync(notifier, clock, Day1.AddHours(3)));

        session.Sent.MessagesOf<DailyPolicyUnconfirmed>().Should().NotBeEmpty();
        session.Sent.MessagesOf<DailyPolicyUnconfirmed>().Should().HaveCount(1);

        await host.StopAsync();
    }

    // 基準3: 翌営業日には再通知する（日報未確定が続いていることを日次で促す）。
    [Fact]
    public async Task 翌営業日には再通知する()
    {
        using var host = await StartHostAsync();
        var clock = new MutableClock(Day1);
        var notifier = NewNotifier(host, clock);

        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(_ => NotifyTwiceAsync(notifier, clock, Day1.AddDays(1)));

        session.Sent.MessagesOf<DailyPolicyUnconfirmed>().Should().NotBeEmpty();
        session.Sent.MessagesOf<DailyPolicyUnconfirmed>().Should().HaveCount(2);

        await host.StopAsync();
    }

    // 1 回目 → 時刻を進めて 2 回目、を追跡ブロック内で行うための補助（元のテストと呼び出し順・回数は同じ）。
    private static async Task NotifyTwiceAsync(
        PublishingDailyPolicyUnconfirmedNotifier notifier, MutableClock clock, DateTimeOffset second)
    {
        await notifier.NotifyAsync();
        clock.UtcNow = second;
        await notifier.NotifyAsync();
    }
}
