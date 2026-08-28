namespace ReportService.Application.State;

// FR-07, ADR-0003: 確定済み日報の方針。取引判断（#11）の IDailyPolicyProvider の実データ源（結線は後続 #22）。
public sealed record ConfirmedDailyPolicy(DateOnly Date, string Summary, int AssumptionsVersion);

// 確定操作の結果。Transitioned=true は Draft→Confirmed の遷移が起きたこと（イベント発行対象）。
public sealed record ConfirmResult(ReportService.Domain.TradingReport Report, bool Transitioned);
