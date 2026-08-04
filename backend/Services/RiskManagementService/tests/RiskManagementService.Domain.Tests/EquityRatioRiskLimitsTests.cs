using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Domain.Tests;

// FR-10, UC-06, ADR-0009, ADR-0016, ADR-0018, #329, IADR-0130:
// 金額系の統制上限 3 値（1 注文 equity の 25% / 1 日 equity の 150% / 保有建玉数 3）の網羅。
//
// テスト戦略（docs/tests/README.md §2）の 3 点セットで構成する。
//   1. 境界値テーブル — 閾値の直下 / 一致 / 直上
//   2. プロパティベース — 入力によらず成り立つ不変条件（比例調整・通貨非依存・厳しい方が効く）
//   3. 否定形 — 統制を迂回できないこと（決済で枠を食えない・上限を注文側から上書きできない）
//
// 閾値はマジックナンバーで書かず統制設定から引く。設定値そのものが計画どおりであることは
// AiStockTrading.PlanConformance.Tests が計画書と突き合わせて別途保証する（責務を分ける）。
public class EquityRatioRiskLimitsTests
{
    private const decimal Equity = 100_000m;

    private static RiskLimitSettings Limits => TradingDefaults.CreateRiskLimits();

    private static RiskManagementSettings Settings(decimal? stageCapitalCap = null)
    {
        var settings = TradingDefaults.CreateSettings();
        return stageCapitalCap is null
            ? settings
            : settings with { Stage = settings.Stage with { CapitalCap = stageCapitalCap.Value } };
    }

    private static PortfolioSnapshot Snapshot(
        decimal equity = Equity,
        decimal dailyOrderedAmount = 0m,
        int openPositionCount = 0,
        decimal investedCapital = 0m) =>
        new()
        {
            Capital = equity,
            DailyOrderedAmount = dailyOrderedAmount,
            OpenPositionCount = openPositionCount,
            InvestedCapital = investedCapital,
        };

    private static OrderIntent Entry(decimal notional, decimal fxRateToBase = 1m) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper,
            1, notional / fxRateToBase, PositionEffect.Open, StopLossPrice: null, fxRateToBase);

    private static OrderIntent Exit(decimal notional) =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, TradeMode.Paper,
            1, notional, PositionEffect.Close);

    // ------------------------------------------------------------------
    // 1. 境界値テーブル
    // ------------------------------------------------------------------

    // FR-10, 05_trading-assumptions §5: 1 注文あたりの発注金額上限は equity の 25%
    // （自己資金 $3,000 で $750）。equity 100,000 → 25,000。判定は「超過のみ拒否」。
    [Theory]
    [InlineData(-1, true)]  // 直下
    [InlineData(0, true)]   // 一致
    [InlineData(+1, false)] // 直上
    public void 一注文金額上限は境界で切り替わる(int delta, bool expectedApproved)
    {
        var cap = Limits.MaxOrderAmountFor(Equity);

        var result = RiskEvaluator.Evaluate(Entry(cap + delta), Settings(), Snapshot());

        result.Reasons.Contains(RejectionReason.PerOrderAmountExceeded).Should().Be(!expectedApproved);
    }

    // FR-10, §5: 1 日あたりの発注金額上限は equity の 150%/日（$3,000 で $4,500）。
    // 判定は「当日累計 ＋ 当該注文」で行う（1 件ずつ上限内でも累計で超えたら拒否）。
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(+1, false)]
    public void 一日あたり発注金額上限は累計の境界で切り替わる(int delta, bool expectedApproved)
    {
        var dailyCap = Limits.MaxDailyOrderAmountFor(Equity);
        var orderNotional = Limits.MaxOrderAmountFor(Equity);
        // 当日累計を「上限 − 今回注文額（± 1 円）」に置き、累計が境界へちょうど載るようにする。
        var alreadyOrdered = dailyCap - orderNotional + delta;

        var result = RiskEvaluator.Evaluate(
            Entry(orderNotional), Settings(), Snapshot(dailyOrderedAmount: alreadyOrdered));

        result.Reasons.Contains(RejectionReason.DailyOrderAmountExceeded).Should().Be(!expectedApproved);
    }

    // FR-10, ADR-0016 決定9: 保有**建玉**数の上限は 3。上限に達した時点で新規建てを止める
    // （「上限を超えたら」ではなく「上限以上なら」＝ 3 件保有中の 4 件目を作らせない）。
    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    public void 保有建玉数上限は境界で切り替わる(int openPositions, bool expectedApproved)
    {
        var result = RiskEvaluator.Evaluate(
            Entry(1_000m), Settings(), Snapshot(openPositionCount: openPositions));

        result.Reasons.Contains(RejectionReason.MaxPositionsExceeded).Should().Be(!expectedApproved);
    }

    // ------------------------------------------------------------------
    // 2. プロパティベース（入力によらず成り立つ不変条件）
    // ------------------------------------------------------------------

    // FR-10, 受け入れ基準「自己資金を増減しても金額系の統制上限が固定額のまま据え置かれない」。
    // 計画 §5「割合で持てば、資金の増額に応じて各上限値が比例的に調整される」。
    [Theory]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(1_700)]
    public void equityをk倍すると金額系の上限もk倍になる(decimal k)
    {
        var limits = Limits;

        limits.MaxOrderAmountFor(Equity * k).Should().Be(limits.MaxOrderAmountFor(Equity) * k);
        limits.MaxDailyOrderAmountFor(Equity * k).Should().Be(limits.MaxDailyOrderAmountFor(Equity) * k);
    }

    // FR-10, IADR-0130 決定3: **equity と注文金額を同一通貨で評価する限り、比率による判定は通貨に依存しない。**
    // これが「equity の権威値は USD・パイプラインは基準通貨（円）」という分離を成立させている根拠であり、
    // 将来 判定通貨を USD へ移しても値の書き換えが要らないことの担保でもある。
    [Theory]
    [InlineData(1)]
    [InlineData(150)]
    [InlineData(163.7)]
    public void equityと注文金額を同一レートで換算しても判定は変わらない(decimal rate)
    {
        var settings = Settings(stageCapitalCap: decimal.MaxValue);
        var cap = Limits.MaxOrderAmountFor(Equity);

        foreach (var notionalInEquityCurrency in new[] { cap - 1m, cap, cap + 1m })
        {
            var inEquityCurrency = RiskEvaluator.Evaluate(
                Entry(notionalInEquityCurrency), settings, Snapshot());
            var converted = RiskEvaluator.Evaluate(
                Entry(notionalInEquityCurrency * rate), settings, Snapshot(equity: Equity * rate));

            converted.IsApproved.Should().Be(
                inEquityCurrency.IsApproved,
                "equity と金額を同じレートで換算しても比率判定は不変であるべきである（換算後 {0}）",
                notionalInEquityCurrency * rate);
        }
    }

    // FR-10「同一の注文に複数の上限が掛かる場合は常に厳しい方が効く」。
    // RiskEvaluator は違反をすべて列挙する（＝ AND 条件）ため、承認されるのは
    // **掛かっているすべての上限を満たすとき**に限る。段階資金上限と 1 注文上限のどちらが小さくても成り立つ。
    [Theory]
    [InlineData(1_000)]   // 段階資金上限が 1 注文上限より厳しい
    [InlineData(25_000)]  // 一致
    [InlineData(999_999)] // 1 注文上限の方が厳しい
    public void 複数の上限が掛かるとき常に厳しい方が効く(decimal stageCapitalCap)
    {
        var perOrderCap = Limits.MaxOrderAmountFor(Equity);
        var effective = Math.Min(perOrderCap, stageCapitalCap);
        var settings = Settings(stageCapitalCap);

        RiskEvaluator.Evaluate(Entry(effective), settings, Snapshot()).IsApproved
            .Should().BeTrue("厳しい方の上限ちょうどは承認される");
        RiskEvaluator.Evaluate(Entry(effective + 1m), settings, Snapshot()).IsApproved
            .Should().BeFalse("厳しい方の上限を 1 円でも超えたら拒否される");
    }

    // ------------------------------------------------------------------
    // 3. 否定形（統制を迂回できないこと）
    // ------------------------------------------------------------------

    // FR-10, ADR-0009, #302, IADR-0130 決定4: 決済（Close）は日次発注枠の判定対象外である。
    // 日次枠が満杯でも手仕舞い・損切りは止まらない（計画 §5「手仕舞い〔決済〕注文は算入しない」）。
    [Fact]
    public void 日次枠が満杯でも決済注文は拒否されない()
    {
        var dailyCap = Limits.MaxDailyOrderAmountFor(Equity);
        var snapshot = Snapshot(dailyOrderedAmount: dailyCap, openPositionCount: 3);

        var result = RiskEvaluator.Evaluate(Exit(dailyCap * 10m), Settings(), snapshot);

        result.IsApproved.Should().BeTrue();
        result.Reasons.Should().BeEmpty();
    }

    // FR-10, ADR-0003, ADR-0009: 1 注文金額上限を大幅に超える手仕舞い（値上がりした建玉の全量決済）も止めない。
    [Fact]
    public void 一注文金額上限を超える決済注文も拒否されない()
    {
        var result = RiskEvaluator.Evaluate(
            Exit(Limits.MaxOrderAmountFor(Equity) * 100m), Settings(), Snapshot());

        result.IsApproved.Should().BeTrue();
    }

    // FR-10「生成AIはこれらを上書きできない」: 上限は**統制設定**から解決される。注文意図側の値
    //（数量・価格・同伴レート）をどう作っても、equity 比から導いた上限そのものは動かない。
    // 迂回経路として最も現実的なのは「外貨建てにして名目額を小さく見せる」であるため、それを固定する。
    [Fact]
    public void 注文側の換算レートを操作しても上限は緩まない()
    {
        var cap = Limits.MaxOrderAmountFor(Equity);
        // ローカル通貨では 1/150 の数字に見えるが、基準通貨換算では上限を 1 円超える。
        var evasive = Entry(cap + 1m, fxRateToBase: 150m);

        evasive.Notional.Should().BeLessThan(cap, "ローカル通貨の名目額は上限より小さく見える");
        RiskEvaluator.Evaluate(evasive, Settings(), Snapshot()).Reasons
            .Should().Contain(RejectionReason.PerOrderAmountExceeded);
    }

    // FR-10, #329 第 3 段階（否定形）: **1 注文上限の直下に刻んだ分割発注で枠を積み上げられない**。
    // 1 注文金額上限（equity の 25%）だけを見ると「上限内の注文は何度でも通る」ように見えるが、
    // 日次枠（equity の 150%/日）が**累計**で効くため、上限いっぱいの注文は 1 日 6 件で尽きる。
    // 分割の粒度を細かくしても（最後に 1 円の注文を足しても）枠は緩まない。
    [Fact]
    public void 分割発注しても日次発注枠は累計で効く()
    {
        // 日次枠だけを見るため、段階資金上限は外す（別の上限で先に止まると本テストの検証にならない）。
        // 保有建玉数上限（3）と段階資金上限も同じ分割経路に並行して効く（別テストで固定済み）。
        var settings = Settings(stageCapitalCap: decimal.MaxValue);
        var perOrderCap = Limits.MaxOrderAmountFor(Equity);
        var dailyCap = Limits.MaxDailyOrderAmountFor(Equity);
        var slices = (int)(dailyCap / perOrderCap);

        var ordered = 0m;
        for (var i = 0; i < slices; i++)
        {
            var result = RiskEvaluator.Evaluate(
                Entry(perOrderCap), settings, Snapshot(dailyOrderedAmount: ordered));

            result.IsApproved.Should().BeTrue("{0} 件目は 1 注文上限以内であり枠も残っている", i + 1);
            ordered += perOrderCap; // 約定するとカウンタ（PortfolioProjection）が新規建てを累計する
        }

        ordered.Should().Be(dailyCap, "上限いっぱいの分割 {0} 件で日次枠を使い切る", slices);
        RiskEvaluator.Evaluate(Entry(perOrderCap), settings, Snapshot(dailyOrderedAmount: ordered))
            .Reasons.Should().Contain(RejectionReason.DailyOrderAmountExceeded);
        RiskEvaluator.Evaluate(Entry(1m), settings, Snapshot(dailyOrderedAmount: ordered))
            .Reasons.Should().Contain(
                RejectionReason.DailyOrderAmountExceeded, "刻みを細かくしても累計は同じ枠を消費する");
    }

    // FR-10: equity が 0 以下（口座が枯渇）なら、金額系の上限は 0 以下に解決され新規建ては通らない。
    // 「基準が取れないときに上限が無限になる」という最悪の縮退が起きないことを固定する。
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void equityが枯渇したら新規建ては通らない(decimal equity)
    {
        var result = RiskEvaluator.Evaluate(Entry(1m), Settings(), Snapshot(equity));

        result.Reasons.Should().Contain(RejectionReason.PerOrderAmountExceeded);
        result.Reasons.Should().Contain(RejectionReason.DailyOrderAmountExceeded);
    }
}
