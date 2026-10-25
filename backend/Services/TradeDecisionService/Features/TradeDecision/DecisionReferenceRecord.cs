using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision;

// FR-06, FR-08, FR-11, #1356, IADR-0541 決定 1: 判断のプロンプトが見た参考情報（RAG）を、判断の記録へ載せる形へ写す（純関数）。
// 🔴 本文抜粋（RetrievedContext.Text）は写さない。表題・出典 URI・発行時刻・出所タグだけを、上限で切って残す。
public static class DecisionReferenceRecord
{
    public static DecisionReferences From(string retrievalState, IReadOnlyList<RetrievedContext> seen)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(retrievalState);
        ArgumentNullException.ThrowIfNull(seen);

        return new DecisionReferences(
            retrievalState,
            [
                .. seen.Take(DecisionReferences.MaxItems).Select(c => new DecisionReference(
                    Truncate(c.Title, DecisionReferences.MaxTitleLength),
                    c.SourceUri is { Length: > 0 and <= DecisionReferences.MaxSourceUriLength } uri ? uri : null,
                    c.PublishedAt,
                    c.Tags.Count > 0 ? [.. c.Tags] : null)),
            ]);
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];
}
