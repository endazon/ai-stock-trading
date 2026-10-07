using System.Globalization;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Kernel.Tests.Trading;

// FR-04, ADR-0003, ADR-0051, #1175, IADR-0470（2026-10-07 追記・オーナー裁定 2026-10-07）: 方針の利確条件と保有を比べた結果を 3 値で返す
// （比べられない／未到達／到達）。判定は丸めない比較（+2.9986% は +3% に未到達）。到達の判定（Reached）は Judge と同じ。T-10-2330。
public class PolicyTakeProfitJudgementTests
{
    private static PolicyTakeProfitCondition Pct(decimal threshold) =>
        new(null, TakeProfitThresholdKind.GainPercent, threshold, null, null);

    private static PolicyTakeProfitCondition Price(decimal threshold, Currency currency = Currency.Usd) =>
        new(null, TakeProfitThresholdKind.Price, threshold, currency, null);

    private static decimal D(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);

    // T-10-2330: PoC の観測（MSFT 511.912→527.15＝+2.977%・NVDA 230.77→237.69＝+2.9986%）は +3% に未到達。ちょうど +3% は到達。
    // ショートは値下がりが含み益。含み損（率 0 以下）は未到達（比べられないではない）。
    [Theory]
    [InlineData(true, "511.912", "527.15", "3", TakeProfitJudgement.NotReached)]
    [InlineData(true, "230.77", "237.69", "3", TakeProfitJudgement.NotReached)]
    [InlineData(true, "100", "103", "3", TakeProfitJudgement.Reached)]
    [InlineData(true, "100", "102.9999", "3", TakeProfitJudgement.NotReached)]
    [InlineData(true, "100", "100", "3", TakeProfitJudgement.NotReached)]
    [InlineData(true, "100", "96", "3", TakeProfitJudgement.NotReached)]
    [InlineData(false, "100", "97", "3", TakeProfitJudgement.Reached)]
    [InlineData(false, "100", "97.0001", "3", TakeProfitJudgement.NotReached)]
    [InlineData(false, "100", "104", "5", TakeProfitJudgement.NotReached)]
    public void 率の条件は丸めずに比べて到達か未到達を返す(bool isLong, string entry, string mark, string threshold, TakeProfitJudgement expected)
    {
        IReadOnlyList<PolicyTakeProfitCondition> conditions = [Pct(D(threshold))];

        PolicyTakeProfitConditions.Judge(conditions, isLong, D(entry), D(mark), Currency.Usd).Should().Be(expected);
        // 到達の判定は Judge と同じ（Reached のときだけ条件を返す）。
        PolicyTakeProfitConditions.Reached(conditions, isLong, D(entry), D(mark), Currency.Usd)
            .Should().HaveCount(expected == TakeProfitJudgement.Reached ? 1 : 0);
    }

    // T-10-2330: 価格の条件は利益の側（ロングは取得単価より上・ショートは下）で市場の通貨のものだけを比べる。
    [Theory]
    [InlineData(true, "200", "229.99", "230", TakeProfitJudgement.NotReached)]
    [InlineData(true, "200", "230", "230", TakeProfitJudgement.Reached)]
    [InlineData(false, "200", "190.01", "190", TakeProfitJudgement.NotReached)]
    [InlineData(false, "200", "190", "190", TakeProfitJudgement.Reached)]
    [InlineData(true, "200", "180", "230", TakeProfitJudgement.NotReached)] // 含み損でも比べられる（未到達）
    public void 価格の条件は利益の側なら到達か未到達を返す(bool isLong, string entry, string mark, string price, TakeProfitJudgement expected)
    {
        PolicyTakeProfitConditions.Judge([Price(D(price))], isLong, D(entry), D(mark), Currency.Usd).Should().Be(expected);
    }

    // T-10-2330: 複数の条件はすべてに達したときだけ到達。片方だけ達していれば未到達（比べられないではない）。
    [Theory]
    [InlineData("106", TakeProfitJudgement.NotReached)]
    [InlineData("108", TakeProfitJudgement.Reached)]
    public void 複数の条件はすべてに達したときだけ到達(string mark, TakeProfitJudgement expected)
    {
        PolicyTakeProfitConditions.Judge([Pct(5m), Pct(8m)], true, 100m, D(mark), Currency.Usd).Should().Be(expected);
        PolicyTakeProfitConditions.Judge([Pct(5m), Price(107m)], true, 100m, D(mark), Currency.Usd).Should().Be(expected);
    }

    // T-10-2330（否定形）: 比べられない（条件が空・取得単価や現在値が正でない・市場の通貨と違う価格・損の側の価格〔取得単価と同じを含む〕を含む）は
    // Unknown（「未到達」とも書かない）。率の条件と並んでいても、比べられない価格の条件が 1 つあれば Unknown。
    [Theory]
    [InlineData("empty", true, "100", "106")]
    [InlineData("pct", true, "0", "106")]
    [InlineData("pct", true, "100", "0")]
    [InlineData("pct", true, "-1", "106")]
    [InlineData("jpy", true, "200", "231")]
    [InlineData("jpy", true, "200", "100")]
    [InlineData("loss-side", false, "200", "190")]
    [InlineData("loss-side", false, "200", "240")]
    [InlineData("loss-side-long", true, "250", "260")]
    [InlineData("loss-side-long", true, "250", "200")]
    [InlineData("equal", true, "230", "240")]
    [InlineData("pct+jpy", true, "100", "103")]
    [InlineData("pct+loss-side", true, "250", "251")]
    public void 比べられなければUnknownを返す(string kind, bool isLong, string entry, string mark)
    {
        IReadOnlyList<PolicyTakeProfitCondition> conditions = kind switch
        {
            "empty" => [],
            "pct" => [Pct(5m)],
            "jpy" => [Price(230m, Currency.Jpy)],
            "loss-side" => [Price(230m)],
            "loss-side-long" => [Price(230m)],
            "equal" => [Price(230m)],
            "pct+jpy" => [Pct(1m), Price(150m, Currency.Jpy)],
            "pct+loss-side" => [Pct(1m), Price(230m)],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        PolicyTakeProfitConditions.Judge(conditions, isLong, D(entry), D(mark), Currency.Usd).Should().Be(TakeProfitJudgement.Unknown);
        PolicyTakeProfitConditions.Reached(conditions, isLong, D(entry), D(mark), Currency.Usd).Should().BeEmpty();
    }

    // T-10-2330: 方針の行から読んだ条件でも同じ（PoC の方針「利確: 全銘柄 +3%」と NVDA）。
    [Fact]
    public void 方針の行から読んだ条件でPoCの場面を未到達と判定する()
    {
        var conditions = PolicyTakeProfitConditions.ForSymbol("利確: 全銘柄 +3%", "NVDA");

        PolicyTakeProfitConditions.Judge(conditions, true, 230.77m, 237.69m, Currency.Usd).Should().Be(TakeProfitJudgement.NotReached);
        PolicyTakeProfitConditions.GainPercent(true, 230.77m, 237.69m).ToString("+0.00", CultureInfo.InvariantCulture).Should().Be("+3.00");
    }
}
