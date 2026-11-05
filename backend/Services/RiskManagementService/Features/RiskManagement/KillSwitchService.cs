using AiStockTrading.Shared.Contracts.Errors;
using RiskManagementService.Common.Abstractions;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, UC-06, ADR-0003: 全停止スイッチ（kill switch）の操作。利用者のみ（アクター・理由必須）。
// 呼び出し側の権限（利用者か否か）はホスト層の Keycloak 認可で担保する（Slice B）。操作は履歴に記録する。
// NFR, #1530, IADR-0613: hold（SSH の経路の保持）は省略可。省略・無効なら従来と同じ振る舞い。
public sealed class KillSwitchService(
    IKillSwitchStore store,
    ISettingsChangeLog changeLog,
    IClock clock,
    OperatorKillSwitchHoldOptions? hold = null)
{
    public KillSwitchState GetState() => store.GetState();

    /// <summary>
    /// NFR, FR-10, #1530, IADR-0613: 保持が入っていれば kill switch を起動する（起動時に 1 回呼ぶ）。
    /// 既に起動していれば上書きしない（先の起動の主体・理由を保つ）。止める向きだけの操作であり、解除はしない。
    /// </summary>
    public OperatorHoldOutcome ApplyOperatorHold()
    {
        if (hold is not { Enabled: true })
            return OperatorHoldOutcome.Inactive;

        if (store.GetState().Engaged)
            return OperatorHoldOutcome.AlreadyEngaged;

        var reason = string.IsNullOrWhiteSpace(hold.Reason) ? OperatorKillSwitchHoldOptions.DefaultReason : hold.Reason.Trim();
        Engage(OperatorKillSwitchHoldOptions.Actor, reason);
        return OperatorHoldOutcome.Engaged;
    }

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

        // NFR, #1530, IADR-0613: 保持の間は解除しない（止める窓口を Discord と独立させる）。外すのは SSH の側だけ。
        if (hold is { Enabled: true })
            throw new KillSwitchHeldByOperatorException();

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
        // NFR-06, IADR-0509, #1230: 利用者（Discord・画面）が入力する欄の空欄検査。文言を 400 / INVALID_ARGUMENT へ載せる印を付ける。
        ClientVisibleArgument.ThrowIfNullOrWhiteSpace(reason, "理由");
    }
}
