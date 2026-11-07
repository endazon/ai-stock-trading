using Microsoft.Extensions.Logging;

namespace AiStockTrading.Shared.Infrastructure.Composable.SecondFactor;

/// <summary>NFR-06, #1542, IADR-0615: 第 2 要素の段（段 1 = 記録だけ、段 2 = 拒む）。既定は Off（何もしない）。</summary>
public enum SecondFactorMode
{
    Off,
    Audit,
    Enforce,
}

/// <summary>NFR-06, FR-14, #1542, IADR-0615 決定 2: 第 2 要素を課す 7 操作（止める向きの操作は含めない）。</summary>
public enum SecondFactorOperation
{
    KillSwitchDisengage,
    TradingResume,
    StagePromote,
    GoodFaithViolationClear,
    PositionDriftAdopt,
    ReportConfirm,
    PolicyConfirm,
}

/// <summary>NFR-06, #1542, IADR-0615: 検証の結果。</summary>
public enum SecondFactorOutcome
{
    /// <summary>段が Off（検証しない）。</summary>
    Skipped,
    Verified,
    Missing,
    Invalid,
    Replayed,
    LockedOut,
    /// <summary>シークレットが構成されていない・読めない。</summary>
    NotConfigured,

    /// <summary>IADR-0626 決定 1: 共有の状態を読めない・書けない（DB の障害・リスク管理へ届かない）。Enforce は拒む（閉じる向き）。</summary>
    Unavailable,
}

/// <summary>NFR-06, #1542, IADR-0615, IADR-0626: 第 2 要素の構成（`OwnerSecondFactor:*`）。</summary>
public sealed class OwnerSecondFactorOptions
{
    public const string SectionName = "OwnerSecondFactor";

    public SecondFactorMode Mode { get; set; } = SecondFactorMode.Off;

    /// <summary>base32 のシークレット。chart は Secret `ast-owner-totp` の optional の参照で渡す（値はリポジトリに置かない）。</summary>
    public string? Secret { get; set; }

    /// <summary>連続して失敗したら止める回数（既定 5）。</summary>
    public int MaxConsecutiveFailures { get; set; } = 5;

    /// <summary>1 回目に止める長さ（既定 15 分）。2 回目以降は倍に延ばす（IADR-0626 決定 2）。</summary>
    public int LockoutMinutes { get; set; } = 15;

    /// <summary>
    /// 止めた回数がこの値に達したら、時間では解かず手動の解除（シークレットの作り直し）を求める（既定 4＝15・30・60 分の後）。
    /// IADR-0626 決定 2。
    /// </summary>
    public int ManualUnlockAfterLockouts { get; set; } = 4;
}

/// <summary>判定。<see cref="Allowed"/> が false なら呼び手は操作をしない。</summary>
public sealed record SecondFactorDecision(SecondFactorOutcome Outcome, bool Allowed);

/// <summary>
/// NFR-06, FR-14, #1542, IADR-0615, IADR-0626: 危険な操作の第 2 要素（TOTP）の門。<b>ボットの外（提供側）で検証する</b>。
/// <list type="bullet">
///   <item>確かめは <see cref="IOwnerSecondFactorVerifier"/> が行う。状態（最後の段・失敗の数・停止）は 2 サービスで 1 つ
///   （リスク管理が持ち、報告書はリスク管理へ問い合わせる。IADR-0626 決定 1）。</item>
///   <item>確かめは操作の種類と対象（<c>target</c>）に結び付ける。一度使ったコードは、どの操作・どのサービスでも再び通らない
///   （IADR-0626 決定 4）。</item>
///   <item>Audit（段 1）は結果を記録するだけで、操作を止めない。Enforce（段 2）は Verified 以外を拒む（シークレットが無ければ拒む）。</item>
///   <item>ログにはコードを載せない（操作・対象・結果・段だけ）。</item>
/// </list>
/// </summary>
public sealed class OwnerSecondFactorGate
{
    /// <summary>ログの印（運用の手順書が数える）。</summary>
    public const string LogMarker = "owner-second-factor:";

    private readonly OwnerSecondFactorOptions _options;
    private readonly IOwnerSecondFactorVerifier _verifier;
    private readonly ILogger _logger;

    /// <summary>プロセスの中の状態で確かめる（単体の実行・試験。状態は共有しない）。</summary>
    public OwnerSecondFactorGate(OwnerSecondFactorOptions options, TimeProvider time, ILogger logger)
        : this(options, new TotpOwnerSecondFactorVerifier(options, new InMemoryOwnerSecondFactorStateStore(), time, logger), logger)
    {
    }

    public OwnerSecondFactorGate(OwnerSecondFactorOptions options, IOwnerSecondFactorVerifier verifier, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(verifier);
        _options = options;
        _verifier = verifier;
        _logger = logger;
    }

    public SecondFactorMode Mode => _options.Mode;

    /// <summary>
    /// 操作 <paramref name="operation"/> を対象 <paramref name="target"/> に行う前に、コードを確かめる。
    /// 🔴 IADR-0626 決定 4: 対象は記録・通知に載せ、オーナーが「何に使われたか」を見られるようにする。
    /// </summary>
    public SecondFactorDecision Check(SecondFactorOperation operation, string target, string? code)
    {
        if (_options.Mode == SecondFactorMode.Off)
            return new SecondFactorDecision(SecondFactorOutcome.Skipped, true);

        var outcome = _verifier.Verify(operation, target, code);
        var allowed = _options.Mode == SecondFactorMode.Audit || outcome == SecondFactorOutcome.Verified;
        _logger.Log(
            outcome == SecondFactorOutcome.Verified ? LogLevel.Information : LogLevel.Warning,
            "{Marker} operation={Operation} target={Target} outcome={Outcome} mode={Mode} allowed={Allowed}",
            LogMarker, operation, OwnerSecondFactorTargets.ForLog(target), outcome, _options.Mode, allowed);
        return new SecondFactorDecision(outcome, allowed);
    }
}

/// <summary>NFR-06, #1542, IADR-0615: Enforce の段で第 2 要素が通らなかった（REST は 403）。文言は固定（入力を含まない）。</summary>
public sealed class SecondFactorRejectedException(SecondFactorOutcome outcome)
    : InvalidOperationException("確認コードを確かめられないため、この操作は行いません。認証アプリの 6 桁を入れ直してください。")
{
    public SecondFactorOutcome Outcome { get; } = outcome;
}
