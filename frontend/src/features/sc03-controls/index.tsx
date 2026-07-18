import type { FeatureModule } from '@foundation/routing/featureRegistry';
import { RequireRole } from '@foundation/auth/RequireRole';
import { TradingRole } from '../roles';
import { ControlStatusPage } from './ControlStatusPage';

// SC-03, FR-10, FR-20, UC-06, IADR-0084: 承認・統制状態参照 feature（参照専用）。
// アクセスは利用者（trading-owner）に限定し、権限外は RequireRole が NotFound を描画して画面の存在を示さない
// （存在秘匿。IADR-0009/0035）。サーバ側 /risk-controls/status・/stage-gate も認可（OwnerOnly）で守る。
// 破壊的操作（pause/resume・kill switch・段階遷移承認）は #165 の Discord Bot 側と役割分担し、本画面には置かない。
export const sc03ControlsFeature: FeatureModule = {
  id: 'sc03-controls',
  routes: [
    {
      path: 'controls',
      element: (
        <RequireRole anyOf={[TradingRole.Owner]}>
          <ControlStatusPage />
        </RequireRole>
      ),
    },
  ],
  nav: { label: '統制状態', to: '/controls', requiresAnyRole: [TradingRole.Owner] },
};
