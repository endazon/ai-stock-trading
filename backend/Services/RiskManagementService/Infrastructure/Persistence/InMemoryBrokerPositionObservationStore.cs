using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-05, FR-10, #849, IADR-0350 決定 1: 最新の建玉観測のインメモリ実装（テスト・単体実行用）。
// 本番は EfBrokerPositionObservationStore（replicas>1 で観測が Pod へ分散するため永続が要る）。
public sealed class InMemoryBrokerPositionObservationStore : IBrokerPositionObservationStore
{
    private readonly Lock _gate = new();
    private BrokerPositionObservation? _latest;

    public void Record(IReadOnlyList<BrokerPositionSnapshot> positions, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(positions);

        lock (_gate)
        {
            // 逆行する観測（再送・順序前後）で新しい観測を古い値へ戻さない。
            if (_latest is not null && observedAt <= _latest.ObservedAt)
                return;

            _latest = new BrokerPositionObservation([.. positions], observedAt);
        }
    }

    public BrokerPositionObservation? GetLatest()
    {
        lock (_gate)
        {
            return _latest;
        }
    }
}
