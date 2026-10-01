using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Kernel.Tests.Trading;

// FR-04, FR-07, ADR-0003, ADR-0048 決定 4, #1129, IADR-0470 決定 2: 方針の文から数値の利確条件を決定的に取り出す（T-10-1880〜1883）。
public class PolicyTakeProfitConditionsTests
{
    // T-10-1880: 含み益の率・価格・一部利確の割合（N% を・保有の N%・半分・N 割）を取り出す。全角の数字と記号も読む。
    [Theory]
    [InlineData("AAPL: 取得単価から +5% で利確。", TakeProfitThresholdKind.GainPercent, "5", null)]
    [InlineData("AAPL は 230 ドル以上で利確する", TakeProfitThresholdKind.Price, "230", null)]
    [InlineData("MSFT は $412.5 に達したら利益確定", TakeProfitThresholdKind.Price, "412.5", null)]
    [InlineData("NVDA: +3% で保有の 50% を利確", TakeProfitThresholdKind.GainPercent, "3", "50")]
    [InlineData("NVDA: +3% で 30%を利確", TakeProfitThresholdKind.GainPercent, "3", "30")]
    [InlineData("NVDA: +4.5% で半分を利食い", TakeProfitThresholdKind.GainPercent, "4.5", "50")]
    [InlineData("NVDA: +6% で 3 割を利確", TakeProfitThresholdKind.GainPercent, "6", "30")]
    [InlineData("ＡＡＰＬ：取得単価から＋５％で利確", TakeProfitThresholdKind.GainPercent, "5", null)]
    public void 数値の利確条件を取り出す(string policy, TakeProfitThresholdKind kind, string threshold, string? partial)
    {
        var condition = PolicyTakeProfitConditions.Extract(policy).Should().ContainSingle().Which;

        condition.Kind.Should().Be(kind);
        condition.Threshold.Should().Be(decimal.Parse(threshold, System.Globalization.CultureInfo.InvariantCulture));
        condition.PartialPercent.Should().Be(partial is null ? null : decimal.Parse(partial, System.Globalization.CultureInfo.InvariantCulture));
        PolicyTakeProfitConditions.HasAny(policy).Should().BeTrue();
    }

    // T-10-1880: 段階の利確（「+3% で半分、+6% で残りを利確」）は 2 件。残りは割合なし。
    [Fact]
    public void 段階の利確は2件を出現順に取り出す()
    {
        var conditions = PolicyTakeProfitConditions.Extract("AAPL は +3% で半分、+6% で残りを利確する。");

        conditions.Select(c => (c.Threshold, c.PartialPercent)).Should().Equal((3m, 50m), (6m, (decimal?)null));
        conditions.Should().OnlyContain(c => c.Symbols.SequenceEqual(new[] { "AAPL" }));
    }

    // T-10-1881（否定形）: 数値の無い利確・損切りの数値・取得単価以外が基準の数値・負の率・利確の語の無い文・上限の金額は条件にしない。
    [Theory]
    [InlineData("含み益が十分に出た段階で利確する。")]
    [InlineData("適切に利確し、損切りは -2% で行う。")]
    [InlineData("損切りは 2% 下落で行う。利確は様子を見て判断する。")]
    [InlineData("前日比 +3% 以上の上昇で利確する。")]
    [InlineData("始値から +2% で利確")]
    [InlineData("-5% で利確")]
    [InlineData("AAPL は +5% 上昇が見込める。押し目で買う。")]
    [InlineData("利確は早めに、発注上限は 5000 ドルを守る")]
    [InlineData("含み損が 3% になったら利確ではなく撤退")]
    [InlineData("")]
    [InlineData(null)]
    public void 数値の利確条件と読めないものは取り出さない(string? policy)
    {
        PolicyTakeProfitConditions.Extract(policy).Should().BeEmpty();
        PolicyTakeProfitConditions.HasAny(policy).Should().BeFalse();
    }

    // T-10-1881: 1 つの方針から取り出す条件は上限まで（プロンプトの行が際限なく伸びない）。
    [Fact]
    public void 取り出す条件は上限まで()
    {
        var policy = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"+{i}% で利確"));

        PolicyTakeProfitConditions.Extract(policy).Should().HaveCount(PolicyTakeProfitConditions.MaxConditions);
    }

    // T-10-1882: 銘柄を名指しした条件を優先し、無ければ名指ししない条件を返す。他の銘柄の条件は掛けない。一般語の略語は銘柄と読まない。
    [Fact]
    public void 銘柄の条件は名指しを優先し他の銘柄には掛けない()
    {
        const string policy = "全銘柄: 取得単価から +4% で利確。\nAAPL: +6% で利確。\nETF や AI 関連は USD 建てで +8% で利確。";

        PolicyTakeProfitConditions.ForSymbol(policy, "AAPL").Select(c => c.Threshold).Should().Equal(6m);
        PolicyTakeProfitConditions.ForSymbol(policy, "aapl").Select(c => c.Threshold).Should().Equal(6m);
        PolicyTakeProfitConditions.ForSymbol(policy, "MSFT").Select(c => c.Threshold).Should().Equal(4m, 8m);
        PolicyTakeProfitConditions.ForSymbol("NVDA: +5% で利確", "MSFT").Should().BeEmpty("他の銘柄の条件は掛けない");
    }

    // T-10-1883: 到達はロングで (現在値 − 取得単価) ÷ 取得単価 ≥ しきい値・価格以上、ショートで符号を反転・価格以下。ちょうどは到達。
    [Theory]
    [InlineData(true, "100", "105", true)]
    [InlineData(true, "100", "104.99", false)]
    [InlineData(false, "100", "95", true)]
    [InlineData(false, "100", "105", false)]
    public void 含み益の率の到達をコードで比べる(bool isLong, string entry, string mark, bool reached)
    {
        var conditions = PolicyTakeProfitConditions.Extract("+5% で利確");
        var e = decimal.Parse(entry, System.Globalization.CultureInfo.InvariantCulture);
        var m = decimal.Parse(mark, System.Globalization.CultureInfo.InvariantCulture);

        PolicyTakeProfitConditions.Reached(conditions, isLong, e, m).Should().HaveCount(reached ? 1 : 0);
    }

    [Theory]
    [InlineData(true, "230", true)]
    [InlineData(true, "229.99", false)]
    [InlineData(false, "230", true)]
    [InlineData(false, "230.01", false)]
    public void 価格の到達をコードで比べる(bool isLong, string mark, bool reached)
    {
        var conditions = PolicyTakeProfitConditions.Extract("230 ドルで利確");

        PolicyTakeProfitConditions.Reached(
                conditions, isLong, 200m, decimal.Parse(mark, System.Globalization.CultureInfo.InvariantCulture))
            .Should().HaveCount(reached ? 1 : 0);
    }

    // T-10-1883（否定形）: 取得単価・現在値が正でなければ到達を作らない（推測しない）。
    [Theory]
    [InlineData("0", "105")]
    [InlineData("100", "0")]
    public void 値が正でなければ到達しない(string entry, string mark)
    {
        var conditions = PolicyTakeProfitConditions.Extract("+1% で利確");

        PolicyTakeProfitConditions.Reached(
                conditions, true,
                decimal.Parse(entry, System.Globalization.CultureInfo.InvariantCulture),
                decimal.Parse(mark, System.Globalization.CultureInfo.InvariantCulture))
            .Should().BeEmpty();
    }

    [Fact]
    public void 条件の表示は率価格と一部利確の割合を書く()
    {
        PolicyTakeProfitConditions.Describe(new([], TakeProfitThresholdKind.GainPercent, 5m, 50m))
            .Should().Be("取得単価から +5% で利確（一部利確 50%）");
        PolicyTakeProfitConditions.Describe(new([], TakeProfitThresholdKind.Price, 230.5m, null))
            .Should().Be("価格 230.5 で利確");
    }
}
