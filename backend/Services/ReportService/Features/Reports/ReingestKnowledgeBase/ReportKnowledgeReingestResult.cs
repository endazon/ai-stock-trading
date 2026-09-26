using ReportService.Domain;

namespace ReportService.Features.Reports.ReingestKnowledgeBase;

// FR-08, #1028, IADR-0436 決定 3: 報告書 1 件の入れ直しの結果（原則 A: 送った／既に在る／送らなかった／失敗／不明を分ける）。
public enum ReportKnowledgeReingestOutcome
{
    /// <summary>KB に無かったので本文つきで作った。</summary>
    Created,

    /// <summary>KB に本文なしの写しがあったので本文を入れた（#565 の修復。文書は増えない）。</summary>
    BodyAttached,

    /// <summary>refreshExisting の指定で、本文のある写しへ本文を入れ直した（索引の作り直し。文書は増えない）。</summary>
    BodyRefreshed,

    /// <summary>KB に本文つきの写しが既に在る。何も送っていない。</summary>
    AlreadyPresent,

    /// <summary>本文が空なので送らなかった（#565 と同じ扱い）。</summary>
    SkippedEmptyBody,

    /// <summary>本文が上限（1 MB・UTF-8）を超えるので送らなかった（基盤が 413 で拒否するため）。</summary>
    SkippedBodyTooLarge,

    /// <summary>失敗した（拒否された・届かなかった）。KB は変わっていない。</summary>
    Failed,

    /// <summary>結果が分からない（タイムアウト等）。KB に入ったかもしれない。次の実行は一覧で見つければ重複させない。</summary>
    Unknown,

    /// <summary>呼び出しが途中で打ち切られ、試していない。</summary>
    NotAttempted,
}

// FR-08, #1028: 報告書 1 件の行。MatchedCopies は KB 上で一致した写しの数（2 以上＝重複があり、どれを使ったかは DocumentId）。
public sealed record ReportKnowledgeReingestItem(
    string PeriodKey,
    ReportKind Kind,
    ReportKnowledgeReingestOutcome Outcome,
    Guid? DocumentId,
    string? Reason,
    int MatchedCopies = 0);

// FR-08, #1028, IADR-0436 決定 3: 入れ直しの応答。Status は "Completed" / "Aborted" / "Cancelled"。
// Sent = Created + BodyAttached + BodyRefreshed、Skipped = SkippedEmptyBody + SkippedBodyTooLarge。
// AuditPublished=false は「実行はしたが監査への発行に失敗した」（黙って捨てない）。
public sealed record ReportKnowledgeReingestResult(
    Guid RunId,
    string Status,
    string? AbortReason,
    string Scope,
    bool RefreshExisting,
    int Targeted,
    int Sent,
    int Created,
    int BodyAttached,
    int BodyRefreshed,
    int AlreadyPresent,
    int Skipped,
    int SkippedEmptyBody,
    int SkippedBodyTooLarge,
    int Failed,
    int Unknown,
    int NotAttempted,
    int DuplicatesInKb,
    IReadOnlyList<ReportKnowledgeReingestItem> Items,
    bool AuditPublished);

// FR-08, #1028: 入れ直しの要求。全件は All=true で明示する。RefreshExisting は本文のある写しにも本文を入れ直す（既定 false）。
public sealed record ReportKnowledgeReingestRequest(
    bool? All = null,
    string? FromPeriodKey = null,
    string? ToPeriodKey = null,
    bool? RefreshExisting = null);
