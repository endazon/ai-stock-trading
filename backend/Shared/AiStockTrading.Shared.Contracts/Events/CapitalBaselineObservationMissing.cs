namespace AiStockTrading.Shared.Contracts.Events;

// FR-10, FR-09, FR-11, SC-03, #1423 M-3, ADR-0073 決定1〜3, IADR-0581:
// **基準資金（equity）の口座照会が、直前の米国東部暦日に 1 件も成功していない**ため、新規建てを拒否している。
//
// リスク管理（RiskManagementService）の巡回が検知し、**同じ取引日（米国東部の暦日）につき 1 回だけ**発行する
// （審査のたびに鳴らして本物の警告を埋もれさせない。ADR-0073 決定3）。NotificationService が「リスク統制の発動」
// として利用者へ知らせ（緊急度: 高）、AuditService が中央監査台帳へ残す（FR-11）。
//
// 🔴 **手仕舞い・損切りは止めていない**（ADR-0041 決定2・FR-10）。解除は自動である
// （直前の暦日の観測が成功すれば、次の審査から供給に戻る。ADR-0073 決定2）。
//
// TradingDay: 新規建てを拒否している取引日（判定した時点の米国東部の暦日）。
// LatestObservedTradingDay: 最後に観測できた取引日（米国東部の暦日）。
// Stale: 最新の観測が鮮度（既定 4 日）も超えている（巡回が長く止まっている）。
public record CapitalBaselineObservationMissing(
    DateOnly TradingDay,
    DateOnly LatestObservedTradingDay,
    bool Stale,
    DateTimeOffset OccurredAt);
