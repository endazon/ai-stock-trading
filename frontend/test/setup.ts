import '@testing-library/jest-dom/vitest';
import { i18n } from '@lingui/core';
import { messages } from '../src/locales/ja/messages';

// MSP/ADR-0031（i18n = Lingui）: テストのロケールを ja に固定する（基盤の `platform/frontend/src/testing/setup.ts`
// と同じ理由——jsdom の navigator.language は既定で en-US であり、検出に委ねると「テストだけ英語」になる）。
// 単独リポには基盤の `@foundation/i18n` が無いので、本ユニットの ja カタログを直接ロードして活性化する。
// `i18n._()` は**ロケール未活性だと例外を投げる**（実測: `Attempted to call a translation function without
// setting a locale`）ため、これを省くと文言を持つ部品の描画がすべて落ちる。
// 合成時は基盤の setup が activate し、合成点が `registerUnitMessages` で本ユニットのカタログを載せる。
i18n.load('ja', messages);
i18n.activate('ja');
