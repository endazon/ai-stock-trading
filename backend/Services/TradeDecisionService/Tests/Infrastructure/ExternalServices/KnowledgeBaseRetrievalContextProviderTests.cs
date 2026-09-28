using AiStockTrading.Shared.KnowledgeBase;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-08, IADR-0069/0072: RAG 取得アダプタが trigger+policy から検索クエリを組み、KnowledgeHit を RetrievedContext へ写像することを検証する。
// FR-08, #1083, IADR-0454: 銘柄の検索と銘柄を持たない文書の検索の 2 本・新しい順・新しさの足切りを検証する。
public class KnowledgeBaseRetrievalContextProviderTests
{
    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 7, 10), "米国株の押し目買い方針");
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 13, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Fresh = Now.AddHours(-1);

    // 検索要求を記録し、銘柄フィルタの有無で返す結果を分ける（① 銘柄の検索 / ② 銘柄を持たない文書の検索）。
    private sealed class FakeSearch(
        IReadOnlyList<KnowledgeHit>? symbolHits = null,
        IReadOnlyList<KnowledgeHit>? marketHits = null) : IKnowledgeBaseSearch
    {
        public List<KnowledgeQuery> Queries { get; } = [];

        public KnowledgeQuery SymbolQuery => Queries.Single(q => q.AttributeFilters is { Count: > 0 });

        public KnowledgeQuery MarketQuery => Queries.Single(q => q.AttributeFilters is not { Count: > 0 });

        public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(KnowledgeQuery query, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            var hits = query.AttributeFilters is { Count: > 0 } ? symbolHits : marketHits;
            return Task.FromResult(hits ?? []);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static KnowledgeBaseRetrievalContextProvider Create(
        FakeSearch search, int topK = 5, TimeSpan? maxAge = null) =>
        new(search, topK, maxAge ?? KnowledgeBaseRetrievalContextProvider.DefaultMaxAge, new FixedTime(Now),
            NullLogger<KnowledgeBaseRetrievalContextProvider>.Instance);

    private static KnowledgeHit Hit(string title, DateTimeOffset? publishedAt, string? symbol = null) =>
        new(Guid.NewGuid(), title, "本文。", 0.5d, null, ["google-news"], publishedAt, symbol);

    private static Task<IReadOnlyList<RetrievedContext>> GetAsync(KnowledgeBaseRetrievalContextProvider provider) =>
        provider.GetContextAsync(DecisionTrigger.Scheduled("AAPL", Market.UnitedStates), Policy);

    [Fact]
    public async Task 銘柄の検索は銘柄と市場と方針要約から組み立てられTopKと銘柄フィルタと新しい順を渡す()
    {
        var search = new FakeSearch();

        await GetAsync(Create(search, topK: 7));

        search.Queries.Should().HaveCount(2);
        var q = search.SymbolQuery;
        q.Query.Should().Contain("AAPL");
        q.Query.Should().Contain(Policy.Summary);
        q.TopK.Should().Be(7);
        q.AttributeFilters.Should().BeEquivalentTo(new Dictionary<string, string> { ["symbol"] = "AAPL" });
        q.SortBy.Should().Be("updated");
    }

    [Fact]
    public async Task 銘柄を持たない文書の検索は銘柄フィルタなしで銘柄をクエリに入れず新しい順を渡す()
    {
        var search = new FakeSearch();

        await GetAsync(Create(search, topK: 7));

        var q = search.MarketQuery;
        q.AttributeFilters.Should().BeNull();
        q.Query.Should().NotContain("AAPL");
        q.Query.Should().Contain(Policy.Summary);
        q.Query.Should().Contain(nameof(Market.UnitedStates));
        q.TopK.Should().Be(7);
        q.SortBy.Should().Be("updated");
    }

    [Fact]
    public async Task 銘柄を持たない文書の検索から銘柄を持つ文書は除き銘柄を持たない文書だけを残す()
    {
        var search = new FakeSearch(
            symbolHits: [Hit("AAPL の決算", Fresh, "AAPL")],
            marketHits: [Hit("MSFT の決算", Fresh, "MSFT"), Hit("市場全体のニュース", Fresh), Hit("AAPL の重複", Fresh, "AAPL")]);

        var result = await GetAsync(Create(search));

        result.Select(r => r.Title).Should().Equal("AAPL の決算", "市場全体のニュース");
    }

    [Fact]
    public async Task 片方の検索が空でも他方の結果を使う()
    {
        var onlyMarket = new FakeSearch(marketHits: [Hit("市場全体のニュース", Fresh)]);
        var onlySymbol = new FakeSearch(symbolHits: [Hit("AAPL の決算", Fresh, "AAPL")]);

        (await GetAsync(Create(onlyMarket))).Should().ContainSingle().Which.Title.Should().Be("市場全体のニュース");
        (await GetAsync(Create(onlySymbol))).Should().ContainSingle().Which.Title.Should().Be("AAPL の決算");
    }

    [Fact]
    public async Task 発行時刻を持つ文書は足切りより古ければ判断文脈に入らない()
    {
        var maxAge = TimeSpan.FromHours(24);
        var search = new FakeSearch(
            symbolHits:
            [
                Hit("境界ちょうど", Now - maxAge, "AAPL"),
                Hit("境界を 1 秒過ぎた", Now - maxAge - TimeSpan.FromSeconds(1), "AAPL"),
            ],
            marketHits: [Hit("古い市場ニュース", Now.AddDays(-30)), Hit("新しい市場ニュース", Fresh)]);

        var result = await GetAsync(Create(search, maxAge: maxAge));

        result.Select(r => r.Title).Should().Equal("境界ちょうど", "新しい市場ニュース");
    }

    // FR-08, UC-01 手順 3, #1083, IADR-0454 決定5: 確定報告書（tag report・symbol なし・publishedAt なし。
    // ReportKnowledgeMapper は publishedAt を書かない）は足切りの対象外で、2 本目の検索から判断文脈へ届く。
    // 🔴 発行時刻なしを落とすと「過去の判断（RAG）」が構造的に届かなくなる。
    [Fact]
    public async Task 発行時刻を持たない確定報告書は足切りされず銘柄を持たない文書の検索から判断文脈へ届く()
    {
        var report = new KnowledgeHit(
            Guid.NewGuid(), "確定報告書 Daily 2026-07-09", "前日の判断の振り返り。", 0.7d, null, ["report"], null, null);
        var search = new FakeSearch(marketHits: [report]);

        var result = await GetAsync(Create(search, maxAge: TimeSpan.FromHours(1)));

        var context = result.Should().ContainSingle().Which;
        context.Title.Should().Be("確定報告書 Daily 2026-07-09");
        context.Tags.Should().Equal("report");
        context.PublishedAt.Should().BeNull();
    }

    // FR-08, #568: 対の否定形。KnowledgeHit.PublishedAt が無ければ RetrievedContext.PublishedAt も
    // null のまま伝播する（捏造しない・最古扱いの保守側既定へつながる）。
    [Fact]
    public async Task 検索ヒットに発行時刻が無ければRetrievedContextのPublishedAtもnullのまま()
    {
        var search = new FakeSearch(symbolHits: new[]
        {
            new KnowledgeHit(Guid.NewGuid(), "発行時刻不明の記事", "本文。", 0.5d, null, [], null, "AAPL"),
        });

        var result = await GetAsync(Create(search));

        result.Should().ContainSingle().Which.PublishedAt.Should().BeNull();
    }

    [Fact]
    public async Task 既定の足切りは168時間()
    {
        KnowledgeBaseRetrievalContextProvider.DefaultMaxAge.Should().Be(TimeSpan.FromHours(168));

        var search = new FakeSearch(symbolHits:
        [
            Hit("6 日前", Now.AddDays(-6), "AAPL"),
            Hit("8 日前", Now.AddDays(-8), "AAPL"),
        ]);

        var result = await GetAsync(Create(search));

        result.Should().ContainSingle().Which.Title.Should().Be("6 日前");
    }

    [Theory]
    [InlineData("24", 24d)]
    [InlineData("0.5", 0.5d)]
    [InlineData(null, 168d)]
    [InlineData("", 168d)]
    [InlineData("abc", 168d)]
    [InlineData("0", 168d)]
    [InlineData("-5", 168d)]
    [InlineData("NaN", 168d)]
    [InlineData("Infinity", 168d)]
    [InlineData("1e300", 168d)]
    public void 足切りの構成値は正の時間だけを受け不正値は既定へ倒す(string? raw, double expectedHours)
    {
        KnowledgeBaseRetrievalContextProvider.ParseMaxAge(raw).Should().Be(TimeSpan.FromHours(expectedHours));
    }

    [Fact]
    public async Task 検索ヒットはRetrievedContextへ写像される()
    {
        var docId = Guid.NewGuid();
        var publishedAt = Now.AddHours(-3);
        var search = new FakeSearch(symbolHits: new[]
        {
            new KnowledgeHit(docId, "決算メモ", "増収増益。", 0.91d, "kb://doc/1", ["earnings"], publishedAt, "AAPL"),
        });

        var result = await GetAsync(Create(search));

        result.Should().HaveCount(1);
        result[0].Title.Should().Be("決算メモ");
        result[0].Text.Should().Be("増収増益。");
        result[0].SourceUri.Should().Be("kb://doc/1");
        result[0].Score.Should().Be(0.91d);
        // FR-08, #568: KnowledgeHit.PublishedAt は RetrievedContext.PublishedAt へそのまま伝播する
        // （ScreeningContextAssembler が段③の並び替え鍵に使う）。
        result[0].PublishedAt.Should().Be(publishedAt);
        result[0].Tags.Should().Equal("earnings");
    }

    [Fact]
    public async Task 検索ヒットが空なら空の文脈を返す()
    {
        var provider = Create(new FakeSearch());

        var result = await provider.GetContextAsync(DecisionTrigger.Scheduled("7203", Market.Japan), Policy);

        result.Should().BeEmpty();
    }

    // IADR-0072 決定5: 長文方針でも検索クエリが冗長化しないよう、方針要約は上限（500 文字）で切り詰める。
    [Fact]
    public async Task 長文方針の検索クエリは上限で切り詰められる()
    {
        var search = new FakeSearch();
        var provider = Create(search);
        var longPolicy = new DailyPolicy(new DateOnly(2026, 7, 10), new string('方', 2000));

        await provider.GetContextAsync(DecisionTrigger.Scheduled("AAPL", Market.UnitedStates), longPolicy);

        // 銘柄・市場・区切り空白 + 上限 500 文字の要約に収まる（2000 文字の全文は載らない）。
        search.SymbolQuery.Query.Length.Should().BeLessThan(560);
        search.SymbolQuery.Query.Should().Contain("AAPL");
        search.MarketQuery.Query.Length.Should().BeLessThan(560);
    }
}
