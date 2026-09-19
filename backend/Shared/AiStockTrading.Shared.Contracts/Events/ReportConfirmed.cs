namespace AiStockTrading.Shared.Contracts.Events;

// FR-07, FR-09, UC-03〜05, IADR-0024: 報告書が利用者により確定された（Draft→Confirmed の遷移時のみ発行）。
// 通知サービスが購読して Discord 通知する。KB 保存など後続の購読者もここに追加する。
//
// FR-09, UC-03, ADR-0003, IADR-0240 決定11, #774: **確定者と認可の主体を分けて残す。**
//   Actor        = 実際に確定を操作した利用者。Discord Bot 経由の確定では、Bot が多層認証で解決し本文で運んだ
//                  Keycloak 利用者名（報告書サービスが**信頼するクライアントのトークンに限って**採る）。
//   AuthorizedBy = 代理確定のときの認可の主体（owner マップ機密クライアントの ID＝トークンの azp）。
//                  利用者本人のトークンで確定したときは null。
// 追加は末尾・任意（既定 null）。旧形式（AuthorizedBy 無し）の JSON はそのまま読める（後方互換の追加のみ）。
public record ReportConfirmed(
    string PeriodKey,
    string Kind,
    string Actor,
    int AssumptionsVersion,
    DateTimeOffset ConfirmedAt,
    string? AuthorizedBy = null);
