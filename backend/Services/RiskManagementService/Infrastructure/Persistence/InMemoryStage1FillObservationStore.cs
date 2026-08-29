using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Domain;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-20, FR-12, #386, IADR-0149: Stage 1 の約定観測ログのインメモリ実装（ユニット試験・ローカル実行用）。
// 本番配線は EfStage1FillObservationStore。未記録＝0 件＝昇格しない安全側。
public sealed class InMemoryStage1FillObservationStore : IStage1FillObservationStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Stage1FillObservation> _observations = [];

    public void Record(Stage1FillObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (_gate)
        {
            // DecisionId で冪等（1 注文 1 行）。分割約定の続報は先着を保つ。
            _observations.TryAdd(observation.DecisionId, observation);
        }
    }

    public int GetTradeCount()
    {
        lock (_gate)
        {
            return Stage1Aggregation.CountTrades(_observations.Values);
        }
    }

    public void ResetWindow()
    {
        lock (_gate)
        {
            _observations.Clear();
        }
    }
}
