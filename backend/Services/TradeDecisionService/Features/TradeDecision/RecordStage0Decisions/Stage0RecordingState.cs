using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AiStockTrading.Shared.Contracts.Backtest;

namespace TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;

// FR-15, NFR（費用）, ADR-0033 決定5, #1342, IADR-0537: Stage 0 の記録の**実行の状態**（完了の印・途中の記録・費用の累計）。
//
// 🔴 記録器は trade-decision の起動ごとに走る（run-once）。PoC のホストは停止・一斉再起動を繰り返す（MSP#1919）ため、
// 「実行したらすぐ Enabled=false に戻す」運用では同じ承認値の記録が二重に課金される。状態を永続領域に残して止める。
//
// 窓の両端は「記録の開始（開始の印）」と「完了の印」である（規則 11。作業仕様書 20261014_1342 の表）。
//   - 開始の印も完了の印も無い → 初回として走る。
//   - 完了（または上限で停止）の印がある → LLM を呼ばずに終える。
//   - 開始の印だけがある（途中で落ちた） → チェックポイントの判断時点を飛ばして**途中から再開**する。
//   - 印・チェックポイントが読めない → 走らせない（人が状態のファイルを消すまで）。
//
// 費用の累計（台帳）は印と**別の鍵・別の置き場**で持つ。印が壊れても、台帳の累計が承認額に届けば止まる（両端で止める）。

/// <summary>実行の段階。</summary>
public enum Stage0RunPhase
{
    /// <summary>開始の印が無い（初回）。</summary>
    NotStarted,

    /// <summary>開始の印はあるが終わりの印が無い（途中で落ちた）。チェックポイントから再開する。</summary>
    InProgress,

    /// <summary>最後まで記録し、書き出した（完了の印）。</summary>
    Completed,

    /// <summary>費用の上限で止め、ここまでの記録を書き出した（終わりの印）。</summary>
    Stopped,

    /// <summary>🔴 印かチェックポイントが読めない。**走らせない**（人が状態のファイルを消すまで）。</summary>
    Unreadable,
}

/// <summary>読み出した実行の状態。<paramref name="Checkpoint"/> は <see cref="Stage0RunPhase.InProgress"/> のときだけ意味を持つ（null＝まだ 1 件も無い）。</summary>
public sealed record Stage0RunState(Stage0RunPhase Phase, Stage0DecisionRecordSet? Checkpoint = null, string? Detail = null);

/// <summary>
/// 実行の状態の置き場（完了の印・チェックポイント・実行のロック）。
/// 🔴 **書き込みの失敗は false**（例外にしない）。記録器は false を見たら LLM を呼ばずに止める。
/// </summary>
public interface IStage0RecordingStateStore
{
    /// <summary>実行のロックを取る。取れなければ null（ほかの実行が走っている）。</summary>
    IDisposable? TryAcquireRunLock();

    Task<Stage0RunState> LoadAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>開始の印を書く（窓の前の端）。LLM を呼ぶ前に必ず書く。</summary>
    Task<bool> MarkStartedAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>ここまでの記録を書く（判断時点ごと）。</summary>
    Task<bool> SaveCheckpointAsync(string runId, Stage0DecisionRecordSet checkpoint, CancellationToken cancellationToken = default);

    /// <summary>終わりの印を書く（窓の後の端）。<paramref name="phase"/> は Completed か Stopped。</summary>
    Task<bool> MarkFinishedAsync(string runId, Stage0RunPhase phase, string detail, CancellationToken cancellationToken = default);
}

/// <summary>
/// 記録器が呼んだ LLM の費用の累計（円）の台帳。印とは独立に持つ（#1342 の 2）。
/// </summary>
public interface IStage0RecordingCostLedger
{
    /// <summary>累計を読む。無ければ 0。🔴 **読めなければ null**（記録器は走らせない）。</summary>
    Task<decimal?> ReadAsync(string ledgerId, CancellationToken cancellationToken = default);

    /// <summary>累計を書く。失敗は false。</summary>
    Task<bool> WriteAsync(string ledgerId, decimal cumulativeJpy, CancellationToken cancellationToken = default);
}

/// <summary>安全既定: 状態の置き場が無い（`Stage0Recording:StateDirectory` 未設定）。読めない・書けないと答え、記録は走らない。</summary>
public sealed class NoStage0RecordingStateStore : IStage0RecordingStateStore, IStage0RecordingCostLedger
{
    private const string Reason = "状態の置き場（Stage0Recording:StateDirectory）が構成されていません。";

    public IDisposable? TryAcquireRunLock() => null;

    public Task<Stage0RunState> LoadAsync(string runId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new Stage0RunState(Stage0RunPhase.Unreadable, Detail: Reason));

    public Task<bool> MarkStartedAsync(string runId, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> SaveCheckpointAsync(
        string runId, Stage0DecisionRecordSet checkpoint, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> MarkFinishedAsync(
        string runId, Stage0RunPhase phase, string detail, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<decimal?> ReadAsync(string ledgerId, CancellationToken cancellationToken = default) =>
        Task.FromResult<decimal?>(null);

    public Task<bool> WriteAsync(string ledgerId, decimal cumulativeJpy, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}

// FR-15, ADR-0033 決定5, #1342, IADR-0537 決定2: 実行の同一性（印の鍵）と承認の単位（台帳の鍵）。
//
// 🔴 **書き出し先（OutputPath）・状態の置き場・Enabled は鍵に入れない** —— 置き場を変えただけで別の実行に見えると、
// 同じ承認値の記録がもう一度走る。銘柄は並べ替えて入れる（並びだけの違いで別の実行にしない）。
public static class Stage0RecordingIdentity
{
    /// <summary>印の鍵。記録の中身を決める値と承認値のすべて。</summary>
    public static string RunId(Stage0RecordingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Hash(
            "run/v1",
            ApprovalUnit(options),
            options.LlmTrainingCutoff?.Trim() ?? string.Empty,
            options.Model?.Trim() ?? string.Empty,
            options.ScreeningModel?.Trim() ?? string.Empty,
            options.VoteCount.ToString(CultureInfo.InvariantCulture),
            options.DecisionsPerDay.ToString(CultureInfo.InvariantCulture),
            options.InputTokensPerDecision.ToString(CultureInfo.InvariantCulture),
            options.OutputTokensPerDecision.ToString(CultureInfo.InvariantCulture),
            options.ScreeningInputTokensPerDecision.ToString(CultureInfo.InvariantCulture),
            options.ScreeningOutputTokensPerDecision.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 台帳の鍵。**承認の単位**（承認額・承認回数・期間・銘柄集合）だけで作る —— 印の鍵と別に計算する。
    /// 同じ承認のまま、モデルやトークン量の欄だけを動かしても累計は引き継がれる（上限は承認額のまま）。
    /// </summary>
    public static string LedgerId(Stage0RecordingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Hash("ledger/v1", ApprovalUnit(options));
    }

    private static string ApprovalUnit(Stage0RecordingOptions options)
    {
        var period = options.ParsePeriod();
        var symbols = options.ResolveSymbols()
            .Select(s => $"{s.Symbol}@{s.Market}")
            .Order(StringComparer.Ordinal);
        return string.Join(
            "|",
            options.ApprovedEstimateJpy is { } jpy
                ? decimal.Round(jpy, 2).ToString("0.00", CultureInfo.InvariantCulture)
                : "-",
            options.ApprovedVoteCount?.ToString(CultureInfo.InvariantCulture) ?? "-",
            period is { } p ? $"{p.From:yyyy-MM-dd}..{p.To:yyyy-MM-dd}" : "-",
            string.Join(",", symbols));
    }

    private static string Hash(params string[] parts) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts))));
}
