import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { FeatureBreadcrumb, NavItem } from '@foundation/routing/featureRegistry';
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

// UI/UX 改善 2026-09-12・基盤 05_screens §共通シェル「パンくず・権限バッジ」: 本画面のパンくず宣言。
//
// 🔴 **ルート・ナビとは別の登録面である**（合成点が `registerBreadcrumbs` へ渡す）。片方だけ足すと
// 「画面は開けるのにパンくずが出ない」になる。親の段「取引」は本ユニットの入口（SC-01 設定）を指す
// ——hi-fi モックの crumb 帯（`基盤ポータル / 取引 / OpenD 認証`）と同じ並びである。
//
// `requiresAnyRole` は**存在秘匿（IADR-0009）の経路**であり、ルートの `RequireRole anyOf` および
// 左ナビの `requiresAnyRole` と同じ値を置く（ずれると権限外にパンくずだけ見える）。
export const sc04OpendAuthBreadcrumb: FeatureBreadcrumb = {
  routePath: '/opend-auth',
  // 基盤の 4 グループのうち `user` は**グループ段を描かない**（本ユニットは基盤の計画に属さないため、
  // ナビと同じく区分を主張しない）。
  group: 'user',
  parents: [{ label: '取引', to: '/settings' }],
  label: 'OpenD 認証',
  requiresAnyRole: [TradingRole.Owner],
};
