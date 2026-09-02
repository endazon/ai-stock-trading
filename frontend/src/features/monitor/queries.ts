import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@foundation/api/apiClient';
import type { MarketMonitorSettings, MonitoredSymbol, MonitorSettingsChangeEntry } from './contracts';

// SC-02, FR-03, FR-13, IADR-0286: MarketMonitorService（BFF `/bff/monitor/*`）のサーバー状態。
//
// **リスク統制（RiskManagementService）とは別サービスである。** 片方の障害・BFF 未結線を
// もう片方が巻き込まないよう、クエリを分けたまま保つ（IADR-0090 決定1）。
// `apiFetch` を呼んでよいのはこの層だけである（理由は `../risk/queries.ts` 冒頭）。

export const monitorQueryKeys = {
  /** `GET /monitor/watchlist` とその配下（履歴）。 */
  watchlist: ['monitor', 'watchlist'] as const,
  watchlistHistory: ['monitor', 'watchlist', 'history'] as const,
  settings: ['monitor', 'settings'] as const,
  settingsHistory: ['monitor', 'settings', 'history'] as const,
};

/** 監視銘柄の一覧。想定外の形（配列でない）は空扱いにする（画面を壊さない・fail-safe）。 */
export function useWatchlist() {
  return useQuery({
    queryKey: monitorQueryKeys.watchlist,
    queryFn: async () => {
      const data = await apiFetch<MonitoredSymbol[]>('/monitor/watchlist');
      return Array.isArray(data) ? data : [];
    },
  });
}

/** 監視銘柄の変更履歴（新しい順）。 */
export function useWatchlistHistory() {
  return useQuery({
    queryKey: monitorQueryKeys.watchlistHistory,
    queryFn: async () => {
      const data = await apiFetch<MonitorSettingsChangeEntry[]>('/monitor/watchlist/history');
      return Array.isArray(data) ? data : [];
    },
  });
}

/** 市場監視パラメータ（変動閾値・クールダウン）。 */
export function useMonitorSettings() {
  return useQuery({
    queryKey: monitorQueryKeys.settings,
    queryFn: () => apiFetch<MarketMonitorSettings>('/monitor/settings'),
  });
}

/**
 * 市場監視パラメータの変更履歴（絞り込みは呼び出し側の関心＝表示の都合であるため行わない）。
 *
 * 🔴 **配列でない応答は「0 件」ではなく失敗として扱う**（理由は `../risk/queries.ts` の同名クエリ）。
 */
export function useMonitorSettingsHistory() {
  return useQuery({
    queryKey: monitorQueryKeys.settingsHistory,
    queryFn: async () => {
      const data = await apiFetch<MonitorSettingsChangeEntry[]>('/monitor/settings/history');
      if (!Array.isArray(data)) {
        throw new TypeError('市場監視パラメータの変更履歴の応答が配列ではありません。');
      }
      return data;
    },
  });
}

interface WatchlistChange {
  symbol: string;
  market: number;
  reason: string;
}

/**
 * 監視銘柄の追加・削除（**個別操作 API を消費する。全置換しない**。IADR-0090 決定2）。
 * 成功後は一覧と履歴を無効化する（`watchlist` キーは前方一致で履歴を含む）。
 */
function useWatchlistMutation(method: 'POST' | 'DELETE') {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (body: WatchlistChange) =>
      apiFetch<MonitoredSymbol[]>('/monitor/watchlist', { method, json: body }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: monitorQueryKeys.watchlist });
    },
  });
}

/** `POST /monitor/watchlist`。重複追加・空・未定義 market はサーバ 400。 */
export function useAddWatchlistSymbol() {
  return useWatchlistMutation('POST');
}

/** `DELETE /monitor/watchlist`（body に対象と理由）。不在削除はサーバ 400。 */
export function useRemoveWatchlistSymbol() {
  return useWatchlistMutation('DELETE');
}

/**
 * 市場監視パラメータの項目単位の更新。
 *
 * **変動閾値とクールダウンで端点が分かれているのはサーバ側の都合そのものである**——1 つの
 * フォームで両方を送ると「片方だけ成功した」状態を作れる。ここでも 1 端点 1 mutation に保つ。
 */
function useMonitorSettingsMutation<TBody>(path: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (body: TBody) => apiFetch<MarketMonitorSettings>(path, { method: 'PUT', json: body }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: monitorQueryKeys.settings });
    },
  });
}

/** `PUT /monitor/settings/movement-threshold`（本文は比率）。 */
export function useSaveMovementThreshold() {
  return useMonitorSettingsMutation<{ movementThresholdRatio: number; reason: string }>(
    '/monitor/settings/movement-threshold',
  );
}

/** `PUT /monitor/settings/cooldown`（本文は TimeSpan 文字列）。 */
export function useSaveCooldown() {
  return useMonitorSettingsMutation<{ cooldown: string; reason: string }>(
    '/monitor/settings/cooldown',
  );
}
