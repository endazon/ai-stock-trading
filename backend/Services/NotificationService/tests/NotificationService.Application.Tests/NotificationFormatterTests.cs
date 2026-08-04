using AiStockTrading.Notification.Application.Services;
using AiStockTrading.Notification.Application.State;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
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

    // FR-06/07/09, UC-03〜05, IADR-0116, #280: 報告書ドラフトの提示は確定依頼として整形される。
    [Fact]
    public void 報告書ドラフトの提示は要約と確定依頼を含む_Info_で整形される()
    {
        var e = new ReportDraftPresented(
            "daily-2026-07-29", "Daily", "2026-07-29",
            "日報 2026-07-29（承認待ち）\n実現損益（税引後・費用込み）: +12,300 円", 3, DateTimeOffset.UtcNow);

        var msg = NotificationFormatter.From(e);

        msg.Title.Should().Contain("承認待ち");
        msg.Severity.Should().Be(NotificationSeverity.Info);
        msg.Content.Should().Contain("+12,300 円");        // 発行側で組み立てた要約をそのまま載せる
        msg.Content.Should().Contain("daily-2026-07-29");
        msg.Content.Should().Contain("版 3");               // 確定 API の expectedVersion（IADR-0024）
        msg.Content.Should().Contain("確定");
    }

    [Fact]
    public void 報告書ドラフトの提示は確定前に方針が変わらないことを明示する()
    {
        // ADR-0003: 確定するまで取引方針は変わらない。通知を読んだ利用者が「もう適用された」と誤解しないようにする。
        var e = new ReportDraftPresented("weekly-2026-W31", "Weekly", "2026-W31", "週報の要約", 1, DateTimeOffset.UtcNow);

        NotificationFormatter.From(e).Content.Should().Contain("確定するまで取引方針は変わりません");
    }

    [Fact]
    public void 建玉の乖離は双方の数量と是正しないことを明示する()
    {
        // FR-05/FR-09/FR-10, #292, IADR-0118: 自動是正しないため、通知が検知の唯一の出口になる。
        // どちらが正しいかを断定せず双方の数量を並べ、利用者が判断できるようにする。
        var e = new PositionReconciliationDrift(
            [
                new PositionDriftItem("AAPL", Market.UnitedStates, 0, 4072, PositionDriftKind.BrokerOnly),
                new PositionDriftItem("7203", Market.Japan, 100, 80, PositionDriftKind.QuantityMismatch),
            ],
            new DateTimeOffset(2026, 7, 30, 6, 0, 0, TimeSpan.Zero),
            DateTimeOffset.UtcNow);

        var msg = NotificationFormatter.From(e);

        // 台帳が誤っていれば統制上限の判定そのものが狂うため Critical。
        msg.Severity.Should().Be(NotificationSeverity.Critical);
        msg.Content.Should().Contain("AAPL").And.Contain("4072");
        msg.Content.Should().Contain("7203").And.Contain("100").And.Contain("80");
        msg.Content.Should().Contain("自動是正は行いません");
    }

    // FR-09, FR-10, UC-06, #330, IADR-0133 決定7: 維持率割れの自動縮小（**記録先 2: Discord 通知**）。
    // 利用者の承認を待たずに決済するため、通知が「建玉が減ったこと」を利用者が知る最初の経路になる。
    // 決済前後の維持率・閾値・回復目標・決済した建玉を本文に出す（数値が無ければ規則どおりの作動を確かめられない）。
    [Fact]
    public void 維持率割れの自動縮小は決済前後の維持率と建玉をCriticalで通知する()
    {
        var e = new MaintenanceMarginReductionExecuted(
            Guid.NewGuid(), RatioBefore: 0.40m, Threshold: 0.40m, RecoveryTarget: 0.45m, RatioAfter: 0.4504m,
            [new MaintenanceMarginReductionItem(
                "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.ShortSell, 112, 100m, 3_360m)],
            new DateTimeOffset(2026, 8, 4, 14, 30, 0, TimeSpan.Zero));

        var msg = NotificationFormatter.From(e);

        msg.Severity.Should().Be(NotificationSeverity.Critical);
        msg.Content.Should().Contain("40.0%").And.Contain("45.0%").And.Contain("45.0%");
        msg.Content.Should().Contain("AAPL").And.Contain("ショート").And.Contain("112").And.Contain("3,360.00");
        msg.Content.Should().Contain("承認と AI の判断は介在していません");
    }
}
