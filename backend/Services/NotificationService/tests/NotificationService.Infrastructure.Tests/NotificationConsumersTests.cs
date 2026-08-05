using AiStockTrading.Notification.Application.Ports;
using AiStockTrading.Notification.Application.State;
using AiStockTrading.Notification.Infrastructure.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace AiStockTrading.Notification.Infrastructure.Tests;

// FR-09, UC-01, UC-02, UC-06: 取引実行・リスク統制発動のイベント購読 → 通知送信を
// Wolverine のテストハーネス（Wolverine.Tracking）+ 記録 sender で検証する。
//
// ADR-0013, IADR-0129, #354: AddMassTransitTestHarness + harness.Consumed から
// TrackActivity + session.Executed へ移行した。表明の意味は同じ
//（イベントを流し、ハンドラが実行され、期待した通知が 1 件だけ送られる）。
// Wolverine はハンドラを明示登録せずアセンブリ走査で発見するため、テスト側の購読列挙は不要になった
//（発見範囲＝本番と同じ「Infrastructure アセンブリ全体」になり、テストと本番のズレが構造的に消える）。
public class NotificationConsumersTests
{
    private static OrderIntent Intent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 1_000m);

    private static async Task<(IHost Host, RecordingNotificationSender Sender)> BuildAsync()
    {
        var sender = new RecordingNotificationSender();
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<INotificationSender>(sender);
                opts.Discovery.IncludeAssembly(typeof(OrderExecutedNotificationHandler).Assembly);
                // 実ブローカへ接続しない（ローカル・CI ともに RabbitMQ を要求しない）。
                opts.StubAllExternalTransports();
            })
            .StartAsync();
        return (host, sender);
    }

    [Fact]
    public async Task 約定イベントは取引実行通知を送信する()
    {
        var (host, sender) = await BuildAsync();
        using var _ = host;

        var session = await host.TrackActivity().InvokeMessageAndWaitAsync(new OrderExecuted(Guid.NewGuid(), "ORD-1", OrderStatus.Filled, 10, 1_050m, DateTimeOffset.UtcNow, BrokerProvider.MoomooSimulate));
        session.Executed.MessagesOf<OrderExecuted>().Should().NotBeEmpty();

        sender.Sent.Should().ContainSingle(m => m.Title == "取引実行");

        await host.StopAsync();
    }

    [Fact]
    public async Task 拒否イベントは理由つきのリスク統制通知を送信する()
    {
        var (host, sender) = await BuildAsync();
        using var _ = host;

        var session = await host.TrackActivity().InvokeMessageAndWaitAsync(new OrderRejected(
            Guid.NewGuid(), Intent(), new[] { RejectionReason.KillSwitchActive }, DateTimeOffset.UtcNow));
        session.Executed.MessagesOf<OrderRejected>().Should().NotBeEmpty();

        sender.Sent.Should().ContainSingle(m => m.Content.Contains(nameof(RejectionReason.KillSwitchActive)));

        await host.StopAsync();
    }

    [Fact]
    public async Task 前提条件変更イベントは設定変更通知を送信する()
    {
        var (host, sender) = await BuildAsync();
        using var _ = host;

        var session = await host.TrackActivity().InvokeMessageAndWaitAsync(new AssumptionsChanged(2, "owner", "税率見直し", DateTimeOffset.UtcNow));
        session.Executed.MessagesOf<AssumptionsChanged>().Should().NotBeEmpty();

        sender.Sent.Should().ContainSingle(m => m.Title.Contains("設定変更"));

        await host.StopAsync();
    }

    [Fact]
    public async Task 報告書確定イベントは確定通知を送信する()
    {
        var (host, sender) = await BuildAsync();
        using var _ = host;

        var session = await host.TrackActivity().InvokeMessageAndWaitAsync(new ReportConfirmed("daily-2026-07-10", "Daily", "owner", 1, DateTimeOffset.UtcNow));
        session.Executed.MessagesOf<ReportConfirmed>().Should().NotBeEmpty();

        sender.Sent.Should().ContainSingle(m => m.Title.Contains("報告書確定") && m.Content.Contains("owner"));

        await host.StopAsync();
    }

    [Fact]
    public async Task 費用しきい値到達イベントは費用統制通知を送信する()
    {
        var (host, sender) = await BuildAsync();
        using var _ = host;

        var session = await host.TrackActivity().InvokeMessageAndWaitAsync(new CostThresholdReached("2026-07", "Llm", 100m, "Halted", DateTimeOffset.UtcNow));
        session.Executed.MessagesOf<CostThresholdReached>().Should().NotBeEmpty();

        sender.Sent.Should().ContainSingle(m => m.Title.Contains("費用統制") && m.Severity == NotificationSeverity.Critical);

        await host.StopAsync();
    }

    [Fact]
    public async Task 損切りイベントは_Critical_通知を送信する()
    {
        var (host, sender) = await BuildAsync();
        using var _ = host;

        var session = await host.TrackActivity().InvokeMessageAndWaitAsync(new StopLossTriggered(
            Guid.NewGuid(), "7203", Market.Japan, TradeSide.Buy, 5, 950m, 940m, DateTimeOffset.UtcNow));
        session.Executed.MessagesOf<StopLossTriggered>().Should().NotBeEmpty();

        sender.Sent.Should().ContainSingle(m => m.Severity == NotificationSeverity.Critical);

        await host.StopAsync();
    }

    [Fact]
    public async Task 撤退基準到達イベントは自動停止つきで_Critical_通知を送信する()
    {
        // FR-20, FR-09, #166: 撤退基準到達（自動停止）の通知。降格提案は「確定は承認が必要」を本文で明示する。
        var (host, sender) = await BuildAsync();
        using var _ = host;

        var session = await host.TrackActivity().InvokeMessageAndWaitAsync(new WithdrawalTriggered(
            ProposedStage: 0, "DrawdownBreachedMultiple", HaltNewEntries: true, DateTimeOffset.UtcNow));
        session.Executed.MessagesOf<WithdrawalTriggered>().Should().NotBeEmpty();

        sender.Sent.Should().ContainSingle(m =>
            m.Title.Contains("撤退基準到達")
            && m.Severity == NotificationSeverity.Critical
            && m.Content.Contains("自動停止")
            && m.Content.Contains("承認"));

        await host.StopAsync();
    }
}
