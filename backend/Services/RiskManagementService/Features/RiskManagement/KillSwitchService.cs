using AiStockTrading.Shared.Contracts.Errors;
using RiskManagementService.Common.Abstractions;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, UC-06, ADR-0003: 全停止スイッチ（kill switch）の操作。利用者のみ（アクター・理由必須）。
// 呼び出し側の権限（利用者か否か）はホスト層の Keycloak 認可で担保する（Slice B）。操作は履歴に記録する。
// 🔴 FR-10, FR-14, #1534, IADR-0620: 発動・解除は「見た状態」の版（expectedVersion）を前提条件に取れる。渡されたら比較と書き込みを
// ストアで原子的に行い、違えば KillSwitchVersionConflictException（409）で何も変えない。渡されないときは段（options）に従う:
// 段 1（Report・既定）は従前どおり受けて Warning を残す。段 2（Require）は解除だけ拒否する（KillSwitchPreconditionRequiredException・428）。
// 発動は安全側の操作であり、段 2 でも前提条件を求めない（自動の安全側＝撤退の発動も同じ口を通る）。
public sealed class KillSwitchService(
    IKillSwitchStore store,
    ISettingsChangeLog changeLog,
    IClock clock,
    KillSwitchPreconditionOptions? preconditions = null,
    Microsoft.Extensions.Logging.ILogger<KillSwitchService>? logger = null)
{
    private readonly KillSwitchPreconditionMode _mode = preconditions?.Mode ?? KillSwitchPreconditionMode.Report;

    public KillSwitchState GetState() => store.GetState();

    public void Engage(string actor, string reason, string? expectedVersion = null)
    {
        RequireActorAndReason(actor, reason);

        var now = clock.UtcNow;
        Write(new KillSwitchState(true, actor, reason, now), expectedVersion, requiredInStage2: false, "発動");
        changeLog.Record(new SettingsChangeEntry(
            actor, SettingsChangeType.KillSwitchEngaged, reason, now,
            Before: "Disengaged", After: "Engaged"));
    }

    public void Disengage(string actor, string reason, string? expectedVersion = null)
    {
        RequireActorAndReason(actor, reason);

        var now = clock.UtcNow;
        Write(new KillSwitchState(false, actor, reason, now), expectedVersion, requiredInStage2: true, "解除");
        changeLog.Record(new SettingsChangeEntry(
            actor, SettingsChangeType.KillSwitchDisengaged, reason, now,
            Before: "Engaged", After: "Disengaged"));
    }

    private void Write(KillSwitchState next, string? expectedVersion, bool requiredInStage2, string operation)
    {
        if (expectedVersion is null)
        {
            if (requiredInStage2 && _mode == KillSwitchPreconditionMode.Require)
                throw new KillSwitchPreconditionRequiredException();

            logger?.Log(
                requiredInStage2 ? Microsoft.Extensions.Logging.LogLevel.Warning : Microsoft.Extensions.Logging.LogLevel.Information,
                "kill switch の{Operation}を、見た状態の版（expectedVersion）なしで受けました（段 1 のため従前どおり書きます）。",
                operation);
            store.SetState(next);
            return;
        }

        if (!store.TrySetState(next, expectedVersion.Trim()))
            throw new KillSwitchVersionConflictException();
    }

    private static void RequireActorAndReason(string actor, string reason)
    {
        // 監査性（FR-11・ADR-0003）: アクターと理由のない変更は受け付けない。
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        // NFR-06, IADR-0509, #1230: 利用者（Discord・画面）が入力する欄の空欄検査。文言を 400 / INVALID_ARGUMENT へ載せる印を付ける。
        ClientVisibleArgument.ThrowIfNullOrWhiteSpace(reason, "理由");
    }
}

/// <summary>NFR, FR-10, #1534, IADR-0620: 前提条件の段（構成 <c>RiskControls:KillSwitch:Precondition</c>）。</summary>
public enum KillSwitchPreconditionMode
{
    /// <summary>段 1（既定）: 前提条件の無い要求も受け、記録する。</summary>
    Report = 0,

    /// <summary>段 2: 前提条件の無い解除を拒否する。</summary>
    Require = 1,
}

/// <summary>NFR, #1534, IADR-0620: 前提条件の設定。</summary>
public sealed record KillSwitchPreconditionOptions(KillSwitchPreconditionMode Mode)
{
    public const string ConfigKey = "RiskControls:KillSwitch:Precondition";

    /// <summary>構成から読む。空は段 1。知らない値は起動で止める。</summary>
    public static KillSwitchPreconditionOptions From(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new(KillSwitchPreconditionMode.Report);
        if (Enum.TryParse<KillSwitchPreconditionMode>(value.Trim(), ignoreCase: false, out var mode) && Enum.IsDefined(mode))
            return new(mode);
        throw new InvalidOperationException($"{ConfigKey} は Report か Require です（値: {value}）。");
    }
}

/// <summary>NFR, #1534, IADR-0620: 見た状態の版が現在の版と違う（409）。状態は変えていない。</summary>
public sealed class KillSwitchVersionConflictException()
    : InvalidOperationException("kill switch の状態は、見た後に変わっています。最新の状態を取得してから操作し直してください。");

/// <summary>NFR, #1534, IADR-0620: 段 2 で、解除に見た状態の版が無い（428）。</summary>
public sealed class KillSwitchPreconditionRequiredException()
    : InvalidOperationException("kill switch の解除には、見た状態の版（expectedVersion）が必要です。状態を取得してから操作してください。");

