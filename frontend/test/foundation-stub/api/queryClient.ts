// IADR-0080 / IADR-0288: @foundation/api/queryClient のテスト/型検査用スタブ。
// 実体は platform の src/platform/frontend/src/lib/api/queryClient.ts（合成時に解決）。挙動を写像する。
//
// MSP/ADR-0031 / MSP/IADR-0121: サーバー状態は TanStack Query に一元化し、**QueryClient の生成点は
// foundation ただ 1 つ**である（ユニット側は作らない）。単独リポの E2E ハーネスだけが、実アプリの
// 代わりにこのスタブから 1 つ作る。
//
// 再試行の既定を写像するのは、**本ユニットの画面が 404 を通常の応答として受ける**ためである
// （IADR-0009 の存在秘匿。権限外の資源は 404 で返る）。数値の `retry: 1` にすると、確実に失敗する
// 2 回目の往復ぶんだけ中立表示が遅れる。
import { QueryClient } from '@tanstack/react-query';
import { ApiError } from './ApiError';

/** 一過性の失敗を吸収する再試行の上限（1 回だけ再試行する）。 */
export const MAX_QUERY_RETRIES = 1;

/** ネットワーク断・一過性の 5xx は 1 度だけ吸収し、4xx は再試行しない（408 / 429 のみ例外）。 */
export function shouldRetryQuery(failureCount: number, error: unknown): boolean {
  if (failureCount >= MAX_QUERY_RETRIES) return false;
  if (
    error instanceof ApiError &&
    error.status !== null &&
    error.status >= 400 &&
    error.status < 500
  ) {
    return error.status === 408 || error.status === 429;
  }
  return true;
}

export const DEFAULT_QUERY_OPTIONS = {
  retry: shouldRetryQuery,
  refetchOnWindowFocus: false,
  staleTime: 30_000,
} as const;

/** アプリ用の QueryClient を生成する（テストは毎回新しいインスタンスを作ってキャッシュを分離する）。 */
export function createAppQueryClient(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { ...DEFAULT_QUERY_OPTIONS } } });
}

/** アプリ本体が使う共有インスタンス。 */
export const queryClient = createAppQueryClient();
