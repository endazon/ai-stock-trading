using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-06, FR-14, 計画 ADR-0052 決定 1・4・5, #1156, IADR-0491 決定 3・5: `/report regenerate` の試行の台帳（report_svc の
// report_regeneration_attempts 表）。`/policy` の台帳（IPolicyRevisionLedger）とは**別の表・別の枠**である（ADR-0052 決定 1）。
//
// - **数える行**: 上限の門を通って LLM を呼ぶ直前に書いた行（Pending → Regenerated / AiFailed / SaveFailed）。
//   応答が返らなかった呼び出しも費用が掛かり得るため数える（`/policy` と同じ）。
// - **数えない行**: 中核の入力の取得に失敗して断った行（RefusedCoreUnsupplied。ADR-0052 決定 4「回数は消費しない」）と、
//   上限で断った行（LimitReached）。どちらも月報 §7 の「断り n 回」「上限到達 n 日」の素であり、監査の記録である。
// - 行は作り直した版の記録も兼ねる（ADR-0052 決定 5: 作り直した旨・日時・なお未供給だった入力・誰が・どの版を）。
public interface IReportRegenerationLedger
{
    /// <summary>指定した JST の暦日に記録された**数える**試行の数（断った行は含めない）。</summary>
    int CountOn(DateOnly jstDate);

    /// <summary>
    /// その JST の暦日の数える試行の数が <paramref name="dailyLimit"/> 未満なら 1 行書く（Pending）。**数えることと書くことを 1 つの排他区間で
    /// 行う**（`/policy` の台帳と同じ。Postgres では暦日を鍵にした勧告ロック、それ以外はプロセス内の排他）。
    /// </summary>
    ReportRegenerationBeginResult TryBegin(ReportRegenerationAttempt attempt, int dailyLimit);

    /// <summary>断った試行（上限・中核の入力の取得失敗）を記録する。**数えない**。</summary>
    void RecordRefusal(ReportRegenerationAttempt attempt);

    /// <summary>試行を結果で閉じる（作り直した版・なお未供給だった入力・期間の時点に復元できなかった入力）。</summary>
    void Complete(
        Guid id, ReportRegenerationOutcome outcome, int? reportVersion, string? unsuppliedInputs, string? notRestorableInputs);

    /// <summary>試行を引く（無ければ null）。</summary>
    ReportRegenerationAttempt? Find(Guid id);

    /// <summary>
    /// FR-06, 計画 ADR-0052 決定 1, IADR-0491 決定 6: 期間 [<paramref name="from"/>, <paramref name="to"/>]（JST の暦日）の集計（月報 §7）。
    /// 上限到達の日数は「上限で断った行がある日」と「数える試行が <paramref name="dailyLimit"/> に届いた日」の和集合。
    /// </summary>
    ReportRegenerationTally Tally(DateOnly from, DateOnly to, int dailyLimit);
}

// TryBegin の結果。Begun=false なら書いていない（上限に達していた）。UsedBefore は書く前のその日の数える試行の数。
public sealed record ReportRegenerationBeginResult(bool Begun, int UsedBefore);

// 試行の結果。列挙名で永続化する（序数に結合しない）。
public enum ReportRegenerationOutcome
{
    /// <summary>LLM の呼び出し中（または呼び出し中にプロセスが落ちた）。数える。</summary>
    Pending,

    /// <summary>作り直して保存した。数える。</summary>
    Regenerated,

    /// <summary>散文の組み立て・入力の取得の途中で例外になり保存していない。数える（費用が掛かり得た）。</summary>
    AiFailed,

    /// <summary>保存に失敗した（並行更新・確定済みへの競合）。数える。</summary>
    SaveFailed,

    /// <summary>中核の入力の取得に失敗したので断った（ADR-0052 決定 4）。**数えない**。</summary>
    RefusedCoreUnsupplied,

    /// <summary>1 日の上限で断った。**数えない**。</summary>
    LimitReached,
}

public static class ReportRegenerationOutcomes
{
    /// <summary>1 日の回数の上限に数える結果か（断った行は数えない）。</summary>
    public static bool Counts(ReportRegenerationOutcome outcome) =>
        outcome is not (ReportRegenerationOutcome.RefusedCoreUnsupplied or ReportRegenerationOutcome.LimitReached);
}

/// <param name="Id">試行の識別子。</param>
/// <param name="AttemptedAt">試行の時刻（UTC）。</param>
/// <param name="JstDate">回数上限を数える暦日（JST）。</param>
/// <param name="Actor">作り直しを指示した利用者（多層認証・代理の解決後）。</param>
/// <param name="PeriodKey">対象の会話キー。</param>
/// <param name="PreviousVersion">作り直す前の版（読んだ時点の版）。</param>
/// <param name="Outcome">結果。</param>
/// <param name="ReportVersion">作り直して保存した版（Regenerated のときだけ）。</param>
/// <param name="UnsuppliedInputs">作り直した版でもなお未供給だった入力（`ReportInputs.Serialize` の形）。断った行では取得に失敗した入力。</param>
/// <param name="NotRestorableInputs">期間の時点に復元できないため未供給として扱った入力（同じ形）。</param>
public sealed record ReportRegenerationAttempt(
    Guid Id,
    DateTimeOffset AttemptedAt,
    DateOnly JstDate,
    string Actor,
    string PeriodKey,
    int PreviousVersion,
    ReportRegenerationOutcome Outcome = ReportRegenerationOutcome.Pending,
    int? ReportVersion = null,
    string? UnsuppliedInputs = null,
    string? NotRestorableInputs = null);
