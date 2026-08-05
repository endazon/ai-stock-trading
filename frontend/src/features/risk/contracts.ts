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
  // #387: 12 は「統制違反があった」（2）とは**別の事由**である。集計そのものが供給されていない状態を指し、
  // 打つ手が違う（前者は AI の抵触の記録、後者は供給経路の欠落）。同じラベルに潰さない。
  12: '統制違反件数の集計が未供給',
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

// ---- FR-10, SC-02, #362, IADR-0151: 比率 ⇄ 百分率の変換（**この 2 関数だけを通す**） ----
//
// 画面（表示・入力）は**百分率**、ワイヤ（HTTP・永続化・バックエンドのドメイン）は**比率**である
// （IADR-0151 決定1）。呼び出し側で `× 100` / `÷ 100` と書くことを許さない——分散させると
// 「その値がどちらの単位か」が呼び出し側ごとにぶれ、比率と百分率の取り違えが型検査を素通りする
// （IADR-0130 決定1 が解決点を 1 本に閉じたのと同じ規律）。
//
// **変換は 10 進文字列の小数点移動で行う。** `Number(s) / 100` は多くの場合に期待どおりだが、
// 丸めの誤差が**統制値へ紛れ込む経路**を残す（`decimal` で受けるサーバへ 0.020000000000000004 が届く）。
// 文字列操作なら誤差は原理的に生じない。

/** 10 進表記の文字列の小数点を `shift` 桁だけ右へ動かす（負なら左へ）。数値として解釈できなければ null。 */
function shiftDecimalPoint(text: string, shift: number): string | null {
  const trimmed = text.trim();
  const m = /^([+-]?)(\d*)(?:\.(\d*))?$/.exec(trimmed);
  // 指数表記（1e-3 等）は正規表現に合わないため、数値経由の縮退へ落とす（誤差より「読めない」ほうが危険）。
  if (!m || (m[2] === '' && (m[3] ?? '') === '')) {
    const n = Number(trimmed);
    if (trimmed === '' || !Number.isFinite(n)) return null;
    return String(n * 10 ** shift);
  }
  const sign = m[1] === '-' ? '-' : '';
  const digits = `${m[2] ?? ''}${m[3] ?? ''}`;
  // 小数点の位置（左からの桁数）を移動させる。桁が足りなければ 0 を補う。
  let point = (m[2] ?? '').length + shift;
  let body = digits;
  if (point < 0) {
    body = '0'.repeat(-point) + body;
    point = 0;
  }
  if (point > body.length) {
    body += '0'.repeat(point - body.length);
  }
  const intPart = body.slice(0, point).replace(/^0+(?=\d)/, '') || '0';
  const fracPart = body.slice(point).replace(/0+$/, '');
  const value = fracPart === '' ? intPart : `${intPart}.${fracPart}`;
  return value === '0' ? '0' : `${sign}${value}`;
}

/** 比率（0.25）を画面表示用の百分率文字列（"25"）にする。非数・未定義は空文字（入力欄を "undefined" にしない）。 */
export function ratioToPercentText(ratio: number | null | undefined): string {
  if (ratio === null || ratio === undefined || !Number.isFinite(ratio)) return '';
  return shiftDecimalPoint(String(ratio), 2) ?? '';
}

/** 画面の百分率文字列（"25"）を比率（0.25）にする。数値として読めなければ null（黙って 0 にしない）。 */
export function percentTextToRatio(text: string): number | null {
  const shifted = shiftDecimalPoint(text, -2);
  if (shifted === null) return null;
  const value = Number(shifted);
  return Number.isFinite(value) ? value : null;
}

/**
 * FR-10, SC-02, #362, IADR-0151 決定4: equity と比率から**実額**を解決する。
 * <p>
 * equity は `RiskStatusView.capital`（IADR-0130 決定2 が定めた「判定に用いる自己資金＝前営業日終値時点の
 * 評価額」）である。**呼び出し側で `equity * ratio` と書かない**（equity の定義が呼び出し側ごとにぶれる）。
 * equity が不明・非数なら null を返し、画面は「—」を出す（併記できないことを黙って隠さない）。
 */
export function resolveEquityAmount(
  equity: number | null | undefined,
  ratio: number | null | undefined,
): number | null {
  if (equity === null || equity === undefined || !Number.isFinite(equity)) return null;
  if (ratio === null || ratio === undefined || !Number.isFinite(ratio)) return null;
  return equity * ratio;
}

/**
 * 金額の表示用整形。**通貨記号は付けない**——`RiskStatusView.capital` は基準通貨（円）建てであり
 * （IADR-0130 決定3・`MarketCurrency.Base = Jpy`）、計画が求める USD 表記（`$750`）にするには
 * 判定通貨の USD 移行が要る。**円建ての数値に「$」を付けることは単位の取り違えそのもの**であるため、
 * 通貨は呼び出し側のラベルで明示する（IADR-0151 決定4）。
 */
export function formatAmount(value: number | null): string {
  if (value === null || !Number.isFinite(value)) return '—';
  return new Intl.NumberFormat('ja-JP', { maximumFractionDigits: 0 }).format(value);
}

// ---- FR-10, SC-02, #362, IADR-0151 決定2: リスク上限として設定できる値域 ----
//
// **実効はサーバ側（`RiskLimitBounds`・ドメイン）である。** ここに同じ表を持つのは、利用者へ即時に
// 提示するためであり（サーバだけだと保存を押すまで誤りが分からない）、画面が統制の実効を担うためではない
// （画面だけの関門は API 直叩きで消える＝IADR-0141 決定1 と同じ判断）。
// **値はサーバ側の `RiskLimitBounds` と一致していなければならない**（片方だけ変えると、画面は通すのに
// サーバが 400 を返す／その逆になる）。

/** 上限の入力欄の単位。表示の接尾辞と検証規則を決める。 */
export type LimitUnit =
  /** equity に対する百分率（実額を併記する） */
  | 'equityPercent'
  /** equity に対する百分率・日次（実額を併記する） */
  | 'equityPercentPerDay'
  /** 件数（整数） */
  | 'count'
  /** 倍率（比率のまま入力する） */
  | 'factor';

export interface LimitFieldSpec {
  /** 画面のラベル（単位を含まない本体）。 */
  label: string;
  /** 入力欄に添える単位の表示。 */
  unit: string;
  kind: LimitUnit;
  /** 許容範囲（画面の入力単位＝百分率／件数／倍率で表す）。 */
  min: number;
  max: number;
  /** 下限を含むか（比率系は 0 を含まない）。 */
  minInclusive: boolean;
  /** 上限を含むか（連敗時サイズ縮小係数だけ含まない）。 */
  maxInclusive: boolean;
  /** 整数のみか。 */
  integer: boolean;
}

/** 上限 8 項目の仕様（表示順）。キーはバックエンドの `RiskLimitSettings` のプロパティ名に一致する。 */
export const LIMIT_FIELDS = {
  maxOrderAmountRatio: {
    label: '1注文発注額上限（equity 比）',
    unit: '%',
    kind: 'equityPercent',
    min: 0,
    max: 100,
    minInclusive: false,
    maxInclusive: true,
    integer: false,
  },
  maxDailyOrderAmountRatio: {
    label: '1日発注額上限（equity 比）',
    unit: '%/日',
    kind: 'equityPercentPerDay',
    min: 0,
    max: 1000,
    minInclusive: false,
    maxInclusive: true,
    integer: false,
  },
  // ADR-0016 決定9・計画 §5: **「保有銘柄数上限」の語は用いない**（同一銘柄で複数の建玉を持ち得る）。
  maxOpenPositions: {
    label: '保有建玉数上限',
    unit: '件',
    kind: 'count',
    min: 1,
    max: 20,
    minInclusive: true,
    maxInclusive: true,
    integer: true,
  },
  dailyLossLimitRatio: {
    label: '日次損失上限（equity 比）',
    unit: '%',
    kind: 'equityPercent',
    min: 0,
    max: 20,
    minInclusive: false,
    maxInclusive: true,
    integer: false,
  },
  perTradeRiskRatio: {
    label: '1取引あたりリスク（equity 比）',
    unit: '%',
    kind: 'equityPercent',
    min: 0,
    max: 10,
    minInclusive: false,
    maxInclusive: true,
    integer: false,
  },
  maxDrawdownRatio: {
    label: '最大ドローダウン上限（equity 比）',
    unit: '%',
    kind: 'equityPercent',
    min: 0,
    max: 50,
    minInclusive: false,
    maxInclusive: true,
    integer: false,
  },
  losingStreakThreshold: {
    label: '連敗しきい値',
    unit: '連敗',
    kind: 'count',
    min: 1,
    max: 20,
    minInclusive: true,
    maxInclusive: true,
    integer: true,
  },
  // 1.0（＝縮小しない）は統制の無効化であるため上限に含めない（IADR-0151 決定2）。
  losingStreakSizeFactor: {
    label: '連敗時サイズ縮小係数',
    unit: '倍',
    kind: 'factor',
    min: 0,
    max: 1,
    minInclusive: false,
    maxInclusive: false,
    integer: false,
  },
} as const satisfies Record<keyof RiskLimitSettings, LimitFieldSpec>;

export type LimitFieldKey = keyof typeof LIMIT_FIELDS;

/** 表示順のキー列（`Object.keys` の順序に暗黙依存しないよう明示する）。 */
export const LIMIT_FIELD_KEYS = Object.keys(LIMIT_FIELDS) as LimitFieldKey[];

/** equity に対する割合の項目か（実額を併記する対象）。 */
export const isEquityRatioField = (key: LimitFieldKey): boolean =>
  LIMIT_FIELDS[key].kind === 'equityPercent' || LIMIT_FIELDS[key].kind === 'equityPercentPerDay';

/** 入力欄の値（画面単位）が値域に収まるか。収まらなければ利用者向けの説明を返す（収まれば null）。 */
export function validateLimitInput(key: LimitFieldKey, text: string): string | null {
  const spec = LIMIT_FIELDS[key];
  if (text.trim() === '') return `${spec.label}を入力してください。`;
  const value = Number(text);
  if (!Number.isFinite(value)) return `${spec.label}は数値で入力してください。`;
  if (spec.integer && !Number.isInteger(value)) {
    return `${spec.label}は整数（${spec.unit}）で入力してください。`;
  }
  const belowMin = spec.minInclusive ? value < spec.min : value <= spec.min;
  const aboveMax = spec.maxInclusive ? value > spec.max : value >= spec.max;
  if (belowMin || aboveMax) return `${spec.label}は ${describeLimitRange(key)} の範囲で入力してください。`;
  return null;
}

/** 値域を利用者向けの文言にする（入力欄のヘルプと警告の双方で使う）。 */
export function describeLimitRange(key: LimitFieldKey): string {
  const spec = LIMIT_FIELDS[key];
  const lower = spec.minInclusive ? `${spec.min} 以上` : `${spec.min} 超`;
  const upper = spec.maxInclusive ? `${spec.max} 以下` : `${spec.max} 未満`;
  return `${lower} ${upper}（${spec.unit}）`;
}

/**
 * 画面の入力（文字列・百分率/件数/倍率）をワイヤの値（比率/整数/倍率）へ変換する。
 * 読めない値は null（**黙って 0 を送らない**）。
 */
export function limitInputToWire(key: LimitFieldKey, text: string): number | null {
  const spec = LIMIT_FIELDS[key];
  if (spec.kind === 'equityPercent' || spec.kind === 'equityPercentPerDay') {
    return percentTextToRatio(text);
  }
  if (text.trim() === '') return null;
  const value = Number(text);
  return Number.isFinite(value) ? value : null;
}

/** ワイヤの値（比率/整数/倍率）を画面の入力（文字列）へ変換する。 */
export function wireToLimitInput(key: LimitFieldKey, value: number): string {
  const spec = LIMIT_FIELDS[key];
  if (spec.kind === 'equityPercent' || spec.kind === 'equityPercentPerDay') {
    return ratioToPercentText(value);
  }
  return String(value);
}
