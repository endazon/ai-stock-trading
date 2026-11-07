using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Logging;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Shared.Infrastructure.Composable.SecondFactor;

/// <summary>NFR-06, FR-14, IADR-0626: 第 2 要素のコードを確かめる（状態の持ち主の側で、または持ち主へ問い合わせて）。</summary>
public interface IOwnerSecondFactorVerifier
{
    SecondFactorOutcome Verify(SecondFactorOperation operation, string target, string? code);
}

/// <summary>NFR-06, IADR-0626 決定 3: 確かめた結果を外へ知らせる口（リスク管理はメッセージで監査と通知へ流す）。</summary>
public interface IOwnerSecondFactorObserver
{
    void OnChecked(OwnerSecondFactorChecked check);
}

/// <summary>
/// NFR-06, IADR-0626 決定 1・2: 2 サービスで共有する第 2 要素の状態（単一）。最後に受け付けた段・失敗の数・停止。
/// </summary>
public sealed record OwnerSecondFactorState(
    long LastAcceptedStep,
    int ConsecutiveFailures,
    int LockoutCount,
    DateTimeOffset? LockedUntil,
    bool ManualUnlockRequired,
    string? LastAcceptedOperation,
    string? LastAcceptedTarget)
{
    public static OwnerSecondFactorState Initial { get; } = new(long.MinValue, 0, 0, null, false, null, null);
}

/// <summary>
/// NFR-06, IADR-0626 決定 1: 状態の置き場。<see cref="Update{T}"/> は読み・決め・書くを 1 つの単位で行う。
/// 🔴 保存された指紋（シークレットの SHA-256 の先頭）が <paramref name="secretFingerprint"/> と違えば、
/// <see cref="OwnerSecondFactorState.Initial"/> から始める（シークレットの作り直しが手動の解除になる。決定 2）。
/// </summary>
public interface IOwnerSecondFactorStateStore
{
    T Update<T>(string secretFingerprint, Func<OwnerSecondFactorState, (OwnerSecondFactorState Next, T Result)> decide);
}

/// <summary>NFR-06, IADR-0626: プロセスの中の置き場（単体の実行・試験。2 サービスでは共有しない）。</summary>
public sealed class InMemoryOwnerSecondFactorStateStore : IOwnerSecondFactorStateStore
{
    private readonly Lock _gate = new();
    private string? _fingerprint;
    private OwnerSecondFactorState _state = OwnerSecondFactorState.Initial;

    public T Update<T>(string secretFingerprint, Func<OwnerSecondFactorState, (OwnerSecondFactorState Next, T Result)> decide)
    {
        ArgumentNullException.ThrowIfNull(decide);
        lock (_gate)
        {
            var current = string.Equals(_fingerprint, secretFingerprint, StringComparison.Ordinal) ? _state : OwnerSecondFactorState.Initial;
            var (next, result) = decide(current);
            _fingerprint = secretFingerprint;
            _state = next;
            return result;
        }
    }
}

/// <summary>NFR-06, IADR-0626 決定 4: 操作の対象の表記（記録・通知へ載せる前に整える）。</summary>
public static class OwnerSecondFactorTargets
{
    public const int MaxLength = 128;

    public const string KillSwitch = "kill-switch";
    public const string TradingPause = "trading-pause";
    public const string GoodFaithViolations = "good-faith-violations";

    public static string Stage(object targetStage) => $"stage:{targetStage}";

    public static string Drift(object? market, string? symbol) => $"drift:{market}:{symbol}";

    public static string Report(string periodKey, int expectedVersion) =>
        $"report:{periodKey}@v{expectedVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>空なら <c>unspecified</c>。制御文字を置き換え、<see cref="MaxLength"/> で切る。</summary>
    public static string Normalize(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return "unspecified";
        var trimmed = target.Trim();
        var chars = trimmed.Select(c => char.IsControl(c) || c is LogSanitizer.LineSeparator or LogSanitizer.ParagraphSeparator ? LogSanitizer.Replacement : c);
        var text = new string([.. chars]);
        return text.Length <= MaxLength ? text : text[..MaxLength];
    }

    public static string ForLog(string? target) => LogSanitizer.Sanitize(Normalize(target), MaxLength + 32)!;
}

/// <summary>
/// NFR-06, FR-14, #1542, IADR-0615, IADR-0626: TOTP を確かめる本体（状態の持ち主の側。リスク管理）。
/// <list type="bullet">
///   <item>前後 1 段（±30 秒）まで受け付け、受け付けた段より新しい段だけを通す。<b>段は 2 サービス・全操作で共有する</b>ので、
///   一度使ったコードは別の操作・別のサービスでも通らない（決定 4）。</item>
///   <item>連続 <see cref="OwnerSecondFactorOptions.MaxConsecutiveFailures"/> 回の失敗（再利用を含む。欠けは数えない）で止める。
///   止める長さは 1 回目 <see cref="OwnerSecondFactorOptions.LockoutMinutes"/> 分、以後は倍。
///   <see cref="OwnerSecondFactorOptions.ManualUnlockAfterLockouts"/> 回目で手動の解除まで閉じる（決定 2）。</item>
///   <item>止めている間は、正しいコードも受け付けない（照合もしない）。</item>
///   <item>状態を読めない・書けないときは <see cref="SecondFactorOutcome.Unavailable"/>（Enforce は拒む）。</item>
/// </list>
/// </summary>
public sealed class TotpOwnerSecondFactorVerifier : IOwnerSecondFactorVerifier
{
    private readonly OwnerSecondFactorOptions _options;
    private readonly IOwnerSecondFactorStateStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly IOwnerSecondFactorObserver? _observer;
    private readonly byte[]? _key;
    private readonly string _fingerprint = string.Empty;
    private readonly Lock _gate = new();

    public TotpOwnerSecondFactorVerifier(
        OwnerSecondFactorOptions options,
        IOwnerSecondFactorStateStore store,
        TimeProvider time,
        ILogger logger,
        IOwnerSecondFactorObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        _options = options;
        _store = store;
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _observer = observer;
        _key = Totp.TryDecodeBase32(options.Secret, out var key) ? key : null;
        if (_key is not null)
            _fingerprint = FingerprintOf(_key);
        if (options.Mode != SecondFactorMode.Off && _key is null)
        {
            _logger.LogWarning(
                "{Marker} シークレットが無いか読めません（段={Mode}）。Audit は記録だけ、Enforce は危険な操作をすべて拒みます。",
                OwnerSecondFactorGate.LogMarker, options.Mode);
        }
    }

    /// <summary>シークレットの指紋（SHA-256 の先頭 16 バイトの 16 進）。状態の持ち主が作り直しを見分けるのに使う。</summary>
    public static string FingerprintOf(byte[] key) => Convert.ToHexStringLower(SHA256.HashData(key).AsSpan(0, 16));

    public SecondFactorOutcome Verify(SecondFactorOperation operation, string target, string? code)
    {
        if (_key is null)
            return SecondFactorOutcome.NotConfigured;

        var normalizedTarget = OwnerSecondFactorTargets.Normalize(target);
        var now = _time.GetUtcNow();
        Decision decision;
        try
        {
            lock (_gate)
                decision = _store.Update(_fingerprint, state => Decide(state, now, operation, normalizedTarget, code));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "{Marker} 第 2 要素の状態を読み書きできませんでした（operation={Operation}）。確かめられないものとして扱います。",
                OwnerSecondFactorGate.LogMarker, operation);
            return SecondFactorOutcome.Unavailable;
        }

        if (decision.Outcome is SecondFactorOutcome.Verified or SecondFactorOutcome.Invalid
            or SecondFactorOutcome.Replayed or SecondFactorOutcome.LockedOut)
        {
            Notify(new OwnerSecondFactorChecked(
                operation.ToString(), normalizedTarget, decision.Outcome.ToString(),
                decision.After.ConsecutiveFailures, decision.After.LockoutCount, decision.After.LockedUntil,
                decision.After.ManualUnlockRequired, decision.LockStarted, now));
        }

        return decision.Outcome;
    }

    private void Notify(OwnerSecondFactorChecked check)
    {
        if (_observer is null)
            return;
        try
        {
            _observer.OnChecked(check);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 知らせの失敗で確かめの結果を変えない（ログの印は門が残す）。
            _logger.LogWarning(ex, "{Marker} 確かめの結果を監査・通知へ流せませんでした（operation={Operation} outcome={Outcome}）。",
                OwnerSecondFactorGate.LogMarker, check.Operation, check.Outcome);
        }
    }

    private readonly record struct Decision(SecondFactorOutcome Outcome, OwnerSecondFactorState After, bool LockStarted);

    private (OwnerSecondFactorState Next, Decision Result) Decide(
        OwnerSecondFactorState state, DateTimeOffset now, SecondFactorOperation operation, string target, string? code)
    {
        // 🔴 止めている間は照合しない（正しいコードも受け付けない）。数えもしない。
        if (state.ManualUnlockRequired || (state.LockedUntil is { } until && now < until))
            return Keep(state, SecondFactorOutcome.LockedOut);

        if (string.IsNullOrEmpty(code))
            return Keep(state, SecondFactorOutcome.Missing);

        var matched = Match(code.Trim(), Totp.StepOf(now));
        if (matched is { } m)
        {
            if (m <= state.LastAcceptedStep)
                return Fail(state, now, SecondFactorOutcome.Replayed);

            var accepted = OwnerSecondFactorState.Initial with
            {
                LastAcceptedStep = m,
                LastAcceptedOperation = operation.ToString(),
                LastAcceptedTarget = target,
            };
            return (accepted, new Decision(SecondFactorOutcome.Verified, accepted, false));
        }

        return Fail(state, now, SecondFactorOutcome.Invalid);
    }

    private static (OwnerSecondFactorState, Decision) Keep(OwnerSecondFactorState state, SecondFactorOutcome outcome) =>
        (state, new Decision(outcome, state, false));

    private (OwnerSecondFactorState, Decision) Fail(OwnerSecondFactorState state, DateTimeOffset now, SecondFactorOutcome outcome)
    {
        var failures = state.ConsecutiveFailures + 1;
        if (failures < Math.Max(1, _options.MaxConsecutiveFailures))
        {
            var counted = state with { ConsecutiveFailures = failures };
            return (counted, new Decision(outcome, counted, false));
        }

        // 止める。失敗の数は次の停止までの数へ戻し、止めた回数は成功まで残す（止めるたびに長くする）。
        var lockouts = state.LockoutCount + 1;
        var manual = lockouts >= Math.Max(1, _options.ManualUnlockAfterLockouts);
        var minutes = Math.Max(1, _options.LockoutMinutes) * Math.Pow(2, Math.Min(lockouts - 1, 16));
        var locked = state with
        {
            ConsecutiveFailures = 0,
            LockoutCount = lockouts,
            LockedUntil = manual ? null : now.AddMinutes(minutes),
            ManualUnlockRequired = manual,
        };
        return (locked, new Decision(outcome, locked, true));
    }

    private long? Match(string code, long step)
    {
        if (code.Length != Totp.Digits || !code.All(char.IsAsciiDigit))
            return null;
        var given = Encoding.ASCII.GetBytes(code);
        for (var d = -1; d <= 1; d++)
        {
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Totp.Compute(_key!, step + d)), given))
                return step + d;
        }

        return null;
    }
}

/// <summary>NFR-06, IADR-0626 決定 1: 状態の持ち主（リスク管理）へ問い合わせる口の要求と応答。</summary>
public sealed record OwnerSecondFactorVerifyRequest(string Operation, string Target, string? Code);

/// <inheritdoc cref="OwnerSecondFactorVerifyRequest"/>
public sealed record OwnerSecondFactorVerifyResponse(string Outcome);

/// <summary>
/// NFR-06, FR-14, IADR-0626 決定 1: 状態の持ち主（リスク管理）へ問い合わせて確かめる（報告書の側）。
/// <list type="bullet">
///   <item>宛先は <c>POST /risk-controls/owner-second-factor/verify</c>（サービスの身元だけが呼べる）。</item>
///   <item>コードの欠けは問い合わせずに <see cref="SecondFactorOutcome.Missing"/>（状態を変えないため）。</item>
///   <item>届かない・2xx でない・知らない応答は <see cref="SecondFactorOutcome.Unavailable"/>（Enforce は拒む）。</item>
/// </list>
/// 🔴 呼び手（門の callback）は同期であるため、ここで待つ（ASP.NET Core には同期文脈が無い。時間の上限は HttpClient の Timeout）。
/// </summary>
public sealed class RemoteOwnerSecondFactorVerifier(HttpClient http, ILogger logger) : IOwnerSecondFactorVerifier
{
    public const string Route = "/risk-controls/owner-second-factor/verify";

    public SecondFactorOutcome Verify(SecondFactorOperation operation, string target, string? code)
    {
        if (string.IsNullOrEmpty(code))
            return SecondFactorOutcome.Missing;

        try
        {
            using var response = http.PostAsJsonAsync(
                    Route, new OwnerSecondFactorVerifyRequest(operation.ToString(), OwnerSecondFactorTargets.Normalize(target), code))
                .GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("{Marker} 状態の持ち主が確かめに応じませんでした（{Status}）。確かめられないものとして扱います。",
                    OwnerSecondFactorGate.LogMarker, (int)response.StatusCode);
                return SecondFactorOutcome.Unavailable;
            }

            var body = response.Content.ReadFromJsonAsync<OwnerSecondFactorVerifyResponse>().GetAwaiter().GetResult();
            // 名前だけを受ける（数字の文字列を列挙へ写さない）。Skipped は持ち主が返さない値なので受けない。
            return body?.Outcome is { } name
                && Enum.GetNames<SecondFactorOutcome>().Contains(name, StringComparer.Ordinal)
                && Enum.Parse<SecondFactorOutcome>(name) is var outcome && outcome != SecondFactorOutcome.Skipped
                ? outcome
                : SecondFactorOutcome.Unavailable;
        }
        catch (Exception ex)
        {
            // 呼び手の取り消しの口は無い（同期の門）ので、時間切れ（TaskCanceledException）も含めて閉じる向きへ倒す。
            logger.LogWarning(ex, "{Marker} 状態の持ち主へ届きませんでした。確かめられないものとして扱います。", OwnerSecondFactorGate.LogMarker);
            return SecondFactorOutcome.Unavailable;
        }
    }
}
