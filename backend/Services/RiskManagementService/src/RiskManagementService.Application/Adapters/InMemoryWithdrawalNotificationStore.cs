using AiStockTrading.RiskManagement.Application.Ports;

namespace AiStockTrading.RiskManagement.Application.Adapters;

// FR-20, FR-09, IADR-0085, #189: 撤退の非停止降格提案の通知重複排除ストアのインメモリ実装（ユニット試験・非 relational 用）。
// 未記録時は null（未通知）を返す＝fail-safe。durable な永続は EF 実装（EfWithdrawalNotificationStore）が担う。
public sealed class InMemoryWithdrawalNotificationStore : IWithdrawalNotificationStore
{
    private readonly Lock _gate = new();
    private string? _signature;

    public string? GetNotifiedSignature()
    {
        lock (_gate)
        {
            return _signature;
        }
    }

    public void SaveNotifiedSignature(string signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        lock (_gate)
        {
            _signature = signature;
        }
    }

    public void ClearNotifiedSignature()
    {
        lock (_gate)
        {
            _signature = null;
        }
    }
}
