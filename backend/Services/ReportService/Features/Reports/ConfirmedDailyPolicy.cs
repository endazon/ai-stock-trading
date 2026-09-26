namespace ReportService.Features.Reports;

// FR-07, ADR-0003: 確定済み日報の方針。取引判断（#11）の IDailyPolicyProvider の実データ源（結線は後続 #22）。
public sealed record ConfirmedDailyPolicy(DateOnly Date, string Summary, int AssumptionsVersion);

// 確定操作の結果。Transitioned=true は Draft→Confirmed の遷移が起きたこと（イベント発行対象）。
// FR-07, FR-13, #1025, IADR-0433 決定 7（PR #1027 の監査 H1）: Version は確定後の行の版（確定は版を 1 進める）。
// 冪等な再確定（Transitioned=false）では、**どの版で確定されたか**を呼び出し側が見分けるために使う
// （確定された下書きの版＝Version − 1。VersionedReport.IsConfirmedAtDraftVersion）。
public sealed record ConfirmResult(ReportService.Domain.TradingReport Report, bool Transitioned, int Version = 0);
