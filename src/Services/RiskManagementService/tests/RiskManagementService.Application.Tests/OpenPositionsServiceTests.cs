using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-03, FR-10, IADR-0030: 保有ポジションの公開ビュー導出（#63 台帳射影＋損切り価格の近似）を検証する。
public class OpenPositionsServiceTests
{
    private static readonly DateTimeOffset At = new(2026, 7, 10, 3, 0, 0, TimeSpan.Zero);

    // 与えた LedgerFill 列をそのまま返す最小のフェイク台帳（射影入力の供給のみ）。
    private sealed class FakeLedger(params LedgerFill[] fills) : IPortfolioLedgerStore
    {
        public void AppendApproval(Guid decisionId, OrderIntent intent, DateTimeOffset approvedAt) { }
        public bool AppendFill(Guid decisionId, string orderId, int filledQuantity, decimal averagePrice, DateTimeOffset executedAt) => true;
        public IReadOnlyList<LedgerFill> GetFills() => fills;
    }

    private static LedgerFill Fill(TradeSide side, int qty, decimal price, string symbol = "AAPL") =>
        new(symbol, Market.UnitedStates, side, side == TradeSide.Buy ? PositionEffect.Open : PositionEffect.Close, qty, price, At);

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
