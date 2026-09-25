using AuditService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AuditService.Tests.Domain;

// FR-10, FR-11, FR-12, ADR-0040 決定1（S2）, #826 項目 5, IADR-0413 決定2: 免除（受付時点・発注数量）を、
// 同じ相関の終端の約定記録で打ち消す／数量を確定する派生記録の判定。
public class ProtectiveStopWaiverSettlementTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RecordedAt = T0.AddMinutes(30);

    private static AuditEntry Waived(Guid decisionId, int quantity = 10) =>
        AuditEntryFactory.From(
            new ProtectiveStopWaived(decisionId, "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
                quantity, 950m, StopLossExecutionMethod.NoProtectiveStop, BrokerProvider.MoomooSimulate, T0),
            Guid.NewGuid(), T0);

    private static AuditEntry Executed(Guid decisionId, OrderStatus status, int filled, int minutes = 5) =>
        AuditEntryFactory.From(
            new OrderExecuted(decisionId, "ORD-1", status, filled, filled == 0 ? 0m : 1_000m, T0.AddMinutes(minutes),
                BrokerProvider.MoomooSimulate),
            Guid.NewGuid(), T0.AddMinutes(minutes));

    // T-10-897: 受付のまま約定 0 で終端（取消・失効・拒否）した免除は「打ち消し（建玉は生じなかった）」を 1 件残す。
    [Theory]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Expired)]
    [InlineData(OrderStatus.Rejected)]
    public void 約定0で終端した免除は打ち消しの記録になる(OrderStatus terminal)
    {
        var decisionId = Guid.NewGuid();
        var chain = new[] { Waived(decisionId), Executed(decisionId, OrderStatus.Accepted, 0, 0), Executed(decisionId, terminal, 0) };

        var settled = ProtectiveStopWaiverSettlement.TryCreate(chain, RecordedAt);

        settled.Should().NotBeNull();
        settled!.EventType.Should().Be("ProtectiveStopWaiverSettled");
        settled.EventType.Should().NotBe(nameof(ProtectiveStopWaived));
        settled.CorrelationId.Should().Be(decisionId, "エントリーと 1 本で辿る");
        settled.Id.Should().Be(ProtectiveStopWaiverSettlement.IdFor(decisionId));
        settled.Symbol.Should().Be("AAPL");
        settled.Summary.Should().Contain("免除を打ち消し").And.Contain("建玉は生じなかった")
            .And.Contain(terminal.ToString()).And.Contain("発注数量10");
        settled.Detail.Should().Contain("\"FilledQuantity\":0").And.Contain("\"WaivedQuantity\":10");
        settled.OccurredAt.Should().Be(T0.AddMinutes(5), "終端の約定記録の時刻");
        settled.RecordedAt.Should().Be(RecordedAt);
    }

    // T-10-898: 一部約定で終端した免除は「免除の対象は約定数 N（発注 M のうち）」を残す。
    [Fact]
    public void 一部約定で終端した免除は約定数で対象を確定する()
    {
        var decisionId = Guid.NewGuid();
        var chain = new[]
        {
            Waived(decisionId), Executed(decisionId, OrderStatus.PartiallyFilled, 4, 1), Executed(decisionId, OrderStatus.Cancelled, 4),
        };

        var settled = ProtectiveStopWaiverSettlement.TryCreate(chain, RecordedAt);

        settled.Should().NotBeNull();
        settled!.Summary.Should().Contain("免除の対象を確定").And.Contain("約定数4").And.Contain("発注数量10")
            .And.NotContain("建玉は生じなかった");
        settled.Detail.Should().Contain("\"FilledQuantity\":4");
    }

    // T-10-899（否定形）: 全量約定・免除の無いエントリー・非終端では作らない。
    [Fact]
    public void 全量約定の免除では打ち消しを作らない()
    {
        var decisionId = Guid.NewGuid();

        ProtectiveStopWaiverSettlement.TryCreate(
            [Waived(decisionId), Executed(decisionId, OrderStatus.Filled, 10)], RecordedAt).Should().BeNull();
    }

    [Fact]
    public void 免除の無いエントリーの終端では打ち消しを作らない()
    {
        var decisionId = Guid.NewGuid();

        ProtectiveStopWaiverSettlement.TryCreate(
            [Executed(decisionId, OrderStatus.Cancelled, 0)], RecordedAt).Should().BeNull();
    }

    [Theory]
    [InlineData(OrderStatus.Accepted, 0)]
    [InlineData(OrderStatus.PartiallyFilled, 4)]
    public void 非終端では打ち消しを作らない(OrderStatus status, int filled)
    {
        var decisionId = Guid.NewGuid();

        ProtectiveStopWaiverSettlement.TryCreate(
            [Waived(decisionId), Executed(decisionId, status, filled)], RecordedAt).Should().BeNull();
    }

    [Fact]
    public void 免除の記録だけでは打ち消しを作らない()
    {
        ProtectiveStopWaiverSettlement.TryCreate([Waived(Guid.NewGuid())], RecordedAt).Should().BeNull();
    }
}
