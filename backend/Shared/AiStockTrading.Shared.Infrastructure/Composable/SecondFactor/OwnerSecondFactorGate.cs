using System.Security.Cryptography;
using System.Text;
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
}

/// <summary>NFR-06, #1542, IADR-0615: 第 2 要素の構成（`OwnerSecondFactor:*`）。</summary>
public sealed class OwnerSecondFactorOptions
{
    public const string SectionName = "OwnerSecondFactor";

    public SecondFactorMode Mode { get; set; } = SecondFactorMode.Off;

    /// <summary>base32 のシークレット。chart は Secret `ast-owner-totp` の optional の参照で渡す（値はリポジトリに置かない）。</summary>
    public string? Secret { get; set; }

    /// <summary>連続して失敗したらロックする回数（既定 5）。</summary>
    public int MaxConsecutiveFailures { get; set; } = 5;

    /// <summary>ロックの長さ（既定 15 分）。</summary>
    public int LockoutMinutes { get; set; } = 15;
}

/// <summary>判定。<see cref="Allowed"/> が false なら呼び手は操作をしない。</summary>
public sealed record SecondFactorDecision(SecondFactorOutcome Outcome, bool Allowed);

/// <summary>
/// NFR-06, FR-14, #1542, IADR-0615: 危険な操作の第 2 要素（TOTP）の門。<b>ボットの外（提供側）で検証する</b>。
/// <list type="bullet">
///   <item>前後 1 段（±30 秒）まで受け付け、受け付けた段より新しい段だけを通す（同じコードの再利用を拒む）。</item>
///   <item>連続して <see cref="OwnerSecondFactorOptions.MaxConsecutiveFailures"/> 回失敗したら一定時間ロックする（コードの欠けは数えない）。</item>
///   <item>Audit（段 1）は結果を記録するだけで、操作を止めない。Enforce（段 2）は Verified 以外を拒む（シークレットが無ければ拒む）。</item>
///   <item>ログにはコードを載せない（操作・結果・段だけ）。</item>
/// </list>
/// 状態（最後の段・失敗の数）はプロセスの中に持つ（Pod は 1 台）。
/// </summary>
public sealed class OwnerSecondFactorGate
{
    /// <summary>ログの印（運用の手順書が数える）。</summary>
    public const string LogMarker = "owner-second-factor:";

    private readonly OwnerSecondFactorOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly byte[]? _key;
    private readonly Lock _gate = new();
    private long _lastAcceptedStep = long.MinValue;
    private int _consecutiveFailures;
    private DateTimeOffset? _lockedUntil;

    public OwnerSecondFactorGate(OwnerSecondFactorOptions options, TimeProvider time, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _key = Totp.TryDecodeBase32(options.Secret, out var key) ? key : null;
        if (options.Mode != SecondFactorMode.Off && _key is null)
        {
            _logger.LogWarning(
                "{Marker} シークレットが無いか読めません（段={Mode}）。Audit は記録だけ、Enforce は危険な操作をすべて拒みます。",
                LogMarker, options.Mode);
        }
    }

    public SecondFactorMode Mode => _options.Mode;

    public SecondFactorDecision Check(SecondFactorOperation operation, string? code)
    {
        if (_options.Mode == SecondFactorMode.Off)
            return new SecondFactorDecision(SecondFactorOutcome.Skipped, true);

        var outcome = Evaluate(code);
        var allowed = _options.Mode == SecondFactorMode.Audit || outcome == SecondFactorOutcome.Verified;
        _logger.Log(
            outcome == SecondFactorOutcome.Verified ? LogLevel.Information : LogLevel.Warning,
            "{Marker} operation={Operation} outcome={Outcome} mode={Mode} allowed={Allowed}",
            LogMarker, operation, outcome, _options.Mode, allowed);
        return new SecondFactorDecision(outcome, allowed);
    }

    private SecondFactorOutcome Evaluate(string? code)
    {
        if (_key is null)
            return SecondFactorOutcome.NotConfigured;

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_lockedUntil is { } until && now < until)
                return SecondFactorOutcome.LockedOut;

            if (string.IsNullOrEmpty(code))
                return SecondFactorOutcome.Missing;

            var trimmed = code.Trim();
            var step = Totp.StepOf(now);
            long? matched = null;
            if (trimmed.Length == Totp.Digits && trimmed.All(char.IsAsciiDigit))
            {
                for (var d = -1; d <= 1; d++)
                {
                    var expected = Encoding.ASCII.GetBytes(Totp.Compute(_key, step + d));
                    if (CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(trimmed)))
                    {
                        matched = step + d;
                        break;
                    }
                }
            }

            if (matched is { } m)
            {
                if (m <= _lastAcceptedStep)
                    return Fail(SecondFactorOutcome.Replayed, now);

                _lastAcceptedStep = m;
                _consecutiveFailures = 0;
                _lockedUntil = null;
                return SecondFactorOutcome.Verified;
            }

            return Fail(SecondFactorOutcome.Invalid, now);
        }
    }

    private SecondFactorOutcome Fail(SecondFactorOutcome outcome, DateTimeOffset now)
    {
        _consecutiveFailures++;
        if (_consecutiveFailures >= Math.Max(1, _options.MaxConsecutiveFailures))
        {
            _lockedUntil = now.AddMinutes(Math.Max(1, _options.LockoutMinutes));
            _consecutiveFailures = 0;
        }

        return outcome;
    }
}

/// <summary>NFR-06, #1542, IADR-0615: Enforce の段で第 2 要素が通らなかった（REST は 403）。文言は固定（入力を含まない）。</summary>
public sealed class SecondFactorRejectedException(SecondFactorOutcome outcome)
    : InvalidOperationException("確認コードを確かめられないため、この操作は行いません。認証アプリの 6 桁を入れ直してください。")
{
    public SecondFactorOutcome Outcome { get; } = outcome;
}
