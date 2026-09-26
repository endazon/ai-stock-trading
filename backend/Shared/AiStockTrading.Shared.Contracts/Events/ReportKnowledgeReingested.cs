using AiStockTrading.Shared.Contracts.Operations;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-08, FR-11, #1028, IADR-0436 決定 4: 所有者が確定済みの報告書を KB へ入れ直した（`POST /reports/knowledge-base/reingest`）。
// 1 回の実行につき 1 件、報告書サービスが発行し、監査サービスが中央監査台帳へ記録する（誰が・何件・失敗の内訳）。
//
// - `Actor` は操作したトークンの主体（名前 → `client:<azp>` → `unknown`）。
// - `Status` は "Completed"（実行した。個別の失敗を含み得る）／"Aborted"（KB の一覧を引けず 1 件も書いていない。
//   理由は `AbortReason`）／"Cancelled"（呼び出しが途中で打ち切られ、残りを試していない）。列挙は wire に晒さず文字列で運ぶ
//   （`ReportConfirmed.Kind` と同じ）。
// - 件数は結果ごと（原則 A: 送った＝Created/BodyAttached/BodyRefreshed、既に在る＝AlreadyPresent、
//   送らなかった＝Skipped*、失敗＝Failed、結果不明＝Unknown、試していない＝NotAttempted）。
//   **`Targeted` はそれらの合計と常に等しい**（中止では全件が NotAttempted）。
// - `Breakdown` は送らなかった／失敗／不明の報告書ごとの内訳（期間キー・結果・理由）。上限を超えた件数は `BreakdownOmitted`。
// - `DuplicatePeriodKeys` は KB 上に一致する写しが 2 件以上あった報告書の期間キー（件数は `DuplicatesInKb`。上限は内訳と同じ）。
public record ReportKnowledgeReingested(
    Guid RunId,
    string Actor,
    string Scope,
    bool RefreshExisting,
    string Status,
    string? AbortReason,
    int Targeted,
    int Created,
    int BodyAttached,
    int BodyRefreshed,
    int AlreadyPresent,
    int SkippedEmptyBody,
    int SkippedBodyTooLarge,
    int Failed,
    int Unknown,
    int NotAttempted,
    int DuplicatesInKb,
    IReadOnlyList<string> DuplicatePeriodKeys,
    IReadOnlyList<ReportKnowledgeReingestEntry> Breakdown,
    int BreakdownOmitted,
    DateTimeOffset OccurredAt);
