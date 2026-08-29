using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Domain;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-20, FR-11, #387, IADR-0148: 発注審査の観測ログのインメモリ実装（ユニット試験・ローカル実行用）。
// 本番配線は EfControlViolationObservationStore。未記録＝未供給（null）＝昇格しない安全側。
public sealed class InMemoryControlViolationObservationStore : IControlViolationObservationStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, OrderScreeningObservation> _observations = [];

    public void Record(OrderScreeningObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (_gate)
        {
            // DecisionId で冪等（1 回の発注審査につき 1 行）。再送は先着を保つ。
            _observations.TryAdd(observation.DecisionId, observation);
        }
    }

    public ControlViolationTally? GetTally()
    {
        lock (_gate)
        {
            return ControlViolationAggregation.Tally(_observations.Values);
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
