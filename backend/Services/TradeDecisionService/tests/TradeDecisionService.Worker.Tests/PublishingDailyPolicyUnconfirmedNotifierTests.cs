using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Worker.Composable.Adapters;
using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.TradeDecision.Worker.Tests;

// UC-01, FR-09, FR-11, IADR-0096, #210: 日報未確定通知の実発行と営業日単位の重複抑止（dedup）を検証する。
public class PublishingDailyPolicyUnconfirmedNotifierTests
{
    // UtcNow を差し替え可能にするテスト用クロック。
    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private static readonly DateTimeOffset Day1 = new(2026, 7, 20, 1, 0, 0, TimeSpan.Zero);

    private static async Task<(ITestHarness Harness, ServiceProvider Provider)> StartHarnessAsync()
    {
        var provider = new ServiceCollection()
            .AddMassTransitTestHarness()
            .BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        return (harness, provider);
    }

    // 基準1: 日報未確定の見送りで DailyPolicyUnconfirmed が発行される（当日初回）。
    [Fact]
    public async Task 初回は営業日つきで発行する()
    {
        var (harness, provider) = await StartHarnessAsync();
        await using var _ = provider;
        var clock = new MutableClock(Day1);
        var notifier = new PublishingDailyPolicyUnconfirmedNotifier(
            harness.Bus, clock, NullLogger<PublishingDailyPolicyUnconfirmedNotifier>.Instance);

        await notifier.NotifyAsync();

        (await harness.Published.Any<DailyPolicyUnconfirmed>()).Should().BeTrue();
        var e = harness.Published.Select<DailyPolicyUnconfirmed>().First().Context.Message;
        e.BusinessDay.Should().Be(new DateOnly(2026, 7, 20));
        e.OccurredAt.Should().Be(Day1);

        await harness.Stop();
    }

    // 基準2: 同一営業日内の 2 回目以降は抑止される（1 巡回で全銘柄が policy-null でも 1 件）。
    [Fact]
    public async Task 同一営業日の再通知は抑止する()
    {
        var (harness, provider) = await StartHarnessAsync();
        await using var _ = provider;
        var clock = new MutableClock(Day1);
        var notifier = new PublishingDailyPolicyUnconfirmedNotifier(
            harness.Bus, clock, NullLogger<PublishingDailyPolicyUnconfirmedNotifier>.Instance);

        await notifier.NotifyAsync();
        clock.UtcNow = Day1.AddHours(3); // 同日・別巡回
        await notifier.NotifyAsync();

        (await harness.Published.Any<DailyPolicyUnconfirmed>()).Should().BeTrue();
        harness.Published.Select<DailyPolicyUnconfirmed>().Should().HaveCount(1);

        await harness.Stop();
    }

    // 基準3: 翌営業日には再通知する（日報未確定が続いていることを日次で促す）。
    [Fact]
    public async Task 翌営業日には再通知する()
    {
        var (harness, provider) = await StartHarnessAsync();
        await using var _ = provider;
        var clock = new MutableClock(Day1);
        var notifier = new PublishingDailyPolicyUnconfirmedNotifier(
            harness.Bus, clock, NullLogger<PublishingDailyPolicyUnconfirmedNotifier>.Instance);

        await notifier.NotifyAsync();
        clock.UtcNow = Day1.AddDays(1); // 翌営業日
        await notifier.NotifyAsync();

        (await harness.Published.Any<DailyPolicyUnconfirmed>()).Should().BeTrue();
        harness.Published.Select<DailyPolicyUnconfirmed>().Should().HaveCount(2);

        await harness.Stop();
    }
}
