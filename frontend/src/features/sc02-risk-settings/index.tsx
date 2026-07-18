import type { FeatureModule } from '@foundation/routing/featureRegistry';
import { RequireRole } from '@foundation/auth/RequireRole';
import { TradingRole } from '../roles';
import { RiskSettingsPage } from './RiskSettingsPage';

// SC-02, FR-13, FR-19, FR-20, UC-06, IADR-0084: リスク設定 feature（リスク上限の閲覧/変更）。
// アクセスは利用者（trading-owner）に限定し、権限外は RequireRole が NotFound を描画して画面の存在を示さない
// （存在秘匿。IADR-0009/0035）。サーバ側 /risk-controls/settings も認可（OwnerOnly）で守る。
// データ源は SC-01（ConfigurationService /assumptions）とは別サービス（RiskManagementService）のため独立画面とする。
export const sc02RiskSettingsFeature: FeatureModule = {
  id: 'sc02-risk-settings',
  routes: [
    {
      path: 'settings/risk',
      element: (
        <RequireRole anyOf={[TradingRole.Owner]}>
          <RiskSettingsPage />
        </RequireRole>
      ),
    },
  ],
  nav: { label: 'リスク設定', to: '/settings/risk', requiresAnyRole: [TradingRole.Owner] },
};
