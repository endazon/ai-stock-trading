using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-10, UC-06, SC-03, ADR-0016（決定3・決定7・決定9・決定15）, #340, IADR-0154:
// SC-03「維持率・空売りの現況」の集約。
//
// **本テストの主眼は「値が正しいこと」ではなく「供給が無いことを供給が無いと言うこと」である。**
// 維持率は計画（05_screens SC-03）が「本画面の最上位に置く。マージンコールは口座を失う唯一の経路である」と
// 定めた指標であり、未供給を 0 や空列で運ぶと画面は正常な統制として描いてしまう
// （#403 の ControlViolationCount 既定 0 が「違反なし」に見えた fail-open と同型）。
public class ShortSellingStatusServiceTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 6, 3, 0, 0, TimeSpan.Zero);

    private sealed class FakeLedger(params LedgerFill[] fills) : IPortfolioLedgerStore
    {
        public void AppendApproval(Guid decisionId, OrderIntent intent, DateTimeOffset approvedAt) { }
        public bool AppendFill(Guid decisionId, string orderId, int filledQuantity, decimal averagePrice, DateTimeOffset executedAt) => true;
        public IReadOnlyList<LedgerFill> GetFills() => fills;
        public PositionEffect? FindApprovedPositionEffect(Guid decisionId) => null;
        public int GetInFlightCloseQuantity(string symbol, Market market, DateTimeOffset approvedAtOrAfter) => 0;
    }

    private sealed class FixedSnapshotSource(MaintenanceMarginSnapshot? snapshot) : IMaintenanceMarginSnapshotSource
    {
        public MaintenanceMarginSnapshot? GetCurrent() => snapshot;
    }

    private sealed class FakePriceSource(Dictionary<(string Symbol, Market Market), decimal> prices) : ICurrentPriceSource
    {
        public IReadOnlyDictionary<(string Symbol, Market Market), decimal> GetCurrentPrices(
            IReadOnlyList<OpenPosition> positions) => prices;
    }

    private static LedgerFill Fill(TradeSide side, int qty, decimal price, string symbol) =>
        new(symbol, Market.UnitedStates, side, PositionEffect.Open, qty, price, At);

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

    private static ShortSellingStatusService Create(
        MaintenanceMarginSnapshot? snapshot = null,
        LedgerFill[]? fills = null,
        Dictionary<(string Symbol, Market Market), decimal>? prices = null) =>
        new(new InMemoryRiskSettingsStore(),
            new FakeLedger(fills ?? []),
            new FixedSnapshotSource(snapshot),
            prices is null ? null : new FakePriceSource(prices));

    // ---- 維持率（ADR-0016 決定7・画面最上位） ----

    // T-340-01: **供給元が無いことを「未供給」として宣言する。** 0 でも「建玉なし」でもない。
    // 既定アダプタ（UnavailableMaintenanceMarginSnapshotSource）が null を返す現状がこれに当たる。
    [Fact]
    public void 維持率の供給元が無ければ未供給として宣言する()
    {
        var view = Create(snapshot: null).Build();

        view.MaintenanceMarginAvailability.Should().Be(MetricAvailability.NotSupplied);
        view.MaintenanceMarginRatio.Should().BeNull();
        view.AppliedMaintenanceMarginThreshold.Should().BeNull();
        view.AppliedMaintenanceRecoveryTarget.Should().BeNull();
    }

    // T-340-02: 既定アダプタ（本番の現行構成）を通しても同じ結論になる。
    // フェイクだけで確かめると「本番の既定が実は値を返している」場合に気づけない。
    [Fact]
    public void 既定アダプタ構成では維持率が未供給になる()
    {
        var service = new ShortSellingStatusService(
            new InMemoryRiskSettingsStore(),
            new FakeLedger(),
            new UnavailableMaintenanceMarginSnapshotSource());

        service.Build().MaintenanceMarginAvailability.Should().Be(MetricAvailability.NotSupplied);
    }

    // T-340-03: 供給があるときは値・適用閾値・回復目標（閾値 + 5pt）を返す。
    // 株価 $100 では自前の 40% が規制側（max(5/100, 30%) = 30%）より厳しいため 40%、回復目標は 45%。
    [Fact]
    public void 維持率の供給があれば値と適用閾値と回復目標を返す()
    {
        var snapshot = new MaintenanceMarginSnapshot
        {
            NetEquityUsd = 40_000m,
            Positions = [Short("AAPL", 100m, 1_000, 30_000m)],
        };

        var view = Create(snapshot).Build();

        view.MaintenanceMarginAvailability.Should().Be(MetricAvailability.Available);
        view.MaintenanceMarginRatio.Should().Be(0.40m);
        view.AppliedMaintenanceMarginThreshold.Should().Be(0.40m);
        view.AppliedMaintenanceRecoveryTarget.Should().Be(0.45m);
    }

    // T-340-04: **回復目標は閾値に連動する**（計画 §5）。規制側が効く低位株（$8）では
    // 閾値 max(5/8, 30%) = 62.5%、回復目標は 67.5% になる（40%/45% の固定値ではない）。
    [Fact]
    public void 回復目標は適用閾値に連動する()
    {
        var snapshot = new MaintenanceMarginSnapshot
        {
            NetEquityUsd = 5_000m,
            Positions = [Short("PENNY", 8m, 1_000, 5_000m)],
        };

        var view = Create(snapshot).Build();

        view.AppliedMaintenanceMarginThreshold.Should().Be(0.625m);
        view.AppliedMaintenanceRecoveryTarget.Should().Be(0.675m);
    }

    // T-340-05: IADR-0133 決定8 と同じ向きに倒す。壊れた建玉が混ざるスナップショットは
    // **「良く見える維持率」を出さない**（壊れた建玉だけを除くと分母が縮んで実際より良く見える）。
    [Fact]
    public void 信頼できないスナップショットは未供給として扱う()
    {
        var snapshot = new MaintenanceMarginSnapshot
        {
            NetEquityUsd = 40_000m,
            Positions = [Short("AAPL", 100m, 1_000, 30_000m), Short("BROKEN", 0m, 10, 1m)],
        };

        var view = Create(snapshot).Build();

        view.MaintenanceMarginAvailability.Should().Be(MetricAvailability.NotSupplied);
        view.MaintenanceMarginRatio.Should().BeNull();
    }

    // T-340-06: 建玉が 1 件も無いのは**異常ではない**（維持率という概念が成立しないだけ）。
    // 未供給と同じ表示にすると「統制が働いていない」と読ませてしまう。
    [Fact]
    public void 信用建玉が無ければ維持率は概念として成立しない()
    {
        var snapshot = new MaintenanceMarginSnapshot { NetEquityUsd = 3_000m, Positions = [] };

        Create(snapshot).Build().MaintenanceMarginAvailability.Should().Be(MetricAvailability.NotApplicable);
    }

    // T-340-07: 設定値（自前閾値 40%・回復目標オフセット 5pt・空売り比率上限 50%）は常に返す。
    // 画面が閾値を直書きしないため（Stage1GateCriteria と同じ方針）、供給が無くても設定値だけは要る。
    [Fact]
    public void 設定上の閾値と上限は供給が無くても返す()
    {
        var defaults = TradingDefaults.CreateShortSellSettings().Limits;
        var view = Create(snapshot: null).Build();

        view.ConfiguredMaintenanceMarginThreshold.Should().Be(defaults.MaintenanceMarginThreshold);
        view.MaintenanceRecoveryTargetOffset.Should().Be(defaults.MaintenanceRecoveryTargetOffset);
        view.ShortExposureRatioCap.Should().Be(defaults.ExposureRatioCap);
    }

    // ---- 空売り比率（ADR-0016 決定9） ----

    // T-340-08: 建玉が無ければ空売り比率は概念として成立しない（異常ではない）。
    [Fact]
    public void 建玉が無ければ空売り比率は概念として成立しない()
    {
        Create().Build().ShortExposureAvailability.Should().Be(MetricAvailability.NotApplicable);
    }

    // T-340-09: **分母は建玉総額＝時価である**（ADR-0016 決定9）。現在値が 1 件でも欠ければ算出しない。
    // 取得原価で代用すると「空売り比率」という名前の別物になり、上限 50% の判定を誤らせる。
    [Fact]
    public void 現在値が欠ける建玉があれば空売り比率を算出しない()
    {
        var view = Create(fills: [Fill(TradeSide.Sell, 10, 100m, "AAPL")]).Build();

        view.ShortExposureAvailability.Should().Be(MetricAvailability.NotSupplied);
        view.ShortExposureRatio.Should().BeNull();
        view.Positions.Single().MarketValueAvailability.Should().Be(MetricAvailability.NotSupplied);
        view.Positions.Single().MarketValueUsd.Should().BeNull();
    }

    // T-340-10: 現在値が揃えば時価で算出する（ショート $2,000 ÷ 総額 $5,000 ＝ 40%）。
    [Fact]
    public void 現在値が揃えば空売り比率を時価で算出する()
    {
        var view = Create(
            fills: [Fill(TradeSide.Sell, 10, 90m, "SHRT"), Fill(TradeSide.Buy, 10, 280m, "LONG")],
            prices: new Dictionary<(string, Market), decimal>
            {
                [("SHRT", Market.UnitedStates)] = 200m,
                [("LONG", Market.UnitedStates)] = 300m,
            }).Build();

        view.ShortExposureAvailability.Should().Be(MetricAvailability.Available);
        view.ShortExposureRatio.Should().Be(0.40m);
    }

    // ---- 保有ポジション（ADR-0016 決定15） ----

    // T-340-11: **建玉の方向（ロング / ショート）**は供給がある。符号付き在庫の向きをそのまま返す。
    [Fact]
    public void 保有ポジションに建玉の方向を返す()
    {
        var view = Create(fills:
            [Fill(TradeSide.Sell, 10, 90m, "SHRT"), Fill(TradeSide.Buy, 5, 280m, "LONG")]).Build();

        view.Positions.Should().HaveCount(2);
        view.Positions.Single(p => p.Symbol == "SHRT").Side.Should().Be(TradeSide.Sell);
        view.Positions.Single(p => p.Symbol == "LONG").Side.Should().Be(TradeSide.Buy);
    }

    // T-340-12: **借株料の累計は供給元がコードに存在しない。** 0 を返すと「費用が発生していない」と読める。
    [Fact]
    public void 借株料の累計は未供給として宣言し0を返さない()
    {
        var view = Create(fills: [Fill(TradeSide.Sell, 10, 90m, "SHRT")]).Build();

        view.BorrowFeeAvailability.Should().Be(MetricAvailability.NotSupplied);
        view.TotalAccruedBorrowFeeUsd.Should().BeNull();
        view.Positions.Single().BorrowFeeAvailability.Should().Be(MetricAvailability.NotSupplied);
        view.Positions.Single().AccruedBorrowFeeUsd.Should().BeNull();
    }

    // ---- 維持率割れによる自動縮小の発動履歴 ----

    // T-340-13: 発火元（維持率）が無い間、発動履歴は**そもそも記録され得ない**。
    // 空列＝「発動なし」と区別する（区別しないと「統制が働いていて発動が無かった」と読める）。
    [Fact]
    public void 発動履歴は維持率の供給が無い間は未供給として宣言する()
    {
        var view = Create(snapshot: null).Build();

        view.ReductionHistoryAvailability.Should().Be(MetricAvailability.NotSupplied);
        view.ReductionHistory.Should().BeEmpty();
    }
}
