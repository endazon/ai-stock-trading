import type { NavItem } from '@foundation/routing/featureRegistry';
import type { ShellRoute } from '@foundation/routing/shell';
import { createSc01SettingsRoute, sc01SettingsNav } from './sc01-settings';
import { createSc02RiskSettingsRoute, sc02RiskSettingsNav } from './sc02-risk-settings';
import { createSc03ControlsRoute, sc03ControlsNav } from './sc03-controls';

// #106, #414, ADR-0001, IADR-0080, IADR-0288: 本ユニット（AI 株取引）の合成面。
//
// platform の合成点（`src/platform/frontend/src/features/index.ts`）は、ここから 2 つを import して
// スプレッドする（MSP/ADR-0031 / MSP/IADR-0124 決定 1 / MSP/IADR-0056）:
//
//   import { createAiStockTradingRoutes, aiStockTradingNavItems } from '@ai-stock-trading/features';
//   createUnitRoutes へ  ...createAiStockTradingRoutes(shell)
//   unitNavGroups の items へ  aiStockTradingNavItems
//
// **ルートとナビは別経路である。** 片方だけ足すと「画面は開けるのに左ナビに出ない」（あるいはその逆）になる。

/**
 * 本ユニットの画面を 1 本のタプルにして公開する。
 *
 * 🔴 **戻り値へ型注釈を書いてはならない。** `readonly AnyRoute[]` などを付けた瞬間にルート ID と
 * パスの union が失われ、`useSearch({ from })` も `<Link to>` も静的検査されなくなる
 * （MSP/IADR-0124 §実測）。**タプルであること（`as const`）が型安全の必要条件**であり、
 * `flatMap` や中間変数を挟むのも同じ理由で不可である。画面を足すときはタプルへ 1 行足す。
 */
export const createAiStockTradingRoutes = (shell: ShellRoute) =>
  [
    createSc01SettingsRoute(shell), // SC-01 設定画面（FR-17 全体前提条件の閲覧/変更・UC-06）
    createSc02RiskSettingsRoute(shell), // SC-02 リスク設定（FR-13/FR-19/FR-20 リスク上限の閲覧/変更）
    createSc03ControlsRoute(shell), // SC-03 承認・統制状態参照（FR-10/FR-20/UC-06・参照専用）
  ] as const;

/**
 * 左ナビへ出す項目。
 *
 * 🔴 **型は `PlanNavItem` ではなく `NavItem` である。** `PlanNavItem` は基盤の計画の 4 グループの
 * いずれかを `group` として必ず宣言する型であり、**本ユニットは基盤の計画に属さないため宣言しない**。
 * 本ユニットの項目は合成点が `unitNavGroups`（見出し＝ユニットの機能名「株式自動売買」）へ束ねる
 * （MSP/IADR-0125 決定 9）。この非対称は意図的である。
 */
export const aiStockTradingNavItems: readonly NavItem[] = [
  sc01SettingsNav,
  sc02RiskSettingsNav,
  sc03ControlsNav,
];
