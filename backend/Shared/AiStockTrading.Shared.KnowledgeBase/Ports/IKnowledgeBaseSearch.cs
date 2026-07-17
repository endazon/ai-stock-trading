namespace AiStockTrading.Shared.KnowledgeBase.Ports;

// FR-08, IADR-0069: RAG 取得ポート（platform RetrievalService の利用境界）。#11（TradeDecision の RAG 文脈）・
// UC-07 の振り返り参照が本ポートに乗る。本 PR では実結線せず、境界・既定 no-op・実アダプタを用意するのみ。
// 実装は fail-safe（非 2xx・例外は空結果に倒す）。実接続は KnowledgeBase:Search:BaseUrl の設定で opt-in。
public interface IKnowledgeBaseSearch
{
    Task<IReadOnlyList<KnowledgeHit>> SearchAsync(KnowledgeQuery query, CancellationToken cancellationToken = default);
}
