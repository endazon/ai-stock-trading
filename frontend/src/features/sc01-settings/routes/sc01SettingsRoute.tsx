import { createRoute, lazyRouteComponent } from '@tanstack/react-router';
import type { FeatureBreadcrumb, NavItem } from '@foundation/routing/featureRegistry';
import type { ShellRoute } from '@foundation/routing/shell';
import { RequireRole } from '@foundation/auth/RequireRole';
import { TradingRole } from '@ai-stock-trading/lib/roles';

// SC-01, FR-13, FR-17, UC-06: 設定画面 feature の公開面。
//
// MSP/ADR-0031 / MSP/IADR-0124 決定 1 / IADR-0288: ルートは **`(shell: ShellRoute) => Route` の factory** で
// 公開する。platform を import せず、共通シェルを引数で受け取る——これが「platform → 可変ユニットの
// 参照禁止」を保ったまま型付きルート木へ載る唯一の形である。
// **旧契約 `FeatureModule { id, routes: [{ path, element }], nav }` は #414 で廃した**（互換ブリッジ
// `createLegacyRoutes` は型付きルート木の外側に置かれ、`<Link to>` の静的検査が効かなかった）。
//
// アクセスは利用者（trading-owner）に限定し、権限外は RequireRole が NotFound を描画して画面の存在を
// 示さない（存在秘匿。IADR-0009/0035）。サーバ側 /bff/assumptions も認可（OwnerOnly）で守る。

// NFR, MSP/IADR-0134: 画面はルート単位の遅延チャンクへ分ける（初期チャンクに載せない）。
const SettingsPage = lazyRouteComponent(() => import('../components/SettingsPage'), 'SettingsPage');

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

// UI/UX 改善 2026-09-12・基盤 05_screens §共通シェル「パンくず・権限バッジ」: 本画面のパンくず宣言。
//
// 🔴 **ルート・ナビとは別の登録面である**（合成点が `registerBreadcrumbs` へ渡す）。片方だけ足すと
// 「画面は開けるのにパンくずが出ない」になる。親の段「取引」は本ユニットの入口（SC-01 設定）を指す
// ——hi-fi モックの crumb 帯（`基盤ポータル / 取引 / 設定`）と同じ並びである。
//
// `requiresAnyRole` は**存在秘匿（IADR-0009）の経路**であり、ルートの `RequireRole anyOf` および
// 左ナビの `requiresAnyRole` と同じ値を置く（ずれると権限外にパンくずだけ見える）。
export const sc01SettingsBreadcrumb: FeatureBreadcrumb = {
  routePath: '/settings',
  // 基盤の 4 グループのうち `user` は**グループ段を描かない**（本ユニットは基盤の計画に属さないため、
  // ナビと同じく区分を主張しない）。
  group: 'user',
  parents: [{ label: '取引', to: '/settings' }],
  label: '設定',
  requiresAnyRole: [TradingRole.Owner],
};
