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

    // FR-08, #1083, IADR-0454 決定1・4: 送信 JSON は基盤 `SearchRequest` の形に合わせる。
    // Scope = AccessScope(Filters=[AttributeFilter(Key, AllowedValues)], GrantsAccess)。
    // 🔴 Scope が無い・GrantsAccess が true でないと基盤は 200＋空を返す（deny-by-default）。
    [Fact]
    public async Task Scopeはproject属性の絞り込みとGrantsAccessを基盤の型の形で送る()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"results":[],"totalHits":0,"elapsedMs":1}""");
        var search = CreateSearch(handler);

        await search.SearchAsync(new KnowledgeQuery("q"));

        var scope = JsonDocument.Parse(handler.LastRequestBody!).RootElement.GetProperty("scope");
        scope.GetProperty("grantsAccess").GetBoolean().Should().BeTrue();
        var filters = scope.GetProperty("filters").EnumerateArray().ToList();
        filters.Should().ContainSingle();
        filters[0].GetProperty("key").GetString().Should().Be("project");
        filters[0].GetProperty("allowedValues").EnumerateArray().Select(v => v.GetString())
            .Should().Equal("ai-stock-trading");
        // 基盤の AccessScope に在るが送らないもの（選言）と、AttributeFilter に無いものを足さない。
        scope.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["filters", "grantsAccess"]);
        filters[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["key", "allowedValues"]);
    }

    [Fact]
    public async Task 銘柄フィルタと並び順を基盤SearchRequestのフィールド名で送り基盤に無いフィールドは送らない()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"results":[],"totalHits":0,"elapsedMs":1}""");
        var search = CreateSearch(handler);

        await search.SearchAsync(new KnowledgeQuery(
            "AAPL 決算", TopK: 5,
            AttributeFilters: new Dictionary<string, string> { [KnowledgeSearchAttributes.Symbol] = "AAPL" },
            SortBy: KnowledgeSearchSorts.Updated));

        var root = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        root.GetProperty("attributeFilters").GetProperty("symbol").GetString().Should().Be("AAPL");
        root.GetProperty("sortBy").GetString().Should().Be("updated");
        // 基盤 SearchRequest(Query, TopK, AttributeFilters, Scope, Mode, SortBy) の部分集合（Mode は既定のまま送らない）。
        root.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(["query", "topK", "attributeFilters", "scope", "sortBy"]);
    }

    [Fact]
    public async Task 並び順の値は基盤のSearchSortsと同じ文字列()
    {
        KnowledgeSearchSorts.Updated.Should().Be("updated");
        KnowledgeSearchSorts.Relevance.Should().Be("relevance");

        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"results":[],"totalHits":0,"elapsedMs":1}""");
        await CreateSearch(handler).SearchAsync(new KnowledgeQuery("q"));

        // 未指定は null（基盤で関連度順へ縮退する＝従来の既定）。
        JsonDocument.Parse(handler.LastRequestBody!).RootElement.GetProperty("sortBy").ValueKind
            .Should().Be(JsonValueKind.Null);
    }

    // FR-08, #1083: 未許可（基盤は 200＋空で返す）は空に倒す。
    [Fact]
    public async Task 未許可で基盤が空を返したら空結果に倒す()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"results":[],"totalHits":0,"elapsedMs":0}""");

        var hits = await CreateSearch(handler).SearchAsync(new KnowledgeQuery("q"));

        hits.Should().BeEmpty();
    }

    // FR-08, #1083, IADR-0454 決定3: 属性 symbol を KnowledgeHit.Symbol へ写す。無い・空白は null（銘柄を持たない文書）。
    [Theory]
    [InlineData("AAPL", "AAPL")]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    public async Task symbol属性をKnowledgeHitのSymbolへ写像する(string? rawValue, string? expected)
    {
        var attributes = rawValue is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["Symbol"] = rawValue };
        var json = JsonSerializer.Serialize(new
        {
            results = new[]
            {
                new
                {
                    chunkId = Guid.NewGuid(),
                    documentId = Guid.NewGuid(),
                    documentTitle = "t",
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

        var hits = await CreateSearch(handler).SearchAsync(new KnowledgeQuery("q"));

        hits.Should().ContainSingle().Which.Symbol.Should().Be(expected);
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

    // T-10-2472, FR-08, FR-11, #1283: 失敗は従来どおり空の結果に倒すが、状態（Failed）と原因の符号を添える。
    // 成功の 0 件は Succeeded（失敗と区別できる）。原因に本文・URL・例外のメッセージを入れない。
    [Theory]
    [InlineData("http-500")]
    [InlineData("exception")]
    [InlineData("timeout")]
    [InlineData("empty")]
    public async Task T_10_2472_失敗は空に倒したうえで状態と原因の符号を返し成功の0件と区別する(string kind)
    {
        var handler = kind switch
        {
            "http-500" => StubHttpMessageHandler.Status(HttpStatusCode.InternalServerError),
            "exception" => StubHttpMessageHandler.Throws(),
            "timeout" => StubHttpMessageHandler.TimesOut(),
            _ => StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"results":[],"totalHits":0,"elapsedMs":1}"""),
        };

        var result = await CreateSearch(handler).SearchWithOutcomeAsync(new KnowledgeQuery("q"));

        result.Hits.Should().BeEmpty("縮退（空に倒す）は変えない");
        if (kind == "empty")
        {
            result.Outcome.Should().Be(KnowledgeSearchOutcome.Succeeded);
            result.FailureCause.Should().BeNull();
        }
        else
        {
            result.Outcome.Should().Be(KnowledgeSearchOutcome.Failed);
            result.FailureCause.Should().Be(kind == "exception" ? "exception:HttpRequestException" : kind);
        }
    }

    // T-10-2478, #1283（PR #1287 の監査 🟡4）: 呼び出し元の取り消しは打ち切り（timeout）・失敗に分類せず、OperationCanceledException を伝播する。
    [Fact]
    public async Task T_10_2478_呼び出し元の取り消しは失敗に分類せず伝播する()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"results":[],"totalHits":0,"elapsedMs":1}""");

        var act = () => CreateSearch(handler).SearchWithOutcomeAsync(new KnowledgeQuery("q"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // T-10-2472: 未構成（no-op）は NotConfigured（検索して 0 件と区別する）。
    [Fact]
    public async Task T_10_2472_未構成のKB検索は状態をNotConfiguredで返す()
    {
        var result = await new NoOpKnowledgeBaseSearch(NullLogger<NoOpKnowledgeBaseSearch>.Instance)
            .SearchWithOutcomeAsync(new KnowledgeQuery("q"));

        result.Outcome.Should().Be(KnowledgeSearchOutcome.NotConfigured);
        result.Hits.Should().BeEmpty();
    }
}
