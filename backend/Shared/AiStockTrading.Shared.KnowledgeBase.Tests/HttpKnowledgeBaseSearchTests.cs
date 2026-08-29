using System.Net;
using System.Text.Json;
using AiStockTrading.Shared.KnowledgeBase;
using AiStockTrading.Shared.KnowledgeBase.Adapters;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.Shared.KnowledgeBase.Tests;

// FR-08, IADR-0069: 取得 HTTP アダプタ（POST /search）の写像・fail-safe を検証する。
public class HttpKnowledgeBaseSearchTests
{
    private static HttpKnowledgeBaseSearch CreateSearch(StubHttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://retrieval") };
        return new HttpKnowledgeBaseSearch(http, NullLogger<HttpKnowledgeBaseSearch>.Instance);
    }

    [Fact]
    public async Task 検索結果をKnowledgeHitへ写像する()
    {
        var docId = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new
        {
            results = new[]
            {
                new
                {
                    chunkId = Guid.NewGuid(),
                    documentId = docId,
                    documentTitle = "日報 2026-07-10",
                    text = "含み益は 3.2%。",
                    score = 0.87f,
                    markdownUri = "storage://reports/2026-07-10.md",
                    attributes = new Dictionary<string, string>(),
                    tags = new[] { "report", "daily" },
                },
            },
            totalHits = 1,
            elapsedMs = 12,
        });
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, json);
        var search = CreateSearch(handler);

        var hits = await search.SearchAsync(new KnowledgeQuery("直近の含み益", TopK: 5));

        hits.Should().HaveCount(1);
        hits[0].DocumentId.Should().Be(docId);
        hits[0].DocumentTitle.Should().Be("日報 2026-07-10");
        hits[0].Text.Should().Be("含み益は 3.2%。");
        hits[0].Score.Should().BeApproximately(0.87, 0.001);
        hits[0].SourceUri.Should().Be("storage://reports/2026-07-10.md");
        hits[0].Tags.Should().Contain(["report", "daily"]);
        handler.LastRequestUri.Should().Be("http://retrieval/search");
    }

    // FR-08, #568: 供給側（platform 検索応答）の attributes.publishedAt を KnowledgeHit.PublishedAt へ写像する
    // （対の肯定形。IADR-0247 残余リスクの解消・IADR-0270）。
    [Fact]
    public async Task publishedAt属性をKnowledgeHitのPublishedAtへ写像する()
    {
        var expected = new DateTimeOffset(2026, 8, 20, 9, 30, 0, TimeSpan.Zero);
        var json = JsonSerializer.Serialize(new
        {
            results = new[]
            {
                new
                {
                    chunkId = Guid.NewGuid(),
                    documentId = Guid.NewGuid(),
                    documentTitle = "重要ニュース",
                    text = "本文",
                    score = 0.5f,
                    markdownUri = (string?)null,
                    attributes = new Dictionary<string, string> { ["publishedAt"] = expected.ToString("O") },
                    tags = new[] { "google-news" },
                },
            },
            totalHits = 1,
            elapsedMs = 3,
        });
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, json);
        var search = CreateSearch(handler);

        var hits = await search.SearchAsync(new KnowledgeQuery("q"));

        hits.Should().ContainSingle().Which.PublishedAt.Should().Be(expected);
    }

    // FR-08, #568: 対の否定形（保守側既定）。attributes に publishedAt が無い／解釈できない値は
    // すべて null に倒す（捏造しない。ScreeningContextPlanner 段③の最古扱いへつながる）。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-date")]
    public async Task publishedAt属性が無いか解釈不能ならPublishedAtはnullに倒す(string? rawValue)
    {
        var attributes = rawValue is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["publishedAt"] = rawValue };
        var json = JsonSerializer.Serialize(new
        {
            results = new[]
            {
                new
                {
                    chunkId = Guid.NewGuid(),
                    documentId = Guid.NewGuid(),
                    documentTitle = "発行時刻不明の記事",
                    text = "本文",
                    score = 0.5f,
                    markdownUri = (string?)null,
                    attributes,
                    tags = Array.Empty<string>(),
                },
            },
            totalHits = 1,
            elapsedMs = 1,
        });
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, json);
        var search = CreateSearch(handler);

        var hits = await search.SearchAsync(new KnowledgeQuery("q"));

        hits.Should().ContainSingle().Which.PublishedAt.Should().BeNull();
    }

    [Fact]
    public async Task クエリとTopKを本文に写像する()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"results":[],"totalHits":0,"elapsedMs":1}""");
        var search = CreateSearch(handler);

        await search.SearchAsync(new KnowledgeQuery("振り返り", TopK: 7));

        var root = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        root.GetProperty("query").GetString().Should().Be("振り返り");
        root.GetProperty("topK").GetInt32().Should().Be(7);
    }

    [Fact]
    public async Task 非2xxは空結果に倒す()
    {
        var handler = StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError);
        var search = CreateSearch(handler);

        var hits = await search.SearchAsync(new KnowledgeQuery("q"));

        hits.Should().BeEmpty();
    }

    [Fact]
    public async Task 送信例外は空結果に倒す()
    {
        var handler = StubHttpMessageHandler.Throws();
        var search = CreateSearch(handler);

        var hits = await search.SearchAsync(new KnowledgeQuery("q"));

        hits.Should().BeEmpty();
    }
}
