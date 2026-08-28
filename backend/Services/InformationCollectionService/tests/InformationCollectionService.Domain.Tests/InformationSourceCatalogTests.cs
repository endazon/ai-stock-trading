using AiStockTrading.InformationCollection.Domain;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.InformationCollection.Domain.Tests;

// FR-01, ADR-0020 決定1, ADR-0005 決定5: 情報源の区分表（必須/推奨/任意/検証用途）が計画の割当表と一致すること。
//
// 🔴 **計画の割当表の写像であることを機械で固定する。** 区分は「欠測したときに何が止まるか」を決める入力であり、
// 静かに変わると**止めるべきときに止まらない／止めなくてよいときに止まる**のどちらかが必ず起きる。
public class InformationSourceCatalogTests
{
    [Theory]
    // 必須（ADR-0020 決定1・02_datasource-candidates「区分の割当」）
    [InlineData("moomoo", SourceTier.Required, MissingSourceBehavior.AbortCycle)]
    [InlineData("finnhub", SourceTier.Required, MissingSourceBehavior.RecordAndNotifyOnly)]
    [InlineData("finnhub-news", SourceTier.Required, MissingSourceBehavior.LimitedDegradation)]
    [InlineData("google-news", SourceTier.Required, MissingSourceBehavior.LimitedDegradation)]
    [InlineData("sec-edgar", SourceTier.Required, MissingSourceBehavior.RecordAndNotifyOnly)]
    [InlineData("fred", SourceTier.Required, MissingSourceBehavior.RecordAndNotifyOnly)]
    [InlineData("finra-short", SourceTier.Required, MissingSourceBehavior.LimitedDegradation)]
    // 推奨
    [InlineData("gdelt", SourceTier.Recommended, MissingSourceBehavior.RecordAndNotifyOnly)]
    [InlineData("edinet", SourceTier.Recommended, MissingSourceBehavior.RecordAndNotifyOnly)]
    [InlineData("boj", SourceTier.Recommended, MissingSourceBehavior.RecordAndNotifyOnly)]
    // 任意
    [InlineData("tdnet-yanoshin", SourceTier.Optional, MissingSourceBehavior.RecordAndNotifyOnly)]
    [InlineData("jpx-supply", SourceTier.Optional, MissingSourceBehavior.RecordAndNotifyOnly)]
    [InlineData("e-stat", SourceTier.Optional, MissingSourceBehavior.RecordAndNotifyOnly)]
    [InlineData("sec-edgar-13f", SourceTier.Optional, MissingSourceBehavior.RecordAndNotifyOnly)]
    [InlineData("reddit", SourceTier.Optional, MissingSourceBehavior.RecordAndNotifyOnly)]
    [InlineData("investing-rss", SourceTier.Optional, MissingSourceBehavior.RecordAndNotifyOnly)]
    // 検証用途
    [InlineData("jquants", SourceTier.VerificationOnly, MissingSourceBehavior.RecordAndNotifyOnly)]
    [InlineData("stooq", SourceTier.VerificationOnly, MissingSourceBehavior.RecordAndNotifyOnly)]
    public void 区分と欠測時の振る舞いは計画の割当表と一致する(
        string name, SourceTier tier, MissingSourceBehavior behavior)
    {
        var definition = InformationSourceCatalog.Default.Find(name);

        definition.Should().NotBeNull();
        definition!.Tier.Should().Be(tier);
        definition.MissingBehavior.Should().Be(behavior);
    }

    // ADR-0020 決定1: 必須は「構成として必ず有効化する」・推奨は「既定で有効」・任意は「既定では無効」。
    [Fact]
    public void 既定の有効無効は区分に従う()
    {
        foreach (var definition in InformationSourceCatalog.Default.Definitions)
        {
            var expected = definition.Tier is SourceTier.Required or SourceTier.Recommended;
            definition.EnabledByDefault.Should().Be(expected, $"{definition.Name} の区分は {definition.Tier}");
        }
    }

    // ADR-0020 決定1: **検証用途はライブの取引判断の入力にしてはならない。**
    [Theory]
    [InlineData("stooq", false)]
    [InlineData("jquants", false)]
    [InlineData("finnhub", true)]
    [InlineData("google-news", true)]
    public void 検証用途はライブ判断に使えない(string name, bool usable)
    {
        InformationSourceCatalog.Default.IsUsableForLiveDecision(name).Should().Be(usable);
    }

    // 🔴 **知らない名前は fail-closed。** 「知らない源だから通す」は統制にならない。
    [Fact]
    public void カタログに無い名前はライブ判断に使えない()
    {
        InformationSourceCatalog.Default.IsUsableForLiveDecision("random-blog").Should().BeFalse();
        InformationSourceCatalog.Default.IsUsableForLiveDecision(null).Should().BeFalse();
    }

    // ADR-0020 決定2: ニュース系は**カテゴリ単位**で「いずれか 1 つ以上」を判定するため、2 系統が同じカテゴリにいる。
    [Fact]
    public void ニュース系は2系統でありどちらも必須である()
    {
        var news = InformationSourceCatalog.Default.InCategory(InformationSourceCatalog.NewsCategory);

        news.Select(n => n.Name).Should().BeEquivalentTo(["finnhub-news", "google-news"]);
        news.Should().OnlyContain(n => n.Tier == SourceTier.Required);
    }

    // ADR-0020 決定3: 空売りだけを止めるのは FINRA だけである（他の限定縮退は新規建て全体を止める）。
    [Fact]
    public void 空売り限定の縮退は_FINRA_だけである()
    {
        InformationSourceCatalog.Default.Definitions
            .Where(d => d.LimitsShortEntriesOnly)
            .Select(d => d.Name)
            .Should().Equal("finra-short");
    }

    // ADR-0005 決定5: 一時降格は必須のみに効く（推奨・任意・検証用途は変えない）。
    [Fact]
    public void 一時降格は必須ソースだけを推奨へ落とす()
    {
        var catalog = InformationSourceCatalog.Default.DemoteToRecommended("edinet");

        catalog.Find("edinet")!.Tier.Should().Be(SourceTier.Recommended, "元から推奨であり変わらない");

        var demoted = InformationSourceCatalog.Default.DemoteToRecommended("moomoo");
        demoted.Find("moomoo")!.Tier.Should().Be(SourceTier.Recommended);
        demoted.Find("moomoo")!.MissingBehavior.Should().Be(MissingSourceBehavior.RecordAndNotifyOnly);

        // 🔴 **元のカタログは変えない**（静的な既定を書き換えると、降格が全プロセスへ波及する）。
        InformationSourceCatalog.Default.Find("moomoo")!.Tier.Should().Be(SourceTier.Required);
    }

    [Fact]
    public void 存在しない情報源の降格は例外にする()
    {
        var act = () => InformationSourceCatalog.Default.DemoteToRecommended("bloomberg");

        act.Should().Throw<ArgumentException>();
    }

    // 欠測時の振る舞いは **3 種に限る**（ADR-0020 決定3）。
    [Fact]
    public void 欠測時の振る舞いは3種に限る()
    {
        Enum.GetValues<MissingSourceBehavior>().Should().HaveCount(3);
    }
}
