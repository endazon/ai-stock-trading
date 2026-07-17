using System.Net.Http.Json;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Shared.KnowledgeBase.Adapters;

// FR-08, IADR-0069 決定 1/3: platform RetrievalService の POST /search（ハイブリッド検索＋ABAC）へ問い合わせる実アダプタ。
// 当リポ DTO（KnowledgeQuery/KnowledgeHit）を platform 契約（SearchRequest/SearchResponse 形状）へ HTTP 境界の内側で写像する。
//
// fail-safe（決定 3）: 非 2xx・例外・タイムアウトはすべて空結果に倒す（RAG 文脈なしへ縮退し、判断側の可用性を守る）。
// /search はロールゲート無し（ABAC は本文 Scope で絞る）だが、本 PR は Scope を送らない（後続 #11 で利用者スコープを伝播）。
internal sealed class HttpKnowledgeBaseSearch(
    HttpClient httpClient,
    ILogger<HttpKnowledgeBaseSearch> logger)
    : IKnowledgeBaseSearch
{
    private static readonly IReadOnlyList<KnowledgeHit> Empty = [];

    // platform SearchRequest と JSON 互換の送信形状（Knowledge.Contracts に依存しない）。
    private sealed record SearchBody(
        string Query,
        int TopK,
        Dictionary<string, string>? AttributeFilters);

    // platform SearchResponse / SearchResultDto の受け皿。
    private sealed record SearchResultBody(
        Guid DocumentId,
        string DocumentTitle,
        string Text,
        float Score,
        string? MarkdownUri,
        List<string>? Tags);

    private sealed record SearchResponseBody(List<SearchResultBody>? Results);

    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(KnowledgeQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var body = new SearchBody(
            query.Query,
            query.TopK,
            query.AttributeFilters is null ? null : new Dictionary<string, string>(query.AttributeFilters, StringComparer.Ordinal));

        try
        {
            using var response = await httpClient
                .PostAsJsonAsync("/search", body, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("KB 検索に失敗（{Status}）。空結果に倒します。", (int)response.StatusCode);
                return Empty;
            }

            var dto = await response.Content
                .ReadFromJsonAsync<SearchResponseBody>(cancellationToken)
                .ConfigureAwait(false);

            if (dto?.Results is null || dto.Results.Count == 0)
                return Empty;

            return dto.Results
                .Select(r => new KnowledgeHit(
                    r.DocumentId,
                    r.DocumentTitle,
                    r.Text,
                    (double)r.Score, // platform は float スコア。KnowledgeHit は double のため明示変換（拡大・非損失）。
                    r.MarkdownUri,
                    r.Tags ?? []))
                .ToList();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("KB 検索がタイムアウト。空結果に倒します。");
            return Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "KB 検索で例外。空結果に倒します。");
            return Empty;
        }
    }
}
