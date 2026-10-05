namespace AiStockTrading.Shared.Contracts.Events;

// FR-06, FR-11, FR-14, 計画 ADR-0052 決定 5, #1156, IADR-0491 決定 5: 所有者が未確定の報告書の下書きを作り直した
// （Discord `/report regenerate <periodKey>`・`POST /reports/{periodKey}/regenerate`）。作り直しが保存できた 1 回につき 1 件、
// 報告書サービスが発行し、監査サービスが中央監査台帳へ記録する（**誰が・どの版を・いつ作り直し、何がなお未供給だったか**）。
//
// - `Kind` は ReportConfirmed と同じく文字列（"Daily"/"Weekly"/"Monthly"）。列挙型を wire 契約に晒さない。
// - `PreviousVersion` は作り直す前の版、`Version` は作り直して保存した版（＝確定に使う版）。
// - `Actor` は作り直した利用者（多層認証・代理の解決後。分からなければ `unknown`）。
// - `UnsuppliedInputs` は作り直した版でもなお未供給だった入力（報告書サービスの `ReportInput` の列挙名。宣言順）。
//   🔴 **期間の時点に復元できないために未供給として扱った入力**（`NotRestorableInputs`）も含む。区別は `NotRestorableInputs` で読む。
// - 断った作り直し（上限・中核の入力の取得失敗）は発行しない（下書きを変えていない。報告書サービスの試行の台帳に残る）。
public record ReportRegenerated(
    string PeriodKey,
    string Kind,
    int PreviousVersion,
    int Version,
    string Actor,
    IReadOnlyList<string> UnsuppliedInputs,
    IReadOnlyList<string> NotRestorableInputs,
    DateTimeOffset RegeneratedAt);
