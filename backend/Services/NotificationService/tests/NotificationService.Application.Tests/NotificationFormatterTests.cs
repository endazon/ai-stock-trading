using AiStockTrading.Notification.Application.Services;
using AiStockTrading.Notification.Application.State;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.Notification.Application.Tests;

// FR-09, UC-01, UC-02, UC-06: イベント→通知メッセージの整形（種別・銘柄・拒否理由・重大度）を検証する。
public class NotificationFormatterTests
{
    private static OrderIntent Intent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper, 10, 1_000m);

    [Fact]
    public void 約定は取引実行として_Info_で整形される()
    {
        var e = new OrderExecuted(Guid.NewGuid(), "ORD-1", OrderStatus.Filled, 10, 1_050m, DateTimeOffset.UtcNow);

        var msg = NotificationFormatter.From(e);

        msg.Title.Should().Be("取引実行");
        msg.Severity.Should().Be(NotificationSeverity.Info);
        msg.Content.Should().Contain("ORD-1");
    }

    [Fact]
    public void 約定以外の終端状態は_Warning_になる()
    {
        var e = new OrderExecuted(Guid.NewGuid(), "ORD-1", OrderStatus.Rejected, 0, 0m, DateTimeOffset.UtcNow);

        NotificationFormatter.From(e).Severity.Should().Be(NotificationSeverity.Warning);
    }

    [Fact]
    public void 発注拒否はリスク統制として理由つきで整形される()
    {
        var reasons = new[] { RejectionReason.KillSwitchActive, RejectionReason.DailyLossLimitReached };
        var e = new OrderRejected(Guid.NewGuid(), Intent(), reasons, DateTimeOffset.UtcNow);

        var msg = NotificationFormatter.From(e);

        msg.Title.Should().Contain("リスク統制");
        msg.Severity.Should().Be(NotificationSeverity.Warning);
        msg.Content.Should().Contain(nameof(RejectionReason.KillSwitchActive));
        msg.Content.Should().Contain("AAPL");
    }

    [Fact]
    public void 損切りライン到達はリスク統制として_Critical_で整形される()
    {
        var e = new StopLossTriggered(Guid.NewGuid(), "7203", Market.Japan, TradeSide.Buy, 5, 950m, 940m, DateTimeOffset.UtcNow);

        var msg = NotificationFormatter.From(e);

        msg.Title.Should().Contain("損切り");
        msg.Severity.Should().Be(NotificationSeverity.Critical);
        msg.Content.Should().Contain("7203");
    }

    // UC-01, FR-09, FR-07, #210: 日報未確定による取引スキップは確定を促す Warning として整形される。
    [Fact]
    public void 日報未確定は確定を促す_Warning_で整形される()
    {
        var e = new DailyPolicyUnconfirmed(new DateOnly(2026, 7, 20), DateTimeOffset.UtcNow);

        var msg = NotificationFormatter.From(e);

        msg.Title.Should().Contain("日報未確定");
        msg.Severity.Should().Be(NotificationSeverity.Warning);
        msg.Content.Should().Contain("2026-07-20");
        msg.Content.Should().Contain("確定");
    }
}
