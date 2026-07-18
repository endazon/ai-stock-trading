// IADR-0080: @foundation/routing/featureRegistry のテスト/型検査用スタブ。
// 実体は platform/frontend/src/foundation/routing/featureRegistry.ts（合成時に解決）。型のみを写像する。
import type { RouteObject } from 'react-router-dom';

export interface FeatureNav {
  label: string;
  to: string;
  requiresAnyRole?: string[];
}

export interface FeatureModule {
  id: string;
  routes: RouteObject[];
  nav?: FeatureNav;
}
