using RiskManagementService.Application.Ports;
using RiskManagementService.Application.State;

namespace RiskManagementService.Application.Services;

// FR-10, FR-14, UC-06, ADR-0009: 取引の一時停止（pause）/再開（resume）の操作。利用者のみ（アクター・理由必須）。
// kill switch（KillSwitchService）と同型。呼び出し側の権限（利用者か否か）はホスト層の Keycloak 認可で担保する。
// 操作は監査のため設定変更履歴（ISettingsChangeLog）に記録する（kill switch と同経路・新イベントは足さない）。
//
// 冪等性（ADR-0009・受け入れ基準）: 既に停止中の Pause、非停止中の Resume は**状態を返すのみで副作用なし**
// （ストア書き込みも変更履歴記録もしない）。同一状態への無意味な操作で監査ログを水増ししない。
public sealed class PauseService(
    IPauseStore store,
    ISettingsChangeLog changeLog,
    IClock clock)
{
    public PauseState GetState() => store.GetState();

    public PauseState Pause(string actor, string reason)
    {
        RequireActorAndReason(actor, reason);

        var current = store.GetState();
        if (current.Paused)
        {
            // 冪等: 既に停止中なら現状態を返すのみ（副作用なし）。
            return current;
        }

        var now = clock.UtcNow;
        var next = new PauseState(true, actor, reason, now);
        store.SetState(next);
        changeLog.Record(new SettingsChangeEntry(
            actor, SettingsChangeType.TradingPaused, reason, now,
            Before: "Active", After: "Paused"));
        return next;
    }

    public PauseState Resume(string actor, string reason)
    {
        RequireActorAndReason(actor, reason);

        var current = store.GetState();
        if (!current.Paused)
        {
            // 冪等: 非停止中なら現状態を返すのみ（副作用なし）。
            return current;
        }

        var now = clock.UtcNow;
        var next = new PauseState(false, actor, reason, now);
        store.SetState(next);
        changeLog.Record(new SettingsChangeEntry(
            actor, SettingsChangeType.TradingResumed, reason, now,
            Before: "Paused", After: "Active"));
        return next;
    }

    private static void RequireActorAndReason(string actor, string reason)
    {
        // 監査性（FR-11・ADR-0009）: アクターと理由のない変更は受け付けない。
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
    }
}
