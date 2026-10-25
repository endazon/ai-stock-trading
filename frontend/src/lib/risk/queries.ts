import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@foundation/api/apiClient';
import type {
  PreCloseControlsView,
  RiskManagementSettings,
  RiskStatusView,
  SettingsChangeEntry,
  ShortSellingStatusView,
  StageGateStatus,
  ThemeConcentrationControlView,
} from './contracts';
import { isPreCloseControlsView, isThemeConcentrationControlView } from './contracts';

// SC-02, SC-03, FR-10, FR-13, FR-19, FR-20, IADR-0288: RiskManagementService（BFF `/bff/risk-controls/*`）の
// サーバー状態。
//
// MSP/ADR-0031: **サーバー状態は TanStack Query に一元化する。** 画面が `useEffect` ＋ `useState` で
// 取得・再取得を手書きすると、同じ画面が 2 つの真実（別々に取った equity など）を持てるようになる。
//
// 🔴 **`apiFetch` を呼んでよいのはこの層だけである。** 基盤は画面（features）からの `apiFetch` を
// ESLint で error にしており（MSP/IADR-0146）、BFF 呼び出しは orval 生成フックへ寄せている。
// **本ユニットは生成フックを持てない**（生成の入力は基盤の OpenAPI の `/bff/` 配下で、本ユニットの
// 端点はそこに載っていない。作業仕様書 20260903_414 §計画書との差異）。したがって
// 「`apiFetch` を使わない」ことはできず、**使ってよい場所を 1 段に閉じる**のが本ユニットの形である。

/**
 * クエリキー。**BFF のパスと 1 対 1 に対応させる**（キーを勝手な語で作ると、どの端点の
 * キャッシュを無効化しているのか読めなくなる）。前方一致で無効化できるよう階層で持つ。
 */
export const riskQueryKeys = {
  /** `GET /risk-controls/settings` とその配下（履歴）。 */
  settings: ['risk-controls', 'settings'] as const,
  settingsHistory: ['risk-controls', 'settings', 'history'] as const,
  status: ['risk-controls', 'status'] as const,
  stageGate: ['risk-controls', 'stage-gate'] as const,
  shortSelling: ['risk-controls', 'short-selling'] as const,
};

/** リスク統制の設定（上限・ガード・段階・発注先）。 */
export function useRiskSettings() {
  return useQuery({
    queryKey: riskQueryKeys.settings,
    queryFn: () => apiFetch<RiskManagementSettings>('/risk-controls/settings'),
  });
}

/**
 * 設定の変更履歴（新しい順）。取得不能はその領域だけの縮退に留める（呼び出し側が判断する）。
 *
 * 🔴 **配列でない応答は「0 件」ではなく失敗として扱う。** 呼び出し側は種別で絞り込むため、
 * 想定外の形を空配列へ丸めると「履歴が無い」という**別の事実**として描かれる（IADR-0154）。
 */
export function useRiskSettingsHistory() {
  return useQuery({
    queryKey: riskQueryKeys.settingsHistory,
    queryFn: async () => {
      const data = await apiFetch<SettingsChangeEntry[]>('/risk-controls/settings/history');
      if (!Array.isArray(data)) throw new TypeError('設定変更履歴の応答が配列ではありません。');
      return data;
    },
  });
}

/**
 * 統制状態（equity・実額・段階・発注先）。
 *
 * FR-10, SC-02, IADR-0151 決定4: **ページで 1 回だけ取得して配る**という従前の要求は、
 * TanStack Query のキャッシュがそのまま満たす（同じキーの購読者は同じ値を見る）。
 */
export function useRiskStatus() {
  return useQuery({
    queryKey: riskQueryKeys.status,
    queryFn: () => apiFetch<RiskStatusView>('/risk-controls/status'),
  });
}

/** 段階ゲートの現況（昇格判定・撤退判定・遷移履歴）。 */
export function useStageGate() {
  return useQuery({
    queryKey: riskQueryKeys.stageGate,
    queryFn: () => apiFetch<StageGateStatus>('/risk-controls/stage-gate'),
  });
}

/** FR-10, ADR-0016: 空売りの現況（維持率・空売り比率・建玉方向・借株料累計）。 */
export function useShortSelling() {
  return useQuery({
    queryKey: riskQueryKeys.shortSelling,
    queryFn: () => apiFetch<ShortSellingStatusView>('/risk-controls/short-selling'),
  });
}

/**
 * 設定変更（PUT）の共通形。
 *
 * 成功後は**設定とその履歴、統制状態を無効化する**——上限が変われば解決済みの実額（統制状態）も
 * 変わるため、実額の併記を古いまま残さない（従前の `loadCurrent` / `loadHistory` / `loadRiskStatus`
 * の呼び直しと同じ範囲を、呼び出し側ではなくこの層が持つ）。
 * `riskQueryKeys.settings` は前方一致で履歴キーも含む。
 */
function useRiskSettingsMutation<TBody>(path: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (body: TBody) => apiFetch<unknown>(path, { method: 'PUT', json: body }),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: riskQueryKeys.settings }),
        queryClient.invalidateQueries({ queryKey: riskQueryKeys.status }),
      ]);
    },
  });
}

/** FR-10, #362, IADR-0151: リスク上限の変更（本文は equity 比）。 */
export function useSaveRiskLimits() {
  return useRiskSettingsMutation<{ limits: Record<string, number>; reason: string }>(
    '/risk-controls/settings/limits',
  );
}

/** FR-19, IADR-0086: 取引ガードの変更（全置換）。 */
export function useSaveTradingGuard() {
  return useRiskSettingsMutation<{
    enabledProductTypes: number[];
    enabledMarkets: number[];
    bannedSymbols: unknown[];
    preventSameDayReentry: boolean;
    prohibitManipulativeOrderPatterns: boolean;
    reason: string;
  }>('/risk-controls/settings/guard');
}

/** FR-20, #334, IADR-0141: 発注先の変更（実弾は確認フレーズを伴う）。 */
export function useSaveBrokerProvider() {
  return useRiskSettingsMutation<{
    provider: number;
    reason: string;
    acknowledgedLiveTrading: boolean;
    acknowledgement: string | null;
  }>('/risk-controls/settings/broker-provider');
}

/**
 * FR-10, FR-12, SC-02, ADR-0040 決定1・決定3, #823, IADR-0422 決定2: 損切りの実行機構の変更（利用者のみ・理由必須）。
 * 実弾（moomoo REAL）の間の S0 以外はサーバが 400 で拒否する（画面の無効化と同じ規則）。
 */
export function useSaveStopLossMethod() {
  return useRiskSettingsMutation<{ method: number; reason: string }>(
    '/risk-controls/settings/stop-loss-method',
  );
}

/** FR-20, #423, IADR-0164: Stage 1 の最小取引件数の変更。 */
export function useSaveStage1MinimumTradeCount() {
  return useRiskSettingsMutation<{ minimumTradeCount: number; reason: string }>(
    '/risk-controls/settings/stage1-minimum-trade-count',
  );
}

/**
 * 🔴 FR-10, SC-02, UC-06, ADR-0066 決定2, ADR-0063 決定1・決定2, #1291, IADR-0530: 高ボラティリティ銘柄の統制値の変更
 * （区分の上限〔equity 比〕と明示指定の一覧を**全置換**。利用者のみ・理由必須・値域外は 400）。
 */
export function useSaveHighVolatility() {
  return useRiskSettingsMutation<{
    maxOrderAmountRatio: number;
    designatedSymbols: { symbol: string; market: number }[];
    reason: string;
  }>('/risk-controls/settings/high-volatility');
}

/**
 * 🔴 FR-10, FR-19, SC-02, ADR-0070 決定 5, #1407, IADR-0564 決定 3: 閉場前の統制（締め切り N・手仕舞いの有効と M）の
 * 効いている値・構成の値・保存値・食い違い。キーは `settings` の配下（設定の保存で一緒に無効化される）。
 */
export function usePreCloseControls() {
  return useQuery({
    queryKey: [...riskQueryKeys.settings, 'pre-close'] as const,
    queryFn: async () => {
      const data = await apiFetch<PreCloseControlsView>('/risk-controls/settings/pre-close');
      // 🔴 想定外の形（旧い後段・BFF 未結線の別応答）を既定値で埋めて描かない。失敗として扱い、区画は「取得できません」に縮退する。
      if (!isPreCloseControlsView(data)) throw new TypeError('閉場前の統制の応答の形が想定と違います。');
      return data;
    },
  });
}

/** 🔴 FR-10, FR-19, SC-02, ADR-0071 決定 5, #1407, IADR-0564 決定 3: テーマの定義の効いている値・構成の値・保存値・食い違い。 */
export function useThemeConcentration() {
  return useQuery({
    queryKey: [...riskQueryKeys.settings, 'theme-concentration'] as const,
    queryFn: async () => {
      const data = await apiFetch<ThemeConcentrationControlView>('/risk-controls/settings/theme-concentration');
      if (!isThemeConcentrationControlView(data)) throw new TypeError('テーマの定義の応答の形が想定と違います。');
      return data;
    },
  });
}

/** 閉場前の統制の保存（締め切り・手仕舞いを一度に置き換える。利用者のみ・理由必須・値域外は 400）。 */
export function useSavePreCloseControls() {
  return useRiskSettingsMutation<{
    cutoffMinutes: { market: number; minutes: number }[];
    flattenEnabled: boolean;
    flattenMinutes: { market: number; minutes: number }[];
    reason: string;
  }>('/risk-controls/settings/pre-close');
}

/** テーマの定義の保存（定義の全体を置き換える。利用者のみ・理由必須・値域外は 400）。 */
export function useSaveThemeConcentration() {
  return useRiskSettingsMutation<{
    themes: { name: string; market: number; symbols: string[]; maxPositions: number }[];
    reason: string;
  }>('/risk-controls/settings/theme-concentration');
}
