namespace AiStockTrading.Shared.KnowledgeBase.Ports;

// FR-08, #1028, IADR-0436 決定 2: 基盤の文書台帳を**保守の操作**（確定報告書の入れ直し）から使うポート。
//
// 基盤（DocumentService）には外部 ID での照会・upsert が無い（ADR-0001: 基盤は改修しない）。そこで呼び出し側が
// 「一覧から属性で探す → 無ければ作る・本文が無ければ入れる」を組み立てられるよう、口を 3 つだけ出す。
// 業務経路の保存（IKnowledgeBaseWriter）とは別のポートにする —— あちらは fail-safe で結果を 1 値に潰す契約であり、
// 変えると情報収集・確定の保存の意味が変わる。
//
// どのメソッドも例外を投げない（呼び出し元のキャンセルだけは伝播する）。結果は KnowledgeCatalogOutcome で分ける。
public interface IKnowledgeDocumentCatalog
{
    /// <summary>台帳の全文書（GET /documents）。基盤は絞り込み・ページングを持たない。</summary>
    Task<KnowledgeCatalogListResult> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>文書の作成（POST /documents・本文つき）。属性の補完は保存ポートと同じ規則。</summary>
    Task<KnowledgeCatalogWriteResult> CreateAsync(KnowledgeDocument document, CancellationToken cancellationToken = default);

    /// <summary>既存文書への本文の投入（PUT /documents/{id}/body）。基盤は所有者にしか許さない（拒否は 404）。</summary>
    Task<KnowledgeCatalogWriteResult> PutBodyAsync(Guid documentId, string body, CancellationToken cancellationToken = default);
}
