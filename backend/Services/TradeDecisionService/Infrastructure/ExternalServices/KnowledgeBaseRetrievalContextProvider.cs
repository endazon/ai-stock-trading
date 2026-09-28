using System.Globalization;
using AiStockTrading.Shared.KnowledgeBase;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using TradeDecisionService.Features.TradeDecision;
using Microsoft.Extensions.Logging;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-08, FR-04, IADR-0069/0072: 判断文脈への RAG 取得アダプタ。#18 の RAG 取得ポート IKnowledgeBaseSearch を包み、
// トリガー（銘柄/市場）＋確定日報方針の要約から検索クエリを組み、KnowledgeHit を Application 側 RetrievedContext へ写像する。
// 安全既定: search は KnowledgeBase:Search:BaseUrl 未設定なら #18 の NoOpKnowledgeBaseSearch（空）＝文脈なし＝現行動作。
// fail-safe: IKnowledgeBaseSearch は非 2xx・例外・タイムアウトを空へ倒す（IADR-0069）。判断側でも例外を握るため二重に安全。
//
// FR-08, #1083, IADR-0453（IADR-0072 決定5 の「Scope は送らない」を置き換える）:
//   - Scope（project = ai-stock-trading）はアダプタ（HttpKnowledgeBaseSearch）が全検索に載せる（決定1）。
//   - 検索は 2 本（決定2・3）: ① 銘柄の文書（attributes["symbol"] = トリガーの銘柄）、
//     ② 銘柄を持たない文書（市場全体のニュース・マクロ・収集状態。銘柄フィルタなしで引き、symbol を持つ文書を落とす）。
//     ① だけにすると google-news・FRED・BoJ・collection-status が全部落ち、#1078 の目的（ニュースを判断へ届ける）に反する。
//   - 両方とも新しい順（SortBy=updated。決定4）。
//   - 新しさの足切り（決定5）: PublishedAt が now − maxAge より古い文書と、PublishedAt の無い文書は入れない。
public sealed class KnowledgeBaseRetrievalContextProvider(
    IKnowledgeBaseSearch search,
    int topK,
    TimeSpan maxAge,
    TimeProvider timeProvider,
    ILogger<KnowledgeBaseRetrievalContextProvider> logger) : IRetrievalContextProvider
{
    // #1083, IADR-0453 決定5: 新しさの足切りの既定（168 時間＝7 日）。根拠は IADR-0453。
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(168);

    // 足切りの上限（時間）。これを超える値は設定の誤りとみなし既定へ倒す（TimeSpan の桁あふれも防ぐ）。
    private const double MaxConfigurableHours = 24 * 366 * 10;

    // FR-08, #1083, IADR-0453 決定5: `Retrieval:MaxAgeHours` を読む。空・不正・非正・上限超は既定（168 時間）。
    public static TimeSpan ParseMaxAge(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours)
            && hours > 0 && hours <= MaxConfigurableHours
            ? TimeSpan.FromHours(hours)
            : DefaultMaxAge;

    public async Task<IReadOnlyList<RetrievedContext>> GetContextAsync(
        DecisionTrigger trigger, DailyPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(policy);

        var summary = Summarize(policy);

        // ① 銘柄の文書。
        var symbolQuery = new KnowledgeQuery(
            $"{trigger.Symbol} {trigger.Market} {summary}".Trim(),
            topK,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [KnowledgeSearchAttributes.Symbol] = trigger.Symbol,
            },
            KnowledgeSearchSorts.Updated);

        // ② 銘柄を持たない文書。基盤は「属性が無い」を条件にできないため、フィルタなしで引いて後段で落とす。
        // クエリに銘柄を入れない（入れると落とす側の銘柄文書へ候補が寄る）。
        var marketQuery = new KnowledgeQuery(
            $"{trigger.Market} {summary}".Trim(),
            topK,
            AttributeFilters: null,
            KnowledgeSearchSorts.Updated);

        var symbolHits = await search.SearchAsync(symbolQuery, cancellationToken).ConfigureAwait(false);
        var marketHits = await search.SearchAsync(marketQuery, cancellationToken).ConfigureAwait(false);

        var cutoff = timeProvider.GetUtcNow() - maxAge;
        var hits = symbolHits
            .Concat(marketHits.Where(h => string.IsNullOrWhiteSpace(h.Symbol)))
            .Where(h => h.PublishedAt is { } publishedAt && publishedAt >= cutoff)
            .ToList();

        var dropped = symbolHits.Count + marketHits.Count - hits.Count;
        if (hits.Count == 0)
        {
            if (dropped > 0)
                logger.LogDebug("RAG 取得: {Symbol} の候補 {Dropped} 件はすべて足切り（銘柄違い・古い・発行時刻なし）で除外した。", trigger.Symbol, dropped);
            return [];
        }

        logger.LogDebug("RAG 取得: {Symbol} に対し {Count} 件の参考情報を判断文脈へ注入する（除外 {Dropped} 件）。", trigger.Symbol, hits.Count, dropped);
        return hits
            // FR-04, #252, IADR-0169 決定2: **出所タグをそのまま運ぶ**（出典限定の判定に使う）。
            // ここで絞り込まないのは、守る対象が「注入点」であって特定の provider ではないためである
            // （絞り込みは TradeDecisionService 側で行う）。
            .Select(h => new RetrievedContext(h.DocumentTitle, h.Text, h.SourceUri, h.Score, h.Tags, h.PublishedAt))
            .ToList();
    }

    // 検索クエリに載せる方針要約の上限。長文方針でクエリが冗長化しないよう先頭を切り出す（実関連度検証は #82 系）。
    private const int MaxSummaryChars = 500;

    // 収集情報・判断根拠は銘柄・当日方針に紐づくため、銘柄・市場・方針で最小の関連検索クエリを組む（IADR-0072 決定5）。
    private static string Summarize(DailyPolicy policy) =>
        policy.Summary.Length > MaxSummaryChars
            ? policy.Summary[..MaxSummaryChars]
            : policy.Summary;
}
