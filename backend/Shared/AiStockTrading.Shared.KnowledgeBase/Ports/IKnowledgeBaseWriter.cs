namespace AiStockTrading.Shared.KnowledgeBase.Ports;

// FR-08, IADR-0069: KB への保存ポート（platform DocumentService の利用境界）。
// 実装は fail-safe（非 2xx・例外・タイムアウトを未保存に倒し、業務経路へ例外を伝播しない）。
// 既定は no-op（NoOpKnowledgeBaseWriter）。実接続は KnowledgeBase:Documents:BaseUrl の設定で opt-in。
public interface IKnowledgeBaseWriter
{
    Task<KnowledgeWriteResult> SaveAsync(KnowledgeDocument document, CancellationToken cancellationToken = default);
}
