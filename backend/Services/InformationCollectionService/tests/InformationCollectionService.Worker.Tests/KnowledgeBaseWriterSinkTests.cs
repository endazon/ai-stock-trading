using AiStockTrading.InformationCollection.Domain;
using AiStockTrading.InformationCollection.Worker.Composable.Adapters;
using AiStockTrading.Shared.KnowledgeBase;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.InformationCollection.Worker.Tests;

// FR-08, IADR-0069 決定 4: KnowledgeBaseWriterSink が CollectedInformation を KnowledgeDocument へ写像し、
// 共有クライアントの IKnowledgeBaseWriter へ各件を委譲することを検証する。
public class KnowledgeBaseWriterSinkTests
{
    // 保存された KnowledgeDocument を記録する偽 writer（常に成功を返す）。
    private sealed class CapturingWriter : IKnowledgeBaseWriter
    {
        public List<KnowledgeDocument> Saved { get; } = [];

        public Task<KnowledgeWriteResult> SaveAsync(KnowledgeDocument document, CancellationToken cancellationToken = default)
        {
            Saved.Add(document);
            return Task.FromResult(KnowledgeWriteResult.Ok(Guid.NewGuid()));
        }
    }

    private static CollectedInformation News(string title, string source, string? symbol) =>
        new(InformationKind.News, source, symbol, title, "本文", DateTimeOffset.Parse("2026-07-18T00:00:00Z"), "https://example.com/a");

    [Fact]
    public async Task 各収集情報を1件ずつwriterへ委譲する()
    {
        var writer = new CapturingWriter();
        var sink = new KnowledgeBaseWriterSink(writer, NullLogger<KnowledgeBaseWriterSink>.Instance);

        await sink.SaveAsync([News("A", "finnhub", "AAPL"), News("B", "sec-edgar", null)]);

        writer.Saved.Should().HaveCount(2);
        writer.Saved[0].Title.Should().Be("A");
        writer.Saved[1].Title.Should().Be("B");
    }

    [Fact]
    public async Task 写像は機密区分internalと種別源銘柄の属性タグを付与する()
    {
        var writer = new CapturingWriter();
        var sink = new KnowledgeBaseWriterSink(writer, NullLogger<KnowledgeBaseWriterSink>.Instance);

        await sink.SaveAsync([News("開示A", "sec-edgar", "MSFT")]);

        var doc = writer.Saved.Single();
        doc.Confidentiality.Should().Be(KnowledgeConfidentiality.Internal);
        doc.SourceUri.Should().Be("https://example.com/a");
        doc.Attributes!["kind"].Should().Be("News");
        doc.Attributes!["source"].Should().Be("sec-edgar");
        doc.Attributes!["symbol"].Should().Be("MSFT");
        doc.Tags.Should().Contain(["News", "sec-edgar", "MSFT"]);
    }

    [Fact]
    public async Task 銘柄なしは銘柄属性タグを付けない()
    {
        var writer = new CapturingWriter();
        var sink = new KnowledgeBaseWriterSink(writer, NullLogger<KnowledgeBaseWriterSink>.Instance);

        await sink.SaveAsync([News("マクロ", "fred", null)]);

        var doc = writer.Saved.Single();
        doc.Attributes!.ContainsKey("symbol").Should().BeFalse();
        doc.Tags.Should().NotContain(string.Empty);
    }

    [Fact]
    public async Task 空入力でも例外を投げない()
    {
        var writer = new CapturingWriter();
        var sink = new KnowledgeBaseWriterSink(writer, NullLogger<KnowledgeBaseWriterSink>.Instance);

        var act = async () => await sink.SaveAsync([]);

        await act.Should().NotThrowAsync();
        writer.Saved.Should().BeEmpty();
    }
}
