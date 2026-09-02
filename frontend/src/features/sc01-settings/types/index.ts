// SC-01, FR-17, UC-06, #529: 全体前提条件（ConfigurationService。BFF `/bff/assumptions`）の型。
//
// MSP/ADR-0031 §ディレクトリ構成（Bulletproof React）: **feature 内で `api/` と `components/` の
// 双方が要る型は `types/` に置く。** 従前は `api/assumptionsQueries.ts` に値（クエリ）と同居して
// おり、画面が「取得の実装」を import しないと型を得られなかった。

export interface CommissionSchedule {
  rate: number;
  minimum: number;
  cap: number;
}

export interface MonthlyCostLimits {
  total: number;
  llm: number;
  infrastructure: number;
  data: number;
}

export interface TradingAssumptions {
  capitalGainsTaxRate: number;
  japanCommission: CommissionSchedule;
  unitedStatesCommission: CommissionSchedule;
  fxSpreadRatio: number;
  minimumExpectedProfitMultiple: number;
  costLimits: MonthlyCostLimits;
}

export interface VersionedAssumptions {
  assumptions: TradingAssumptions;
  version: number;
  /**
   * FR-17, #424, IADR-0162 決定4: **供給可否はサーバが宣言する。** 画面は値の中身から推測しない
   * （未解決のときに表示しているのは組み込みの既定値であって権威値ではない）。
   */
  isResolved: boolean;
}

export interface ChangeEntry {
  actor: string;
  reason: string;
  changedAt: string;
  version: number;
  before?: string | null;
  after?: string | null;
}
