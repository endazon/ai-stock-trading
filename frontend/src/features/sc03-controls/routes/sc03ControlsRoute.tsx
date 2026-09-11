import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { FeatureBreadcrumb, NavItem } from '@foundation/routing/featureRegistry';
import type { ShellRoute } from '@foundation/routing/shell';
import { RequireRole } from '@foundation/auth/RequireRole';
import { TradingRole } from '@ai-stock-trading/lib/roles';

// SC-03, FR-10, FR-20, UC-06, IADR-0084: 承認・統制状態参照 feature の公開面（参照専用）。
//
// ルートの契約と存在秘匿の扱いは SC-01 と同じである（MSP/IADR-0124 決定 1 /
// IADR-0288）。破壊的操作（pause/resume・kill switch・段階遷移承認）は #165 の Discord Bot 側と
// 役割分担し、本画面には置かない。

// NFR, MSP/IADR-0134: 画面はルート単位の遅延チャンクへ分ける。
const ControlStatusPage = lazyRouteComponent(
  () => import('../components/ControlStatusPage'),
  'ControlStatusPage',
);

export const createSc03ControlsRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/controls',
    wrapInSuspense: true,
    component: function GuardedSc03Controls() {
      return (
        <RequireRole anyOf={[TradingRole.Owner]}>
          <ControlStatusPage />
        </RequireRole>
      );
    },
  });

// `group` を宣言しない理由は SC-01 と同じ（MSP/IADR-0125 決定 9）。
export const sc03ControlsNav: NavItem = {
  id: 'sc03-controls',
  label: '統制状態',
  to: '/controls',
  requiresAnyRole: [TradingRole.Owner],
};

// UI/UX 改善 2026-09-12・基盤 05_screens §共通シェル「パンくず・権限バッジ」: 本画面のパンくず宣言。
//
// 🔴 **ルート・ナビとは別の登録面である**（合成点が `registerBreadcrumbs` へ渡す）。片方だけ足すと
// 「画面は開けるのにパンくずが出ない」になる。親の段「取引」は本ユニットの入口（SC-01 設定）を指す
// ——hi-fi モックの crumb 帯（`基盤ポータル / 取引 / 統制状態`）と同じ並びである。
//
// `requiresAnyRole` は**存在秘匿（IADR-0009）の経路**であり、ルートの `RequireRole anyOf` および
// 左ナビの `requiresAnyRole` と同じ値を置く（ずれると権限外にパンくずだけ見える）。
export const sc03ControlsBreadcrumb: FeatureBreadcrumb = {
  routePath: '/controls',
  // 基盤の 4 グループのうち `user` は**グループ段を描かない**（本ユニットは基盤の計画に属さないため、
  // ナビと同じく区分を主張しない）。
  group: 'user',
  parents: [{ label: '取引', to: '/settings' }],
  label: '統制状態',
  requiresAnyRole: [TradingRole.Owner],
};
