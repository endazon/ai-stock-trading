using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Domain;

namespace AiStockTrading.RiskManagement.Application.Services;

// FR-10, FR-19, FR-20, UC-06, ADR-0007: リスク管理設定（ガード・上限・段階）の変更。利用者のみ（アクター・理由必須）。
// 変更は前後値つきで履歴に記録する（ADR-0007「変更は利用者のみ・変更履歴を記録」）。生成AI・自動処理は本サービスを
// 呼ばない（呼び出し側の権限はホスト層の Keycloak 認可で担保・Slice B）。
public sealed class RiskSettingsService(
    IRiskSettingsStore store,
    ISettingsChangeLog changeLog,
    IClock clock)
{
    public RiskManagementSettings GetCurrent() => store.GetCurrent();

    public void UpdateGuard(TradingGuardSettings guard, string actor, string reason)
    {
        ArgumentNullException.ThrowIfNull(guard);
        RequireActorAndReason(actor, reason);

        var current = store.GetCurrent();
        Save(current with { Guard = guard }, current.Guard, guard, SettingsChangeType.Guard, actor, reason);
    }

    public void UpdateLimits(RiskLimitSettings limits, string actor, string reason)
    {
        ArgumentNullException.ThrowIfNull(limits);
        RequireActorAndReason(actor, reason);

        var current = store.GetCurrent();
        Save(current with { Limits = limits }, current.Limits, limits, SettingsChangeType.Limits, actor, reason);
    }

    public void UpdateStage(StageSettings stage, string actor, string reason)
    {
        ArgumentNullException.ThrowIfNull(stage);
        RequireActorAndReason(actor, reason);

        var current = store.GetCurrent();
        Save(current with { Stage = stage }, current.Stage, stage, SettingsChangeType.Stage, actor, reason);
    }

    private void Save(
        RiskManagementSettings updated,
        object before,
        object after,
        SettingsChangeType changeType,
        string actor,
        string reason)
    {
        var now = clock.UtcNow;
        store.Save(updated);
        changeLog.Record(new SettingsChangeEntry(
            actor, changeType, reason, now,
            Before: before.ToString(), After: after.ToString()));
    }

    private static void RequireActorAndReason(string actor, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
    }
}
