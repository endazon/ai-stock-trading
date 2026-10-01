using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Kernel.Tests.Trading;

// FR-04, FR-07, ADR-0003, ADR-0048 決定 4, #1129, IADR-0470 決定 2: 方針の文から数値の利確条件を決定的に取り出す（T-10-1880〜1883・
// 独立監査の是正 T-10-1892〜1896）。
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

    // 価格はロングで取得単価 200 より上・ショートで取得単価 250 より下の、利益の側の水準（#1129 監査 F2 で是正）。
    [Theory]
    [InlineData(true, "200", "230", true)]
    [InlineData(true, "200", "229.99", false)]
    [InlineData(false, "250", "230", true)]
    [InlineData(false, "250", "230.01", false)]
    public void 価格の到達をコードで比べる(bool isLong, string entry, string mark, bool reached)
    {
        var conditions = PolicyTakeProfitConditions.Extract("230 ドルで利確");

        PolicyTakeProfitConditions.Reached(
                conditions, isLong,
                decimal.Parse(entry, System.Globalization.CultureInfo.InvariantCulture),
                decimal.Parse(mark, System.Globalization.CultureInfo.InvariantCulture))
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
    // T-10-1892（#1129 監査 F1）: 銘柄は節ごとに決める。1 つの文に 2 銘柄の条件が並んでも、他の銘柄の条件を掛けない。
    [Fact]
    public void 銘柄は節ごとに決め他の銘柄の条件を掛けない()
    {
        const string policy = "AAPLは+5%で利確、MSFTは+8%で利確する。";

        PolicyTakeProfitConditions.ForSymbol(policy, "MSFT").Select(c => c.Threshold).Should().Equal(8m);
        PolicyTakeProfitConditions.ForSymbol(policy, "AAPL").Select(c => c.Threshold).Should().Equal(5m);
        // 監査の場面: MSFT が +6% でも +5%（AAPL の条件）に達したとは言わない。
        PolicyTakeProfitConditions.Reached(PolicyTakeProfitConditions.ForSymbol(policy, "MSFT"), true, 100m, 106m)
            .Should().BeEmpty();
    }

    // T-10-1892（#1129 監査 F1）: 節に銘柄が無く、文に銘柄が 2 つ以上あれば主語を決められない。数値の条件としては数えるが、
    // どの銘柄にも掛けない。文の銘柄がちょうど 1 つなら、その銘柄の条件と読む。
    [Fact]
    public void 節に銘柄が無く文に銘柄が複数なら主語を決めない()
    {
        const string ambiguous = "AAPLとMSFTは様子見、+5%で利確。";

        PolicyTakeProfitConditions.Extract(ambiguous).Should().ContainSingle()
            .Which.SubjectUnresolved.Should().BeTrue();
        PolicyTakeProfitConditions.HasAny(ambiguous).Should().BeTrue("方針は数値を書いている");
        PolicyTakeProfitConditions.ForSymbol(ambiguous, "AAPL").Should().BeEmpty();
        PolicyTakeProfitConditions.ForSymbol(ambiguous, "NVDA").Should().BeEmpty("銘柄を名指ししない条件としても掛けない");

        const string single = "AAPL は +3% で半分、+6% で残りを利確する。";
        PolicyTakeProfitConditions.ForSymbol(single, "AAPL").Select(c => c.Threshold).Should().Equal(3m, 6m);
        PolicyTakeProfitConditions.ForSymbol(single, "MSFT").Should().BeEmpty();
    }

    // T-10-1893（#1129 監査 F2・否定形）: 値幅・利益の額は価格の水準と読まない（符号つきの金額・直後が「高・分」も同じ）。
    [Theory]
    [InlineData("AAPLは5ドル上昇したら利確")]
    [InlineData("AAPLは5ドル上がったら利確")]
    [InlineData("利益が500ドルに達したら利確")]
    [InlineData("含み益が300ドルを超えたら利確")]
    [InlineData("値幅 10 ドルで利確")]
    [InlineData("AAPL は +5 ドルで利確")]
    [InlineData("AAPL は 5 ドル高で利確")]
    [InlineData("ショートは 5 ドル下がったら利確")]
    public void 値幅と利益の額は価格と読まない(string policy)
    {
        PolicyTakeProfitConditions.Extract(policy).Should().BeEmpty();
    }

    // T-10-1893（#1129 監査 F2）: 価格の条件は利益の側だけ。ショートで取得単価 200 より上の 230・ロングで取得単価より下の価格は、
    // 現在値がそこを越えても到達としない（監査の場面: ショート 200→220 で −10% なのに「230 ドルに達した」と書いていた）。
    [Theory]
    [InlineData(false, "200", "220")]
    [InlineData(false, "200", "190")]
    [InlineData(true, "250", "240")]
    [InlineData(true, "250", "260")]
    public void 価格の条件は利益の側だけを到達とする(bool isLong, string entry, string mark)
    {
        var conditions = PolicyTakeProfitConditions.Extract("230 ドルで利確");

        PolicyTakeProfitConditions.Reached(
                conditions, isLong,
                decimal.Parse(entry, System.Globalization.CultureInfo.InvariantCulture),
                decimal.Parse(mark, System.Globalization.CultureInfo.InvariantCulture))
            .Should().BeEmpty();
    }

    // T-10-1893（#1129 監査 F2・多重の防御）: 含み益の率が 0 以下なら、しきい値が正でない条件を直接渡されても到達としない。
    [Theory]
    [InlineData(true, "100", "100", "0")]
    [InlineData(true, "100", "97", "-5")]
    [InlineData(false, "100", "103", "-5")]
    public void 含み益が0以下なら到達としない(bool isLong, string entry, string mark, string threshold)
    {
        var conditions = new[]
        {
            new PolicyTakeProfitCondition(
                [], TakeProfitThresholdKind.GainPercent,
                decimal.Parse(threshold, System.Globalization.CultureInfo.InvariantCulture), null),
        };

        PolicyTakeProfitConditions.Reached(
                conditions, isLong,
                decimal.Parse(entry, System.Globalization.CultureInfo.InvariantCulture),
                decimal.Parse(mark, System.Globalization.CultureInfo.InvariantCulture))
            .Should().BeEmpty();
    }

    // T-10-1894（#1129 監査 F3）: 節に「N%」が 2 つあり片方の直後が「（を）利確・売却・決済」なら、そちらは一部利確の割合。
    // 「保有の N%」は後ろに「を」が無くても一部利確の割合。
    [Theory]
    [InlineData("+5%到達で50%利確", "5", "50")]
    [InlineData("+5%で50%利確", "5", "50")]
    [InlineData("利確は +5% で 30% 売却", "5", "30")]
    [InlineData("+5% で 25%決済して利確", "5", "25")]
    [InlineData("保有の 30% について +3% で利確", "3", "30")]
    [InlineData("数量の 20% は +4% で利確", "4", "20")]
    public void 一部利確の割合をしきい値と読まない(string policy, string threshold, string partial)
    {
        var condition = PolicyTakeProfitConditions.Extract(policy).Should().ContainSingle().Which;

        condition.Kind.Should().Be(TakeProfitThresholdKind.GainPercent);
        condition.Threshold.Should().Be(decimal.Parse(threshold, System.Globalization.CultureInfo.InvariantCulture));
        condition.PartialPercent.Should().Be(decimal.Parse(partial, System.Globalization.CultureInfo.InvariantCulture));
    }

    // T-10-1894: % が 1 つだけなら「5%利確」の 5% はしきい値のまま（割合と読み替えない）。
    [Theory]
    [InlineData("+5%利確")]
    [InlineData("5%利確")]
    [InlineData("含み益 5% 利確")]
    public void 割合の読み替えは2つ目の率があるときだけ(string policy)
    {
        var condition = PolicyTakeProfitConditions.Extract(policy).Should().ContainSingle().Which;

        condition.Threshold.Should().Be(5m);
        condition.PartialPercent.Should().BeNull();
    }

    // T-10-1895（#1129 監査 F4・否定形）: 利確に結び付かない %（配分・建てる量・指数・期間・利確でない売買）は条件にしない。
    [Theory]
    [InlineData("1銘柄あたり口座の10%まで、含み益が十分に出たら利確。")]
    [InlineData("資金の 20% を配分し、利確は様子を見る。")]
    [InlineData("日経平均が +3% なら利確")]
    [InlineData("S&P500 が +2% 上昇したら利確")]
    [InlineData("ナスダックが 1% 高ければ利確を急がない")]
    [InlineData("保有期間の 50% が経過したら利確")]
    [InlineData("+3% で買い増し、利確は様子を見る")]
    [InlineData("AAPLは決算後に利確、出来高は見る、+20% の急騰も視野")]
    public void 利確に結び付かない率は条件にしない(string policy)
    {
        PolicyTakeProfitConditions.Extract(policy).Should().BeEmpty();
        PolicyTakeProfitConditions.HasAny(policy).Should().BeFalse("警告を黙らせない");
    }

    // T-10-1895（#1129 監査 F4）: 利確の節と、そこから途切れずに隣り合う数値の節は読む（段階の利確）。除外の節と数値の無い節の先は読まない。
    // 「平均取得単価」の平均は指数の語に当たらない。
    [Theory]
    [InlineData("+3% で 3 割、+6% で 3 割、+9% で残りを利確", new[] { "3", "6", "9" })]
    [InlineData("+3% で買い増し、+8% で利確", new[] { "8" })]
    [InlineData("1銘柄あたり口座の10%まで、+6% で利確", new[] { "6" })]
    [InlineData("+8% で利確、決算前は様子見、+20% の急騰も視野", new[] { "8" })]
    [InlineData("平均取得単価から +5% で利確", new[] { "5" })]
    [InlineData("保有から 5 日経過、+3% で利確", new[] { "3" })]
    public void 利確に結び付く率だけを読む(string policy, string[] thresholds)
    {
        PolicyTakeProfitConditions.Extract(policy).Select(c => c.Threshold)
            .Should().Equal(thresholds.Select(t => decimal.Parse(t, System.Globalization.CultureInfo.InvariantCulture)));
    }

    // T-10-1896（#1129 監査 F5）: ティッカーでない社名・コードを名指しした条件は、どの銘柄にも掛けない（全銘柄へ落とさない）。
    // 方針は数値を書いているので、数値の利確条件としては数える（警告は出さない）。
    [Theory]
    [InlineData("アップルは+5%で利確")]
    [InlineData("トヨタ(7203)は+5%で利確")]
    [InlineData("トヨタ（７２０３）は、＋５％で利確")]
    [InlineData("任天堂: +5% で利確")]
    [InlineData("Appleは +5% で利確")]
    [InlineData("7203 は +5% で利確")]
    public void ティッカーでない名指しの条件はどの銘柄にも掛けない(string policy)
    {
        PolicyTakeProfitConditions.Extract(policy).Should().ContainSingle()
            .Which.SubjectUnresolved.Should().BeTrue();
        PolicyTakeProfitConditions.HasAny(policy).Should().BeTrue();
        PolicyTakeProfitConditions.ForSymbol(policy, "AAPL").Should().BeEmpty();
        PolicyTakeProfitConditions.ForSymbol(policy, "MSFT").Should().BeEmpty();
    }

    // T-10-1896（#1129 監査 F5）: 一般語の主語（保有株・全銘柄・利確・〜関連）は名指しではない。どの銘柄にも掛かる。
    [Theory]
    [InlineData("保有株は +5% で利確")]
    [InlineData("全銘柄: +5% で利確")]
    [InlineData("利確は +5% で行う")]
    [InlineData("半導体関連は +5% で利確")]
    [InlineData("ロングは +5% で利確")]
    public void 一般語の主語はどの銘柄にも掛かる(string policy)
    {
        PolicyTakeProfitConditions.ForSymbol(policy, "MSFT").Select(c => c.Threshold).Should().Equal(5m);
    }
}
