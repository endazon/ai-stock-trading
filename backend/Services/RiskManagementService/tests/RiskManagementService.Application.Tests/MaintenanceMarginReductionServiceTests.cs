using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-10, FR-11, UC-06, ADR-0003, ADR-0009, #330, IADR-0133:
// 維持率割れの自動縮小の実行組み立て（決済注文＋記録イベント）。
//
// 本サービスは「動かす」統制であり、**利用者の承認も AI も統制ストアも介在しない**。
// 介在しないことは依存関係（コンストラクタ）で構造的に示す（T-10-197）。
public class MaintenanceMarginReductionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 14, 30, 0, TimeSpan.Zero);

    private sealed class FixedSnapshotSource(MaintenanceMarginSnapshot? snapshot) : IMaintenanceMarginSnapshotSource
    {
        public MaintenanceMarginSnapshot? GetCurrent() => snapshot;
    }

    private static MarginPosition Short(string symbol, decimal price, int quantity, decimal requiredMargin) =>
        new()
        {
            Symbol = symbol,
            Market = Market.UnitedStates,
            Side = TradeSide.Sell,
            ProductType = ProductType.ShortSell,
            Quantity = quantity,
            PriceUsd = price,
            RequiredMarginUsd = requiredMargin,
        };

    private static MaintenanceMarginReductionService Create(
        MaintenanceMarginSnapshot? snapshot,
        IPortfolioLedgerStore? ledger = null) =>
        new(new InMemoryRiskSettingsStore(),
            new FixedSnapshotSource(snapshot),
            new FakeClock(Now, DateOnly.FromDateTime(Now.UtcDateTime)),
            ledger);

    // 純資産 40,000／建玉 100,000（$100 × 1,000 株）＝ 維持率 40%（閾値ちょうど・発動する）。
    private static MaintenanceMarginSnapshot BreachedSnapshot() =>
        new() { NetEquityUsd = 40_000m, Positions = [Short("AAPL", 100m, 1_000, 30_000m)] };

    // T-10-195: 発動すると**決済（Close）の承認**と**記録イベント**が対で出る。
    // 決済は建玉方向の反対売買であり、空売り（Sell 建て）は Buy で手仕舞う。
    [Fact]
    public void 発動すると決済の承認と記録イベントが対で出る()
    {
        var outcome = Create(BreachedSnapshot()).Evaluate()!;

        outcome.Approvals.Should().ContainSingle();
        var intent = outcome.Approvals[0].Intent;
        intent.Side.Should().Be(TradeSide.Buy, "空売り建玉は買い戻しで手仕舞う");
        intent.PositionEffect.Should().Be(PositionEffect.Close);
        intent.ProductType.Should().Be(ProductType.ShortSell);
        intent.Quantity.Should().Be(112);
        intent.StopLossPrice.Should().BeNull("決済注文に損切りは無い");

        outcome.Executed.Items.Should().ContainSingle();
        outcome.Executed.Items[0].PositionSide.Should().Be(TradeSide.Sell, "記録に残すのは**建玉の**方向である");
        outcome.Executed.Items[0].Quantity.Should().Be(112);
        outcome.Approvals[0].ApprovedQuantity.Should().Be(outcome.Executed.Items[0].Quantity);
    }

    // T-10-196: 記録イベントは日報の表が要求する 7 列（04_report-templates）をすべて持つ。
    [Fact]
    public void 記録イベントは日報が要求する項目をすべて持つ()
    {
        var executed = Create(BreachedSnapshot()).Evaluate()!.Executed;

        executed.ExecutedAt.Should().Be(Now);                 // 時刻
        executed.RatioBefore.Should().Be(0.40m);              // 決済前の維持率
        executed.Threshold.Should().Be(0.40m);                // 閾値
        executed.RecoveryTarget.Should().Be(0.45m);           // 回復目標（閾値+5pt）
        executed.RatioAfter.Should().BeGreaterThanOrEqualTo(0.45m); // 決済後の維持率
        var item = executed.Items[0];
        (item.Symbol, item.Market).Should().Be(("AAPL", Market.UnitedStates)); // 銘柄
        item.PositionSide.Should().Be(TradeSide.Sell);        // 方向
        item.Quantity.Should().Be(112);                       // 数量
        item.RequiredMarginUsd.Should().Be(30_000m * 112 / 1_000); // 必要証拠金
    }

    // T-10-197（否定形・構造）: **AI・利用者の承認・3 統制・発注前スクリーニングを経由しない**。
    // UC-06 は「3 統制のいずれかが成立していても自動縮小は動く」「AI を介在させない」と定める。
    // 依存に持たないことがその構造的な保証であり、依存が増えたら本テストが赤くなる。
    [Fact]
    public void 統制ストアもスクリーニングもAIも依存に持たない()
    {
        var dependencies = typeof(MaintenanceMarginReductionService)
            .GetConstructors().Single()
            .GetParameters()
            .Select(p => p.ParameterType.Name)
            .ToList();

        dependencies.Should().NotContain([
            nameof(IKillSwitchStore), nameof(ILockoutStore), nameof(IPauseStore),
            nameof(OrderScreeningService), nameof(RiskEvaluator),
        ]);
        dependencies.Should().BeEquivalentTo(
            [
                nameof(IRiskSettingsStore), nameof(IMaintenanceMarginSnapshotSource),
                nameof(IClock), nameof(IPortfolioLedgerStore),
            ],
            "自動縮小は統制でも AI でも止められない経路である（ADR-0009・UC-06）");
    }

    // T-10-198（否定形）: 供給が無いなら 1 株も決済しない（データが無いのに決済しない・IADR-0133 決定5）。
    [Fact]
    public void 維持率の供給が無いなら決済しない()
    {
        Create(snapshot: null).Evaluate().Should().BeNull();
        Create(new MaintenanceMarginSnapshot { NetEquityUsd = 100_000m, Positions = [] })
            .Evaluate().Should().BeNull();
    }

    // T-10-199: 維持率が閾値を上回っていれば発動しない。
    [Fact]
    public void 維持率が閾値を上回るなら発動しない()
    {
        var healthy = new MaintenanceMarginSnapshot
        {
            NetEquityUsd = 60_000m,
            Positions = [Short("AAPL", 100m, 1_000, 30_000m)],
        };

        Create(healthy).Evaluate().Should().BeNull();
    }

    // T-10-200: #257/IADR-0107 — 決済レグへ建玉の加重平均約定時レートを引き継ぐ。
    // 引き継がないと外貨建て（＝信用・空売りが起こる米国株）の決済だけが未換算で台帳へ積まれ、
    // 基準通貨の実現損益が桁で誤る。台帳が無ければレート 1 で**決済は続行する**（手仕舞いを止めない）。
    [Fact]
    public void 決済注文は建玉の換算レートを引き継ぐ()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var decisionId = Guid.NewGuid();
        ledger.AppendApproval(
            decisionId,
            new OrderIntent(
                "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.ShortSell, TradeMode.Paper,
                1_000, 100m, PositionEffect.Open, StopLossPrice: 110m, FxRateToBase: 150m),
            Now.AddDays(-1));
        ledger.AppendFill(decisionId, $"open-{decisionId:N}", 1_000, 100m, Now.AddDays(-1));

        Create(BreachedSnapshot(), ledger).Evaluate()!.Approvals[0].Intent.FxRateToBase.Should().Be(150m);
        Create(BreachedSnapshot()).Evaluate()!.Approvals[0].Intent.FxRateToBase.Should().Be(1m);
    }
}
