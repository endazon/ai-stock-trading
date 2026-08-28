using TradeDecisionService.Application.Ports;
using TradeDecisionService.Application.State;

namespace TradeDecisionService.Application.Adapters;

// FR-08, IADR-0072 決定4: RAG 取得の安全既定。実検索を呼ばず常に空を返す（＝参考情報なし＝現行動作）。
// 実 RAG 取得（KnowledgeBaseRetrievalContextProvider）は Worker が KnowledgeBase:Search:BaseUrl 設定時に明示配線したときのみ有効。
public sealed class NoOpRetrievalContextProvider : IRetrievalContextProvider
{
    private static readonly IReadOnlyList<RetrievedContext> Empty = [];

    public Task<IReadOnlyList<RetrievedContext>> GetContextAsync(
        DecisionTrigger trigger, DailyPolicy policy, CancellationToken cancellationToken = default) =>
        Task.FromResult(Empty);
}
