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
  // FR-19, #375, ADR-0021 決定3: 利用者が設定した口座種別（AccountType enum・0=信用口座 / 1=現金口座）。
  //
  // **統制の切り替えには使われない。** 統制はバックエンドがブローカーへ照会した結果で切り替わり、
  // 本値の役割は**照会結果との食い違いの検知**だけである（食い違えば新規建てが止まる）。
  //
  // SC-02 の取引ガードフォームは本値を**編集も送信もしない**。`PUT /risk-controls/settings/guard` は
  // 全置換だが、本キーの省略は「変更しない」として扱われる（送り漏らしで信用口座へ戻らないため）。
  configuredAccountType: number;
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
  // FR-20, FR-13, SC-02, #423, IADR-0164: **Stage 1 の最小取引件数**（06_daytrading-review §4.1 条件 3）。
  // 2026-08-07 の裁定で設定値になった（既定 100・値域 1〜1000）。変更操作を持つ画面は SC-02 だけである。
  stage1MinimumTradeCount: number;
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

// ---- 空売りの現況（GET /risk-controls/short-selling） ----
// FR-10, UC-06, SC-03, ADR-0016（決定3・決定7・決定9・決定15）, #340, IADR-0154。
//
// **本契約が運ぶ主役は値ではなく「値が供給されているか」である。** 維持率・借株料の累計・自動縮小の
// 発動履歴は供給元が無く、0 や空列で受けると画面は正常な統制として描いてしまう（#403 の
// `ControlViolationCount` 既定 0 が「違反なし」に見えた fail-open と同型）。
//
// **供給可否をフロントに書き込まない。** 「維持率は未供給」と画面へ直書きすると、供給元が入った日に
// 画面が嘘をつき続ける（誰も気づかない・逆向きの同型事故）。判定はサーバの `MetricAvailability` に従う。

/** 指標 1 つの供給可否（バックエンド `MetricAvailability` の序数）。 */
export const METRIC_AVAILABLE = 0;
/** 供給元が無い／取得できない。画面は「取得できていません」と明示する。 */
export const METRIC_NOT_SUPPLIED = 1;
/** 概念が成立しない（建玉が 1 件も無い等）。異常ではない。 */
export const METRIC_NOT_APPLICABLE = 2;

export interface ShortSellingPositionView {
  symbol: string;
  market: number; // Market enum（数値）
  /** 建玉の方向。0=Buy＝ロング / 1=Sell＝ショート（TradeSide enum・数値）。 */
  side: number;
  quantity: number;
  /** 平均取得単価（**ローカル通貨**）。 */
  averageEntryPrice: number;
  marketValueAvailability: number; // MetricAvailability
  marketValueUsd: number | null;
  borrowFeeAvailability: number; // MetricAvailability
  accruedBorrowFeeUsd: number | null;
}

export interface MaintenanceMarginReductionLegView {
  symbol: string;
  market: number;
  positionSide: number; // TradeSide enum（数値）
  quantity: number;
  requiredMarginUsd: number;
}

export interface MaintenanceMarginReductionRecordView {
  executedAt: string; // DateTimeOffset（ISO 文字列）
  ratioBefore: number;
  threshold: number;
  recoveryTarget: number;
  ratioAfter: number | null;
  /** 決済した建玉（**必要証拠金の降順**。UC-06。含み損の大きい順ではない）。 */
  legs: MaintenanceMarginReductionLegView[];
}

export interface ShortSellingStatusView {
  maintenanceMarginAvailability: number; // MetricAvailability
  maintenanceMarginRatio: number | null;
  /** 実際に適用される閾値（自前 40% と規制要求の厳しい方）。株価に依存するため未供給時は null。 */
  appliedMaintenanceMarginThreshold: number | null;
  /** 適用される回復目標（＝適用閾値 + オフセット）。 */
  appliedMaintenanceRecoveryTarget: number | null;
  /** 設定上の維持率閾値（既定 0.40）。**設定値であり実測ではない。** */
  configuredMaintenanceMarginThreshold: number;
  /** 回復目標のオフセット（既定 0.05 ＝ +5 ポイント）。 */
  maintenanceRecoveryTargetOffset: number;
  shortExposureAvailability: number; // MetricAvailability
  shortExposureRatio: number | null;
  shortExposureRatioCap: number;
  positions: ShortSellingPositionView[];
  borrowFeeAvailability: number; // MetricAvailability
  totalAccruedBorrowFeeUsd: number | null;
  reductionHistoryAvailability: number; // MetricAvailability
  reductionHistory: MaintenanceMarginReductionRecordView[];
  /**
   * ADR-0016 決定15, #424, IADR-0162: **強制買戻しの発生回数**の供給可否。
   * 現状は常に `NotSupplied`（推定台帳は「観測が届いていない」と「観測して 0 件」を区別できない）。
   * **0 件と表示してはならない**（計画 05_screens SC-03 の供給元の表が名指しで禁じている）。
   */
  buyInCountAvailability: number; // MetricAvailability
  buyInCount: number | null;
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

// 06_daytrading-review §4.1〜§4.3: 目標営業日数 60 / 最小取引件数（既定 100・設定値） / 打ち切り 120。
// 画面が閾値を直書きすると計画の改訂に追随しないため、サーバの応答から受け取る。
export interface Stage1GateCriteria {
  targetTradingDays: number;
  /** #423: **SC-02 から変更できる設定値**（既定 100・値域 1〜1000）。他の 2 項目は設定で変わらない。 */
  minimumTradeCount: number;
  maximumTradingDays: number;
  // FR-20, SC-02, SC-03, §4.3, #423, IADR-0164 決定6:
  // **最小取引件数が統計的根拠（100 件）を下回っているかをサーバが宣言する。**
  // 画面は `minimumTradeCount < 100` を自分で判定しない——判定を画面へ書き込むと、
  // 警告を出す場所（SC-02・SC-03・Discord）が増えるたびに同じ条件が写経され、
  // 1 か所の写し間違いで「下げたのに警告が出ない」状態になる（IADR-0154 と同じ論法）。
  belowStatisticalBasis: boolean;
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

// SettingsChangeType（SettingsChangeEntry.cs の列挙順）。7 は #334、8 は #423 で末尾追加。
const CHANGE_TYPE_LABELS: Record<number, string> = {
  0: 'ガード',
  1: '上限',
  2: '段階',
  3: '緊急停止 発動',
  4: '緊急停止 解除',
  5: '一時停止',
  6: '再開',
  7: '発注先',
  8: 'Stage 1 最小取引件数',
};

// FR-13, SC-03, #334: 発注先の変更履歴を絞り込むための種別値（SettingsChangeType.BrokerProviderChanged）。
export const CHANGE_TYPE_BROKER_PROVIDER = 7;

// FR-13, FR-20, SC-02, #423: Stage 1 最小取引件数の変更（SettingsChangeType.Stage1MinimumTradeCountChanged）。
export const CHANGE_TYPE_STAGE1_MINIMUM_TRADE_COUNT = 8;

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

// TradeSide（0=Buy, 1=Sell）。SC-03 の保有ポジション表の「方向」列（ADR-0016 決定15）。
// **建玉の方向であり注文の売買方向ではない**（空売り建玉は Buy で手仕舞う）。
const POSITION_SIDE_LABELS: Record<number, string> = {
  0: 'ロング',
  1: 'ショート',
};

export const positionSideLabel = (v: number): string => labelOf(POSITION_SIDE_LABELS, v);

// FR-10, SC-03, #340, IADR-0154: 供給が無い指標の**表示文言**。
//
// **0 や「—」だけを出さない。** 維持率は計画が「本画面の最上位に置く。マージンコールは口座を失う唯一の
// 経路である」と定めた指標であり、未供給を正常のように見せることが最悪の失敗である（#403 と同型）。
// 定数として 1 か所に置くのは、テストから直接参照して「文言が消えたら赤くなる」ようにするためである。
export const METRIC_NOT_SUPPLIED_TEXT = '取得できていません（供給元がありません）';
export const METRIC_NOT_APPLICABLE_TEXT = '該当なし（対象の建玉がありません）';

/**
 * 供給可否つきの比率を表示文字列にする。
 * - `Available` … 百分率（`40.0%`）
 * - `NotSupplied` … **「取得できていません」**（値を出さない）
 * - `NotApplicable` … 「該当なし」
 * - 未知の供給可否 … 安全側フォールバックとして未供給扱い（**値があるように見せない**）
 */
export function availabilityRatioText(availability: number, ratio: number | null): string {
  if (availability === METRIC_AVAILABLE && ratio !== null && Number.isFinite(ratio)) {
    return `${(ratio * 100).toFixed(1)}%`;
  }
  if (availability === METRIC_NOT_APPLICABLE) return METRIC_NOT_APPLICABLE_TEXT;
  return METRIC_NOT_SUPPLIED_TEXT;
}

/** 供給可否つきの金額（USD）を表示文字列にする。未供給は「取得できていません」（`$0` にしない）。 */
export function availabilityAmountText(availability: number, value: number | null): string {
  if (availability === METRIC_AVAILABLE && value !== null && Number.isFinite(value)) {
    return formatAmount(value);
  }
  if (availability === METRIC_NOT_APPLICABLE) return METRIC_NOT_APPLICABLE_TEXT;
  return METRIC_NOT_SUPPLIED_TEXT;
}

/**
 * 供給可否つきの**件数**を表示文字列にする（SC-03 の強制買戻しの発生回数など）。
 *
 * **`Available` かつ 0 は「0」と描く。** 05_screens「供給が無い値の表示規約」の 3 状態のうち
 * **「値が 0」は正当な測定結果**であり、正常値として表示しなければならない（例: 当日の統制違反 0 件）。
 * ここで 0 を未供給へ倒すと、**供給されているのに「取得できていません」と嘘をつく**——未供給を 0 に
 * 見せるのと逆向きだが、どちらも「サーバの宣言に従う」という規約への違反である。
 */
export function availabilityCountText(availability: number, count: number | null): string {
  if (availability === METRIC_AVAILABLE && count !== null && Number.isFinite(count)) {
    return `${count}`;
  }
  if (availability === METRIC_NOT_APPLICABLE) return METRIC_NOT_APPLICABLE_TEXT;
  return METRIC_NOT_SUPPLIED_TEXT;
}

/** 供給されていない（＝利用者へ警告として見せるべき）状態か。`NotApplicable` は異常ではないので含めない。 */
export const isNotSupplied = (availability: number): boolean => availability !== METRIC_AVAILABLE
  && availability !== METRIC_NOT_APPLICABLE;

/** 比率（0.40）を百分率表示（`40.0%`）にする。設定値の表示に用いる（供給可否を持たない値）。 */
export function ratioToPercentDisplay(ratio: number): string {
  return Number.isFinite(ratio) ? `${(ratio * 100).toFixed(1)}%` : '—';
}

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
 * 金額の表示用整形。**基準通貨（USD）の記号を付ける**——`RiskStatusView.capital` は基準通貨建てであり、
 * #364 / IADR-0152 決定1 で `MarketCurrency.Base = Usd` へ移行した。これにより計画 SC-02 の表記例
 * 「**25%（$750）**」がそのまま正しくなる（[#409](https://github.com/endazon/ai-stock-trading/issues/409) は本移行で解消）。
 *
 * IADR-0151 決定4 が `$` を付けなかったのは、当時の供給値が円建てであり「円建ての数値に `$` を付けることは
 * 単位の取り違えそのもの」だったためである。**通貨が一致した今、記号を付けることが正しい表示になる。**
 * 小数はセントまで（`$750` / `$1,234.56`）。equity が不明なら「—」を返し、誤った実額を出さない。
 */
export function formatAmount(value: number | null): string {
  if (value === null || !Number.isFinite(value)) return '—';
  return `$${new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 }).format(value)}`;
}

/**
 * FR-10, SC-02, #424, IADR-0162 決定3: equity 比の項目に併記する**実額**の表示。
 *
 * **equity が供給されていないことを「—」で描かない。** 05_screens「供給が無い値の表示規約」は
 * 供給が無い値を「0」「—」で表示することを禁じている。SC-02 の実額併記は「25%（$750）」のように
 * **発注規模を判断させる**ための表示であり、equity が取れていないのか「該当が無い」のかで
 * 利用者が採るべき手はまったく違う（前者は統制の判断材料が無い状態＝画面を信用してはいけない）。
 *
 * 2 つの理由を分ける。
 * - **equity が供給されていない**（`/risk-controls/status` の取得失敗）… **未供給**として明示する
 * - **入力値が読めない**（入力欄が空・非数値）… 「—」。値域の警告が同じ画面に出ており、
 *   これは供給の問題ではなく**その入力に対して実額が定義できない**（＝対象なし）状態である
 */
export function equityAmountText(
  equity: number | null | undefined,
  ratio: number | null | undefined,
): string {
  if (equity === null || equity === undefined || !Number.isFinite(equity)) {
    return METRIC_NOT_SUPPLIED_TEXT;
  }
  const amount = resolveEquityAmount(equity, ratio);
  return amount === null ? '—' : formatAmount(amount);
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

// ---- FR-20, FR-13, SC-02, #423, IADR-0164 決定5/決定6: Stage 1 の最小取引件数の値域（画面側の即時提示） ----
//
// **実効はサーバ側（`Stage1TradeCountBounds`）である。** ここに同じ表を持つのは利用者へ即時に提示する
// ためであり（サーバだけだと保存を押すまで誤りが分からない）、画面が統制の実効を担うためではない
// （画面だけの関門は API 直叩きで消える＝IADR-0141 決定1 と同じ判断）。
// **値はサーバ側の `Stage1TradeCountBounds` と一致していなければならない。**

/** 既定 100 件（06_daytrading-review §4.1 条件 3）。 */
export const STAGE1_TRADE_COUNT_DEFAULT = 100;

/** 下限 1 件（**含む**）。0 以下では条件 3 が無条件に成立し、期間だけで昇格できてしまう。 */
export const STAGE1_TRADE_COUNT_MIN = 1;

/** 上限 1000 件（**含む**。計画が定めた値）。 */
export const STAGE1_TRADE_COUNT_MAX = 1000;

/** 値域を利用者向けの文言にする。 */
export const STAGE1_TRADE_COUNT_RANGE_TEXT =
  `${STAGE1_TRADE_COUNT_MIN} 以上 ${STAGE1_TRADE_COUNT_MAX} 以下（件）`;

/**
 * FR-20, §4.3, #423: 100 件未満を設定したときに**常時表示する**警告の文言。
 *
 * 裁定（質問票 第 13 回 Q6 の追加指示）は「**統計的な根拠（§4.3）を満たさない設定である**旨の警告を
 * 常時表示する。**警告は設定を妨げない。下げた事実が記録に残ることを担保する**」と定めている。
 */
export const STAGE1_TRADE_COUNT_BELOW_BASIS_WARNING =
  '統計的な根拠（06_daytrading-review §4.3）を満たさない設定です。'
  + '100 件は「30 件が床・100 件が実用最低限」という実務上の一致点の下限であり、'
  + 'これを下回ると勝率・平均損益の推定分散が大きく、'
  + '条件 3 の目的（運用に足るかを統計的に判断できる）を満たしません。'
  + 'この警告は設定を妨げません（変更は理由とともに履歴へ残ります）。';

/**
 * 入力欄の値（件数の文字列）が値域に収まるか。収まらなければ利用者向けの説明を返す（収まれば null）。
 * **空欄・非数値・小数を黙って通さない**（件数は整数である）。
 */
export function validateStage1TradeCountInput(text: string): string | null {
  if (text.trim() === '') return 'Stage 1 の最小取引件数を入力してください。';
  const value = Number(text);
  if (!Number.isFinite(value)) return 'Stage 1 の最小取引件数は数値（件）で入力してください。';
  if (!Number.isInteger(value)) return 'Stage 1 の最小取引件数は整数（件）で入力してください。';
  if (value < STAGE1_TRADE_COUNT_MIN || value > STAGE1_TRADE_COUNT_MAX) {
    return `Stage 1 の最小取引件数は ${STAGE1_TRADE_COUNT_RANGE_TEXT} の範囲で入力してください。`;
  }
  return null;
}

/**
 * 入力中の件数が統計的根拠を下回るか（**入力に対する即時提示のためだけに用いる**）。
 *
 * **保存済みの値については本関数を使わない。** サーバが宣言する
 * `Stage1GateCriteria.belowStatisticalBasis` に従う（IADR-0164 決定6）。
 * ここで判定するのは「まだ保存されていない入力値」であり、問い合わせる相手が存在しない。
 */
export function inputBelowStatisticalBasis(text: string): boolean {
  if (validateStage1TradeCountInput(text) !== null) return false;
  return Number(text) < STAGE1_TRADE_COUNT_DEFAULT;
}
