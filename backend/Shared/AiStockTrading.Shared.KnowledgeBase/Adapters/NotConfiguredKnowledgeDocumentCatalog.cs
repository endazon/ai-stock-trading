using AiStockTrading.Shared.KnowledgeBase.Ports;

namespace AiStockTrading.Shared.KnowledgeBase.Adapters;

// FR-08, #1028, IADR-0436 決定 2: KnowledgeBase:Documents:BaseUrl 未設定/不正のときの台帳。何も送らず NotConfigured を返す。
// 🔴 保存ポートの no-op（ログだけ出して未保存）と違い、**成功のふりも「空の台帳」のふりもしない** —— 空の一覧を返すと、
// 入れ直しは「KB に 1 件も無い」と読んで作成へ進む（構成を直した後に重複を作る）。
internal sealed class NotConfiguredKnowledgeDocumentCatalog : IKnowledgeDocumentCatalog
{
    public Task<KnowledgeCatalogListResult> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(KnowledgeCatalogListResult.NotConfigured);

    public Task<KnowledgeCatalogWriteResult> CreateAsync(KnowledgeDocument document, CancellationToken cancellationToken = default) =>
        Task.FromResult(KnowledgeCatalogWriteResult.NotConfigured);

    public Task<KnowledgeCatalogWriteResult> PutBodyAsync(Guid documentId, string body, CancellationToken cancellationToken = default) =>
        Task.FromResult(KnowledgeCatalogWriteResult.NotConfigured);
}
