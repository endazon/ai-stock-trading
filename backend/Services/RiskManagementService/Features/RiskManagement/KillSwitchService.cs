using Microsoft.Extensions.Logging;
using AiStockTrading.Shared.Contracts.Errors;
using RiskManagementService.Common.Abstractions;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, UC-06, ADR-0003: 全停止スイッチ（kill switch）の操作。利用者のみ（アクター・理由必須）。
// 呼び出し側の権限（利用者か否か）はホスト層の Keycloak 認可で担保する（Slice B）。操作は履歴に記録する。
// 🔴 FR-10, FR-14, #1534, IADR-0620: 発動・解除は「見た状態」の版（expectedVersion）を前提条件に取れる。渡されたら比較と書き込みを
// ストアで原子的に行い、違えば KillSwitchVersionConflictException（409）で何も変えない。渡されないときは段（options）に従う:
// 段 1（Report・既定）は従前どおり受けて Warning を残す。段 2（Require）は解除だけ拒否する（KillSwitchPreconditionRequiredException・428）。
// 発動は安全側の操作であり、段 2 でも前提条件を求めない（自動の安全側＝撤退の発動も同じ口を通る）。
// NFR, #1530, IADR-0613: hold（SSH の経路の保持）は省略可。省略・無効なら従来と同じ振る舞い。
public sealed class KillSwitchService(
    IKillSwitchStore store,
    ISettingsChangeLog changeLog,
    IClock clock,
    OperatorKillSwitchHoldOptions? hold = null,
    KillSwitchPreconditionOptions? preconditions = null,
    Microsoft.Extensions.Logging.ILogger<KillSwitchService>? logger = null)
{
    private readonly KillSwitchPreconditionMode _mode = preconditions?.Mode ?? KillSwitchPreconditionMode.Report;

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

    public void Engage(string actor, string reason, string? expectedVersion = null)
    {
        RequireActorAndReason(actor, reason);

        var now = clock.UtcNow;
        Write(new KillSwitchState(true, actor, reason, now), expectedVersion, requiredInStage2: false, "発動");
        changeLog.Record(new SettingsChangeEntry(
            actor, SettingsChangeType.KillSwitchEngaged, reason, now,
            Before: "Disengaged", After: "Engaged"));
    }

    // NFR-06, #1542, IADR-0615 決定 9: beforeCommit（第 2 要素の門）は、入力の検証・冪等の判定の後、状態を変える直前にだけ呼ぶ。
    // #1534, IADR-0620: expectedVersion は beforeCommit の後ろ（位置で渡す既存の呼び手を変えない）。
    public void Disengage(string actor, string reason, Action? beforeCommit = null, string? expectedVersion = null)
    {
        RequireActorAndReason(actor, reason);

        // NFR, #1530, IADR-0613: 保持の間は解除しない（止める窓口を Discord と独立させる）。外すのは SSH の側だけ。
        // #1534, IADR-0620: 保持の拒否は版の前提条件（409 version_conflict・428）より先に効く（code は operator_hold）。
        if (hold is { Enabled: true })
            throw new KillSwitchHeldByOperatorException();

        // #1534, IADR-0620: 前提条件で拒否される解除は、第 2 要素のコードを消費する前に止める（428・版の不一致の 409）。
        // 版の比較は書き込みの中でも原子的にもう一度行う（ここは門の手前の早期の判定）。
        if (expectedVersion is null && _mode == KillSwitchPreconditionMode.Require)
            throw new KillSwitchPreconditionRequiredException();
        var observed = store.GetState();
        if (expectedVersion is not null && !string.Equals(observed.Version, expectedVersion.Trim(), StringComparison.Ordinal))
            throw new KillSwitchVersionConflictException();

        // 既に解除済みなら門を通さない（再送はコードを消費しない）。書き込みは従来どおり。
        if (observed.Engaged)
            beforeCommit?.Invoke();

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

            // ここへ来るのは、段 1 の発動・解除と、段に関わらない発動（安全側の操作）だけ。
            if (requiredInStage2)
                logger?.LogWarning(
                    "kill switch の{Operation}を、見た状態の版（expectedVersion）なしで受けました。段 1 のため書きます（段 2 では拒否されます）。",
                    operation);
            else
                logger?.LogInformation(
                    "kill switch の{Operation}を、見た状態の版（expectedVersion）なしで受けました。発動は段に関わらず受けます。",
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
    : InvalidOperationException("kill switch の状態は、見た後に変わっています。最新の状態を取得してから操作し直してください。")
{
    /// <summary>REST の 409 の本文の <c>code</c>（同じ 409 の別の理由と呼び出し側が区別するための機械可読な印）。</summary>
    public const string Code = "version_conflict";
}

/// <summary>NFR, #1534, IADR-0620: If-Match の <c>*</c> や複数の値は解釈しない（400）。版として比べて常に 409 にしない。</summary>
public sealed class KillSwitchIfMatchUnsupportedException()
    : InvalidOperationException("If-Match は、照会で受け取った版を 1 つだけ指定してください（* と複数の値は扱いません）。")
{
    /// <summary>REST の 400 の本文の <c>code</c>。</summary>
    public const string Code = "if_match_unsupported";
}

/// <summary>NFR, #1534, IADR-0620: 段 2 で、解除に見た状態の版が無い（428）。</summary>
public sealed class KillSwitchPreconditionRequiredException()
    : InvalidOperationException("kill switch の解除には、見た状態の版（expectedVersion）が必要です。状態を取得してから操作してください。")
{
    /// <summary>REST の 428 の本文の <c>code</c>。</summary>
    public const string Code = "precondition_required";
}
