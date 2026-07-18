import type { FeatureModule } from '@foundation/routing/featureRegistry';
import { RequireRole } from '@foundation/auth/RequireRole';
import { TradingRole } from '../roles';
import { SettingsPage } from './SettingsPage';

// SC-01, FR-13, FR-17, UC-06: 設定画面 feature。
// アクセスは利用者（trading-owner）に限定し、権限外は RequireRole が NotFound を描画して画面の存在を示さない
// （存在秘匿。IADR-0009/0035）。サーバ側 /bff/assumptions も認可（OwnerOnly）で守る。
// 第1スライスは FR-17 全体前提条件の閲覧/変更のみ（IADR-0080）。FR-13 設定（監視銘柄/閾値/リスク上限）は後続スライス。
export const sc01SettingsFeature: FeatureModule = {
  id: 'sc01-settings',
  routes: [
    {
      path: 'settings',
      element: (
        <RequireRole anyOf={[TradingRole.Owner]}>
          <SettingsPage />
        </RequireRole>
      ),
    },
  ],
  nav: { label: '設定', to: '/settings', requiresAnyRole: [TradingRole.Owner] },
};
