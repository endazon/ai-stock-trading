using AiStockTrading.InformationCollection.Application.Adapters;
using AiStockTrading.InformationCollection.Application.Ports;
using AiStockTrading.InformationCollection.Application.Services;
using AiStockTrading.InformationCollection.Application.State;
using AiStockTrading.InformationCollection.Domain;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.InformationCollection.Application.Tests;

// FR-01, ADR-0003, ADR-0004: 収集オーケストレーション（許可リスト選別・サニタイズ・KB 保存・件数）を検証する。
public class InformationCollectionServiceTests
{
    private static RawInformationItem News(string source, string content) =>
        new(InformationKind.News, source, "AAPL", "見出し", content, DateTimeOffset.UtcNow);

    private sealed class StubSource(IReadOnlyList<RawInformationItem> items) : IInformationSource
    {
        public Task<IReadOnlyList<RawInformationItem>> FetchAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(items);
    }

    [Fact]
    public async Task 許可ソースは正規化サニタイズされ_KB_に保存される()
    {
        var sink = new InMemoryKnowledgeBaseSink();
        var svc = new InformationCollectionService(
            new StubSource([News("finnhub", "好決算のニュース")]), sink, SourceAllowlist.Default);

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
        var svc = new InformationCollectionService(
            new StubSource([News("random-blog", "怪しい投稿")]), sink, SourceAllowlist.Default);

        var result = await svc.CollectAsync();

        result.ItemCount.Should().Be(0);
        sink.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task 許可と非許可が混在すると許可分だけ保存される()
    {
        var sink = new InMemoryKnowledgeBaseSink();
        var svc = new InformationCollectionService(
            new StubSource([News("finnhub", "A"), News("random-blog", "B"), News("edinet", "C")]),
            sink, SourceAllowlist.Default);

        var result = await svc.CollectAsync();

        result.ItemCount.Should().Be(2);
        sink.Saved.Select(s => s.Source).Should().BeEquivalentTo(new[] { "finnhub", "edinet" });
    }

    [Fact]
    public async Task 収集ゼロなら_KB_保存は呼ばれず件数0()
    {
        var sink = new InMemoryKnowledgeBaseSink();
        var svc = new InformationCollectionService(new NoOpInformationSource(), sink, SourceAllowlist.Default);

        var result = await svc.CollectAsync();

        result.ItemCount.Should().Be(0);
        sink.Saved.Should().BeEmpty();
    }
}
