using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
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
        decimal price = 3000m) =>
        new(symbol, market, TradeSide.Buy, productType, mode, quantity, price);

    private static PortfolioSnapshot Snapshot(
        decimal capital = 100_000m,
        int openPositionCount = 0,
        decimal dailyOrderedAmount = 0m,
        decimal dailyRealizedPnl = 0m,
        decimal drawdownRatio = 0m,
        int consecutiveLosses = 0,
        IReadOnlySet<string>? symbolsTradedToday = null,
        bool killSwitchEngaged = false) =>
        new()
        {
            Capital = capital,
            OpenPositionCount = openPositionCount,
            DailyOrderedAmount = dailyOrderedAmount,
            DailyRealizedPnl = dailyRealizedPnl,
            DrawdownRatio = drawdownRatio,
            ConsecutiveLosses = consecutiveLosses,
            SymbolsTradedToday = symbolsTradedToday ?? new HashSet<string>(),
            KillSwitchEngaged = killSwitchEngaged,
        };

    private static RiskManagementSettings DefaultSettings() => TradingDefaults.CreateSettings();

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
        // FR-20: 段階ごとの資金上限を強制
        var intent = Buy(quantity: 100, price: 3000m); // 300,000 円 > CapitalCap 100,000 円
        var result = RiskEvaluator.Evaluate(intent, DefaultSettings(), Snapshot());

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.StageCapitalCapExceeded);
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
        var snapshot = Snapshot(symbolsTradedToday: new HashSet<string> { "AAPL" });

        var result = RiskEvaluator.Evaluate(Buy(symbol: "AAPL"), DefaultSettings(), snapshot);

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.SameDayReentry);
    }

    [Theory]
    [InlineData(35_000, true)]  // 上限ちょうど → 承認
    [InlineData(35_001, false)] // 上限超過 → 拒否
    public void 一注文あたり金額上限を境界値で強制する(decimal notional, bool expectedApproved)
    {
        // FR-10: 1注文あたり金額上限
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
        // FR-10: 1日あたり発注金額上限（当日累計＋今回注文で判定）
        var snapshot = Snapshot(dailyOrderedAmount: 80_000m);
        var intent = Buy(quantity: 1, price: 30_000m); // 80,000 + 30,000 > 100,000

        var result = RiskEvaluator.Evaluate(intent, DefaultSettings(), snapshot);

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.DailyOrderAmountExceeded);
    }

    [Fact]
    public void 保有銘柄数上限に達した状態の新規買いは拒否する()
    {
        var snapshot = Snapshot(openPositionCount: 3); // 既定上限 3

        var result = RiskEvaluator.Evaluate(Buy(), DefaultSettings(), snapshot);

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.MaxPositionsExceeded);
    }

    [Fact]
    public void 日次損失上限に達したら当日の新規注文を拒否する()
    {
        // FR-10: 日次損失上限（既定: 資金の2%到達で当日全停止）
        var snapshot = Snapshot(dailyRealizedPnl: -2_000m); // 資金 100,000 の 2%

        var result = RiskEvaluator.Evaluate(Buy(), DefaultSettings(), snapshot);

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.DailyLossLimitReached);
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
        // 売り（手仕舞い）は保有数上限・同日再エントリー・段階資金上限の対象外
        var snapshot = Snapshot(openPositionCount: 3, symbolsTradedToday: new HashSet<string> { "AAPL" });
        var sell = new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, TradeMode.Paper, 10, 3000m);

        var result = RiskEvaluator.Evaluate(sell, DefaultSettings(), snapshot);

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
        var sell = new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, TradeMode.Paper, 10, 3000m);

        var result = RiskEvaluator.Evaluate(sell, DefaultSettings(), snapshot);

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
        var sell = new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, TradeMode.Paper, 1, 50_000m);

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
}
