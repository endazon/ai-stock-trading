using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-10, FR-03, ADR-0003, IADR-0015: 損切りの決済（Close）承認組み立ての検証。
public class StopLossExecutionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 6, 0, 0, TimeSpan.Zero);

    private static StopLossExecutionService Create(RiskManagementSettings? settings = null) =>
        new(new InMemoryRiskSettingsStore(settings), new FakeClock(Now, new DateOnly(2026, 7, 10)));

    private static StopLossTriggered Triggered(TradeSide side, int qty = 10, decimal price = 950m) =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, side, qty, price, 970m, Now);

    [Fact]
    public void ロング建玉の損切りは同数量の売り決済を発行する()
    {
        var approval = Create().BuildCloseApproval(Triggered(TradeSide.Buy));

        approval.Intent.Side.Should().Be(TradeSide.Sell);          // 反対売買
        approval.Intent.PositionEffect.Should().Be(PositionEffect.Close);
        approval.Intent.Quantity.Should().Be(10);
        approval.Intent.Price.Should().Be(950m);
        approval.ApprovedQuantity.Should().Be(10);
        approval.ApprovedAt.Should().Be(Now);
    }

    [Fact]
    public void ショート建玉の損切りは買い決済を発行する()
    {
        var approval = Create().BuildCloseApproval(Triggered(TradeSide.Sell));

        approval.Intent.Side.Should().Be(TradeSide.Buy);
        approval.Intent.PositionEffect.Should().Be(PositionEffect.Close);
    }

    [Fact]
    public void 決済のモードは現行段階の動作モードを用いる()
    {
        // Stage2（最小実弾・Live）に設定 → 決済も Live。
        var settings = TradingDefaults.CreateSettings() with
        {
            Stage = new StageSettings(TradingStage.Stage2MinimalLive, TradeMode.Live, 100_000m),
        };

        var approval = Create(settings).BuildCloseApproval(Triggered(TradeSide.Buy));

        approval.Intent.Mode.Should().Be(TradeMode.Live);
    }

    [Fact]
    public void 決済の商品種別は現物とする()
    {
        Create().BuildCloseApproval(Triggered(TradeSide.Buy))
            .Intent.ProductType.Should().Be(ProductType.Cash);
    }

    [Fact]
    public void DecisionIdはイベントIDから決定的に採られ再送でも同一になる()
    {
        // IADR-0015: 冪等性。同一 StopLossTriggered を 2 回処理しても DecisionId は同じ（= EventId）。
        var service = Create();
        var triggered = Triggered(TradeSide.Buy);

        var first = service.BuildCloseApproval(triggered);
        var second = service.BuildCloseApproval(triggered);

        first.DecisionId.Should().Be(triggered.EventId);
        second.DecisionId.Should().Be(triggered.EventId);
    }

    // --- FR-17, #257, IADR-0107: 決済レグにも建玉の換算レートを引き継ぐ ---

    // 20 USD × 10 株をレート 150 で建てた台帳（承認＋約定）。
    private static InMemoryPortfolioLedgerStore LedgerWithUsdPosition(decimal fxRateToBase)
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var decisionId = Guid.NewGuid();
        ledger.AppendApproval(
            decisionId,
            new OrderIntent(
                "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper,
                10, 20m, PositionEffect.Open, StopLossPrice: 19m, FxRateToBase: fxRateToBase),
            Now.AddHours(-1));
        ledger.AppendFill(decisionId, "order-1", 10, 20m, Now.AddHours(-1));
        return ledger;
    }

    [Fact]
    public void 損切り決済は建玉の換算レートを引き継ぐ()
    {
        // 引き継がないと決済レグだけ未換算（レート 1）で台帳へ積まれ、実現損益・取得額が桁で誤る。
        var service = new StopLossExecutionService(
            new InMemoryRiskSettingsStore(null), new FakeClock(Now, new DateOnly(2026, 7, 10)),
            LedgerWithUsdPosition(fxRateToBase: 150m));

        var approval = service.BuildCloseApproval(Triggered(TradeSide.Buy, price: 19m));

        approval.Intent.FxRateToBase.Should().Be(150m);
        approval.Intent.Price.Should().Be(19m, "決済価格はローカル通貨（USD）のまま");
        approval.Intent.NotionalInBase.Should().Be(10 * 19m * 150m);
    }

    [Fact]
    public void 該当建玉が台帳に無くても決済は必ず発行する_failsafe()
    {
        // ADR-0003: 損切りは必ず実行する。レートが引けないことを理由に決済を止めない（レート 1 へ縮退）。
        var service = new StopLossExecutionService(
            new InMemoryRiskSettingsStore(null), new FakeClock(Now, new DateOnly(2026, 7, 10)),
            new InMemoryPortfolioLedgerStore());

        var approval = service.BuildCloseApproval(Triggered(TradeSide.Buy));

        approval.Intent.Quantity.Should().Be(10);
        approval.Intent.FxRateToBase.Should().Be(1m);
    }

    [Fact]
    public void 台帳未注入でも決済は従来どおり発行する_回帰()
    {
        var approval = Create().BuildCloseApproval(Triggered(TradeSide.Buy));

        approval.Intent.FxRateToBase.Should().Be(1m);
    }
}
