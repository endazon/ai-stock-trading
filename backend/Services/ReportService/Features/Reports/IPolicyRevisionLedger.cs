namespace ReportService.Features.Reports;

// FR-14, FR-07, ADR-0042 決定 3, #1024, IADR-0432 決定 1: `/policy` の試行の台帳。
// **LLM を呼ぶ前に 1 行書き、結果で閉じる。** 1 日の回数上限（JST の暦日）はこの台帳の行数で判定する。
// 行は案の監査記録も兼ねる（誰が・いつ・どの会話キーに・何が起きたか・案の入れ替え）。
//
// 🔴 **呼ぶ前に数える。** 応答が返らなかった（タイムアウト・例外）呼び出しも費用が掛かり得るため、上限には数える。
public interface IPolicyRevisionLedger
{
    /// <summary>指定した JST の暦日に記録された試行の数。</summary>
    int CountOn(DateOnly jstDate);

    /// <summary>試行を記録する（結果は <see cref="PolicyRevisionAttemptOutcome.Pending"/>）。</summary>
    Guid Begin(PolicyRevisionAttempt attempt);

    /// <summary>試行を結果で閉じる。</summary>
    void Complete(Guid id, PolicyRevisionAttemptOutcome outcome, int? reportVersion, string? watchlistChangesJson);

    /// <summary>試行を引く（無ければ null）。</summary>
    PolicyRevisionAttempt? Find(Guid id);
}

// 試行の結果。Pending は LLM の呼び出し中（または呼び出し中にプロセスが落ちた）。
public enum PolicyRevisionAttemptOutcome
{
    Pending,
    Proposed,
    AiFailed,
    SaveFailed,
}

/// <param name="Id">試行の識別子。</param>
/// <param name="AttemptedAt">試行の時刻（UTC）。</param>
/// <param name="JstDate">回数上限を数える暦日（JST）。</param>
/// <param name="Actor">指示者（多層認証・代理の解決後）。</param>
/// <param name="PeriodKey">対象の会話キー。</param>
/// <param name="Outcome">結果。</param>
/// <param name="ReportVersion">案を保存した報告書の版（Proposed のときだけ）。</param>
/// <param name="WatchlistChangesJson">案の監視銘柄の入れ替え（JSON。Proposed のときだけ）。</param>
public sealed record PolicyRevisionAttempt(
    Guid Id,
    DateTimeOffset AttemptedAt,
    DateOnly JstDate,
    string Actor,
    string PeriodKey,
    PolicyRevisionAttemptOutcome Outcome = PolicyRevisionAttemptOutcome.Pending,
    int? ReportVersion = null,
    string? WatchlistChangesJson = null);
