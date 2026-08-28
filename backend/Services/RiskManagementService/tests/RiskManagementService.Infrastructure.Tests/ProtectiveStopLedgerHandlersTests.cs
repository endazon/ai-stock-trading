using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Infrastructure.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.RiskManagement.Infrastructure.Tests;

// FR-10, UC-02, #331, IADR-0210 決定2/3: 保護レグ（逆指値・成行手仕舞い）の決済 Intent を
// 取引台帳の承認行へ結線するハンドラの検証。承認行が無いとレグの約定（OrderExecuted）を
// 台帳が相関できず、損切りが成立しても建玉が減らない。
public class ProtectiveStopLedgerHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 6, 0, 0, TimeSpan.Zero);

    private static OrderIntent CloseIntent(int qty = 10) =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
            qty, 950m, PositionEffect.Close, StopLossPrice: null, FxRateToBase: 1m);

    [Fact]
    public void 逆指値レグの承認行が台帳へ記録される()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var handler = new ProtectiveStopPlacedLedgerHandler(ledger, NullLogger<ProtectiveStopPlacedLedgerHandler>.Instance);
        var stopDecisionId = Guid.NewGuid();
        var closeIntent = CloseIntent();

        handler.Handle(new ProtectiveStopPlaced(Guid.NewGuid(), stopDecisionId, "stop-1", closeIntent, 950m, 1, Now));

        var recorded = ledger.FindApprovedIntent(stopDecisionId);
        recorded.Should().NotBeNull("逆指値の約定（OrderExecuted）を台帳が相関できるようにする");
        recorded!.PositionEffect.Should().Be(PositionEffect.Close);
        recorded.Side.Should().Be(TradeSide.Sell);
    }

    [Fact]
    public void 逆指値レグの承認行は再送で二重計上されない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var handler = new ProtectiveStopPlacedLedgerHandler(ledger, NullLogger<ProtectiveStopPlacedLedgerHandler>.Instance);
        var stopDecisionId = Guid.NewGuid();
        var evt = new ProtectiveStopPlaced(Guid.NewGuid(), stopDecisionId, "stop-1", CloseIntent(), 950m, 1, Now);

        handler.Handle(evt);
        handler.Handle(evt); // 再送

        // AppendApproval の DecisionId 冪等が担保する（二重の承認行は在庫の二重控除に見える）。
        ledger.FindApprovedIntent(stopDecisionId).Should().NotBeNull();
        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Now.AddMinutes(-1))
            .Should().Be(10, "承認行は 1 本分だけ（二重計上なし）");
    }

    [Fact]
    public void 手仕舞いレグを伴う保護喪失は承認行が台帳へ記録される()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var handler = new ProtectiveStopCoverageLostLedgerHandler(
            ledger, NullLogger<ProtectiveStopCoverageLostLedgerHandler>.Instance);
        var closeDecisionId = Guid.NewGuid();

        handler.Handle(new ProtectiveStopCoverageLost(
            Guid.NewGuid(), "AAPL", Market.UnitedStates,
            ProtectiveStopLossCause.RejectedAtEntry, ProtectiveStopRemediation.PositionClosed,
            10, closeDecisionId, CloseIntent(), Now));

        ledger.FindApprovedIntent(closeDecisionId).Should().NotBeNull();
    }

    [Theory]
    [InlineData(ProtectiveStopRemediation.EntryCancelled)]
    [InlineData(ProtectiveStopRemediation.None)]
    public void 手仕舞いレグの無い保護喪失は台帳へ何も書かない_否定形(ProtectiveStopRemediation remediation)
    {
        // EntryCancelled / None に決済レグは無い。無い承認行を書くと、存在しない決済が在庫を控除する。
        var ledger = new InMemoryPortfolioLedgerStore();
        var handler = new ProtectiveStopCoverageLostLedgerHandler(
            ledger, NullLogger<ProtectiveStopCoverageLostLedgerHandler>.Instance);

        handler.Handle(new ProtectiveStopCoverageLost(
            Guid.NewGuid(), "AAPL", Market.UnitedStates,
            ProtectiveStopLossCause.RejectedAtEntry, remediation,
            10, CloseDecisionId: null, CloseIntent: null, Now));

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Now.AddMinutes(-1)).Should().Be(0);
    }
}
