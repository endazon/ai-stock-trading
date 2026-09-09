import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { NavItem } from '@foundation/routing/featureRegistry';
import type { ShellRoute } from '@foundation/routing/shell';
import { RequireRole } from '@foundation/auth/RequireRole';
import { TradingRole } from '@ai-stock-trading/lib/roles';

// SC-04, FR-09, FR-11, UC-06（代替フロー「ゲートウェイの有人認証」）, NFR-06, IADR-0321:
// OpenD 認証操作 feature の公開面。
//
// 認可は `trading-owner` 限定であり、**権限外は存在秘匿（NotFound）**である（NFR-06）。
// ルートの契約と存在秘匿の扱いは SC-01 / SC-03 と同じ（MSP/IADR-0124 決定 1 / IADR-0288）。
//
// 🔴 **本画面はゲートウェイの認証操作であり、取引の破壊的統制操作ではない。** よって
// 「破壊的操作は Discord Bot へ一元化する」対象では**ない**（画像 CAPTCHA の提示が
// テキスト対話で成立しないため。計画 05_screens SC-04）。
//
// ルートは**ユニット相対**の `/opend-auth` である（計画の表記は `/trading/opend-auth`）。
// 既存 3 画面も同じ形（計画 `/trading/settings` → 実装 `/settings` 等）であり、`/trading` は
// 基盤 SPA がユニットを載せる位置である。**ここへ書くと二重になる。**

// NFR, MSP/IADR-0134: 画面はルート単位の遅延チャンクへ分ける。
const OpendAuthPage = lazyRouteComponent(
  () => import('../components/OpendAuthPage'),
  'OpendAuthPage',
);

export const createSc04OpendAuthRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/opend-auth',
    wrapInSuspense: true,
    component: function GuardedSc04OpendAuth() {
      return (
        <RequireRole anyOf={[TradingRole.Owner]}>
          <OpendAuthPage />
        </RequireRole>
      );
    },
  });

// `group` を宣言しない理由は SC-01 / SC-03 と同じ（MSP/IADR-0125 決定 9）。
export const sc04OpendAuthNav: NavItem = {
  id: 'sc04-opend-auth',
  label: 'OpenD 認証',
  to: '/opend-auth',
  requiresAnyRole: [TradingRole.Owner],
};
