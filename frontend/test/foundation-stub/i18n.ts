// IADR-0080 / IADR-0340: @foundation/i18n のテスト/型検査用スタブ。
// 実体は platform の src/platform/frontend/src/lib/i18n（合成時に `tsconfig.json` の paths ／
// 基盤 `vite.config.ts` の alias が解決する）。単独リポでは `@foundation/*` のワイルドカードで
// ここへ落ちる（`tsconfig.standalone.json` / `vitest.config.ts` / `e2e/vite.harness.config.ts`）。
import { i18n, type Messages } from '@lingui/core';

/** 基盤が対応するロケール（MSP/ADR-0031: ja / en）。 */
type Locale = 'ja' | 'en';

/**
 * 可変機能ユニットのカタログを**追加ロード**する（実体は基盤 `lib/i18n` の同名関数）。
 *
 * 🔴 **実体の `en` フォールバック（「与えられていないロケールには、そのロケールに未登録の ID だけ
 * ユニットの文言を流す」）はここでは写さない。** 単独リポは `en` カタログを持たず（本ユニットは
 * ja 単独。IADR-0338 決定 3）、基盤の英訳も存在しないため、**写しても検証できる差が無い**。
 * 写した場合に生じるのは「スタブにだけ在る挙動」であり、単独リポのテストだけ通って合成時に
 * 食い違う原因になる（`test/ui-stub/index.ts` 冒頭の規律と同じ理由）。
 *
 * ここが担うのは 1 つだけ——**渡されたカタログが `i18n` に載ること**である。
 * これは「4 画面の遅延チャンクがカタログ登録を連れている」不変条件（`src/lib/i18n.test.ts`）と、
 * E2E ハーネス（自前で `load` せず活性化だけ行う）が実際に依存している挙動である。
 */
export function registerUnitMessages(messagesByLocale: Partial<Record<Locale, Messages>>): void {
  for (const [locale, messages] of Object.entries(messagesByLocale)) {
    if (messages === undefined) continue;
    i18n.load(locale, messages);
  }
}

export { i18n };
export type { Locale };
