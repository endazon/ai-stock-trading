using AiStockTrading.RiskManagement.Application.State;

namespace AiStockTrading.RiskManagement.Application.Ports;

// FR-10, IADR-0008: 日次損失ロックアウト状態の取得・保存・解除。実運用では PostgreSQL 永続化（Slice B）。
public interface ILockoutStore
{
    /// <summary>現在保持しているロックアウト状態。未設定なら null。</summary>
    LockoutState? Get();

    void Set(LockoutState state);

    void Clear();
}
