using RiskManagementService.Common.Abstractions;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, UC-06, ADR-0003: 全停止スイッチ（kill switch）の操作。利用者のみ（アクター・理由必須）。
// 呼び出し側の権限（利用者か否か）はホスト層の Keycloak 認可で担保する（Slice B）。操作は履歴に記録する。
public sealed class KillSwitchService(
    IKillSwitchStore store,
    ISettingsChangeLog changeLog,
    IClock clock)
{
    public KillSwitchState GetState() => store.GetState();

    public void Engage(string actor, string reason)
    {
        RequireActorAndReason(actor, reason);

        var now = clock.UtcNow;
        store.SetState(new KillSwitchState(true, actor, reason, now));
        changeLog.Record(new SettingsChangeEntry(
            actor, SettingsChangeType.KillSwitchEngaged, reason, now,
            Before: "Disengaged", After: "Engaged"));
    }

    public void Disengage(string actor, string reason)
    {
        RequireActorAndReason(actor, reason);

        var now = clock.UtcNow;
        store.SetState(new KillSwitchState(false, actor, reason, now));
        changeLog.Record(new SettingsChangeEntry(
            actor, SettingsChangeType.KillSwitchDisengaged, reason, now,
            Before: "Engaged", After: "Disengaged"));
    }

    private static void RequireActorAndReason(string actor, string reason)
    {
        // 監査性（FR-11・ADR-0003）: アクターと理由のない変更は受け付けない。
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
    }
}
