// IADR-0080 / IADR-0286: @foundation/routing/shell のテスト/型検査用スタブ。
// 実体は platform の src/platform/frontend/src/app/routing/shell.tsx（合成時に解決）。
//
// MSP/IADR-0124 決定 1: 認証済み領域の共通シェルは **path を持たないレイアウトルート**であり、
// `id` は `_shell`（配下ルートの ID が `/_shell/<path>` になる）。可変機能ユニットは platform の
// ルート木を import せず、**この型の値を引数で受け取る**。
//
// スタブが実体の骨格（root → `_shell`）をそのまま組むのは、`ShellRoute` が
// `typeof shellRoute` ＝ 親子関係と ID を含む具体型だからである。**型だけを手書きすると
// `createRoute({ getParentRoute: () => shell })` の推論が実体と食い違う。**
import { createRootRoute, createRoute, Outlet } from '@tanstack/react-router';

const rootRoute = createRootRoute({ component: Outlet });

const shellRoute = createRoute({
  getParentRoute: () => rootRoute,
  id: '_shell',
  component: Outlet,
});

/** 可変機能ユニットがルートを生やす親（MSP/IADR-0124 決定 1）。 */
export type ShellRoute = typeof shellRoute;
