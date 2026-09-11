import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { FeatureBreadcrumb, NavItem } from '@foundation/routing/featureRegistry';
import type { ShellRoute } from '@foundation/routing/shell';
import { RequireRole } from '@foundation/auth/RequireRole';
import { TradingRole } from '@ai-stock-trading/lib/roles';

// SC-02, FR-13, FR-19, FR-20, UC-06, IADR-0084: リスク設定 feature の公開面（リスク上限の閲覧/変更）。
//
// ルートの契約と存在秘匿の扱いは SC-01 と同じである（MSP/IADR-0124 決定 1 /
// IADR-0288）。データ源は SC-01（ConfigurationService `/assumptions`）とは別サービス
// （RiskManagementService・MarketMonitorService）のため独立画面とする。

// NFR, MSP/IADR-0134: 画面はルート単位の遅延チャンクへ分ける。
const RiskSettingsPage = lazyRouteComponent(() => import('../components/RiskSettingsPage'), 'RiskSettingsPage');

export const createSc02RiskSettingsRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    path: '/settings/risk',
    wrapInSuspense: true,
    component: function GuardedSc02RiskSettings() {
      return (
        <RequireRole anyOf={[TradingRole.Owner]}>
          <RiskSettingsPage />
        </RequireRole>
      );
    },
  });

// `group` を宣言しない理由は SC-01 と同じ（MSP/IADR-0125 決定 9）。
export const sc02RiskSettingsNav: NavItem = {
  id: 'sc02-risk-settings',
  label: 'リスク設定',
  to: '/settings/risk',
  requiresAnyRole: [TradingRole.Owner],
};

// UI/UX 改善 2026-09-12・基盤 05_screens §共通シェル「パンくず・権限バッジ」: 本画面のパンくず宣言。
//
// 🔴 **ルート・ナビとは別の登録面である**（合成点が `registerBreadcrumbs` へ渡す）。片方だけ足すと
// 「画面は開けるのにパンくずが出ない」になる。親の段「取引」は本ユニットの入口（SC-01 設定）を指す
// ——hi-fi モックの crumb 帯（`基盤ポータル / 取引 / リスク設定`）と同じ並びである。
//
// `requiresAnyRole` は**存在秘匿（IADR-0009）の経路**であり、ルートの `RequireRole anyOf` および
// 左ナビの `requiresAnyRole` と同じ値を置く（ずれると権限外にパンくずだけ見える）。
export const sc02RiskSettingsBreadcrumb: FeatureBreadcrumb = {
  routePath: '/settings/risk',
  // 基盤の 4 グループのうち `user` は**グループ段を描かない**（本ユニットは基盤の計画に属さないため、
  // ナビと同じく区分を主張しない）。
  group: 'user',
  parents: [{ label: '取引', to: '/settings' }],
  label: 'リスク設定',
  requiresAnyRole: [TradingRole.Owner],
};
