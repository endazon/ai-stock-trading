import { defineConfig } from '@lingui/cli';
import type { CatalogFormatter } from '@lingui/conf';
import { formatter } from '@lingui/format-po';

// SC-01, SC-02, SC-03, SC-04（利用者裁定 2026-09-12 #3）: 本ユニットの文言を Lingui（MSP/ADR-0031）へ載せる。
//
// **英訳はしない。** 対応ロケールは `ja` のみで、`en` のカタログを持たない。基盤（platform）へ合成された
// ときは、合成点が `registerUnitMessages` で本ユニットの ja カタログを基盤の i18n へ**追加ロード**し、
// `en` ロケールには ja の文言を流す（本ユニットの画面は en でも日本語で出る）。
//
// 基盤の `src/lingui.config.ts` は本ユニットを抽出範囲に含めない（MSP/IADR-0120。別プロジェクトの
// submodule に基盤の規約を及ぼさない）。よって抽出・コンパイルは**本リポジトリで完結**させる
// （`npm run i18n`）。生成物（`src/locales/ja/messages.{po,ts}`）はコミットする（基盤の orval / lingui
// 生成物と同じ作法。MSP/IADR-0121 決定 3）。

/**
 * `POT-Creation-Date` を落とす決定的なフォーマッタ（基盤の `src/lingui.config.ts` と同じ理由）。
 *
 * `@lingui/format-po` は**実行時刻をヘッダへ毎回書き込む**ため、内容が変わっていなくても
 * カタログのバイト列が変わり、「`npm run i18n` の再生成に差分が出ないこと」の検査が常に赤になる。
 * 当該ヘッダを無効化するオプションは無い（型定義で確認）ので、serialize の出力から行ごと落とす。
 * 固定の日時を書かない（嘘の値を残さない）のは、抽出日時が意味を持つ情報だからである。
 */
function deterministicPoFormatter(options?: Parameters<typeof formatter>[0]): CatalogFormatter {
  const base = formatter(options);
  return {
    ...base,
    serialize(catalog, ctx) {
      const out = base.serialize(catalog, ctx);
      return String(out).replace(/^"POT-Creation-Date: .*\\n"\r?\n/m, '');
    },
  };
}

export default defineConfig({
  sourceLocale: 'ja',
  // ja のみ（英訳不要の裁定）。`en` を足すときは英訳を書き、基盤側の `registerUnitMessages` の
  // フォールバック（en ← ja）が不要になった旨をそちらのコメントにも反映する。
  locales: ['ja'],
  catalogs: [
    {
      path: '<rootDir>/src/locales/{locale}/messages',
      include: ['<rootDir>/src'],
      exclude: ['**/node_modules/**', '**/*.{test,spec}.{ts,tsx}'],
    },
  ],
  // 行番号は入れない（`#: path:line` は無関係な編集で差分が動き、再生成差分検査を騒がしくする）。
  format: deterministicPoFormatter({ lineNumbers: false }),
  // 基盤と同じく、コンパイル結果を素の TS モジュールとして import する（`@lingui/vite-plugin` は使わない）。
  compileNamespace: 'ts',
});
