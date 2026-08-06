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

// ---- 監視設定（GET /monitor/settings） ----
// FR-03, FR-13, SC-01 §2, #340, IADR-0155: 収集パラメータ（変動閾値・クールダウン）と監視銘柄。
// **`cooldown` は `TimeSpan` であり JSON では `"00:15:00"` の文字列で往来する**（数値ではない）。
export interface MarketMonitorSettings {
  /** 変動閾値（前回判断時点価格比。0.03 = ±3%）。**比率であり百分率ではない。** */
  movementThresholdRatio: number;
  /** 同一銘柄の再トリガー抑制期間（`"HH:mm:ss"` 形式の TimeSpan 文字列）。 */
  cooldown: string;
  monitoredSymbols: MonitoredSymbol[];
}

// ---- 変更履歴（GET /monitor/watchlist/history・GET /monitor/settings/history） ----
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
// 0=WatchlistSymbolAdded, 1=WatchlistSymbolRemoved, 2=MovementThresholdChanged, 3=CooldownChanged（#340 で末尾追加）。
const MONITOR_CHANGE_TYPE_LABELS: Record<number, string> = {
  0: '追加',
  1: '削除',
  2: '変動閾値',
  3: 'クールダウン',
};

// FR-13, SC-01 §2, #340: 収集パラメータの履歴を絞り込むための種別値。
export const MONITOR_CHANGE_TYPE_MOVEMENT_THRESHOLD = 2;
export const MONITOR_CHANGE_TYPE_COOLDOWN = 3;

// 写像テーブルに無い数値は「不明(N)」で表示する（安全側フォールバック・画面を壊さない）。
function labelOf(map: Record<number, string>, value: number): string {
  return map[value] ?? `不明(${value})`;
}

export const monitorChangeTypeLabel = (v: number): string => labelOf(MONITOR_CHANGE_TYPE_LABELS, v);

// ---- FR-03, FR-13, SC-01 §2, #340, IADR-0155: 変動閾値の値域（画面側の即時提示） ----
//
// **実効はサーバ側（`MonitorSettingsBounds`）である。** ここに同じ表を持つのは利用者へ即時に提示する
// ためであり（サーバだけだと保存を押すまで誤りが分からない）、画面が統制の実効を担うためではない
// （画面だけの関門は API 直叩きで消える＝IADR-0141 決定1 と同じ判断）。
// **値はサーバ側の `MonitorSettingsBounds` と一致していなければならない**（片方だけ変えると、画面は
// 通すのにサーバが 400 を返す／その逆になる）。
//
// 画面の入力単位は**百分率**（3 ＝ ±3%）、ワイヤは**比率**（0.03）である（SC-02 の上限入力と同じ規律。
// IADR-0151 決定1）。変換は `percentTextToRatio` / `ratioToPercentText`（risk/contracts）だけを通す。

/** 変動閾値の下限（**含まない**。百分率）。0 以下ではあらゆる変動が閾値超過となり監視が統制にならない。 */
export const MOVEMENT_THRESHOLD_MIN_PERCENT_EXCLUSIVE = 0;

/** 変動閾値の上限（**含む**。百分率）。50% 超では 1 日で半値になる変動でしか発火せず監視が事実上無効になる。 */
export const MOVEMENT_THRESHOLD_MAX_PERCENT = 50;

/** 値域を利用者向けの文言にする（入力欄のヘルプと警告の双方で使う）。 */
export const MOVEMENT_THRESHOLD_RANGE_TEXT =
  `${MOVEMENT_THRESHOLD_MIN_PERCENT_EXCLUSIVE} 超 ${MOVEMENT_THRESHOLD_MAX_PERCENT} 以下（%）`;

/**
 * 入力欄の値（百分率の文字列）が値域に収まるか。収まらなければ利用者向けの説明を返す（収まれば null）。
 * **空欄・非数値を黙って 0 として扱わない**（0 は「すべての変動で発火する」設定であり危険側）。
 */
export function validateMovementThresholdPercent(text: string): string | null {
  if (text.trim() === '') return '変動閾値を入力してください。';
  const value = Number(text);
  if (!Number.isFinite(value)) return '変動閾値は数値（%）で入力してください。';
  if (value <= MOVEMENT_THRESHOLD_MIN_PERCENT_EXCLUSIVE || value > MOVEMENT_THRESHOLD_MAX_PERCENT) {
    return `変動閾値は ${MOVEMENT_THRESHOLD_RANGE_TEXT} の範囲で入力してください。`;
  }
  return null;
}
