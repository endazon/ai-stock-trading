// IADR-0080 / IADR-0286: @foundation/testing/renderUnitRoute のテスト用スタブ。
// 実体は platform の src/platform/frontend/src/testing/renderUnitRoute.tsx（合成時に解決）。
//
// MSP/ADR-0031 / MSP/IADR-0124: 可変ユニットの画面テスト用ハーネス。ユニットの画面は型付きルート
// factory で公開されるので、**テストでも実アプリと同じ id（`_shell`）を持つレイアウトルートの下へ
// 載せる**必要がある（`useSearch({ from })` などがルート ID のリテラルに依存するため）。
//
// 実アプリのルート木をそのまま使わないのは、それが合成点（＝他ユニット）まで引き込み、
// ユニット単体のテストが他ユニットの存在に依存するためである。
//
// **本スタブは Lingui の I18nProvider を重ねない**（本ユニットは Lingui を採らない。作業仕様書
// 20260903_414 §計画書との差異）。実体は重ねるが、翻訳マクロを使わない画面には影響しない。
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  createMemoryHistory,
  createRootRoute,
  createRoute,
  createRouter,
  Outlet,
  RouterProvider,
} from '@tanstack/react-router';
import type { AnyRoute } from '@tanstack/react-router';
import { configure, getConfig } from '@testing-library/dom';
import { act, render } from '@testing-library/react';
import { afterEach } from 'vitest';
import { AuthContext } from '../auth/AuthContext';
import type { AuthState } from '../auth/AuthContext';
import type { ShellRoute } from '../routing/shell';

// `configure()` は @testing-library/dom の**グローバル設定**を書き換えるため、各テストの後に戻す
// （戻さないと、この入口を使っていないテストまで待ち時間の延長を受ける）。
const DEFAULT_ASYNC_UTIL_TIMEOUT = getConfig().asyncUtilTimeout;

afterEach(() => {
  configure({ asyncUtilTimeout: DEFAULT_ASYNC_UTIL_TIMEOUT });
});

/** ロールを持つ認証済みユーザーの AuthState（MSP/ADR-0032: 身元は /bff/auth/me の形。トークンは無い）。 */
export function authStateWithRoles(roles: readonly string[]): AuthState {
  return {
    user: { name: 'tester', subject: 'tester', roles: [...roles] },
    isAuthenticated: true,
    isLoading: false,
    login: async () => {},
    logout: async () => {},
  };
}

export interface RenderUnitRouteOptions {
  /** 初期 URL（検索パラメータを含めてよい）。 */
  initialEntry: string;
  /** 認証済みユーザーのロール（RequireRole を通す画面で使う）。省略時は空＝権限なし。 */
  roles?: readonly string[];
}

/**
 * 検査用の QueryClient。
 *
 * - **描画のたびに新しく作る**（使い回すと、あるテストが載せたキャッシュを次のテストが読む）。
 * - `retry: false`: 本番の既定は 5xx とネットワーク断を 1 度だけ再試行する。異常系の検証で
 *   往復を待つことになり、`apiFetch` の呼び出し回数を数えるテストも壊れる。
 * - `staleTime: 0` / `gcTime: 0`: キャッシュの持ち越しを作らない。
 */
function createTestQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, staleTime: 0, gcTime: 0, refetchOnWindowFocus: false },
      mutations: { retry: false },
    },
  });
}

/** ユニットのルート factory を、実アプリと同じ id を持つ検査用シェルの下で描画する。 */
export async function renderUnitRoute(
  createRoutes: (shell: ShellRoute) => readonly AnyRoute[],
  { initialEntry, roles = [] }: RenderUnitRouteOptions,
) {
  // ガード（RequireRole）配下の画面は router.load() の事前読み込みが効かず、描画が始まってから
  // 動的 import が走る。既定の 1000 ms では足りないことがあるため、この入口を使ったテストに限り延長する。
  configure({ asyncUtilTimeout: 5000 });

  const testRoot = createRootRoute({ component: Outlet });
  // 実アプリの shellRoute と同じ id を持つ検査用レイアウト。ここだけが型の付け替えであり、
  // ユニット側から見た形（親ルート）は実アプリと同一である。
  const testShell = createRoute({
    getParentRoute: () => testRoot,
    id: '_shell',
    component: Outlet,
  }) as unknown as ShellRoute;

  // 検査用の木は実行時に組み立てるため型付けを持たない（本番の型安全は platform の router.tsx が担う）。
  type Composable = { addChildren: (children: readonly AnyRoute[]) => AnyRoute };
  const shell = (testShell as unknown as Composable).addChildren(createRoutes(testShell));
  const routeTree = (testRoot as unknown as Composable).addChildren([shell]);
  const router = createRouter({
    routeTree,
    history: createMemoryHistory({ initialEntries: [initialEntry] }),
  });

  const queryClient = createTestQueryClient();
  const result = render(
    <QueryClientProvider client={queryClient}>
      <AuthContext.Provider value={authStateWithRoles(roles)}>
        <RouterProvider router={router as never} />
      </AuthContext.Provider>
    </QueryClientProvider>,
  );
  // TanStack Router の初期描画は非同期（マッチの解決を待つ）。ここで待たないと、
  // 描画直後に getBy* を使うテストが空の DOM を見る。
  await act(async () => {
    await router.load();
  });

  return { router, queryClient, ...result };
}
