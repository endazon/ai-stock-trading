import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { NavItem } from '@foundation/routing/featureRegistry';
import type { ShellRoute } from '@foundation/routing/shell';
import { RequireRole } from '@foundation/auth/RequireRole';
import { TradingRole } from '@ai-stock-trading/lib/roles';

// SC-03, FR-10, FR-20, UC-06, IADR-0084: 承認・統制状態参照 feature の公開面（参照専用）。
//
// ルートの契約と存在秘匿の扱いは SC-01（`../sc01-settings/index.tsx`）と同じである（MSP/IADR-0124 決定 1 /
// IADR-0288）。破壊的操作（pause/resume・kill switch・段階遷移承認）は #165 の Discord Bot 側と
// 役割分担し、本画面には置かない。

// NFR, MSP/IADR-0134: 画面はルート単位の遅延チャンクへ分ける。
const ControlStatusPage = lazyRouteComponent(
  () => import('./ControlStatusPage'),
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
