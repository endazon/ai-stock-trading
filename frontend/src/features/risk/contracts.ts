// FR-10, FR-13, FR-19, FR-20, UC-06, IADR-0084: RiskManagementService `/risk-controls/*`（OwnerOnly）の
// 応答型と数値 enum の表示ラベル写像。SC-02（リスク設定）と SC-03（統制状態参照）が共有する。
// バックエンド（Risk Worker）は HTTP 応答に JsonStringEnumConverter を設定していないため enum は「数値」で届く。
// フロントは数値→ラベルの写像を持ち、未知値は安全側フォールバック表示にする（画面を壊さない・fail-safe）。

// ---- リスク設定（GET /risk-controls/settings） ----
export interface RiskLimitSettings {
  maxOrderAmount: number;
  maxDailyOrderAmount: number;
  maxOpenPositions: number;
  dailyLossLimitRatio: number;
  perTradeRiskRatio: number;
  maxDrawdownRatio: number;
  losingStreakThreshold: number;
  losingStreakSizeFactor: number;
}

export interface BannedSymbol {
  symbol: string;
  market: number; // Market enum（数値）
  reason: string;
  registeredOn: string; // DateOnly（ISO 文字列）
}

export interface TradingGuardSettings {
  enabledProductTypes: number[]; // ProductType enum（数値）
  enabledMarkets: number[]; // Market enum（数値）
  bannedSymbols: BannedSymbol[];
  preventSameDayReentry: boolean;
  prohibitManipulativeOrderPatterns: boolean;
}

export interface StageSettings {
  stage: number; // TradingStage enum（数値）
  mode: number; // TradeMode enum（数値）
  capitalCap: number;
}

export interface RiskManagementSettings {
  guard: TradingGuardSettings;
  limits: RiskLimitSettings;
  stage: StageSettings;
}

export interface SettingsChangeEntry {
  actor: string;
  changeType: number; // SettingsChangeType enum（数値）
  reason: string;
  changedAt: string; // DateTimeOffset（ISO 文字列）
  before?: string | null;
  after?: string | null;
}

// ---- 統制状態（GET /risk-controls/status） ----
export interface RiskStatusView {
  killSwitchEngaged: boolean;
  dailyLossLockoutActive: boolean;
  lockoutReleaseOn: string | null; // DateOnly?
  tradingPaused: boolean;
  activeControl: number; // ActiveTradingControl enum（数値）
  newEntriesBlocked: boolean;
  stage: number; // TradingStage enum（数値）
  dailyRealizedPnl: number;
  unrealizedPnl: number;
  dailyPnl: number;
  capital: number;
  dailyOrderedAmount: number;
  maxDailyOrderAmount: number;
  drawdownRatio: number;
  maxDrawdownRatio: number;
  openPositionCount: number;
  maxOpenPositions: number;
}

// ---- 段階ゲート（GET /risk-controls/stage-gate） ----
export interface PromotionAssessment {
  targetStage: number | null; // TradingStage?（最上段なら null）
  eligible: boolean;
  unmetCriteria: number[]; // StageGateCriterion enum（数値）
}

export interface WithdrawalAssessment {
  triggered: boolean;
  reason: number | null; // WithdrawalReason?
  haltNewEntries: boolean;
  proposedStage: number | null; // TradingStage?
}

export interface StageTransition {
  sequence: number;
  fromStage: number; // TradingStage enum（数値）
  toStage: number; // TradingStage enum（数値）
  kind: number; // StageTransitionKind enum（数値）
  approvedBy: string;
  occurredAtUtc: string; // DateTimeOffset（ISO 文字列）
  reason: string;
}

export interface StageGateStatus {
  currentStage: number; // TradingStage enum（数値）
  currentSettings: StageSettings;
  history: StageTransition[];
  promotion: PromotionAssessment;
  withdrawal: WithdrawalAssessment;
}

// ---- 数値 enum → 表示ラベルの写像（未知値は安全側フォールバック） ----

// TradingStage（0..3）。バックエンド enum の連番（StageSettings.cs）に対応。
const STAGE_LABELS: Record<number, string> = {
  0: 'Stage 0（検証）',
  1: 'Stage 1（ペーパー）',
  2: 'Stage 2（少額実弾）',
  3: 'Stage 3（拡大実弾）',
};

// TradeMode（0=Paper, 1=Live）。
const MODE_LABELS: Record<number, string> = {
  0: 'ペーパー',
  1: '実弾',
};

// ActiveTradingControl（0=None,1=KillSwitch,2=DailyLossLockout,3=Pause）。
const ACTIVE_CONTROL_LABELS: Record<number, string> = {
  0: 'なし',
  1: '緊急停止（kill switch）',
  2: '日次損失ロックアウト',
  3: '一時停止',
};

// StageTransitionKind（0=Promotion,1=Demotion）。
const TRANSITION_KIND_LABELS: Record<number, string> = {
  0: '昇格',
  1: '差し戻し',
};

// StageGateCriterion（StageTransition.cs の列挙順）。
const CRITERION_LABELS: Record<number, string> = {
  0: 'バックテスト未合格',
  1: 'ペーパー乖離が説明不能',
  2: '統制違反あり',
  3: 'スリッページ/費用が想定超過',
  4: '日次損失上限の運用違反',
  5: '承認なし',
  6: '昇格は 1 段ずつ',
  7: '遷移先が現段階',
  8: '既に最上段',
};

// WithdrawalReason（0=DrawdownBreachedMultiple,1=PaperDeviationUnexplained）。
const WITHDRAWAL_REASON_LABELS: Record<number, string> = {
  0: '実DD がバックテスト最大DD × 倍率に到達',
  1: 'ペーパー乖離が説明不能',
};

// SettingsChangeType（SettingsChangeEntry.cs の列挙順）。
const CHANGE_TYPE_LABELS: Record<number, string> = {
  0: 'ガード',
  1: '上限',
  2: '段階',
  3: '緊急停止 発動',
  4: '緊急停止 解除',
  5: '一時停止',
  6: '再開',
};

// ProductType（0=Cash,1=MarginLong,2=ShortSell）。FR-19 / ADR-0016 決定1・#332: 商品種別は 3 値であり
// それぞれ独立に有効・無効を設定できる（既定は現物のみ有効）。序数はバックエンドの enum と一致させる。
const PRODUCT_TYPE_LABELS: Record<number, string> = {
  0: '現物',
  1: '信用買い',
  2: '空売り',
};

// Market（0=Japan,1=UnitedStates）。
const MARKET_LABELS: Record<number, string> = {
  0: '日本',
  1: '米国',
};

// 写像テーブルに無い数値は「不明(N)」で表示する（安全側フォールバック・画面を壊さない）。
function labelOf(map: Record<number, string>, value: number): string {
  return map[value] ?? `不明(${value})`;
}

// FR-13, FR-19, IADR-0086: 編集 UI 用の選択肢（既知 enum 値のみ）。ガード変更 UI のチェックボックス列挙に用いる。
// バックエンドの数値 enum 表現は変えず、既知値の写像テーブルから選択肢を導出する（未知値は選択肢に出さないが、
// 現在値としての表示は labelOf のフォールバックで安全側に倒れる）。
export interface EnumOption {
  value: number;
  label: string;
}
const optionsOf = (map: Record<number, string>): EnumOption[] =>
  Object.entries(map).map(([v, label]) => ({ value: Number(v), label }));
export const PRODUCT_TYPE_OPTIONS: EnumOption[] = optionsOf(PRODUCT_TYPE_LABELS);
export const MARKET_OPTIONS: EnumOption[] = optionsOf(MARKET_LABELS);
// ProductType の信用買い・空売り。新規有効化を「危険な緩和」と判定するための定数（IADR-0086 決定 3）。
// **空売りは損失に上限が無い**ため（ADR-0016）、信用買いと同様に危険な緩和として確認を求める。
export const PRODUCT_TYPE_MARGIN_LONG = 1;
export const PRODUCT_TYPE_SHORT_SELL = 2;
// 有効化が「危険な緩和」にあたる商品種別（現物以外）。
export const RISKY_PRODUCT_TYPES: readonly { value: number; label: string }[] = [
  { value: PRODUCT_TYPE_MARGIN_LONG, label: '信用買いを有効化' },
  { value: PRODUCT_TYPE_SHORT_SELL, label: '空売りを有効化' },
];

export const stageLabel = (v: number): string => labelOf(STAGE_LABELS, v);
export const modeLabel = (v: number): string => labelOf(MODE_LABELS, v);
export const activeControlLabel = (v: number): string => labelOf(ACTIVE_CONTROL_LABELS, v);
export const transitionKindLabel = (v: number): string => labelOf(TRANSITION_KIND_LABELS, v);
export const criterionLabel = (v: number): string => labelOf(CRITERION_LABELS, v);
export const withdrawalReasonLabel = (v: number): string => labelOf(WITHDRAWAL_REASON_LABELS, v);
export const changeTypeLabel = (v: number): string => labelOf(CHANGE_TYPE_LABELS, v);
export const productTypeLabel = (v: number): string => labelOf(PRODUCT_TYPE_LABELS, v);
export const marketLabel = (v: number): string => labelOf(MARKET_LABELS, v);

// ISO 8601 / DateOnly をロケール表記に整形する。解釈できない値・空はそのまま/ダッシュで表示する（縮退）。
export function formatAt(value: string | null | undefined): string {
  if (!value) return '—';
  const t = Date.parse(value);
  return Number.isNaN(t) ? value : new Date(t).toLocaleString();
}

// 使用率（分子/分母）を % 文字列にする。分母 0・非数は「—」（0 除算を安全側に倒す）。
export function ratioPercent(used: number, limit: number): string {
  if (!Number.isFinite(used) || !Number.isFinite(limit) || limit === 0) return '—';
  return `${((used / limit) * 100).toFixed(1)}%`;
}
