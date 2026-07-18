// FR-03, FR-11, FR-13, UC-06, IADR-0088, IADR-0090: MarketMonitorService `/monitor/watchlist`（OwnerOnly）の
// 応答型と数値 enum の表示ラベル写像。SC-02（リスク設定）の監視銘柄セクションが消費する。
// バックエンド（MarketMonitor Worker）は HTTP 応答に JsonStringEnumConverter を設定していないため enum は「数値」で届く。
// 市場（Market）は Risk と同じ共有 enum のため `risk/contracts` の写像を再利用し、監視銘柄固有の変更種別のみ本ファイルで写像する。
// 未知値は安全側フォールバック表示にする（画面を壊さない・fail-safe）。

// ---- 監視銘柄（GET /monitor/watchlist） ----
// MonitoredSymbol（backend: MarketMonitor.Domain.MonitoredSymbol）。market は数値 enum（Trading.Market）。
export interface MonitoredSymbol {
  symbol: string;
  market: number; // Market enum（数値）
}

// ---- 変更履歴（GET /monitor/watchlist/history） ----
// MonitorSettingsChangeEntry（backend: MarketMonitor.Application.State）。Risk の SettingsChangeEntry をミラーするが
// changeType は別 enum（MonitorSettingsChangeType）である点に注意する。
export interface MonitorSettingsChangeEntry {
  actor: string;
  changeType: number; // MonitorSettingsChangeType enum（数値）
  reason: string;
  changedAt: string; // DateTimeOffset（ISO 文字列）
  before?: string | null;
  after?: string | null;
}

// MonitorSettingsChangeType（MonitorSettingsChangeEntry.cs の列挙順）。Risk の SettingsChangeType とは別系統。
// 0=WatchlistSymbolAdded, 1=WatchlistSymbolRemoved。
const MONITOR_CHANGE_TYPE_LABELS: Record<number, string> = {
  0: '追加',
  1: '削除',
};

// 写像テーブルに無い数値は「不明(N)」で表示する（安全側フォールバック・画面を壊さない）。
function labelOf(map: Record<number, string>, value: number): string {
  return map[value] ?? `不明(${value})`;
}

export const monitorChangeTypeLabel = (v: number): string => labelOf(MONITOR_CHANGE_TYPE_LABELS, v);
