using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiStockTrading.Shared.Infrastructure.Composable.Observability;

/// <summary>
/// 🔴 NFR, FR-10, FR-11, #1092, IADR-0462 決定1: 建玉照会・保有照会の成功／失敗を報告するポート。
/// <para>
/// <b>照会するたびに呼んでよい</b>——状態の変化の判定と重複の抑止は実装が持つ（呼び出し元へ配らない。
/// <c>IFxSourceStatusNotifier</c> と同じ形）。<b>例外を投げない</b>（照会した側の挙動を変えない）。
/// </para>
/// </summary>
public interface IPositionQueryHealthReporter
{
    /// <param name="source">照会の発生源。</param>
    /// <param name="succeeded">照会できたか（不明・例外は false）。</param>
    /// <param name="failureKind">失敗の種類（分かるときだけ）。</param>
    Task ReportAsync(PositionQuerySource source, bool succeeded, string? failureKind = null);
}

/// <summary>何もしない既定（単体の試験・未配線の構成）。本番は <see cref="PositionQueryHealthReporter"/> を配線する。</summary>
public sealed class NoOpPositionQueryHealthReporter : IPositionQueryHealthReporter
{
    public static NoOpPositionQueryHealthReporter Instance { get; } = new();

    public Task ReportAsync(PositionQuerySource source, bool succeeded, string? failureKind = null) => Task.CompletedTask;
}

/// <summary>状態の変化 1 件（発行する事実と、発行に失敗したときに戻す先）。</summary>
public sealed record PositionQueryHealthTransition(PositionQueryStatusChanged Event)
{
    internal long Version { get; init; }

    internal PositionQueryHealthState Before { get; init; } = PositionQueryHealthState.Initial;
}

/// <summary>発生源 1 つの状態（<see cref="PositionQueryHealthTracker"/> の中だけで使う）。</summary>
internal sealed record PositionQueryHealthState(
    PositionQueryStatus Status, DateTimeOffset? FailingSince, int FailedQueries, long Version)
{
    public static readonly PositionQueryHealthState Initial = new(PositionQueryStatus.Unknown, null, 0, 0);
}

/// <summary>
/// 🔴 NFR, FR-10, #1092, IADR-0462 決定1・決定2: 発生源ごとの照会の状態（プロセスの中だけ・永続しない）。
/// <list type="bullet">
/// <item>Unknown（起動直後）→ 失敗／成功: 変化として返す（再起動で状態が消えても最初の失敗は必ず出る）。</item>
/// <item>Healthy → 失敗、Failing → 成功: 変化として返す（回復には失敗の始まりと回数を載せる）。</item>
/// <item>Healthy → 成功、Failing → 失敗: 返さない（周期ごとに洪水させない）。失敗の回数だけ数える。</item>
/// </list>
/// 期間の集計はしない（台帳が権威。IADR-0254 決定3）。ここが持つのは「次に出すか」だけである。
/// </summary>
public sealed class PositionQueryHealthTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<PositionQuerySource, PositionQueryHealthState> _states = [];
    private long _version;

    /// <summary>観測 1 回。状態が変わったら発行する事実を返す（変わらなければ null）。状態は先に進める（戻すのは <see cref="Revert"/>）。</summary>
    public PositionQueryHealthTransition? Observe(
        PositionQuerySource source, bool succeeded, string? failureKind, DateTimeOffset at)
    {
        lock (_gate)
        {
            var before = _states.GetValueOrDefault(source) ?? PositionQueryHealthState.Initial;
            if (succeeded)
            {
                if (before.Status == PositionQueryStatus.Healthy)
                    return null;

                var after = new PositionQueryHealthState(PositionQueryStatus.Healthy, null, 0, ++_version);
                _states[source] = after;
                return new PositionQueryHealthTransition(
                    new PositionQueryStatusChanged(
                        source, PositionQueryStatus.Healthy, before.Status, FailureKind: null,
                        before.FailingSince, before.FailedQueries, at))
                { Version = after.Version, Before = before };
            }

            if (before.Status == PositionQueryStatus.Failing)
            {
                _states[source] = before with { FailedQueries = before.FailedQueries + 1 };
                return null;
            }

            var failing = new PositionQueryHealthState(PositionQueryStatus.Failing, at, 1, ++_version);
            _states[source] = failing;
            return new PositionQueryHealthTransition(
                new PositionQueryStatusChanged(
                    source, PositionQueryStatus.Failing, before.Status, failureKind, at, 1, at))
            { Version = failing.Version, Before = before };
        }
    }

    /// <summary>
    /// 発行に失敗した変化を戻す（次の観測で出し直す）。その後に別の変化が起きていたら何もしない（新しい状態を巻き戻さない）。
    /// </summary>
    public void Revert(PositionQueryHealthTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        lock (_gate)
        {
            var source = transition.Event.Source;
            if (_states.TryGetValue(source, out var current) && current.Version == transition.Version)
                _states[source] = transition.Before;
        }
    }
}

/// <summary>
/// 🔴 NFR, FR-10, FR-11, #1092, IADR-0462 決定1・決定3: 状態が変わったときだけ <see cref="PositionQueryStatusChanged"/> を発行する実装。
/// <para>
/// 発行口は呼び出し側が渡す（Wolverine のランタイムの <c>MessageBus</c>。ハンドラの処理中に呼ばれても、その処理が例外で終わったときに
/// 記録が捨てられないようにするため）。発行に失敗したら状態を戻し、警告ログを残して例外を飲む。
/// </para>
/// <para>サービスごとに singleton で 1 つだけ登録する（状態を業務クラスの間で共有する）。</para>
/// </summary>
public sealed class PositionQueryHealthReporter(
    Func<object, ValueTask> publish,
    TimeProvider timeProvider,
    ILogger<PositionQueryHealthReporter>? logger = null) : IPositionQueryHealthReporter
{
    private readonly PositionQueryHealthTracker _tracker = new();
    private readonly ILogger _logger = logger ?? NullLogger<PositionQueryHealthReporter>.Instance;

    public async Task ReportAsync(PositionQuerySource source, bool succeeded, string? failureKind = null)
    {
        var transition = _tracker.Observe(source, succeeded, failureKind, timeProvider.GetUtcNow());
        if (transition is null)
            return;

        var e = transition.Event;
        try
        {
            await publish(e).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _tracker.Revert(transition);
            _logger.LogWarning(
                ex, "照会の状態の変化を監査台帳へ発行できませんでした（次の照会で出し直します）: source={Source} status={Status}",
                e.Source, e.Status);
            return;
        }

        if (e.Status == PositionQueryStatus.Failing)
        {
            _logger.LogWarning(
                "照会が失敗に変わりました（監査台帳へ記録）: source={Source} previous={Previous} kind={Kind}",
                e.Source, e.PreviousStatus, e.FailureKind);
        }
        else
        {
            _logger.LogInformation(
                "照会が成功に変わりました（監査台帳へ記録）: source={Source} previous={Previous} failingSince={Since} failedQueries={Count}",
                e.Source, e.PreviousStatus, e.FailingSince, e.FailedQueries);
        }
    }
}
