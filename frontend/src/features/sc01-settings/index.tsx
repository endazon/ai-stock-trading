import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { NavItem } from '@foundation/routing/featureRegistry';
import type { ShellRoute } from '@foundation/routing/shell';
import { RequireRole } from '@foundation/auth/RequireRole';
import { TradingRole } from '../roles';

// SC-01, FR-13, FR-17, UC-06: 設定画面 feature の公開面。
//
// MSP/ADR-0031 / MSP/IADR-0124 決定 1 / IADR-0286: ルートは **`(shell: ShellRoute) => Route` の factory** で
// 公開する。platform を import せず、共通シェルを引数で受け取る——これが「platform → 可変ユニットの
// 参照禁止」を保ったまま型付きルート木へ載る唯一の形である。
// **旧契約 `FeatureModule { id, routes: [{ path, element }], nav }` は #414 で廃した**（互換ブリッジ
// `createLegacyRoutes` は型付きルート木の外側に置かれ、`<Link to>` の静的検査が効かなかった）。
//
// アクセスは利用者（trading-owner）に限定し、権限外は RequireRole が NotFound を描画して画面の存在を
// 示さない（存在秘匿。IADR-0009/0035）。サーバ側 /bff/assumptions も認可（OwnerOnly）で守る。

// NFR, MSP/IADR-0134: 画面はルート単位の遅延チャンクへ分ける（初期チャンクに載せない）。
const SettingsPage = lazyRouteComponent(() => import('./SettingsPage'), 'SettingsPage');

export const createSc01SettingsRoute = (shell: ShellRoute) =>
  createRoute({
    getParentRoute: () => shell,
    // 共通シェル配下の**絶対表記**。旧契約は相対表記（`settings`）で、互換ブリッジが実行時に
    // 先頭の `/` を補っていた。宣言の側へ移す。
    path: '/settings',
    // ガード（RequireRole）は初期チャンクに残し、画面だけを遅延させる。ガードが先に評価されるため
    // 権限外の利用者は画面チャンクを取得しない（存在秘匿）。反面 router.load() の事前読み込みが
    // 効かず描画時に suspend するため、このルートには Suspense 境界が要る。
    wrapInSuspense: true,
    component: function GuardedSc01Settings() {
      return (
        <RequireRole anyOf={[TradingRole.Owner]}>
          <SettingsPage />
        </RequireRole>
      );
    },
  });

// 左ナビ項目。**`group` は宣言しない**——基盤の 4 グループは基盤の計画に属するユニットの区分であり、
// 本ユニットの項目は合成点が「株式自動売買」のグループへ束ねる（MSP/IADR-0125 決定 9）。
export const sc01SettingsNav: NavItem = {
  id: 'sc01-settings',
  label: '設定',
  to: '/settings',
  requiresAnyRole: [TradingRole.Owner],
};
