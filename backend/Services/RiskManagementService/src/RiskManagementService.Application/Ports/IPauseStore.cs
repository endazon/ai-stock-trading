using RiskManagementService.Application.State;

namespace RiskManagementService.Application.Ports;

// FR-10, ADR-0009: 取引の一時停止（pause）状態の取得・保存。kill switch（IKillSwitchStore）と同型・単一行。
// 実運用では PostgreSQL 永続化（EfPauseStore）。日次損失ロックアウト（ILockoutStore）とは別のストア。
public interface IPauseStore
{
    PauseState GetState();

    void SetState(PauseState state);
}
