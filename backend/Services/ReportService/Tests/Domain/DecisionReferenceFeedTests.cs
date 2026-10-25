using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using ReportService.Domain;
using Xunit;
using Rec = ReportService.Domain.DecisionReferenceFeedBuilder.DecisionRecord;

namespace ReportService.Tests;

// FR-06, FR-08, NFR-18, #1356, IADR-0541 決定 2・3: 判断の記録の参照を出典ごとに畳む規則と、外部の表題・URL の無害化を固定する。
public class DecisionReferenceFeedTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 1, 0, 0, TimeSpan.Zero);

    private static DecisionReferences Refs(string state, params DecisionReference[] items) => new(state, items);

    [Fact]
    public void 同じ出典は1件に束ね参照した判断の件数と銘柄を数える()
    {
        var feed = DecisionReferenceFeedBuilder.Build(
        [
            new Rec("AAPL", T0, Refs("retrieved",
                new DecisionReference("A 社決算", "https://example.com/a", T0),
                new DecisionReference("A 社決算", "https://example.com/a", T0), // 同じ判断の別チャンクは 1 件
                new DecisionReference("マクロ", "https://example.com/m", T0.AddHours(-1)))),
            new Rec("MSFT", T0, Refs("retrieved", new DecisionReference("A 社決算", "https://example.com/a", T0.AddHours(1)))),
        ]);

        feed.Decisions.Should().Be(2);
        feed.Items.Should().HaveCount(2);
        feed.Items[0].Should().BeEquivalentTo(new
        {
            Title = "A 社決算",
            SafeUrl = "https://example.com/a",
            DecisionCount = 2,
            Symbols = new[] { "AAPL", "MSFT" },
            PublishedAt = (DateTimeOffset?)T0.AddHours(1),
        });
        feed.Items[1].DecisionCount.Should().Be(1);
    }

    [Fact]
    public void 判断なし_記録なし_取得失敗_未構成_0件を区別して数える()
    {
        DecisionReferenceFeedBuilder.Build([]).NoDecisions.Should().BeTrue();

        var feed = DecisionReferenceFeedBuilder.Build(
        [
            new Rec("AAPL", T0, null),
            new Rec("AAPL", T0, Refs("failed")),
            new Rec("AAPL", T0, Refs("not-configured")),
            new Rec("AAPL", T0, Refs("empty")),
        ]);

        (feed.Decisions, feed.Unrecorded, feed.RetrievalFailed, feed.NotConfigured, feed.Recorded).Should().Be((4, 1, 1, 1, 3));
        feed.Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://example.com/a?b=1", "https://example.com/a?b=1")]
    [InlineData("http://example.com/", "http://example.com/")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("data:text/html,<b>x</b>", null)]
    [InlineData("kb://doc/9", null)]
    [InlineData("/relative/path", null)]
    [InlineData("https://user:pass@example.com/", null)]
    [InlineData("https://example.com/a b", null)]
    [InlineData("https://example.com/a)[x](https://evil", null)]
    [InlineData("https://example.com/\n", null)]
    [InlineData("", null)]
    public void リンクにするのはhttp_httpsの絶対URIだけ(string url, string? expected) =>
        ReportLinkSafety.SafeHttpUrl(url).Should().Be(expected);

    [Fact]
    public void 長すぎるURLはリンクにしない() =>
        ReportLinkSafety.SafeHttpUrl("https://example.com/" + new string('a', ReportLinkSafety.MaxUrlLength)).Should().BeNull();

    [Fact]
    public void 表題のMarkdown記号と改行を無害化し上限で切る()
    {
        ReportLinkSafety.MarkdownTitle("速報](javascript:x)\n# 見出し *強調* <b>", 100)
            .Should().Be("速報\\]\\(javascript:x\\) \\# 見出し \\*強調\\* \\<b\\>");
        ReportLinkSafety.MarkdownTitle(new string('あ', 50), 10).Should().Be(new string('あ', 10) + "…");
        ReportLinkSafety.MarkdownTitle(" \n ", 10).Should().Be("（表題なし）");
    }
}
