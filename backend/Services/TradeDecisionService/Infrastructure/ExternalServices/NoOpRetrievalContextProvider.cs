using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-08, IADR-0072 決定4: RAG 取得の安全既定。実検索を呼ばず常に空を返す（＝参考情報なし＝現行動作）。
// 実 RAG 取得（KnowledgeBaseRetrievalContextProvider）は Worker が KnowledgeBase:Search:BaseUrl 設定時に明示配線したときのみ有効。
public sealed class NoOpRetrievalContextProvider : IRetrievalContextProvider
{
    private static readonly IReadOnlyList<RetrievedContext> Empty = [];

    public Task<IReadOnlyList<RetrievedContext>> GetContextAsync(
        DecisionTrigger trigger, DailyPolicy policy, CancellationToken cancellationToken = default) =>
        Task.FromResult(Empty);

    // FR-08, FR-11, #1283: 未構成であることを状態で返す（判断の記録は ragContext=not-configured）。
    public Task<RetrievalResult> GetContextWithStatusAsync(
        DecisionTrigger trigger, DailyPolicy policy, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RetrievalResult(Empty, RetrievalStatus.NotConfigured));
}
