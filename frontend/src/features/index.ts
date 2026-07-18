// Issue #106, ADR-0001, IADR-0080: 本ユニット（AI 株取引）の feature 束ね。
// platform の合成点（src/platform/frontend/src/features/index.ts）から
// `import { features as aiStockTradingFeatures } from '@ai-stock-trading/features'` で 1 行合成される（後続スライス）。
// 各 SC-xx は FeatureModule を 1 つ公開し、この配列へ 1 行追加するだけでマウントされる（骨組みへの追加が疎結合）。
import type { FeatureModule } from '@foundation/routing/featureRegistry';
import { sc01SettingsFeature } from './sc01-settings';

export const features: FeatureModule[] = [
  sc01SettingsFeature, // SC-01 設定画面（FR-17 全体前提条件の閲覧/変更・UC-06）（#106）
];
