using InformationCollectionService.Domain;
using InformationCollectionService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.KnowledgeBase;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InformationCollectionService.Tests;

// FR-01, FR-08, #1084, IADR-0456: 巡回ごとに同じ内容を KB へ保存し直さない。
// 保存に成功した内容だけを覚え、全項目のどれかが違えば別の内容として保存する。
public class KnowledgeBaseWriterSinkDedupTests
{
    // 保存を記録する偽 writer。Fail を立てると未保存を返す。
    private sealed class CapturingWriter : IKnowledgeBaseWriter
    {
        public List<KnowledgeDocument> Saved { get; } = [];
        public bool Fail { get; set; }

        public Task<KnowledgeWriteResult> SaveAsync(KnowledgeDocument document, CancellationToken cancellationToken = default)
        {
            if (Fail)
                return Task.FromResult(KnowledgeWriteResult.NotSaved);

            Saved.Add(document);
            return Task.FromResult(KnowledgeWriteResult.Ok(Guid.NewGuid()));
        }
    }

    // 時刻を手で進める TimeProvider。
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-29T00:00:00Z");

    private static CollectedInformation Item(
        InformationKind kind = InformationKind.News,
        string source = "finnhub-news",
        string? symbol = "NVDA",
        string title = "見出し",
        string content = "要約",
        DateTimeOffset? publishedAt = null,
        string? url = "https://example.com/a") =>
        new(kind, source, symbol, title, content, publishedAt ?? DateTimeOffset.Parse("2026-09-28T12:00:00Z"), url);

    private static KnowledgeBaseWriterSink NewSink(IKnowledgeBaseWriter writer, TimeProvider time) =>
        new(writer, new SavedContentFingerprints(time), NullLogger<KnowledgeBaseWriterSink>.Instance);

    [Fact]
    public async Task 同じ内容は次の巡回で保存し直さない()
    {
        var writer = new CapturingWriter();
        var sink = NewSink(writer, new ManualTimeProvider(Start));

        await sink.SaveAsync([Item()]);
        await sink.SaveAsync([Item()]);
        await sink.SaveAsync([Item()]);

        writer.Saved.Should().HaveCount(1);
    }

    [Fact]
    public async Task 同じ巡回の中の重複も1件だけ保存する()
    {
        var writer = new CapturingWriter();
        var sink = NewSink(writer, new ManualTimeProvider(Start));

        await sink.SaveAsync([Item(), Item()]);

        writer.Saved.Should().HaveCount(1);
    }

    public static TheoryData<string, CollectedInformation> OneFieldDiffers() => new()
    {
        { "種別", Item(kind: InformationKind.Disclosure) },
        { "源", Item(source: "google-news") },
        { "銘柄", Item(symbol: "AAPL") },
        { "銘柄なし", Item(symbol: null) },
        { "表題", Item(title: "別の見出し") },
        { "本文", Item(content: "別の要約") },
        { "公開時刻", Item(publishedAt: DateTimeOffset.Parse("2026-09-28T12:00:01Z")) },
        { "URL", Item(url: "https://example.com/b") },
        { "URLなし", Item(url: null) },
        { "URL空文字", Item(url: "") },
    };

    // FRED は URL が一定で値だけ変わり、現在値は URL を持たない。一部の項目で同定すると新しい観測を捨てる。
    [Theory]
    [MemberData(nameof(OneFieldDiffers))]
    public async Task 項目が1つでも違えば別の内容として保存する(string field, CollectedInformation changed)
    {
        var writer = new CapturingWriter();
        var sink = NewSink(writer, new ManualTimeProvider(Start));

        await sink.SaveAsync([Item()]);
        await sink.SaveAsync([changed]);

        writer.Saved.Should().HaveCount(2, $"{field} が違う内容は保存済みと見なさない");
    }

    [Fact]
    public void 項目の境目がずれた内容は同じ指紋にならない()
    {
        SavedContentFingerprints.Of(Item(title: "ab", content: "c"))
            .Should().NotBe(SavedContentFingerprints.Of(Item(title: "a", content: "bc")));
        SavedContentFingerprints.Of(Item(url: null))
            .Should().NotBe(SavedContentFingerprints.Of(Item(url: "")));
    }

    [Fact]
    public void 同じ時点は時差の表記が違っても同じ指紋になる()
    {
        SavedContentFingerprints.Of(Item(publishedAt: DateTimeOffset.Parse("2026-09-28T21:00:00+09:00")))
            .Should().Be(SavedContentFingerprints.Of(Item(publishedAt: DateTimeOffset.Parse("2026-09-28T12:00:00Z"))));
    }

    [Fact]
    public async Task 保存できなかった内容は覚えず次の巡回で送り直す()
    {
        var writer = new CapturingWriter { Fail = true };
        var sink = NewSink(writer, new ManualTimeProvider(Start));

        await sink.SaveAsync([Item()]);
        writer.Fail = false;
        await sink.SaveAsync([Item()]);
        await sink.SaveAsync([Item()]);

        writer.Saved.Should().HaveCount(1);
    }

    [Fact]
    public async Task 保持期間が過ぎた内容は保存し直す()
    {
        var writer = new CapturingWriter();
        var time = new ManualTimeProvider(Start);
        var sink = NewSink(writer, time);

        await sink.SaveAsync([Item()]);
        time.Now = Start + SavedContentFingerprints.Retention - TimeSpan.FromSeconds(1);
        await sink.SaveAsync([Item()]);
        writer.Saved.Should().HaveCount(1, "保持期間の内側では送らない");

        time.Now = Start + SavedContentFingerprints.Retention;
        await sink.SaveAsync([Item()]);
        writer.Saved.Should().HaveCount(2, "保持期間を過ぎたら送り直す");
    }

    [Fact]
    public void 上限を超えたら古い指紋から捨てる()
    {
        var fingerprints = new SavedContentFingerprints(new ManualTimeProvider(Start));

        for (var i = 0; i <= SavedContentFingerprints.Capacity; i++)
            fingerprints.Add((UInt128)i);

        fingerprints.Count.Should().Be(SavedContentFingerprints.Capacity);
        fingerprints.Contains(0).Should().BeFalse("最も古い指紋が捨てられる");
        fingerprints.Contains(1).Should().BeTrue();
        fingerprints.Contains(SavedContentFingerprints.Capacity).Should().BeTrue();
    }

    [Fact]
    public void 同じ指紋を重ねて覚えても1件に数える()
    {
        var fingerprints = new SavedContentFingerprints(new ManualTimeProvider(Start));

        fingerprints.Add(7);
        fingerprints.Add(7);

        fingerprints.Count.Should().Be(1);
    }
}
