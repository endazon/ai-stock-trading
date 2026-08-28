using InformationCollectionService.Domain;
using AwesomeAssertions;
using Xunit;

namespace InformationCollectionService.Domain.Tests;

// FR-01, ADR-0020 決定2・決定3: **区分 × 欠測の判定テーブル**（#336 受け入れ基準①）。
//
// 統制系のため 3 点セットを揃える:
//   1. 境界値テーブル（本クラス前半の [Theory]）
//   2. プロパティベース（全組み合わせで成り立つ不変量）
//   3. **否定形**（縮退しても手仕舞い・損切りが止まらないこと）
public class DegradationEvaluatorTests
{
    private static CollectionDegradation Evaluate(params SourceOutcome[] outcomes) =>
        DegradationEvaluator.Evaluate(InformationSourceCatalog.Default, outcomes);

    // --- 1. 判定テーブル（区分 × 欠測 → 3 種の振る舞い） ---

    [Theory]
    // 必須・サイクル中止（moomoo）
    [InlineData("moomoo", true, false, false)]
    // 必須・記録通知のみ（Finnhub 市況面 / SEC EDGAR / FRED）
    [InlineData("finnhub", false, false, false)]
    [InlineData("sec-edgar", false, false, false)]
    [InlineData("fred", false, false, false)]
    // 必須・限定縮退（空売りのみ）
    [InlineData("finra-short", false, false, true)]
    // 推奨・任意は止めない（記録のみ）
    [InlineData("gdelt", false, false, false)]
    [InlineData("edinet", false, false, false)]
    [InlineData("boj", false, false, false)]
    [InlineData("reddit", false, false, false)]
    public void 単一ソースの欠測は区分と振る舞いのとおりに落とす(
        string source, bool abortCycle, bool blocksNewEntries, bool blocksShortEntries)
    {
        var degradation = Evaluate(SourceOutcome.Failed(source));

        degradation.AbortCycle.Should().Be(abortCycle);
        degradation.BlocksNewEntries.Should().Be(blocksNewEntries);
        degradation.BlocksShortEntries.Should().Be(blocksShortEntries);
    }

    [Theory]
    // ニュース系は「いずれか 1 つ以上が生きていること」が必須条件である（ADR-0020 決定2）。
    [InlineData(true, true, true)]    // 両方欠測 → 全滅
    [InlineData(false, true, false)]  // Finnhub 生存 → 満たす
    [InlineData(true, false, false)]  // Google 生存 → 満たす
    [InlineData(false, false, false)] // 両方生存
    public void ニュース系はいずれか1つ以上が生きていれば縮退しない(
        bool finnhubNewsFailed, bool googleNewsFailed, bool expectedOutage)
    {
        var degradation = Evaluate(
            new SourceOutcome("finnhub-news", !finnhubNewsFailed),
            new SourceOutcome("google-news", !googleNewsFailed));

        degradation.NewsOutage.Should().Be(expectedOutage);
        degradation.BlocksNewEntries.Should().Be(expectedOutage);
    }

    // 🔴 **試行していないソースは欠測ではない。** 数えると、外部接続しない安全既定のままで毎サイクル縮退する。
    [Fact]
    public void 構成されていない必須ソースは欠測ではなく未構成として記録される()
    {
        var degradation = Evaluate(SourceOutcome.Ok("fred"));

        degradation.IsDegraded.Should().BeFalse();
        degradation.NewsOutage.Should().BeFalse("ニュース系は 1 つも試行していない（未構成）");
        degradation.UnconfiguredRequired.Should().Contain(["moomoo", "finnhub", "sec-edgar", "finra-short"]);
        degradation.UnconfiguredRequired.Should().NotContain("fred");
    }

    [Fact]
    public void 何も欠測していなければ縮退しない()
    {
        var degradation = Evaluate(
            SourceOutcome.Ok("finnhub"), SourceOutcome.Ok("finnhub-news"), SourceOutcome.Ok("google-news"));

        degradation.IsDegraded.Should().BeFalse();
        degradation.Notifications.Should().BeEmpty();
    }

    [Fact]
    public void 通知文には止まっていないものを必ず書く()
    {
        var degradation = Evaluate(
            SourceOutcome.Failed("finnhub-news"), SourceOutcome.Failed("google-news"));

        degradation.Notifications.Should().ContainMatch("*手仕舞い・損切りは止めない*");
    }

    // ADR-0005 決定5: 一時降格したソースは、欠測しても「記録のみ」になる（取引を止めない）。
    [Fact]
    public void 推奨へ一時降格した必須ソースは欠測しても新規建てを止めない()
    {
        var catalog = InformationSourceCatalog.Default.DemoteToRecommended("finra-short");

        var degradation = DegradationEvaluator.Evaluate(catalog, [SourceOutcome.Failed("finra-short")]);

        degradation.BlocksShortEntries.Should().BeFalse();
        degradation.AbortCycle.Should().BeFalse();
    }

    // --- 2. プロパティベース（全組み合わせで成り立つ不変量） ---

    // 必須ソース全件について、成功・失敗のすべての組み合わせ（2^n）を回す。
    public static TheoryData<IReadOnlyList<SourceOutcome>> AllRequiredCombinations()
    {
        var required = InformationSourceCatalog.Default.Definitions
            .Where(d => d.Tier == SourceTier.Required)
            .Select(d => d.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var data = new TheoryData<IReadOnlyList<SourceOutcome>>();
        for (var mask = 0; mask < 1 << required.Count; mask++)
        {
            var combination = required
                .Select((name, index) => new SourceOutcome(name, (mask & (1 << index)) != 0))
                .ToList();
            data.Add(combination);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllRequiredCombinations))]
    public void どの欠測の組み合わせでも判定は矛盾しない(IReadOnlyList<SourceOutcome> outcomes)
    {
        var degradation = DegradationEvaluator.Evaluate(InformationSourceCatalog.Default, outcomes);

        // 欠測として挙がるのは、実際に失敗したソースだけである（推測で増やさない）。
        degradation.MissingRequired.Should().OnlyContain(
            name => outcomes.Any(o => o.Name == name && !o.Succeeded));

        // 何かを止めるなら、必ず欠測がある（理由のない停止を作らない）。
        if (degradation.AbortCycle || degradation.BlocksNewEntries || degradation.BlocksShortEntries)
            degradation.MissingRequired.Should().NotBeEmpty();

        // 試行したソースは未構成に数えない。
        degradation.UnconfiguredRequired.Should().NotIntersectWith(outcomes.Select(o => o.Name));
    }

    // --- 3. 否定形（#336 受け入れ基準②） ---

    // 🔴 **どのような欠測の組み合わせでも、手仕舞い・損切りは止まらない。**
    // 限定縮退で止まるのは新規建てだけであり（ADR-0020 決定2/決定3）、
    // **「止められない」より「閉じられない」ほうが危険である。**
    [Theory]
    [MemberData(nameof(AllRequiredCombinations))]
    public void どの欠測の組み合わせでも手仕舞いと損切りは止まらない(IReadOnlyList<SourceOutcome> outcomes)
    {
        var degradation = DegradationEvaluator.Evaluate(InformationSourceCatalog.Default, outcomes);

        degradation.ClosesAllowed.Should().BeTrue();
        degradation.StopLossAllowed.Should().BeTrue();
    }

    // 型として「決済を止める」表現を持たないことを構造で固定する。
    // **プロパティが増えて出口が塞がれるようになったら、このテストが落ちる。**
    [Fact]
    public void 縮退の型は決済を止める表現を持たない()
    {
        var stoppable = typeof(CollectionDegradation).GetProperties()
            .Where(p => p.PropertyType == typeof(bool))
            .Where(p => p.Name.Contains("Close", StringComparison.Ordinal)
                || p.Name.Contains("StopLoss", StringComparison.Ordinal))
            .ToList();

        stoppable.Should().OnlyContain(
            p => (bool)p.GetValue(CollectionDegradation.None)!,
            "決済側は常に許可される。止める向きのフラグを増やしてはならない");
    }
}
