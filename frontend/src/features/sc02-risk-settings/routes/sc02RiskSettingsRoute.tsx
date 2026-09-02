import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { NavItem } from '@foundation/routing/featureRegistry';
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
