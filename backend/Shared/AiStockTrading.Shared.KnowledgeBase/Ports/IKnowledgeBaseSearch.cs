namespace AiStockTrading.Shared.KnowledgeBase.Ports;

// FR-08, IADR-0069: RAG 取得ポート（platform RetrievalService の利用境界）。#11（TradeDecision の RAG 文脈）・
// UC-07 の振り返り参照が本ポートに乗る。本 PR では実結線せず、境界・既定 no-op・実アダプタを用意するのみ。
// 実装は fail-safe（非 2xx・例外は空結果に倒す）。実接続は KnowledgeBase:Search:BaseUrl の設定で opt-in。
public interface IKnowledgeBaseSearch
{
    Task<IReadOnlyList<KnowledgeHit>> SearchAsync(KnowledgeQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// FR-08, FR-11, #1283: 検索し、結果に状態（成功・失敗・未構成）を添えて返す。失敗は <see cref="SearchAsync"/> と同じく
    /// 空の結果に倒す（縮退は不変）。「失敗で空」と「成功して 0 件」を呼び出し側が区別するために使う。
    /// 既定は <see cref="SearchAsync"/> を包んで成功とする（失敗を区別できない実装の既定）。
    /// </summary>
    async Task<KnowledgeSearchResult> SearchWithOutcomeAsync(KnowledgeQuery query, CancellationToken cancellationToken = default) =>
        KnowledgeSearchResult.Succeeded(await SearchAsync(query, cancellationToken).ConfigureAwait(false));
}
