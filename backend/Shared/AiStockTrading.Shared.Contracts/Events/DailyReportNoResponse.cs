namespace AiStockTrading.Shared.Contracts.Events;

// FR-07, FR-09, FR-11, UC-03, #1423 M-6, ADR-0075 決定4, IADR-0583: 提示した日報が承認待ちのまま期限（翌営業日の米国市場の開場）を過ぎた。
//
// 報告書サービスの常駐が検知し、**期間キーごとに 1 回**発行する。設定が継続でも停止でも出す（UC-03「その旨を通知」）。
// NotificationService が Discord へ知らせ（継続は Warning・停止は Critical）、AuditService が台帳へ残す。
//
// TradingHalted: 設定が「停止」で、日報が確定するまで取引判断の全体を見送っている（損切り・手仕舞いは止めない）。
//                false は直近の確定済み日報の方針で取引を続けている。
// LastConfirmedPeriodKey: 効いている（直近の確定済み）日報の期間キー。確定済みが 1 件も無ければ null。
public record DailyReportNoResponse(
    string PeriodKey,
    DateOnly ReportDate,
    DateTimeOffset Deadline,
    bool TradingHalted,
    string? LastConfirmedPeriodKey,
    DateTimeOffset OccurredAt);
