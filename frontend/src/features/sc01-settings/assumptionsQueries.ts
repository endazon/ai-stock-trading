import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@foundation/api/apiClient';

// SC-01, FR-17, UC-06, IADR-0286: 全体前提条件（ConfigurationService。BFF `/bff/assumptions`）の
// サーバー状態と、その型。
//
// `apiFetch` を呼んでよいのはこの層だけである（理由は `../risk/queries.ts` 冒頭）。
// **本 feature 以外はこの端点を消費しない**ため、共有層ではなく feature の中に置く。

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

export const assumptionsQueryKeys = {
  /** `GET /assumptions` とその配下（履歴）。 */
  current: ['assumptions'] as const,
  history: ['assumptions', 'history'] as const,
};

/** 現在の全体前提条件（版つき）。 */
export function useAssumptions() {
  return useQuery({
    queryKey: assumptionsQueryKeys.current,
    queryFn: () => apiFetch<VersionedAssumptions>('/assumptions'),
  });
}

/**
 * 変更履歴（新しい順）。取得不能はその領域だけの縮退に留める（呼び出し側が判断する）。
 *
 * 🔴 **配列でない応答は「0 件」ではなく失敗として扱う**（想定外の形を空配列へ丸めると
 * 「履歴が無い」という別の事実として描かれる。IADR-0154）。
 */
export function useAssumptionsHistory() {
  return useQuery({
    queryKey: assumptionsQueryKeys.history,
    queryFn: async () => {
      const data = await apiFetch<ChangeEntry[]>('/assumptions/history');
      if (!Array.isArray(data)) throw new TypeError('変更履歴の応答が配列ではありません。');
      return data;
    },
  });
}

/**
 * FR-17: 全体前提条件の変更（楽観排他 `expectedVersion` ＋ 理由必須）。
 *
 * 成功後は現在値と履歴を無効化する（`assumptionsQueryKeys.current` は前方一致で履歴を含む）。
 * **競合（409）・検証（400）で自動再試行はしない**——同じ要求を繰り返しても結果は変わらず、
 * 破壊的な再送になり得る（安全既定）。再試行の既定そのものは foundation の QueryClient が持つ。
 */
export function useSaveAssumptions() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (body: {
      assumptions: TradingAssumptions;
      expectedVersion: number;
      reason: string;
    }) => apiFetch<VersionedAssumptions>('/assumptions', { method: 'PUT', json: body }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: assumptionsQueryKeys.current });
    },
  });
}
