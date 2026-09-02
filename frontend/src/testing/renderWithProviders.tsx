import type { ReactElement } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react';

// IADR-0286: 画面**単体**のテスト用ハーネス（test-only）。
//
// MSP/ADR-0031 でサーバー状態を TanStack Query へ移したため、取得を行う画面は
// `QueryClientProvider` の下でしか描画できない。**各テストファイルで provider を書き直さない**
// （書き直すと既定値が少しずつずれ、あるファイルだけ再試行が有効という状態が静かに生まれる）。
//
// 🔴 **ルート・ナビ・存在秘匿の検証にはこれを使わない。** それらは `@foundation/testing/renderUnitRoute`
// （実アプリと同じ id の共通シェルの下にルート factory を載せる）が担う——ここで描くのは
// **ガードの内側のコンポーネントだけ**であり、ルート木に載っているかどうかは何も言えない。

/**
 * 検査用の QueryClient。
 *
 * - **描画のたびに新しく作る**（使い回すと、あるテストが載せたキャッシュを次のテストが読む）。
 * - `retry: false`: 本番の既定は 5xx とネットワーク断を 1 度だけ再試行する。異常系の検証で
 *   往復を待つことになり、`apiFetch` の呼び出し回数を数えるテストも壊れる。
 * - `staleTime: 0` / `gcTime: 0`: キャッシュの持ち越しを作らない。
 */
export function createTestQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, staleTime: 0, gcTime: 0, refetchOnWindowFocus: false },
      mutations: { retry: false },
    },
  });
}

/** 画面を TanStack Query の provider の下で描画する（同期。既存の `render` と同じ戻り値）。 */
export function renderWithProviders(ui: ReactElement) {
  const queryClient = createTestQueryClient();
  const result = render(<QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>);
  return { queryClient, ...result };
}
