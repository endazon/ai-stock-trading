
namespace RiskManagementService.Features.RiskManagement;

// FR-10, ADR-0003: kill switch 状態の取得・保存。実運用では PostgreSQL 永続化（Slice B）。
public interface IKillSwitchStore
{
    KillSwitchState GetState();

    void SetState(KillSwitchState state);

    /// <summary>
    /// 🔴 FR-10, #1534, IADR-0620: 現在の版が <paramref name="expectedVersion"/> のときだけ書く（比較と書き込みの間に別の書き込みを入れない）。
    /// 書けたら true、版が違えば false（何も書かない）。既定の実装は比較して書くだけで原子的ではない——永続の実装は上書きする。
    /// </summary>
    bool TrySetState(KillSwitchState state, string expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!string.Equals(GetState().Version, expectedVersion, StringComparison.Ordinal))
            return false;
        SetState(state);
        return true;
    }
}
