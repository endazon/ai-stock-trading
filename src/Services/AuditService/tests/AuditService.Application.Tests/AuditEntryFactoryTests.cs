using AiStockTrading.Audit.Application.Services;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.Audit.Application.Tests;

// FR-11, UC-07, IADR-0019: 各ドメインイベント→AuditEntry の写像（EventType・相関・銘柄・拒否理由）を検証する。
public class AuditEntryFactoryTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private static readonly DateTimeOffset RecordedAt = new(2026, 7, 10, 3, 0, 0, TimeSpan.Zero);

    private static OrderIntent Intent(PositionEffect effect = PositionEffect.Open) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper, 10, 1_000m, effect);

    [Fact]
    public void TradeDecisionMade_は_DecisionId_相関で銘柄と根拠を記録する()
    {
        var decisionId = Guid.NewGuid();
        var e = new TradeDecisionMade(decisionId, Intent(), "上昇トレンドのため買い", new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero));

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("TradeDecisionMade");
        entry.CorrelationId.Should().Be(decisionId);
        entry.Symbol.Should().Be("AAPL");
        entry.Summary.Should().Contain("上昇トレンド");
        entry.OccurredAt.Should().Be(e.DecidedAt);
        entry.RecordedAt.Should().Be(RecordedAt);
        entry.Detail.Should().Contain("AAPL");
    }

    [Fact]
    public void OrderApproved_は_承認数量を要約に含める()
    {
        var decisionId = Guid.NewGuid();
        var e = new OrderApproved(decisionId, Intent(), 7, DateTimeOffset.UtcNow);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("OrderApproved");
        entry.CorrelationId.Should().Be(decisionId);
        entry.Summary.Should().Contain("7");
    }

    [Fact]
    public void OrderRejected_は_拒否理由を記録し照会できる()
    {
        var decisionId = Guid.NewGuid();
        var reasons = new[] { RejectionReason.KillSwitchActive, RejectionReason.DailyLossLimitReached };
        var e = new OrderRejected(decisionId, Intent(), reasons, DateTimeOffset.UtcNow);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("OrderRejected");
        entry.CorrelationId.Should().Be(decisionId);
        entry.Summary.Should().Contain(nameof(RejectionReason.KillSwitchActive));
        entry.Detail.Should().Contain(nameof(RejectionReason.DailyLossLimitReached)); // 列挙は文字列化
    }

    [Fact]
    public void OrderExecuted_は_銘柄なしで_DecisionId_相関を記録する()
    {
        var decisionId = Guid.NewGuid();
        var e = new OrderExecuted(decisionId, "ORD-1", OrderStatus.Filled, 10, 1_050m, DateTimeOffset.UtcNow);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("OrderExecuted");
        entry.CorrelationId.Should().Be(decisionId);
        entry.Symbol.Should().BeNull();
        entry.Summary.Should().Contain("ORD-1");
    }

    [Fact]
    public void PriceMovementDetected_は_EventId_相関で銘柄を記録する()
    {
        var eventId = Guid.NewGuid();
        var e = new PriceMovementDetected(eventId, "7203", Market.Japan, 1_100m, 1_000m, 0.1m, DateTimeOffset.UtcNow);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("PriceMovementDetected");
        entry.CorrelationId.Should().Be(eventId);
        entry.Symbol.Should().Be("7203");
    }

    [Fact]
    public void StopLossTriggered_は_EventId_相関で損切り情報を記録する()
    {
        var eventId = Guid.NewGuid();
        var e = new StopLossTriggered(eventId, "7203", Market.Japan, TradeSide.Buy, 5, 950m, 940m, DateTimeOffset.UtcNow);

        var entry = AuditEntryFactory.From(e, Id, RecordedAt);

        entry.EventType.Should().Be("StopLossTriggered");
        entry.CorrelationId.Should().Be(eventId);
        entry.Symbol.Should().Be("7203");
        entry.Summary.Should().Contain("損切り");
    }
}
