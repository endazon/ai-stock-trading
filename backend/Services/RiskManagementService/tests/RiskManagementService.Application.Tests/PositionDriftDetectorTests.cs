using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-05, FR-10, FR-11, #292, IADR-0118: 取引台帳とブローカ実ポジションの突合（純関数）。
public class PositionDriftDetectorTests
{
    private static OpenPosition Ledger(string symbol, int quantity, TradeSide side = TradeSide.Buy,
        decimal avg = 20m, Market market = Market.UnitedStates) =>
        new(symbol, market, side, quantity, avg, StopLossPrice: null, FxRateToBase: 150m);

    private static BrokerPositionSnapshot Broker(string symbol, int signedQuantity,
        decimal avg = 20m, Market market = Market.UnitedStates) =>
        new(symbol, market, signedQuantity, avg);

    [Fact]
    public void 一致していれば乖離は無い()
    {
        var drifts = PositionDriftDetector.Detect([Ledger("AAPL", 100)], [Broker("AAPL", 100)]);

        drifts.Should().BeEmpty();
    }

    [Fact]
    public void 双方が空なら乖離は無い()
    {
        PositionDriftDetector.Detect([], []).Should().BeEmpty();
    }

    [Fact]
    public void ブローカにだけある建玉を検出する()
    {
        // 手動売買・外部約定など AST が知らない取引。#270 破損期の過大建玉もこの形で現れる。
        var drifts = PositionDriftDetector.Detect([], [Broker("AAPL", 4072)]);

        var drift = drifts.Should().ContainSingle().Subject;
        drift.Symbol.Should().Be("AAPL");
        drift.Kind.Should().Be(PositionDriftKind.BrokerOnly);
        drift.LedgerQuantity.Should().Be(0);
        drift.BrokerQuantity.Should().Be(4072);
    }

    [Fact]
    public void 台帳にだけある建玉を検出する()
    {
        var drifts = PositionDriftDetector.Detect([Ledger("AAPL", 100)], []);

        var drift = drifts.Should().ContainSingle().Subject;
        drift.Kind.Should().Be(PositionDriftKind.LedgerOnly);
        drift.LedgerQuantity.Should().Be(100);
        drift.BrokerQuantity.Should().Be(0);
    }

    [Fact]
    public void 数量の不一致を双方の数量つきで検出する()
    {
        var drifts = PositionDriftDetector.Detect([Ledger("AAPL", 100)], [Broker("AAPL", 80)]);

        var drift = drifts.Should().ContainSingle().Subject;
        drift.Kind.Should().Be(PositionDriftKind.QuantityMismatch);
        drift.LedgerQuantity.Should().Be(100);
        drift.BrokerQuantity.Should().Be(80);
    }

    [Fact]
    public void ショート建玉は負の数量として突合する()
    {
        // 台帳の OpenPosition は Side × 正の数量。ブローカは符号付き。表現を揃えないと全件が乖離になる。
        PositionDriftDetector
            .Detect([Ledger("AAPL", 100, TradeSide.Sell)], [Broker("AAPL", -100)])
            .Should().BeEmpty();
    }

    [Fact]
    public void 方向違いは数量不一致として検出する()
    {
        var drift = PositionDriftDetector
            .Detect([Ledger("AAPL", 100, TradeSide.Buy)], [Broker("AAPL", -100)])
            .Should().ContainSingle().Subject;

        drift.Kind.Should().Be(PositionDriftKind.QuantityMismatch);
        drift.LedgerQuantity.Should().Be(100);
        drift.BrokerQuantity.Should().Be(-100);
    }

    [Fact]
    public void 平均取得単価の違いは乖離としない()
    {
        // 手数料・端数・為替で必ずズレる。判定に使うと恒常的に鳴り続ける。
        PositionDriftDetector
            .Detect([Ledger("AAPL", 100, avg: 20m)], [Broker("AAPL", 100, avg: 20.37m)])
            .Should().BeEmpty();
    }

    [Fact]
    public void 同一コードの別市場は別建玉として扱う()
    {
        var drifts = PositionDriftDetector.Detect(
            [Ledger("6902", 100, market: Market.Japan)],
            [Broker("6902", 100, market: Market.UnitedStates)]);

        drifts.Should().HaveCount(2);
        drifts.Should().Contain(d => d.Market == Market.Japan && d.Kind == PositionDriftKind.LedgerOnly);
        drifts.Should().Contain(d => d.Market == Market.UnitedStates && d.Kind == PositionDriftKind.BrokerOnly);
    }

    [Fact]
    public void 数量ゼロのブローカ建玉は保有なしとして扱う()
    {
        // 全決済済みの行を返すブローカがあっても「ブローカにだけある建玉」にしない。
        PositionDriftDetector.Detect([], [Broker("AAPL", 0)]).Should().BeEmpty();
    }

    [Fact]
    public void 複数銘柄の乖離を銘柄順に返す()
    {
        var drifts = PositionDriftDetector.Detect(
            [Ledger("MSFT", 10), Ledger("AAPL", 100)],
            [Broker("AAPL", 80)]);

        drifts.Select(d => d.Symbol).Should().Equal("AAPL", "MSFT");
    }
}
