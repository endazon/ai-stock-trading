// IADR-0080 / IADR-0288: @foundation/routing/featureRegistry のテスト/型検査用スタブ。
// 実体は platform（microservices-platform）の src/platform/frontend/src/app/routing/featureRegistry.ts
// （合成時に解決）。**型のみを写像する。**
//
// MSP/ADR-0031 / MSP/IADR-0124: 可変機能ユニットは platform を import せず、共通シェル（ShellRoute）を
// 引数で受け取る型付きルート factory で画面を公開する。旧契約（`FeatureModule { id, routes, nav }`）は
// 互換ブリッジ（`createLegacyRoutes`。@deprecated）専用であり、**本ユニットは #414 で新契約へ移った**。
// よってスタブからも旧契約の型を落とす（残すと「まだ使ってよい」ように読める）。

/**
 * 左ナビのグループ（基盤の計画 05_screens §共通シェル の 4 グループ）。
 *
 * 🔴 **本ユニットはこれを宣言しない。** 4 グループは**基盤の計画に属するユニット**のための区分であり、
 * 本ユニット（別プロジェクト）の画面は合成点が「株式自動売買」を見出しとするグループ（`UnitNavGroup`）へ
 * 束ねる（MSP/IADR-0125 決定 9）。型は写像しておく——`PlanNavItem` の形を読む側のために要る。
 */
export type NavGroup = 'user' | 'personal' | 'admin' | 'ops';

/**
 * ナビ表示名。
 *
 * 基盤は `MessageDescriptor`（Lingui のマクロ）も受けるが、**本ユニットは Lingui を採らない**
 * （単独リポジトリでは基盤のカタログ・抽出経路に載れない。作業仕様書 20260903_414 §計画書との差異）。
 * スタブは `string` だけを写像する——実体はより広い型なので、合成時も代入可能である。
 */
export type NavLabel = string;

/** 共通ナビへ出すメニュー項目の本体。権限外には表示しない（存在秘匿の UI 表現。IADR-0009/0035）。 */
export interface FeatureNav {
  /** ナビ表示名（例: "設定"）。 */
  label: NavLabel;
  /** 遷移先パス（共通シェル配下の絶対表記。例: "/settings"）。 */
  to: string;
  /** 表示に必要なロール（いずれか一致で表示）。省略時は認証済み全員に表示する。 */
  requiresAnyRole?: string[];
  /**
   * 左ナビのグループ。**本ユニットは宣言しない**（上の `NavGroup` の注記）。
   * 基盤の計画に属するユニットだけが宣言し、その型は `PlanNavItem` で強制される。
   */
  group?: NavGroup;
}

/** ナビ項目に由来 feature の識別子を添えたもの（描画の key と診断に使う）。 */
export interface NavItem extends FeatureNav {
  id: string;
}

/**
 * 基盤の 4 グループのいずれかを**必ず宣言した**ナビ項目。
 *
 * 本ユニットはこの型を使わない（`group` を持たないため）。合成点が `UnitNavGroup.items` として
 * `readonly NavItem[]` で受ける。**型を写像しておくのは、両者の非対称が意図的であることを
 * スタブの読み手に見せるためである。**
 */
export type PlanNavItem = NavItem & { group: NavGroup };
