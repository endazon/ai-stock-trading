using AiStockTrading.InformationCollection.Application.Adapters;
using AiStockTrading.InformationCollection.Application.Ports;
using AiStockTrading.InformationCollection.Application.Services;
using AiStockTrading.InformationCollection.Application.State;
using AiStockTrading.InformationCollection.Domain;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.InformationCollection.Application.Tests;

// FR-01, ADR-0003, ADR-0004, ADR-0020: 収集オーケストレーション（検証用途の排除・許可リスト選別・サニタイズ・
// KB 保存・件数・欠測の明示）を検証する。
public class InformationCollectionServiceTests
{
    private static RawInformationItem News(string source, string content) =>
        new(InformationKind.News, source, "AAPL", "見出し", content, DateTimeOffset.UtcNow);

    private sealed class StubFetcher(SourceFetchResult result) : ISourceFetcher
    {
        public StubFetcher(IReadOnlyList<RawInformationItem> items)
            : this(new SourceFetchResult(items, [.. items.Select(i => SourceOutcome.Ok(i.Source)).Distinct()]))
        {
        }

        public Task<SourceFetchResult> FetchAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private static InformationCollectionService Create(ISourceFetcher fetcher, IKnowledgeBaseSink sink) =>
        new(fetcher, sink, SourceAllowlist.Default, InformationSourceCatalog.Default,
            new FixedClock(DateTimeOffset.UnixEpoch));

    [Fact]
    public async Task 許可ソースは正規化サニタイズされ_KB_に保存される()
    {
        var sink = new InMemoryKnowledgeBaseSink();
        var svc = Create(new StubFetcher([News("finnhub", "好決算のニュース")]), sink);

        var result = await svc.CollectAsync();

        result.ItemCount.Should().Be(1);
        sink.Saved.Should().HaveCount(1);
        var saved = sink.Saved.Single();
        PromptSafetySanitizer.IsWrapped(saved.Content).Should().BeTrue(); // データとして分離済み
        saved.Content.Should().Contain("好決算");
        saved.Source.Should().Be("finnhub");
    }

    [Fact]
    public async Task 非許可ソースは破棄され保存されない()
    {
        var sink = new InMemoryKnowledgeBaseSink();
        var svc = Create(new StubFetcher([News("random-blog", "怪しい投稿")]), sink);

        var result = await svc.CollectAsync();

        result.ItemCount.Should().Be(0);
        sink.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task 許可と非許可が混在すると許可分だけ保存される()
    {
        var sink = new InMemoryKnowledgeBaseSink();
        var svc = Create(
            new StubFetcher([News("finnhub", "A"), News("random-blog", "B"), News("edinet", "C")]), sink);

        var result = await svc.CollectAsync();

        result.ItemCount.Should().Be(2);
        sink.Saved.Select(s => s.Source).Should().BeEquivalentTo(new[] { "finnhub", "edinet" });
    }

    [Fact]
    public async Task 収集ゼロなら_KB_保存は呼ばれず件数0()
    {
        var sink = new InMemoryKnowledgeBaseSink();
        var svc = Create(new NoSourcesFetcher(), sink);

        var result = await svc.CollectAsync();

        result.ItemCount.Should().Be(0);
        result.Degradation.IsDegraded.Should().BeFalse(); // 未構成は欠測ではない
        sink.Saved.Should().BeEmpty();
    }

    // FR-01, ADR-0020 決定1: **検証用途はライブの取引判断の入力にしてはならない。**
    // 収集段で落とす——KB へ入れてから「使わない」運用に頼ると RAG がいつか拾う。
    [Fact]
    public async Task 検証用途区分のアイテムは_KB_へ保存されない()
    {
        var sink = new InMemoryKnowledgeBaseSink();
        var svc = Create(
            new StubFetcher([News("finnhub", "ライブ"), News("stooq", "検証用途"), News("jquants", "検証用途")]), sink);

        var result = await svc.CollectAsync();

        result.ItemCount.Should().Be(1);
        sink.Saved.Select(s => s.Source).Should().Equal("finnhub");
    }

    // FR-01, ADR-0020 決定2-1: ニュース系が全滅したら「欠測している」ことを明示して判断文脈へ渡す。
    // **無言の空データにしない。**
    [Fact]
    public async Task ニュース系が全滅すると欠測を明示する文書が保存される()
    {
        var sink = new InMemoryKnowledgeBaseSink();
        var fetcher = new StubFetcher(new SourceFetchResult(
            [],
            [SourceOutcome.Failed("finnhub-news"), SourceOutcome.Failed("google-news")]));
        var svc = Create(fetcher, sink);

        var result = await svc.CollectAsync();

        result.Degradation.NewsOutage.Should().BeTrue();
        result.Degradation.BlocksNewEntries.Should().BeTrue();
        var notice = sink.Saved.Should().ContainSingle().Which;
        notice.Source.Should().Be(DegradationNotice.SourceName);
        notice.Kind.Should().Be(InformationKind.SourceStatus);
        notice.Content.Should().Contain("ニュース情報は欠測している");
    }

    // 🔴 **否定形**: 縮退しても手仕舞い・損切りは止まらない（ADR-0020 決定2/決定3）。
    [Fact]
    public async Task ニュース欠測でも手仕舞いと損切りは止めないことが記録と結果の両方に残る()
    {
        var sink = new InMemoryKnowledgeBaseSink();
        var fetcher = new StubFetcher(new SourceFetchResult(
            [], [SourceOutcome.Failed("finnhub-news"), SourceOutcome.Failed("google-news")]));

        var result = await Create(fetcher, sink).CollectAsync();

        result.Degradation.ClosesAllowed.Should().BeTrue();
        result.Degradation.StopLossAllowed.Should().BeTrue();
        sink.Saved.Single().Content.Should().Contain("手仕舞い（Close）と損切りは止まっていない");
    }

    // ニュース系の片方が生きていれば必須条件は満たされる（ADR-0020 決定2「いずれか 1 つ以上」）。
    [Fact]
    public async Task ニュース系の片方が生きていれば縮退しない()
    {
        var sink = new InMemoryKnowledgeBaseSink();
        var fetcher = new StubFetcher(new SourceFetchResult(
            [News("google-news", "見出しのみ")],
            [SourceOutcome.Failed("finnhub-news"), SourceOutcome.Ok("google-news")]));

        var result = await Create(fetcher, sink).CollectAsync();

        result.Degradation.NewsOutage.Should().BeFalse();
        result.Degradation.BlocksNewEntries.Should().BeFalse();
        sink.Saved.Select(s => s.Source).Should().Equal("google-news");
    }
}
