using AiStockTrading.Notification.Application.Ports;
using AiStockTrading.Notification.Application.State;
using AiStockTrading.Notification.Infrastructure.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.Notification.Infrastructure.Tests;

// FR-09, UC-01, UC-02, UC-06: 取引実行・リスク統制発動のイベント購読 → 通知送信を MassTransit ハーネス + 記録 sender で検証する。
public class NotificationConsumersTests
{
    private static OrderIntent Intent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper, 10, 1_000m);

    private static (ServiceProvider Provider, RecordingNotificationSender Sender) Build()
    {
        var sender = new RecordingNotificationSender();
        var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<INotificationSender>(sender)
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<OrderExecutedNotificationConsumer>();
                x.AddConsumer<OrderRejectedNotificationConsumer>();
                x.AddConsumer<StopLossTriggeredNotificationConsumer>();
                x.AddConsumer<AssumptionsChangedNotificationConsumer>();
                x.AddConsumer<ReportConfirmedNotificationConsumer>();
                x.AddConsumer<CostThresholdReachedNotificationConsumer>();
                x.AddConsumer<WithdrawalTriggeredNotificationConsumer>();
            })
            .BuildServiceProvider(true);
        return (provider, sender);
    }

    [Fact]
    public async Task 約定イベントは取引実行通知を送信する()
    {
        var (provider, sender) = Build();
        await using var _ = provider;
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new OrderExecuted(Guid.NewGuid(), "ORD-1", OrderStatus.Filled, 10, 1_050m, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<OrderExecuted>()).Should().BeTrue();

        sender.Sent.Should().ContainSingle(m => m.Title == "取引実行");

        await harness.Stop();
    }

    [Fact]
    public async Task 拒否イベントは理由つきのリスク統制通知を送信する()
    {
        var (provider, sender) = Build();
        await using var _ = provider;
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new OrderRejected(
            Guid.NewGuid(), Intent(), new[] { RejectionReason.KillSwitchActive }, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<OrderRejected>()).Should().BeTrue();

        sender.Sent.Should().ContainSingle(m => m.Content.Contains(nameof(RejectionReason.KillSwitchActive)));

        await harness.Stop();
    }

    [Fact]
    public async Task 前提条件変更イベントは設定変更通知を送信する()
    {
        var (provider, sender) = Build();
        await using var _ = provider;
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new AssumptionsChanged(2, "owner", "税率見直し", DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<AssumptionsChanged>()).Should().BeTrue();

        sender.Sent.Should().ContainSingle(m => m.Title.Contains("設定変更"));

        await harness.Stop();
    }

    [Fact]
    public async Task 報告書確定イベントは確定通知を送信する()
    {
        var (provider, sender) = Build();
        await using var _ = provider;
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new ReportConfirmed("daily-2026-07-10", "Daily", "owner", 1, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<ReportConfirmed>()).Should().BeTrue();

        sender.Sent.Should().ContainSingle(m => m.Title.Contains("報告書確定") && m.Content.Contains("owner"));

        await harness.Stop();
    }

    [Fact]
    public async Task 費用しきい値到達イベントは費用統制通知を送信する()
    {
        var (provider, sender) = Build();
        await using var _ = provider;
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new CostThresholdReached("2026-07", "Llm", 100m, "Halted", DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<CostThresholdReached>()).Should().BeTrue();

        sender.Sent.Should().ContainSingle(m => m.Title.Contains("費用統制") && m.Severity == NotificationSeverity.Critical);

        await harness.Stop();
    }

    [Fact]
    public async Task 損切りイベントは_Critical_通知を送信する()
    {
        var (provider, sender) = Build();
        await using var _ = provider;
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new StopLossTriggered(
            Guid.NewGuid(), "7203", Market.Japan, TradeSide.Buy, 5, 950m, 940m, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<StopLossTriggered>()).Should().BeTrue();

        sender.Sent.Should().ContainSingle(m => m.Severity == NotificationSeverity.Critical);

        await harness.Stop();
    }

    [Fact]
    public async Task 撤退基準到達イベントは自動停止つきで_Critical_通知を送信する()
    {
        // FR-20, FR-09, #166: 撤退基準到達（自動停止）の通知。降格提案は「確定は承認が必要」を本文で明示する。
        var (provider, sender) = Build();
        await using var _ = provider;
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish(new WithdrawalTriggered(
            ProposedStage: 0, "DrawdownBreachedMultiple", HaltNewEntries: true, DateTimeOffset.UtcNow));
        (await harness.Consumed.Any<WithdrawalTriggered>()).Should().BeTrue();

        sender.Sent.Should().ContainSingle(m =>
            m.Title.Contains("撤退基準到達")
            && m.Severity == NotificationSeverity.Critical
            && m.Content.Contains("自動停止")
            && m.Content.Contains("承認"));

        await harness.Stop();
    }
}
