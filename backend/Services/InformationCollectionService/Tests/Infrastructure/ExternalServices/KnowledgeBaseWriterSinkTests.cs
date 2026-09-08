using InformationCollectionService.Domain;
using InformationCollectionService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.KnowledgeBase;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InformationCollectionService.Tests;

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
    public async Task 写像は機密区分internalと種別源の属性タグを付与し銘柄は属性のみに置く()
    {
        var writer = new CapturingWriter();
        var sink = new KnowledgeBaseWriterSink(writer, NullLogger<KnowledgeBaseWriterSink>.Instance);

        await sink.SaveAsync([News("開示A", "sec-edgar", "MSFT")]);

        var doc = writer.Saved.Single();
        doc.Confidentiality.Should().Be(KnowledgeConfidentiality.Internal);
        doc.SourceUri.Should().Be("https://example.com/a");
        doc.Attributes!["kind"].Should().Be("News");
        doc.Attributes!["source"].Should().Be("sec-edgar");
        // FR-01, FR-08, #705, IADR-0315: 銘柄は attributes には残すが、タグには載せない
        // （タグは platform 側の辞書検証を通る静的語彙に閉じる必要がある。監視銘柄は動的集合）。
        doc.Attributes!["symbol"].Should().Be("MSFT");
        doc.Tags.Should().Contain(["News", "sec-edgar"]);
    }

    // FR-01, FR-08, #705, IADR-0315: 否定形——銘柄コードはタグ集合へ一切現れない
    // （属性 attributes["symbol"] にのみ現れる。絞り込み手段は失われない）。
    [Fact]
    public async Task 銘柄はタグに含まれず属性にのみ含まれる()
    {
        var writer = new CapturingWriter();
        var sink = new KnowledgeBaseWriterSink(writer, NullLogger<KnowledgeBaseWriterSink>.Instance);

        await sink.SaveAsync([News("開示A", "sec-edgar", "MSFT")]);

        var doc = writer.Saved.Single();
        doc.Tags.Should().NotContain("MSFT");
        doc.Attributes!["symbol"].Should().Be("MSFT");
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

    // FR-01, FR-08, #705, IADR-0315: プロパティベース——監視銘柄をどれだけ追加しても（＝任意の Symbol
    // 文字列に対しても）、タグ集合は Kind・Source の静的語彙（KnowledgeTagVocabulary）の部分集合に閉じる。
    // 監視銘柄コードは運用中に無限に増える動的集合であり、タグへ載せると platform 側の辞書検証（未登録は
    // 400。MSP#635）を構造的に満たせない——本テストはその再発を防ぐ不変条件を固定する。
    [Fact]
    public async Task 任意の銘柄に対してタグ集合はKindとSourceの静的語彙に閉じる()
    {
        var random = new Random(705);
        var writer = new CapturingWriter();
        var sink = new KnowledgeBaseWriterSink(writer, NullLogger<KnowledgeBaseWriterSink>.Instance);
        var staticVocabulary = KnowledgeTagVocabulary.CollectionKinds
            .Concat(KnowledgeTagVocabulary.CollectionSources)
            .ToHashSet(StringComparer.Ordinal);

        for (var i = 0; i < 50; i++)
        {
            var symbol = RandomSymbol(random);
            await sink.SaveAsync([News($"件名{i}", "finnhub", symbol)]);
        }

        writer.Saved.Should().HaveCount(50);
        foreach (var doc in writer.Saved)
        {
            doc.Tags.Should().BeSubsetOf(staticVocabulary,
                "タグは Kind・Source の静的語彙に閉じること（監視銘柄の増加でタグ辞書検証が破綻しない）");
        }
    }

    private static string RandomSymbol(Random random)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var length = random.Next(1, 10);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = alphabet[random.Next(alphabet.Length)];
        return new string(chars);
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
