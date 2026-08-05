// FR-10, FR-13, FR-19, FR-20, UC-06, IADR-0084: RiskManagementService `/risk-controls/*`（OwnerOnly）の
// 応答型と数値 enum の表示ラベル写像。SC-02（リスク設定）と SC-03（統制状態参照）が共有する。
// バックエンド（Risk Worker）は HTTP 応答に JsonStringEnumConverter を設定していないため enum は「数値」で届く。
// フロントは数値→ラベルの写像を持ち、未知値は安全側フォールバック表示にする（画面を壊さない・fail-safe）。

// ---- リスク設定（GET /risk-controls/settings） ----
// FR-10, #329, #389, IADR-0130 決定1: 金額で表す上限は**固定額ではなく equity（自己資金）比**である。
// バックエンドのプロパティ名は `MaxOrderAmountRatio` / `MaxDailyOrderAmountRatio`（BFF は素通しのため
// 変換で吸収されない）。**同名の `RiskStatusView.maxOrderAmount` は equity から解決済みの「実額」であり
// 別物である**（後述）。単位が違うものに同じ名前を使わない。
export interface RiskLimitSettings {
  /** 1 注文あたりの発注金額上限（**equity 比**。既定 0.25 ＝ equity の 25%）。 */
  maxOrderAmountRatio: number;
  /** 1 日あたりの発注金額上限（**equity 比・日次**。既定 1.50 ＝ equity の 150%/日）。 */
  maxDailyOrderAmountRatio: number;
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
  // FR-20, #334: 段階が定める**既定の発注先**（BrokerProvider enum・数値）。現在の発注先ではない
  // （現在値は RiskManagementSettings.brokerProvider）。プロパティ名 mode はバックエンドの
  // StageSettings.Mode に対応する（序数と JSON キーを動かさないための据え置き。IADR-0140 決定3）。
  mode: number;
  // FR-20, #333, #389, IADR-0136: 段階の発注可能額は**総資金（equity）比**である（Stage 2 は 0.30 ＝ 30%）。
  // バックエンドのプロパティ名は `CapitalCapRatio`。固定額ではないため「¥1,000,000」のようには表示しない。
  capitalCapRatio: number;
}

export interface RiskManagementSettings {
  guard: TradingGuardSettings;
  limits: RiskLimitSettings;
  stage: StageSettings;
  // FR-20, FR-12, FR-13, INDEX 決定 46, #334: **現在の発注先**（BrokerProvider enum・数値）。
  // 運用段階とは独立した軸であり、変更操作を持つ画面は SC-02 だけである（SC-03 は参照専用）。
  brokerProvider: number;
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
  brokerProvider: number; // BrokerProvider enum（数値）。段階とは別の軸（1 行に混ぜない）
  dailyRealizedPnl: number;
  unrealizedPnl: number;
  dailyPnl: number;
  capital: number;
  dailyOrderedAmount: number;
  // FR-10, FR-20, #334, #389: equity から解決した 1 注文あたりの上限**額**（比率ではない）。
  // 実弾切替モーダル③と SC-03 の上限表示・使用率表示が用いる。
  // **設定側の `RiskLimitSettings.maxOrderAmountRatio` と同名にしない・改名しない。**
  // バックエンドの `RiskStatusView.MaxOrderAmount` は解決済みの実額であり、ここは正しい（#389 で触らない）。
  maxOrderAmount: number;
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

// FR-20, #334, IADR-0142: Stage 1 の進捗（**moomoo SIMULATE の実績のみ**）と、内蔵 paper 稼働により
// 算入されなかった営業日数。画面は「経過 42 / 60 営業日（paper 稼働により 3 日を除外）」と併記する。
export interface Stage1Progress {
  qualifiedTradingDays: number;
  tradeCount: number;
  excludedInternalPaperDays: number;
}

// 06_daytrading-review §4.1〜§4.3: 目標営業日数 60 / 最小取引件数 100 / 打ち切り 120。
// 画面が閾値を直書きすると計画の改訂に追随しないため、サーバの応答から受け取る。
export interface Stage1GateCriteria {
  targetTradingDays: number;
  minimumTradeCount: number;
  maximumTradingDays: number;
}

export interface StageGateStatus {
  currentStage: number; // TradingStage enum（数値）
  currentSettings: StageSettings;
  history: StageTransition[];
  promotion: PromotionAssessment;
  withdrawal: WithdrawalAssessment;
  stage1Progress: Stage1Progress;
  stage1Criteria: Stage1GateCriteria;
}

// ---- 数値 enum → 表示ラベルの写像（未知値は安全側フォールバック） ----

// TradingStage（0..3）。バックエンド enum の連番（StageSettings.cs）に対応。
// #333 / #334: 段階の呼称は計画（05_screens「表示規約（共通）」・06_daytrading-review §4 表）に従う。
// **「ペーパー」の語を単独で使わない**（moomoo SIMULATE と内蔵 paper のどちらとも読めるため）。
const STAGE_LABELS: Record<number, string> = {
  0: 'Stage 0（検証）',
  1: 'Stage 1（SIMULATE）',
  2: 'Stage 2（最小実弾）',
  3: 'Stage 3（段階増額）',
};

// BrokerProvider（0=内蔵 paper, 1=moomoo REAL, 2=moomoo SIMULATE）。FR-20 / INDEX 決定 46 / #334。
// 序数はバックエンド enum と一致させる（旧 TradeMode の Paper=0 / Live=1 を保存し、SIMULATE を末尾へ追加）。
// 用語: SIMULATE を「ペーパー」と呼ばない。内蔵 paper を「SIMULATE」「デモ取引」と呼ばない。
const BROKER_PROVIDER_LABELS: Record<number, string> = {
  0: '内蔵 paper（擬似約定・外部へ発注しない）',
  1: 'moomoo REAL（実弾）',
  2: 'moomoo SIMULATE（デモ環境）',
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
// #389: 9〜11 は #333（Stage 1 の合格集計）で追加されたが写像が追随しておらず、SC-03 は
// 実サーバの応答（既定で `unmetCriteria: [9, 10]` が出る）を「不明(9)」と表示していた。
// 契約フィクスチャ（IADR-0146）の導入で判明した実測のずれである。
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
  9: 'Stage 1 の営業日数が不足',
  10: 'Stage 1 の取引件数が不足',
  11: 'Stage 1 の延長期限切れ（打ち切り）',
};

// WithdrawalReason（0=DrawdownBreachedMultiple, 2=Stage1ExtensionExhausted）。
// #389: **1 は欠番**である（旧 PaperDeviationUnexplained。#333 で機械判定の撤退事由から外され「再利用しない」と
// 明記された）。2（Stage 1 の打ち切り）の写像が追随しておらず「不明(2)」表示になっていた。
// 欠番 1 は写像から外す——存在しない事由のラベルを残すと、将来 1 が再利用されたときに**誤ったラベルを
// 自信満々に表示する**（未知値のフォールバック「不明(1)」の方が安全側である）。
const WITHDRAWAL_REASON_LABELS: Record<number, string> = {
  0: '実DD がバックテスト最大DD × 倍率に到達',
  2: 'Stage 1 の打ち切り（120 営業日で取引件数に未達）',
};

// SettingsChangeType（SettingsChangeEntry.cs の列挙順）。7 は #334 で末尾追加。
const CHANGE_TYPE_LABELS: Record<number, string> = {
  0: 'ガード',
  1: '上限',
  2: '段階',
  3: '緊急停止 発動',
  4: '緊急停止 解除',
  5: '一時停止',
  6: '再開',
  7: '発注先',
};

// FR-13, SC-03, #334: 発注先の変更履歴を絞り込むための種別値（SettingsChangeType.BrokerProviderChanged）。
export const CHANGE_TYPE_BROKER_PROVIDER = 7;

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
// FR-20, SC-02, #334: 発注先の選択肢（3 値）。変更 UI は SC-02 だけが持つ。
export const BROKER_PROVIDER_OPTIONS: EnumOption[] = optionsOf(BROKER_PROVIDER_LABELS);

// FR-20, #334: 発注先の序数（バックエンド BrokerProvider と一致）。
export const BROKER_PROVIDER_INTERNAL_PAPER = 0;
export const BROKER_PROVIDER_MOOMOO_REAL = 1;
export const BROKER_PROVIDER_MOOMOO_SIMULATE = 2;

// FR-20, IADR-0141 決定2: 実弾切替の確認に打ち込ませる文字列。**サーバの
// BrokerProviderChange.LiveAcknowledgementPhrase と同じ値でなければならない**（片方だけ変えると、
// 画面は通すのにサーバが 400 を返す／その逆になる）。
export const LIVE_ACKNOWLEDGEMENT_PHRASE = 'REAL';

// 実弾（実資金で執行される発注先）か。「実資金かどうか」の判定は本関数だけを通す。
export const isLiveProvider = (v: number): boolean => v === BROKER_PROVIDER_MOOMOO_REAL;

// 内蔵 paper（外部へ一度も発注しない擬似約定）か。FR-12 の警告バナー・paper ラベルの判定に用いる。
export const isInternalPaper = (v: number | null | undefined): boolean =>
  v === BROKER_PROVIDER_INTERNAL_PAPER;
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
export const brokerProviderLabel = (v: number): string => labelOf(BROKER_PROVIDER_LABELS, v);
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
