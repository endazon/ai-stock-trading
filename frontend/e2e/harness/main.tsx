import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClientProvider } from '@tanstack/react-query';
import {
  createRootRoute,
  createRoute,
  createRouter,
  Link,
  Outlet,
  RouterProvider,
} from '@tanstack/react-router';
import type { AnyRoute } from '@tanstack/react-router';
import { queryClient } from '@foundation/api/queryClient';
import type { ShellRoute } from '@foundation/routing/shell';
import { NotFound } from '@foundation/ui/NotFound';
import { aiStockTradingNavItems, createAiStockTradingRoutes } from '@ai-stock-trading/features';
import { AuthHarness } from './AuthHarness';

// SC-01/02/03, IADR-0087, IADR-0288: E2E 実行用の test-only ハーネス。
//
// 本ユニットは platform SPA へ合成される feature ユニットで単独の実行アプリを持たないため、
// **合成点がするのと同じこと**——共通シェル（id `_shell`）の下へルート factory を載せる——を
// 最小構成で行う（検証対象は本番コンポーネント・ハーネスは配線のみ）。
// #414 で `react-router-dom` から TanStack Router へ移した。
//
// 認証/ロールは AuthHarness が URL クエリから供給し、BFF 応答は Playwright の page.route がモックする。

// ナビゲーション＋子ルートの描画枠。nav はロール可視性を問わず全項目を列挙する
// （E2E は URL 直接遷移が基本であり、ここは「ナビ項目が公開されている」ことの表示面である）。
function Layout() {
  return (
    <div>
      <nav>
        <ul>
          {aiStockTradingNavItems.map((nav) => (
            <li key={nav.id}>
              <Link to={nav.to}>{nav.label}</Link>
            </li>
          ))}
        </ul>
      </nav>
      <Outlet />
    </div>
  );
}

const rootRoute = createRootRoute({ component: Outlet, notFoundComponent: NotFound });

// 実アプリの共通シェルと**同じ id** を持たせる（配下ルートの ID が `/_shell/<path>` になる。
// ユニット側から見た形を実アプリと一致させる唯一の要件である）。
const harnessShell = createRoute({
  getParentRoute: () => rootRoute,
  id: '_shell',
  component: Layout,
  notFoundComponent: NotFound,
}) as unknown as ShellRoute;

// 実行時に組み立てる木は型付けを持たない（本番の型安全は platform の router.tsx が担う）。
type Composable = { addChildren: (children: readonly AnyRoute[]) => AnyRoute };
const shellWithUnit = (harnessShell as unknown as Composable).addChildren(
  createAiStockTradingRoutes(harnessShell),
);
const routeTree = (rootRoute as unknown as Composable).addChildren([shellWithUnit]);
const router = createRouter({ routeTree });

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <AuthHarness>
        <RouterProvider router={router as never} />
      </AuthHarness>
    </QueryClientProvider>
  </StrictMode>,
);
