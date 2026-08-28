using RiskManagementService.Application.Ports;
using RiskManagementService.Application.Services;
using RiskManagementService.Application.State;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Application.Tests;

// FR-03, FR-10, IADR-0030/0035: 保有ポジションの公開ビュー導出（#63 台帳射影＋損切り価格の実値/近似）を検証する。
public class OpenPositionsServiceTests
{
    private static readonly DateTimeOffset At = new(2026, 7, 10, 3, 0, 0, TimeSpan.Zero);

    // 与えた LedgerFill 列をそのまま返す最小のフェイク台帳（射影入力の供給のみ）。
    private sealed class FakeLedger(params LedgerFill[] fills) : IPortfolioLedgerStore
    {
        public void AppendApproval(Guid decisionId, OrderIntent intent, DateTimeOffset approvedAt) { }
        public bool AppendFill(Guid decisionId, string orderId, int filledQuantity, decimal averagePrice, DateTimeOffset executedAt) => true;
        public IReadOnlyList<LedgerFill> GetFills() => fills;
        public PositionEffect? FindApprovedPositionEffect(Guid decisionId) => null;
        public OrderIntent? FindApprovedIntent(Guid decisionId) => null;
        public int GetInFlightCloseQuantity(string symbol, Market market, DateTimeOffset approvedAtOrAfter) => 0;
    }

    // 損切り価格を持たない約定（欠損＝近似フォールバックの検証用）。
    private static LedgerFill Fill(TradeSide side, int qty, decimal price, string symbol = "AAPL") =>
        new(symbol, Market.UnitedStates, side, side == TradeSide.Buy ? PositionEffect.Open : PositionEffect.Close, qty, price, At);

    // 損切り価格を持つ約定（権威データ・IADR-0035）。
    private static LedgerFill FillSl(TradeSide side, int qty, decimal price, decimal stop, string symbol = "AAPL") =>
        new(symbol, Market.UnitedStates, side, side == TradeSide.Buy ? PositionEffect.Open : PositionEffect.Close, qty, price, At, stop);

    [Fact]
    public void 損切り価格があれば実値を用いる_近似しない()
    {
        // IADR-0035: 権威データ（ATR 連動の実損切り価格 950）をそのまま返す（近似 970 ではない）。
        var service = new OpenPositionsService(new FakeLedger(FillSl(TradeSide.Buy, 10, 1_000m, 950m)));

        service.Build().Single().StopLossPrice.Should().Be(950m);
    }

    [Fact]
    public void 損切り価格が欠損なら近似にフォールバックする()
    {
        // IADR-0030/0035: 実値が無いレガシー建玉は既定比率 3% 近似（1,000×0.97=970）。
        var service = new OpenPositionsService(new FakeLedger(Fill(TradeSide.Buy, 10, 1_000m)));

        service.Build().Single().StopLossPrice.Should().Be(1_000m * (1m - TradingDefaults.DefaultStopLossRatio));
    }

    [Fact]
    public void ロングは取得単価より下に損切り価格を近似導出する()
    {
        // 平均取得 1,000・既定比率 3% → 損切り 970。
        var service = new OpenPositionsService(new FakeLedger(Fill(TradeSide.Buy, 10, 1_000m)));

        var view = service.Build().Single();

        view.Symbol.Should().Be("AAPL");
        view.Side.Should().Be(TradeSide.Buy);
        view.Quantity.Should().Be(10);
        view.EntryPrice.Should().Be(1_000m);
        view.StopLossPrice.Should().Be(1_000m * (1m - TradingDefaults.DefaultStopLossRatio)); // 970
    }

    [Fact]
    public void ショートは取得単価より上に損切り価格を近似導出する()
    {
        // 5 株買い建て後に 10 株売り → ネット −5 ショート（反転で取得単価は売り単価 1,100）。損切りは上側。
        var service = new OpenPositionsService(new FakeLedger(
            Fill(TradeSide.Buy, 5, 1_000m),
            Fill(TradeSide.Sell, 10, 1_100m)));

        var view = service.Build().Single();

        view.Side.Should().Be(TradeSide.Sell);
        view.Quantity.Should().Be(5);
        view.EntryPrice.Should().Be(1_100m);
        view.StopLossPrice.Should().Be(1_100m * (1m + TradingDefaults.DefaultStopLossRatio)); // 1,133
    }

    [Fact]
    public void 保有が無ければ空を返す()
    {
        new OpenPositionsService(new FakeLedger()).Build().Should().BeEmpty();
    }
}
