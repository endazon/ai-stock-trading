import type { Messages } from '@lingui/core';
import { messages as ja } from '../locales/ja/messages';

// SC-01, SC-02, SC-03, SC-04（利用者裁定 2026-09-12 #3: Lingui を導入するが英訳はしない）:
// 本ユニットの文言カタログを**ロケール別の表**として束ね、合成点（`src/features/index.ts`）から公開する。
//
// 基盤（platform）の合成点は、ルート・ナビと同様に**ユニットを知る唯一の場所**として
// これを `registerUnitMessages(aiStockTradingMessages)` へ渡す。基盤は与えられたロケール（ja）を
// **追加ロード**し（既存カタログを上書きしない）、与えられていないロケール（en）には ja を流す
// ——本ユニットの画面は en ロケールでも日本語で出る（英訳不要の裁定）。
//
// 🔴 **本番ビルドでは `msg` マクロが `message` を落とし、ID（ハッシュ）だけを残す**
// （`@lingui/babel-plugin-lingui-macro` の `descriptorFields: 'auto'` ＝ production では `id-only`。実測）。
// よってカタログが基盤の i18n に載っていないと、本番の画面には**ハッシュがそのまま出る**。
// 開発・テストでは `message` が残るため気付けない——合成点への配線を外してはならない。
//
// カタログの実体（`../locales/ja/messages.ts`）は `npm run i18n` の生成物である。手で編集しない。
export const aiStockTradingMessages: { readonly ja: Messages } = { ja };
