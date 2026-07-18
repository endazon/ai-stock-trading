import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { createBrowserRouter, RouterProvider, Link, Outlet } from 'react-router-dom';
import type { RouteObject } from 'react-router-dom';
import { features } from '@ai-stock-trading/features';
import { AuthHarness } from './AuthHarness';

// SC-01/02/03, IADR-0087: E2E 実行用の test-only ハーネス。
// 本ユニットは platform SPA へ合成される feature ユニットで単独の実行アプリを持たないため、src の実 feature 群を
// react-router へマウントする最小アプリをここに用意する（検証対象は本番コンポーネント・ハーネスは配線のみ）。
// 認証/ロールは AuthHarness が URL クエリから供給し、BFF 応答は Playwright の page.route がモックする。

// レイアウト（ナビゲーション＋子ルートの描画枠）。nav はロール可視性を問わず全 feature を列挙する（E2E はURL直接遷移が基本）。
function Layout() {
  return (
    <div>
      <nav>
        <ul>
          {features
            .filter((f) => f.nav)
            .map((f) => (
              <li key={f.id}>
                <Link to={f.nav!.to}>{f.nav!.label}</Link>
              </li>
            ))}
        </ul>
      </nav>
      <Outlet />
    </div>
  );
}

// 各 feature の routes（相対パス）をレイアウト配下の子ルートへ束ねる（platform の合成配置に相当）。
const childRoutes: RouteObject[] = features.flatMap((f) => f.routes);

const router = createBrowserRouter([
  {
    path: '/',
    element: <Layout />,
    children: childRoutes,
  },
]);

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <AuthHarness>
      <RouterProvider router={router} />
    </AuthHarness>
  </StrictMode>,
);
