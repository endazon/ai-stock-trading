using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Domain.Tests;

// FR-10, FR-19, FR-20, UC-06: リスク管理コアの決定的判定
// 受け入れ基準（計画書 02_requirements）:
// - リスク上限を超える判断を生成AIが出力した場合に、発注が拒否されログと通知が残る
// - kill switch 起動後、新規発注が一切行われない
// - 取引ガードに反する注文（禁止銘柄・無効化された商品種別・差金決済該当）が発注段階で拒否され、記録される
public class RiskEvaluatorTests
{
    private static OrderIntent Buy(
        string symbol = "AAPL",
        Market market = Market.UnitedStates,
        ProductType productType = ProductType.Cash,
        TradeMode mode = TradeMode.Paper,
        int quantity = 10,
        // #329: 既定の注文額 20,000 円は 1 注文上限（equity 100,000 × 25% ＝ 25,000 円）の内側。
        decimal price = 2_000m,
        PositionEffect positionEffect = PositionEffect.Open) =>
        new(symbol, market, TradeSide.Buy, productType, mode, quantity, price, positionEffect);

    // 手仕舞い（Close）注文。既定はロング決済（売り）。ショート決済の検証では side/productType を差し替える。
    private static OrderIntent Close(
        string symbol = "AAPL",
        Market market = Market.UnitedStates,
        ProductType productType = ProductType.Cash,
        TradeSide side = TradeSide.Sell,
        int quantity = 10,
        decimal price = 3000m) =>
        new(symbol, market, side, productType, TradeMode.Paper, quantity, price, PositionEffect.Close);

    // ショートエントリー（新規売り建て）。信用有効化後に発生する Side == Sell の Open。
    private static OrderIntent ShortEntry(
        string symbol = "AAPL",
        Market market = Market.UnitedStates,
        int quantity = 10,
        decimal price = 2_000m,
        TradeMode mode = TradeMode.Paper) =>
        new(symbol, market, TradeSide.Sell, ProductType.Margin, mode, quantity, price, PositionEffect.Open);

    // FR-10, #329, IADR-0130 決定2: capital は判定に用いる自己資金（equity・前営業日終値時点）。
    // 金額系の上限（1 注文 25% / 1 日 150%）はこの値から解決される。
    private static PortfolioSnapshot Snapshot(
        decimal capital = 100_000m,
        int openPositionCount = 0,
        decimal investedCapital = 0m,
        decimal dailyOrderedAmount = 0m,
        decimal dailyRealizedPnl = 0m,
        decimal unrealizedPnl = 0m,
        decimal drawdownRatio = 0m,
        int consecutiveLosses = 0,
        IReadOnlySet<(string Symbol, Market Market)>? symbolsTradedToday = null,
        bool killSwitchEngaged = false,
        bool tradingPaused = false) =>
        new()
        {
            Capital = capital,
            OpenPositionCount = openPositionCount,
            InvestedCapital = investedCapital,
            DailyOrderedAmount = dailyOrderedAmount,
            DailyRealizedPnl = dailyRealizedPnl,
            UnrealizedPnl = unrealizedPnl,
            DrawdownRatio = drawdownRatio,
            ConsecutiveLosses = consecutiveLosses,
            SymbolsTradedToday = symbolsTradedToday ?? new HashSet<(string, Market)>(),
            KillSwitchEngaged = killSwitchEngaged,
            TradingPaused = tradingPaused,
        };

    private static RiskManagementSettings DefaultSettings() => TradingDefaults.CreateSettings();

    // 信用（Margin）を有効化した設定。ショートエントリー（Issue #25）の検証に用いる。
    private static RiskManagementSettings MarginEnabledSettings() => DefaultSettings() with
    {
        Guard = TradingDefaults.CreateGuardSettings() with
        {
            EnabledProductTypes = new HashSet<ProductType> { ProductType.Cash, ProductType.Margin },
        },
    };

    [Fact]
    public void 制約のない買い注文は承認され承認数量が返る()
    {
        var result = RiskEvaluator.Evaluate(Buy(), DefaultSettings(), Snapshot());

        result.IsApproved.Should().BeTrue();
        result.ApprovedQuantity.Should().Be(10);
        result.Reasons.Should().BeEmpty();
    }

    [Fact]
    public void KillSwitch起動中はすべての新規注文を拒否する()
    {
        var result = RiskEvaluator.Evaluate(Buy(), DefaultSettings(), Snapshot(killSwitchEngaged: true));

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.KillSwitchActive);
    }

    [Fact]
    public void 一時停止中はすべての新規建てを拒否する()
    {
        // FR-10, ADR-0009: pause は kill switch と同位置で新規建て（エントリー）を止める。
        var result = RiskEvaluator.Evaluate(Buy(), DefaultSettings(), Snapshot(tradingPaused: true));

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.TradingPaused);
    }

    [Fact]
    public void 一時停止中はショートエントリーも拒否する()
    {
        // FR-10, ADR-0009: 信用有効化後の新規売り建て（Side == Sell の Open）も pause の対象（isEntry 判定）。
        var result = RiskEvaluator.Evaluate(
            ShortEntry(), MarginEnabledSettings(), Snapshot(tradingPaused: true));

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.TradingPaused);
    }

    [Fact]
    public void 一時停止中でも手仕舞いの売り注文は承認する()
    {
        // ADR-0009 共通不変条件: pause は新規建てのみ止め、手仕舞い（Close）・損切りは止めない。
        var result = RiskEvaluator.Evaluate(Close(), DefaultSettings(), Snapshot(tradingPaused: true));

        result.IsApproved.Should().BeTrue();
        result.Reasons.Should().NotContain(RejectionReason.TradingPaused);
    }

    [Fact]
    public void ペーパー段階で実弾モードの注文は拒否する()
    {
        // FR-20: Stage 0/1 はペーパーのみ許可
        var result = RiskEvaluator.Evaluate(Buy(mode: TradeMode.Live), DefaultSettings(), Snapshot());

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.StageProhibitsLiveTrading);
    }

    [Fact]
    public void 段階別資金上限を超える注文は拒否する()
    {
        // FR-20: 段階ごとの資金上限を強制（既定 CapitalCap ＝ TradingDefaults.InitialCapital ＝ 491,100 円）
        var intent = Buy(quantity: 100, price: 6_000m); // 600,000 円 > CapitalCap
        var result = RiskEvaluator.Evaluate(intent, DefaultSettings(), Snapshot());

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.StageCapitalCapExceeded);
    }

    [Fact]
    public void 保有投入額を含む累計が段階資金上限を超える新規注文は拒否する()
    {
        // Issue #27 / FR-20, ADR-0008: 単一注文額は上限内でも、投入中資金＋当該注文で上限超過なら拒否。
        // 投入中（CapitalCap − 10,000）＋ 20,000 = CapitalCap + 10,000 > CapitalCap。
        var snapshot = Snapshot(investedCapital: TradingDefaults.InitialCapital - 10_000m);
        var intent = Buy(quantity: 1, price: 20_000m);

        var result = RiskEvaluator.Evaluate(intent, DefaultSettings(), snapshot);

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.StageCapitalCapExceeded);
    }

    [Fact]
    public void 保有投入額を含む累計が段階資金上限ちょうどなら承認する()
    {
        // Issue #27: 境界値。投入中（CapitalCap − 20,000）＋ 20,000 == CapitalCap（判定は超過のみ拒否）。
        var snapshot = Snapshot(investedCapital: TradingDefaults.InitialCapital - 20_000m);
        var intent = Buy(quantity: 1, price: 20_000m);

        var result = RiskEvaluator.Evaluate(intent, DefaultSettings(), snapshot);

        result.IsApproved.Should().BeTrue();
        result.Reasons.Should().NotContain(RejectionReason.StageCapitalCapExceeded);
    }

    [Fact]
    public void 段階資金上限は手仕舞いの投入額には適用しない()
    {
        // Issue #27: 手仕舞い（Close）は投入額判定の対象外（フェイルセーフ）。
        var snapshot = Snapshot(investedCapital: 100_000m, openPositionCount: 2);

        var result = RiskEvaluator.Evaluate(Close(), DefaultSettings(), snapshot);

        result.IsApproved.Should().BeTrue();
    }

    [Fact]
    public void 無効化された商品種別の注文は拒否する()
    {
        // FR-19, ADR-0007: 既定は現物のみ有効（信用は無効）
        var result = RiskEvaluator.Evaluate(Buy(productType: ProductType.Margin), DefaultSettings(), Snapshot());

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.ProductTypeDisabled);
    }

    [Fact]
    public void 無効化された市場の注文は拒否する()
    {
        var settings = DefaultSettings() with
        {
            Guard = TradingDefaults.CreateGuardSettings() with
            {
                EnabledMarkets = new HashSet<Market> { Market.UnitedStates },
            },
        };
        var intent = Buy(symbol: "7203", market: Market.Japan, price: 2500m);

        var result = RiskEvaluator.Evaluate(intent, settings, Snapshot());

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.MarketDisabled);
    }

    [Theory]
    [InlineData("6457")] // グローリー
    [InlineData("6902")] // デンソー
    [InlineData("6502")] // 東芝（旧。再上場時に適用）
    public void 取引禁止銘柄の注文は拒否する(string bannedSymbol)
    {
        // FR-19: 利用者登録（2026-07-07）の禁止銘柄リストを強制
        var intent = Buy(symbol: bannedSymbol, market: Market.Japan, quantity: 10, price: 2000m);

        var result = RiskEvaluator.Evaluate(intent, DefaultSettings(), Snapshot());

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.BannedSymbol);
    }

    [Fact]
    public void 同一銘柄の同日再エントリーは拒否する()
    {
        // FR-19: 差金決済防止（現物の同日回転禁止）
        var snapshot = Snapshot(symbolsTradedToday: new HashSet<(string, Market)> { ("AAPL", Market.UnitedStates) });

        var result = RiskEvaluator.Evaluate(Buy(symbol: "AAPL"), DefaultSettings(), snapshot);

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.SameDayReentry);
    }

    [Fact]
    public void 同日取引済みでも市場が異なれば再エントリーを拒否しない()
    {
        // Issue #26 / FR-19: 差金決済防止は（銘柄, 市場）で照合する。禁止銘柄判定と対称。
        // 日本株「6902」を当日取引済みでも、同一コードの米国株は同日再エントリー対象外。
        var snapshot = Snapshot(
            symbolsTradedToday: new HashSet<(string, Market)> { ("6902", Market.Japan) });

        var result = RiskEvaluator.Evaluate(
            Buy(symbol: "6902", market: Market.UnitedStates), DefaultSettings(), snapshot);

        result.Reasons.Should().NotContain(RejectionReason.SameDayReentry);
    }

    // FR-10, #329, 05_trading-assumptions §5: 1 注文あたりの発注金額上限は **equity の 25%**。
    // equity 100,000 円 → 上限 25,000 円。境界値テーブル（直下 / 一致 / 直上）。
    [Theory]
    [InlineData(24_999, true)]  // 上限の直下 → 承認
    [InlineData(25_000, true)]  // 上限ちょうど → 承認
    [InlineData(25_001, false)] // 上限超過 → 拒否
    public void 一注文あたり金額上限を境界値で強制する(decimal notional, bool expectedApproved)
    {
        var intent = Buy(quantity: 1, price: notional);

        var result = RiskEvaluator.Evaluate(intent, DefaultSettings(), Snapshot());

        result.IsApproved.Should().Be(expectedApproved);
        if (!expectedApproved)
        {
            result.Reasons.Should().Contain(RejectionReason.PerOrderAmountExceeded);
        }
    }

    [Fact]
    public void 一日あたり発注金額上限を累計で強制する()
    {
        // FR-10, #329: 1 日あたり発注金額上限は equity の 150%/日（当日累計＋今回注文で判定）。
        // equity 100,000 円 → 上限 150,000 円。140,000 + 20,000 > 150,000。
        var snapshot = Snapshot(dailyOrderedAmount: 140_000m);
        var intent = Buy(quantity: 1, price: 20_000m);

        var result = RiskEvaluator.Evaluate(intent, DefaultSettings(), snapshot);

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.DailyOrderAmountExceeded);
    }

    [Fact]
    public void 保有建玉数上限に達した状態の新規買いは拒否する()
    {
        // FR-10, ADR-0016 決定9: 保有**建玉**数の上限（既定 3）。「保有銘柄数」では数えない。
        var snapshot = Snapshot(openPositionCount: 3);

        var result = RiskEvaluator.Evaluate(Buy(), DefaultSettings(), snapshot);

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.MaxPositionsExceeded);
    }

    [Fact]
    public void 日次損失上限に達したら当日の新規注文を拒否する()
    {
        // FR-10: 日次損失上限（既定: 資金の2%到達で当日全停止）。実現損益のみで到達するケース。
        var snapshot = Snapshot(dailyRealizedPnl: -2_000m); // 資金 100,000 の 2%

        var result = RiskEvaluator.Evaluate(Buy(), DefaultSettings(), snapshot);

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.DailyLossLimitReached);
    }

    [Fact]
    public void 実現ゼロでも含み損の合算で日次損失上限に達したら拒否する()
    {
        // Issue #31 / FR-10, IADR-0008: 日次損失は実現損益＋含み損益で判定する。
        // 実現 0・含み損 -2,000 円（資金 100,000 の 2%）でデイリーストップに到達。
        var snapshot = Snapshot(dailyRealizedPnl: 0m, unrealizedPnl: -2_000m);

        var result = RiskEvaluator.Evaluate(Buy(), DefaultSettings(), snapshot);

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.DailyLossLimitReached);
    }

    [Fact]
    public void 実現と含み損の合算が上限未満なら日次損失上限で拒否しない()
    {
        // Issue #31: 実現 -1,000 ＋ 含み損 -500 = -1,500 円 > -2,000 円（上限未満）→ 到達しない。
        var snapshot = Snapshot(dailyRealizedPnl: -1_000m, unrealizedPnl: -500m);

        var result = RiskEvaluator.Evaluate(Buy(), DefaultSettings(), snapshot);

        result.Reasons.Should().NotContain(RejectionReason.DailyLossLimitReached);
    }

    [Fact]
    public void 含み益は実現損失を相殺して日次損失上限の到達を防ぐ()
    {
        // Issue #31: 合算判定のため、含み益 +1,000 は実現損 -2,500 を相殺し、合算 -1,500 円は上限未満。
        var snapshot = Snapshot(dailyRealizedPnl: -2_500m, unrealizedPnl: 1_000m);

        var result = RiskEvaluator.Evaluate(Buy(), DefaultSettings(), snapshot);

        result.Reasons.Should().NotContain(RejectionReason.DailyLossLimitReached);
    }

    [Fact]
    public void 含み損で日次損失上限に達していても手仕舞いの売り注文は承認する()
    {
        // Issue #31: フェイルセーフ。含み損合算で上限到達中でも手仕舞い（Close）はブロックしない。
        var snapshot = Snapshot(unrealizedPnl: -3_000m, openPositionCount: 2);

        var result = RiskEvaluator.Evaluate(Close(), DefaultSettings(), snapshot);

        result.IsApproved.Should().BeTrue();
        result.Reasons.Should().NotContain(RejectionReason.DailyLossLimitReached);
    }

    [Fact]
    public void 最大ドローダウン上限に達したら注文を拒否する()
    {
        // FR-10: 最大DD上限（既定: 10%で停止）
        var snapshot = Snapshot(drawdownRatio: 0.10m);

        var result = RiskEvaluator.Evaluate(Buy(), DefaultSettings(), snapshot);

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.MaxDrawdownReached);
    }

    [Fact]
    public void 複数の違反はすべて列挙される()
    {
        // FR-11: 監査のため違反理由を網羅的に記録する
        var snapshot = Snapshot(killSwitchEngaged: true, drawdownRatio: 0.10m);
        var intent = Buy(symbol: "6457", market: Market.Japan, productType: ProductType.Margin);

        var result = RiskEvaluator.Evaluate(intent, DefaultSettings(), snapshot);

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(
        [
            RejectionReason.KillSwitchActive,
            RejectionReason.MaxDrawdownReached,
            RejectionReason.BannedSymbol,
            RejectionReason.ProductTypeDisabled,
        ]);
    }

    [Fact]
    public void 保有ポジションの売り注文にはエントリー専用の制約を適用しない()
    {
        // 手仕舞い（Close）は保有数上限・同日再エントリー・段階資金上限の対象外
        var snapshot = Snapshot(
            openPositionCount: 3,
            symbolsTradedToday: new HashSet<(string, Market)> { ("AAPL", Market.UnitedStates) });

        var result = RiskEvaluator.Evaluate(Close(), DefaultSettings(), snapshot);

        result.IsApproved.Should().BeTrue();
    }

    [Fact]
    public void KillSwitch_日次損失上限_最大DD到達中でも損切りの売り注文は承認する()
    {
        // NFR フェイルセーフ（新規発注停止・保有ポジションの損切り監視は最後まで維持）／ADR-0003（損切りは機械的に執行）。
        // kill switch・日次損失上限・最大DD がすべて成立していても、手仕舞い（売り）は止めない。
        var snapshot = Snapshot(
            dailyRealizedPnl: -2_000m, // 資金 100,000 の 2%（日次損失上限）
            drawdownRatio: 0.10m,      // 最大DD 上限
            killSwitchEngaged: true);

        var result = RiskEvaluator.Evaluate(Close(), DefaultSettings(), snapshot);

        result.IsApproved.Should().BeTrue();
        result.Reasons.Should().NotContain(RejectionReason.KillSwitchActive);
        result.Reasons.Should().NotContain(RejectionReason.DailyLossLimitReached);
        result.Reasons.Should().NotContain(RejectionReason.MaxDrawdownReached);
    }

    [Fact]
    public void 金額上限を超える状況でも損切りの売り注文は承認する()
    {
        // フェイルセーフ／ADR-0003: 1注文金額上限・日次発注金額上限は新規発注（エントリー）の
        // 資金投入を制限するもの。値上がりで時価が上限超過したポジションの手仕舞いや、当日発注累計が
        // 上限近い状況での損切り売りをブロックしてはならない。
        var snapshot = Snapshot(dailyOrderedAmount: 99_000m); // 日次上限 100,000 円の直前
        // 1株 50,000 円（MaxOrderAmount 35,000 円超）× 1株。売り時価も日次累計上限を超える。
        var sell = Close(quantity: 1, price: 50_000m);

        var result = RiskEvaluator.Evaluate(sell, DefaultSettings(), snapshot);

        result.IsApproved.Should().BeTrue();
        result.Reasons.Should().NotContain(RejectionReason.PerOrderAmountExceeded);
        result.Reasons.Should().NotContain(RejectionReason.DailyOrderAmountExceeded);
    }

    [Fact]
    public void 禁止銘柄コードでも市場が異なれば拒否しない()
    {
        // FR-19: 禁止銘柄は銘柄コードと市場の両方で照合する。
        // 既定の禁止銘柄「6457」は Market.Japan 登録。同一コードでも米国株なら禁止対象外。
        var intent = Buy(symbol: "6457", market: Market.UnitedStates, quantity: 1, price: 2000m);

        var result = RiskEvaluator.Evaluate(intent, DefaultSettings(), Snapshot());

        result.Reasons.Should().NotContain(RejectionReason.BannedSymbol);
    }

    [Fact]
    public void KillSwitch起動中はショートエントリーも拒否する()
    {
        // Issue #25 / FR-10, FR-19: 信用有効化後の新規売り建て（Side == Sell の Open）も
        // kill switch の対象。Side == Buy でエントリーを近似していた欠陥の回帰防止。
        var result = RiskEvaluator.Evaluate(
            ShortEntry(), MarginEnabledSettings(), Snapshot(killSwitchEngaged: true));

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.KillSwitchActive);
    }

    [Fact]
    public void ショートエントリーにも段階資金上限と金額上限が適用される()
    {
        // Issue #25 / FR-10, FR-20: エントリー専用の金額上限がショートエントリーにも効く。
        var intent = ShortEntry(quantity: 100, price: 6_000m); // 600,000 円 > CapitalCap・1 注文上限

        var result = RiskEvaluator.Evaluate(intent, MarginEnabledSettings(), Snapshot());

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.StageCapitalCapExceeded);
        result.Reasons.Should().Contain(RejectionReason.PerOrderAmountExceeded);
    }

    // Issue #28: テスト用の相場操縦検出器。固定の判定を返すスタブ。
    private sealed class StubPatternDetector(bool result) : IManipulativeOrderPatternDetector
    {
        public bool IsSuspectedManipulation(OrderIntent intent, PortfolioSnapshot snapshot) => result;
    }

    [Fact]
    public void 相場操縦パターン検出器が該当と判定した注文は拒否する()
    {
        // Issue #28 / FR-19: ガード有効かつ検出器が true を返す場合に拒否する。
        var result = RiskEvaluator.Evaluate(
            Buy(), DefaultSettings(), Snapshot(), new StubPatternDetector(true));

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.ManipulativeOrderPattern);
    }

    [Fact]
    public void 相場操縦検出器を注入しなければ現行どおり承認する()
    {
        // Issue #28: 検出器未注入では判定をスキップし、初期スライスの挙動を保つ（回帰防止）。
        var result = RiskEvaluator.Evaluate(Buy(), DefaultSettings(), Snapshot());

        result.IsApproved.Should().BeTrue();
        result.Reasons.Should().NotContain(RejectionReason.ManipulativeOrderPattern);
    }

    [Fact]
    public void ガード無効時は相場操縦検出器が該当と判定してもスキップする()
    {
        // Issue #28: ProhibitManipulativeOrderPatterns = false のとき判定を呼ばない。
        var settings = DefaultSettings() with
        {
            Guard = TradingDefaults.CreateGuardSettings() with { ProhibitManipulativeOrderPatterns = false },
        };

        var result = RiskEvaluator.Evaluate(
            Buy(), settings, Snapshot(), new StubPatternDetector(true));

        result.Reasons.Should().NotContain(RejectionReason.ManipulativeOrderPattern);
    }

    [Fact]
    public void 相場操縦パターンは手仕舞いの注文にも適用する()
    {
        // Issue #28 / IADR-0006: 相場操縦判定はエントリー/手仕舞いを問わず適用する（建玉効果非依存）。
        // 手仕舞い（Close）でも検出器が該当と判定すれば拒否されることを固定する（フェイルセーフの例外）。
        var result = RiskEvaluator.Evaluate(
            Close(), DefaultSettings(), Snapshot(), new StubPatternDetector(true));

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.ManipulativeOrderPattern);
    }

    // --- FR-10, FR-17, #257, IADR-0107: 金額上限は基準通貨（円）で判定する ---

    [Fact]
    public void 外貨建て注文の金額上限は換算後の金額で判定する()
    {
        // 100 USD × 5 株 = 500 USD。レート 150 なら 75,000 円で 1 注文金額上限（35,000 円）を超える。
        // 換算しなければ 500 が「500 円」として通り、統制が桁で緩む（本 issue の症状）。
        var intent = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper,
            5, 100m, PositionEffect.Open, StopLossPrice: null, FxRateToBase: 150m);

        var result = RiskEvaluator.Evaluate(intent, DefaultSettings(), Snapshot());

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.PerOrderAmountExceeded);
    }

    [Fact]
    public void 外貨建て注文の日次発注累計と段階資金上限も換算後の金額で判定する()
    {
        // 15 USD × 10 株 = 150 USD ＝ 22,500 円（レート 150）。1 注文上限（equity 100,000 × 25% ＝ 25,000）は
        // 超えないが、当日発注累計 140,000 円との合計 162,500 円は日次上限（150,000）を、
        // 投入中資金（CapitalCap − 10,000）との合計は段階資金上限を超える。
        var intent = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper,
            10, 15m, PositionEffect.Open, StopLossPrice: null, FxRateToBase: 150m);

        var result = RiskEvaluator.Evaluate(
            intent,
            DefaultSettings(),
            Snapshot(
                dailyOrderedAmount: 140_000m,
                investedCapital: TradingDefaults.InitialCapital - 10_000m));

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.DailyOrderAmountExceeded);
        result.Reasons.Should().Contain(RejectionReason.StageCapitalCapExceeded);
    }

    [Fact]
    public void 換算レート既定1の注文は従来どおり判定される()
    {
        // 基準通貨（日本株）およびレート未記録の既存データ＝現行挙動と等価であることを固定する。
        var intent = new OrderIntent(
            "7203", Market.Japan, TradeSide.Buy, ProductType.Cash, TradeMode.Paper, 10, 2_000m);

        var result = RiskEvaluator.Evaluate(intent, DefaultSettings(), Snapshot());

        result.IsApproved.Should().BeTrue();
        intent.FxRateToBase.Should().Be(1m);
    }

    [Fact]
    public void ショート決済の買い戻しはエントリー制約の対象外()
    {
        // Issue #25: 手仕舞い（ショート決済 = Buy × Close）はフェイルセーフにより
        // kill switch・保有数上限・同日再エントリーの対象外。
        var snapshot = Snapshot(
            openPositionCount: 3,
            killSwitchEngaged: true,
            symbolsTradedToday: new HashSet<(string, Market)> { ("AAPL", Market.UnitedStates) });
        var shortCover = Close(side: TradeSide.Buy, productType: ProductType.Margin);

        var result = RiskEvaluator.Evaluate(shortCover, MarginEnabledSettings(), snapshot);

        result.IsApproved.Should().BeTrue();
    }
}
