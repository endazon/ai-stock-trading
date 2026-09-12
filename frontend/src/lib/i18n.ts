import { i18n } from '@lingui/core';
import { registerUnitMessages } from '@foundation/i18n';
import { messages as ja } from '../locales/ja/messages';

// SC-01, SC-02, SC-03, SC-04（利用者裁定 2026-09-12 #3: Lingui を導入するが英訳はしない）:
// 本ユニットの文言カタログを基盤（platform）の i18n へ**登録する**モジュール。
//
// 基盤は与えられたロケール（ja）を**追加ロード**し（既存カタログを上書きしない）、
// 与えられていないロケール（en）には ja を流す——本ユニットの画面は en ロケールでも日本語で出る
// （英訳不要の裁定）。**`i18n.load` を自前で呼ばず `registerUnitMessages` を通す**のは、
// この `en` フォールバックの意味論（「そのロケールに**未登録の ID だけ** ja を流す」＝基盤の英訳を
// 日本語で上書きしない）が**基盤側にしか無い**ためである。写すと 2 か所に持つことになる。
//
// 🔴 **本番ビルドでは `msg` マクロが `message` を落とし、ID（ハッシュ）だけを残す**
// （`@lingui/babel-plugin-lingui-macro` の `descriptorFields: 'auto'` ＝ production では `id-only`。実測）。
// よってカタログが基盤の i18n に載っていないと、本番の画面には**ハッシュがそのまま出る**。
// 開発・テストでは `message` が残るため気付けない——登録の経路を外してはならない。
//
// カタログの実体（`../locales/ja/messages.ts`）は `npm run i18n` の生成物である。手で編集しない。

// 🔴 **本モジュールは「初期ロードに載ってはならない」側である**（IADR-0340 決定 2 / MSP/IADR-0134）。
//
// 何が基盤の初期チャンクに載るかを決めているのは**静的 import の連鎖**である。従前は
// `src/features/index.ts`（＝合成点が静的 import する本ユニットの公開面）が `aiStockTradingMessages`
// を再公開しており、カタログ **25,267 B（442 キー）** が基盤の `index-*.js` に入っていた
// （実測。合成時の初期ロード増 +25,907 B の 97.5%）。
//
// **登録をここへ移し、import するのを 4 画面の Page（＝`lazyRouteComponent` の遅延チャンク）だけに
// 閉じることで、カタログは遅延側へ移る。** よって:
//   - **ルート factory・ナビ・パンくず・`src/features/index.ts` から本モジュールを import しない。**
//     1 本でも静的辺ができた瞬間にカタログは初期ロードへ戻る（**ビルドは成功し、誰も気付かない**）。
//   - 4 画面の Page は `i18n` を**ここから**受け取る（`@lingui/core` から直接取らない）。
//     こうすると「文言を描く入口」と「カタログの登録」が同じ import で結ばれ、**片方だけ消せない**。
//   - Page の**子部品**（`QueryPhase` / `PaperModeBanner` / 各 Form 等）は `@lingui/core` から
//     直接 `i18n` を取ってよい。**必ず Page を経由して描かれる**ため、描画時点で登録済みである。
// 不変条件は `src/features/catalogRegistration.test.ts` が固定する（4 画面それぞれが登録を連れていること）。
const aiStockTradingMessages = { ja } as const;

let registered = false;

/**
 * 本ユニットの文言カタログを基盤の i18n へ登録する（多重呼び出しに対して冪等）。
 *
 * ES モジュールは 1 度しか評価されないため実運用では下の 1 行で 1 回だけ走るが、
 * **テストが `vi.resetModules()` でレジストリを分ける**ため、フラグで明示的に閉じる。
 */
export function registerAiStockTradingMessages(): void {
  if (registered) return;
  registered = true;
  registerUnitMessages(aiStockTradingMessages);
}

registerAiStockTradingMessages();

// 画面が `i18n._(msg`…`)` で引くための再公開。**上の登録より後に評価される順序は保証されている**
// （import した側は本モジュールの評価完了後に実行される）。
export { i18n };
