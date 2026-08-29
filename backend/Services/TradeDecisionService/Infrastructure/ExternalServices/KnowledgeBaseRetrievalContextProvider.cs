using AiStockTrading.Shared.KnowledgeBase;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using TradeDecisionService.Features.TradeDecision;
using Microsoft.Extensions.Logging;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-08, FR-04, IADR-0069/0072: 判断文脈への RAG 取得アダプタ。#18 の RAG 取得ポート IKnowledgeBaseSearch を包み、
// トリガー（銘柄/市場）＋確定日報方針の要約から検索クエリを組み、KnowledgeHit を Application 側 RetrievedContext へ写像する。
// 安全既定: search は KnowledgeBase:Search:BaseUrl 未設定なら #18 の NoOpKnowledgeBaseSearch（空）＝文脈なし＝現行動作。
// fail-safe: IKnowledgeBaseSearch は非 2xx・例外・タイムアウトを空へ倒す（IADR-0069）。判断側でも例外を握るため二重に安全。
// IADR-0072 決定5: ABAC Scope（利用者スコープ）は #18 同様に本作業では送らない（後続）。
public sealed class KnowledgeBaseRetrievalContextProvider(
    IKnowledgeBaseSearch search,
    int topK,
    ILogger<KnowledgeBaseRetrievalContextProvider> logger) : IRetrievalContextProvider
{
    public async Task<IReadOnlyList<RetrievedContext>> GetContextAsync(
        DecisionTrigger trigger, DailyPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(policy);

        var query = new KnowledgeQuery(BuildQuery(trigger, policy), topK);
        var hits = await search.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        if (hits.Count == 0)
            return [];

        logger.LogDebug("RAG 取得: {Symbol} に対し {Count} 件の参考情報を判断文脈へ注入する。", trigger.Symbol, hits.Count);
        return hits
            // FR-04, #252, IADR-0169 決定2: **出所タグをそのまま運ぶ**（出典限定の判定に使う）。
            // ここで絞り込まないのは、守る対象が「注入点」であって特定の provider ではないためである
            // （絞り込みは TradeDecisionService 側で行う）。
            .Select(h => new RetrievedContext(h.DocumentTitle, h.Text, h.SourceUri, h.Score, h.Tags))
            .ToList();
    }

    // 検索クエリに載せる方針要約の上限。長文方針でクエリが冗長化しないよう先頭を切り出す（実関連度検証は #82 系）。
    private const int MaxSummaryChars = 500;

    // 収集情報・判断根拠は銘柄・当日方針に紐づくため、この 3 要素で最小の関連検索クエリを組む（IADR-0072 決定5）。
    private static string BuildQuery(DecisionTrigger trigger, DailyPolicy policy)
    {
        var summary = policy.Summary.Length > MaxSummaryChars
            ? policy.Summary[..MaxSummaryChars]
            : policy.Summary;
        return $"{trigger.Symbol} {trigger.Market} {summary}".Trim();
    }
}
