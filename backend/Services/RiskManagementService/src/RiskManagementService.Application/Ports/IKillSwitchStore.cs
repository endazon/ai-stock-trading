using RiskManagementService.Application.State;

namespace RiskManagementService.Application.Ports;

// FR-10, ADR-0003: kill switch 状態の取得・保存。実運用では PostgreSQL 永続化（Slice B）。
public interface IKillSwitchStore
{
    KillSwitchState GetState();

    void SetState(KillSwitchState state);
}
