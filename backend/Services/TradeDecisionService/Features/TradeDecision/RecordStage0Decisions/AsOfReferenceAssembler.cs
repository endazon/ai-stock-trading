using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;

/// <summary>判断時点の参考情報（(b)）と、取れなかった源・過去分の無い源。</summary>
/// <param name="Contexts">本番の取得規則で組んだ参考情報（銘柄の文書の後に市場の文書）。</param>
/// <param name="FailedSources">取得に失敗した・構成されていない<b>必須の源</b>（1 つでもあれば (b) は再構成できない）。</param>
/// <param name="MissingSources">本番には在るが過去分を取れない源（記録に「欠けた源」として残す。利用者裁定 2026-10-13）。</param>
public sealed record AsOfReferenceSet(
    IReadOnlyList<RetrievedContext> Contexts,
    IReadOnlyList<string> FailedSources,
    IReadOnlyList<string> MissingSources)
{
    /// <summary>必須の源がすべて取れたか。</summary>
    public bool IsComplete => FailedSources.Count == 0;

    /// <summary>(b) の申告の理由欄へ添える注記（失敗した源と欠けた源）。</summary>
    public string Note()
    {
        var parts = new List<string>(2);
        if (FailedSources.Count > 0)
            parts.Add($"取れなかった源: {string.Join(", ", FailedSources)}");
        if (MissingSources.Count > 0)
            parts.Add($"欠けた源: {string.Join(", ", MissingSources)}");
        return string.Join("／", parts);
    }
}

// FR-01, FR-08, FR-15, ADR-0036 決定1, #1308, IADR-0528 決定4: as-of の参考情報（(b)）を、本番の取得規則で組む。
//
// 本番（`KnowledgeBaseRetrievalContextProvider`）の規則のうち、過去時点で再現できる部分をそのまま写す:
//   - 銘柄の文書と市場の文書を**別々に**引く（KB 検索の `symbol` 属性と `coverage=market` の 2 系統）。
//   - どちらも**新しい順**に**上位 TopK 件**。
//   - **新しさの足切り**（MaxAge。既定 168 時間）: 発行時刻が `t − MaxAge` より古いものを入れない。
//   - 出所タグで出典を限定する（`RetrievalSourcePolicy`）。
// 🔴 **窓は両端で切る**（仕様書「母集合」の窓の表）: `t` より後（未来）と `t − MaxAge` より前（古い）を落とし、境界ちょうどは残す。
//   後の端だけでは古い記事が残り（本番は落とす）、前の端だけでは未来の記事が残る（先読み）。
// 🔴 **発行時刻の無い文書は落とさずに渡す**（`AsOfDecisionInput` が「時点に置けない」として (b) を再構成不可にする）。
//   ここで黙って捨てると、時点に置けなかった資料が在った事実が記録から消える。
//   **上位 K 件の枠の外で必ず渡す**（#1323 の AI レビュー）: 新しい順に並べてから K 件で切ると、時刻つきの文書が K 件以上ある日に
//   時刻なしの文書が枠から押し出され、(b) が「再構成できた」と誤って申告される。
//
// 利用者裁定 2026-10-13（#1219 のコメント）: 3 源（finnhub-news・sec-edgar・fred）の過去分をこの規則で組めば再構成できたとみなす。
// 必須の源が 1 つでも取れない・構成されていなければ、(b) は再構成できない（`FailedSources`）。
public sealed class AsOfReferenceAssembler
{
    /// <summary>利用者裁定 2026-10-13 が (b) の再構成に求める源（収集の源の名前）。</summary>
    public static IReadOnlyList<string> RequiredSources { get; } = ["finnhub-news", "sec-edgar", "fred"];

    /// <summary>
    /// 本番には在るが過去分を取れない源（記録に「欠けた源」として残す）。経路B の収集源（finnhub・google-news）と、
    /// KB に入る確定報告書（過去の判断の RAG）。
    /// </summary>
    public static IReadOnlyList<string> MissingSourcesInProduction { get; } = ["google-news", "finnhub（現在値の文書）", "report（確定報告書の RAG）"];

    private readonly IReadOnlyList<IAsOfReferenceFeed> _feeds;
    private readonly int _topK;
    private readonly TimeSpan _maxAge;
    private readonly RetrievalSourcePolicy _sourcePolicy;

    public AsOfReferenceAssembler(
        IEnumerable<IAsOfReferenceFeed> feeds, int topK, TimeSpan maxAge, RetrievalSourcePolicy? sourcePolicy = null)
    {
        ArgumentNullException.ThrowIfNull(feeds);
        ArgumentOutOfRangeException.ThrowIfLessThan(topK, 1);
        if (maxAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxAge), maxAge, "新しさの足切りは正の長さでなければならない。");

        _feeds = [.. feeds];
        _topK = topK;
        _maxAge = maxAge;
        _sourcePolicy = sourcePolicy ?? RetrievalSourcePolicy.Default;
    }

    public async Task<AsOfReferenceSet> AssembleAsync(
        string symbol, Market market, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        var from = at - _maxAge;
        var failed = new List<string>();
        var symbolDocs = new List<RetrievedContext>();
        var marketDocs = new List<RetrievedContext>();

        foreach (var required in RequiredSources)
        {
            if (!_feeds.Any(f => string.Equals(f.SourceName, required, StringComparison.Ordinal)))
                failed.Add($"{required}（未構成）");
        }

        foreach (var feed in _feeds)
        {
            IReadOnlyList<RetrievedContext>? fetched;
            try
            {
                fetched = await feed.FetchAsync(symbol, market, from, at, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                fetched = null;
            }

            if (fetched is null)
            {
                failed.Add(feed.SourceName);
                continue;
            }

            (feed.Coverage == AsOfReferenceCoverage.Symbol ? symbolDocs : marketDocs).AddRange(fetched);
        }

        var contexts = Select(symbolDocs, from, at).Concat(Select(marketDocs, from, at)).ToList();
        return new AsOfReferenceSet(_sourcePolicy.Filter(contexts), failed, MissingSourcesInProduction);
    }

    // 窓の両端で切り、新しい順に上位 TopK 件。発行時刻の無い文書は枠の外で後ろへ全部足す（`AsOfDecisionInput` が申告に反映する）。
    private IEnumerable<RetrievedContext> Select(IEnumerable<RetrievedContext> docs, DateTimeOffset from, DateTimeOffset at)
    {
        var list = docs.ToList();
        var dated = list
            .Where(d => d.PublishedAt is { } p && p >= from && p <= at)
            .OrderByDescending(d => d.PublishedAt!.Value)
            .Take(_topK);
        return dated.Concat(list.Where(d => d.PublishedAt is null));
    }
}
