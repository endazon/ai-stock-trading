// FR-17, UC-06, ADR-0007, IADR-0080: 本ユニット固有のロール定数。
// 利用者（設定変更・kill switch 等を行える本人）は Keycloak ロール `trading-owner`（バックエンドの OwnerOnly と同一ソース）。
// foundation の PlatformRole は platform 管理者/運用者向けのため、本ユニットはこの定数を RequireRole へ渡す（foundation 無改修）。
// 認可の実効境界はサーバ側（403/404）に置き、本定数は表示制御・存在秘匿の出し分け専用（IADR-0009/0035）。
export const TradingRole = {
  Owner: 'trading-owner',
} as const;
