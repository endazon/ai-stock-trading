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
// FR-01, FR-02, FR-08, #1138, IADR-0474: 2 本目は目印 coverage=market で絞り、足りない間だけ旧文書を補充する（T-10-1980〜T-10-1987）。
public class KnowledgeBaseRetrievalContextProviderTests
{
    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 7, 10), "米国株の押し目買い方針");
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 13, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Fresh = Now.AddHours(-1);

    // 検索要求を記録し、絞り込みで返す結果を分ける（① 銘柄の検索〔symbol〕/ ② 銘柄を持たない文書の検索〔coverage=market。
    // #1138, IADR-0474〕/ ③ 目印を持たない旧文書の補充〔フィルタなし〕）。
    private sealed class FakeSearch(
        IReadOnlyList<KnowledgeHit>? symbolHits = null,
        IReadOnlyList<KnowledgeHit>? marketHits = null,
        IReadOnlyList<KnowledgeHit>? fallbackHits = null) : IKnowledgeBaseSearch
    {
        public List<KnowledgeQuery> Queries { get; } = [];

        public KnowledgeQuery SymbolQuery => Queries.Single(q => q.AttributeFilters?.ContainsKey("symbol") == true);

        public KnowledgeQuery MarketQuery => Queries.Single(q => q.AttributeFilters?.ContainsKey("coverage") == true);

        public KnowledgeQuery? FallbackQuery => Queries.SingleOrDefault(q => q.AttributeFilters is not { Count: > 0 });

        public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(KnowledgeQuery query, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            var hits = query.AttributeFilters switch
            {
                { } f when f.ContainsKey("symbol") => symbolHits,
                { } f when f.ContainsKey("coverage") => marketHits,
                _ => fallbackHits,
            };
            return Task.FromResult(hits ?? []);
        }
    }

    // FR-08, #1138, IADR-0474: 基盤の検索を写した偽物。単値の完全一致フィルタ（キー間 AND・キーが無い文書は除外）を掛け、
    // 索引の更新日時の新しい順に並べて TopK 件で切る（HybridSearchService の updated 並べ替えと同じ向き）。
    private sealed class PlatformLikeSearch : IKnowledgeBaseSearch
    {
        private readonly List<(KnowledgeHit Hit, IReadOnlyDictionary<string, string> Attributes, DateTimeOffset UpdatedAt)> _docs = [];

        public List<KnowledgeQuery> Queries { get; } = [];

        public void Add(KnowledgeHit hit, DateTimeOffset updatedAt, bool marketTag)
        {
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            if (hit.Symbol is { } symbol)
                attributes["symbol"] = symbol;
            if (marketTag)
                attributes["coverage"] = "market";
            _docs.Add((hit, attributes, updatedAt));
        }

        public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(KnowledgeQuery query, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            IReadOnlyList<KnowledgeHit> hits = _docs
                .Where(d => query.AttributeFilters is null
                    || query.AttributeFilters.All(f => d.Attributes.TryGetValue(f.Key, out var v) && v == f.Value))
                .OrderByDescending(d => d.UpdatedAt)
                .Take(query.TopK)
                .Select(d => d.Hit)
                .ToList();
            return Task.FromResult(hits);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static KnowledgeBaseRetrievalContextProvider Create(
        IKnowledgeBaseSearch search, int topK = 5, TimeSpan? maxAge = null) =>
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

        // ① 銘柄・② 目印つき・③ 補充（② が空なので引く）の 3 本。
        search.Queries.Should().HaveCount(3);
        var q = search.SymbolQuery;
        q.Query.Should().Contain("AAPL");
        q.Query.Should().Contain(Policy.Summary);
        q.TopK.Should().Be(7);
        q.AttributeFilters.Should().BeEquivalentTo(new Dictionary<string, string> { ["symbol"] = "AAPL" });
        q.SortBy.Should().Be("updated");
    }

    // T-10-1980, FR-01, FR-02, FR-08, #1138, IADR-0474 決定2: 2 本目は目印 coverage=market の単値フィルタで引く。
    [Fact]
    public async Task 銘柄を持たない文書の検索は目印の単値フィルタで銘柄をクエリに入れず新しい順を渡す()
    {
        var search = new FakeSearch();

        await GetAsync(Create(search, topK: 7));

        var q = search.MarketQuery;
        q.AttributeFilters.Should().BeEquivalentTo(new Dictionary<string, string> { ["coverage"] = "market" });
        q.Query.Should().NotContain("AAPL");
        q.Query.Should().Contain(Policy.Summary);
        q.Query.Should().Contain(nameof(Market.UnitedStates));
        q.TopK.Should().Be(7);
        q.SortBy.Should().Be("updated");
    }

    // 🔴 T-10-1981, FR-01, FR-02, FR-08, #1138, IADR-0474 決定2（否定形）: フィルタなしの新しい順の上位が全部銘柄つきでも、
    // 目印つきの市場全体の文書は届く。是正前（フィルタなしで引いて後段で落とす）は 0 件だった。
    [Fact]
    public async Task 上位が全部銘柄つきでも目印つきの市場全体の文書は判断文脈へ届く()
    {
        var search = new PlatformLikeSearch();
        for (var i = 0; i < 40; i++)
        {
            var symbol = i % 2 == 0 ? "AAPL" : "MSFT";
            search.Add(Hit($"{symbol} の現在値 {i}", Fresh, symbol), Fresh.AddMinutes(-i), marketTag: false);
        }
        search.Add(Hit("FOMC の結果", Fresh.AddHours(-5)), Fresh.AddHours(-5), marketTag: true);
        search.Add(Hit("日銀の政策金利", Fresh.AddHours(-6)), Fresh.AddHours(-6), marketTag: true);

        var result = await GetAsync(Create(search, topK: 5));

        result.Select(r => r.Title).Should().Contain(["FOMC の結果", "日銀の政策金利"]);
        result.Should().NotContain(r => r.Title.StartsWith("MSFT", StringComparison.Ordinal));
    }

    // T-10-1982, FR-08, UC-01 手順 3, #1138, IADR-0474 決定3: 目印つきが TopK 件に満たなければ、フィルタなし・TopK×4・新しい順で
    // 補充を引き、銘柄を持たない文書（配備前の確定報告書・市場ニュース）だけを足す。
    [Fact]
    public async Task 目印つきが足りなければ目印の無い旧文書をフィルタなしの広げた検索で補充する()
    {
        var search = new FakeSearch(
            marketHits: [Hit("目印つきの市場ニュース", Fresh)],
            fallbackHits:
            [
                Hit("MSFT の決算", Fresh, "MSFT"),
                new KnowledgeHit(Guid.NewGuid(), "確定報告書 Daily 2026-07-09", "前日の振り返り。", 0.7d, null, ["report"], null, null),
                Hit("配備前の市場ニュース", Fresh),
            ]);

        var result = await GetAsync(Create(search, topK: 5));

        var q = search.FallbackQuery!;
        q.AttributeFilters.Should().BeNull();
        q.TopK.Should().Be(20);
        q.SortBy.Should().Be("updated");
        q.Query.Should().NotContain("AAPL");
        result.Select(r => r.Title).Should().Equal("目印つきの市場ニュース", "確定報告書 Daily 2026-07-09", "配備前の市場ニュース");
    }

    // T-10-1982, #1138, IADR-0474 決定3: 補充の件数は構成の TopK が大きくても int の上限で頭打ちにする（あふれて負にしない）。
    [Theory]
    [InlineData(1, 4)]
    [InlineData(5, 20)]
    [InlineData(int.MaxValue / 4 + 1, int.MaxValue)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void 補充の件数はTopKの4倍でintの上限で頭打ち(int topK, int expected)
    {
        KnowledgeBaseRetrievalContextProvider.FallbackTopK(topK).Should().Be(expected);
    }

    // T-10-1983, #1138, IADR-0474 決定3・5: 目印つき（足切り後）が TopK 件あれば補充は引かない（要求は従来どおり 2 回）。
    [Fact]
    public async Task 目印つきがTopK件あれば補充を引かず要求は2回()
    {
        var search = new PlatformLikeSearch();
        for (var i = 0; i < 10; i++)
            search.Add(Hit($"AAPL の現在値 {i}", Fresh, "AAPL"), Fresh.AddMinutes(-i), marketTag: false);
        for (var i = 0; i < 6; i++)
            search.Add(Hit($"市場ニュース {i}", Fresh), Fresh.AddMinutes(-20 - i), marketTag: true);
        search.Add(Hit("配備前の市場ニュース", Fresh), Fresh.AddDays(-1), marketTag: false);

        var result = await GetAsync(Create(search, topK: 5));

        search.Queries.Should().HaveCount(2);
        search.Queries.Should().NotContain(q => q.AttributeFilters == null);
        result.Select(r => r.Title).Should().NotContain("配備前の市場ニュース");
        result.Count(r => r.Title.StartsWith("市場ニュース", StringComparison.Ordinal)).Should().Be(5);
    }

    // T-10-1984, #1138, IADR-0474 決定3: 補充の判定は足切りの後の件数で行う（目印つきが TopK 件あっても全部古ければ補充を引く）。
    // 補充の結果にも足切りを掛ける（補充が拾うのは配備前＝古い文書が主。独立監査 🟡: 足切りを外す変異が生き残っていた）。
    [Fact]
    public async Task 目印つきがTopK件あっても全部古ければ補充を引く()
    {
        var stale = Now.AddDays(-30);
        var search = new FakeSearch(
            marketHits: [.. Enumerable.Range(0, 5).Select(i => Hit($"古い市場ニュース {i}", stale))],
            fallbackHits: [Hit("古い配備前の市場ニュース", stale), Hit("新しい配備前の市場ニュース", Fresh)]);

        var result = await GetAsync(Create(search, topK: 5));

        search.FallbackQuery.Should().NotBeNull();
        result.Select(r => r.Title).Should().Equal("新しい配備前の市場ニュース");
    }

    // T-10-1985, #1138, IADR-0474 決定3: 目印つきと補充の両方に出た同じチャンクは 1 件にする。同じ文書の別のチャンクは別に数える。
    [Fact]
    public async Task 目印つきと補充の重複チャンクは1件にし同じ文書の別チャンクは残す()
    {
        var docId = Guid.NewGuid();
        KnowledgeHit Chunk(string text) => new(docId, "FOMC の結果", text, 0.5d, null, ["google-news"], Fresh, null);
        var search = new FakeSearch(
            marketHits: [Chunk("チャンク 1")],
            fallbackHits: [Chunk("チャンク 1"), Chunk("チャンク 2"), Chunk("チャンク 2")]);

        var result = await GetAsync(Create(search, topK: 5));

        result.Select(r => r.Text).Should().Equal("チャンク 1", "チャンク 2");
    }

    // T-10-1986, #1138, IADR-0474 決定3: 並びは「銘柄の文書 → 目印つき（基盤の順）→ 補充（基盤の順）」。
    [Fact]
    public async Task 並びは銘柄の文書から目印つき補充の順で各検索の順を保つ()
    {
        var search = new FakeSearch(
            symbolHits: [Hit("AAPL の決算", Fresh, "AAPL"), Hit("AAPL の現在値", Fresh, "AAPL")],
            marketHits: [Hit("目印つき 新", Fresh), Hit("目印つき 旧", Fresh.AddHours(-2))],
            fallbackHits: [Hit("補充 新", Fresh.AddHours(-1)), Hit("補充 旧", Fresh.AddHours(-3))]);

        var result = await GetAsync(Create(search, topK: 5));

        result.Select(r => r.Title).Should().Equal(
            "AAPL の決算", "AAPL の現在値", "目印つき 新", "目印つき 旧", "補充 新", "補充 旧");
    }

    // T-10-1987, #1138, IADR-0474 決定4: 市場側（目印つき＋補充）は TopK 件で切る。注入は全体で 2×TopK 件を超えない
    // （IADR-0313 の予算の前提を変えない）。
    [Fact]
    public async Task 市場側はTopK件で切り全体は2倍のTopKを超えない()
    {
        var search = new FakeSearch(
            symbolHits: [.. Enumerable.Range(0, 3).Select(i => Hit($"AAPL {i}", Fresh, "AAPL"))],
            marketHits: [.. Enumerable.Range(0, 2).Select(i => Hit($"目印つき {i}", Fresh))],
            fallbackHits: [.. Enumerable.Range(0, 10).Select(i => Hit($"補充 {i}", Fresh))]);

        var result = await GetAsync(Create(search, topK: 3));

        result.Select(r => r.Title).Should().Equal("AAPL 0", "AAPL 1", "AAPL 2", "目印つき 0", "目印つき 1", "補充 0");
        result.Should().HaveCountLessThanOrEqualTo(2 * 3);
    }

    [Fact]
    public async Task 補充から銘柄を持つ文書は除き銘柄を持たない文書だけを残す()
    {
        var search = new FakeSearch(
            symbolHits: [Hit("AAPL の決算", Fresh, "AAPL")],
            fallbackHits: [Hit("MSFT の決算", Fresh, "MSFT"), Hit("市場全体のニュース", Fresh), Hit("AAPL の重複", Fresh, "AAPL")]);

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
