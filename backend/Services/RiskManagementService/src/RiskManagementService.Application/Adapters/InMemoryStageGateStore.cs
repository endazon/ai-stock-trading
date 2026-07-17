using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Domain;

namespace AiStockTrading.RiskManagement.Application.Adapters;

// FR-20, IADR-0070: 段階遷移台帳のインメモリ実装（ユニット試験・非 relational 用）。EF 実装（EfStageGateStore）は
// Worker 側で差し替える。Append は純ドメイン StageGateLedger.Append の追記整合（遷移元＝現在段階・連番）を用いる。
public sealed class InMemoryStageGateStore : IStageGateStore
{
    private readonly Lock _gate = new();
    private StageGateLedger _ledger;

    public InMemoryStageGateStore(TradingStage initialStage = TradingStage.Stage0Verification)
    {
        _ledger = StageGateLedger.Empty(initialStage);
    }

    public StageGateLedger Load()
    {
        lock (_gate)
        {
            return _ledger;
        }
    }

    public void Append(StageTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        lock (_gate)
        {
            _ledger = _ledger.Append(transition);
        }
    }
}
