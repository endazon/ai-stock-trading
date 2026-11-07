---
title: フロントエンドの vitest 5・@vitejs/plugin-react 6（vite 8）・@lingui/* 6.12 を 1 本で上げる（Dependabot #1598・#1600・#1601 の置き換え）
type: spec
status: done
related_ids: [NFR, IADR-0641, IADR-0338, IADR-0340, MSP:ADR-0031]
author: claude (Claude Code)
created: 2026-11-08
updated: 2026-11-08
plan_refs: []
---

# 仕様書: vitest 5・plugin-react 6・@lingui/* 6.12 を 1 本で上げる（NFR）

> 本仕様書は実装着手前に作成する。方式の判断は IADR-0641。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（依存の保守）。非機能 `NFR`（計画に該当番号は無い）。
- 関連 ADR: `MSP/ADR-0031`（i18n = Lingui・コンパイル時抽出）・IADR-0338（カタログの再生成差分を CI で固定）・IADR-0340（カタログ登録は遅延チャンク側）。
- 起点: Dependabot の 3 本。いずれもそのままでは入れられない。

| PR | 更新 | そのままで通らない理由（手元で再現） |
| --- | --- | --- |
| #1598 | vitest 3.2.7 → 5.1.0 | `frontend` の Typecheck が `src/features/catalogRegistration.test.ts` の `node:fs`・`node:path` で TS2307。`vitest/globals` が 3 系では Node の型（`@types/node`）を推移的に読み込んでいたが、5 系では読み込まない。tsconfig の `types` は `vite/client`・`vitest/globals`・`@testing-library/jest-dom` だけで `node` を持たない。単体試験そのものは 494 件通る |
| #1600 | @vitejs/plugin-react 4.7.0 → 6.2.1 | `npm ci` が ERESOLVE。plugin-react 6 の peer は `vite@^8.0.0` で、本ユニットは vite `^6.4.3`。さらに 6 系は **`babel` オプションを削除**しており（6.0.0-beta.0 の破壊的変更。Babel は依存から外れ、React Refresh は Vite 8 の Oxc が担う）、`vitest.config.ts`・`e2e/vite.harness.config.ts` の `react({ babel: { plugins: ['@lingui/babel-plugin-lingui-macro'] } })` は黙って無視される |
| #1601 | @lingui/react 6.7.0 → 6.12.0 | CI は緑だが、`@lingui/react` だけが上がり、lockfile に `@lingui/react/node_modules/@lingui/core@6.12.0` が入れ子でできる（トップは 6.7.0）。画面の `i18n`（`@lingui/core` の単一インスタンス）と `I18nProvider` 側が別の実体になり得る |

- どれもベースは develop の 1 つ前（cb3bd489f。差は CHANGELOG だけ）で、「古いベース」の問題ではない。
- develop の時点（b2a2e876d。#1599 @types/node 26・#1602 @playwright/test 1.64.1 を含む）でも `@lingui/*` はすでに割れていた:
  `@lingui/format-po` だけが 6.12.0（`^6.12.0`）で、その配下に `@lingui/conf`・`@lingui/message-utils` 6.12.0 が入れ子、
  `@lingui/cli` の配下に `@lingui/format-po` 6.7.0 が入れ子。

## 公式の要件（移行ガイド・CHANGELOG・npm の宣言で確かめた）

| 対象 | 要件 |
| --- | --- |
| vitest 5.1.0 | vite `^6.4.0 \|\| ^7 \|\| ^8`（peer。5 系で vite は直接依存から peer へ）・Node `^22.12.0 \|\| ^24 \|\| >=26`・`@types/node` `^22 \|\| >=24`（optional peer）。`clearMocks` 既定 true・`vi.mock` はトップレベル必須・`toHaveTextContent` 厳格化ほか |
| @vitejs/plugin-react 6.2.1 | vite `^8.0.0`（7 以下は非対応）・Node `^20.19 \|\| >=22.12`。`babel` オプション削除。Babel の変換は `@rolldown/plugin-babel` へ渡す |
| vite 8.3.6 | Node `^20.19 \|\| >=22.12`・`@types/node` `^20.19 \|\| >=22.12`。バンドラは rolldown |
| @rolldown/plugin-babel 0.3.0 | peer: vite `^8`（optional）・rolldown `^1.0.0-rc.5`・`@babel/core` `^7.29 \|\| ^8`。`enforce: 'pre'` |
| @lingui/* 6.12.0 | `@lingui/cli` は `@lingui/{conf,core,format-po,message-utils,babel-plugin-lingui-macro,...}` を**同じ版で固定**して依存する |

- 本ユニットの `engines.node` は `>=22.19`、CI は Node 22（最新の 22.x）で、どの要件も満たす。

## 対象範囲

- 対象: `frontend/package.json`・`frontend/package-lock.json`・`frontend/vitest.config.ts`・`frontend/e2e/vite.harness.config.ts`・
  `frontend/src/features/catalogRegistration.test.ts`（型の参照 1 行）。
- 対象外: 基盤（MSP）の合成時のツールチェーン（基盤の `pnpm.overrides` が vite `^6.4.3`・vitest `^3.2.7` に固定し、基盤の設定で本ユニットの試験を走らせる）。
  #1599・#1602（develop へマージ済み。取り込むだけ）。

## 母集合（規則 9: 誤りになる記述を走査してから挙げた）

```sh
git grep -n -E "plugin-react|react\(\{ ?babel|Vitest ?[0-9]|vitest@?[ ]?[0-9]|Vite ?[5-8]\b|vite@?[ ]?[5-8]\.|@lingui/[a-z-]+@?[ ]?6\.[0-9]" \
  -- ':!frontend/package-lock.json' ':!.ai-context/specs' ':!CHANGELOG.md'
grep -rn "lingui-macro\|plugin-react\|vitest" scripts/*.js
```

- 設定で直すのは `react({ babel: ... })` の 2 箇所（`vitest.config.ts`・`e2e/vite.harness.config.ts`）だけ。両方を同じ形にそろえる（片方だけだと静かに割れる、と既存のコメントが定める）。
- 文書で版を書いているのは凍結記録（過去の IADR・作業仕様書）だけで、書き換えない。`scripts/` に vitest・plugin-react・マクロの設定を固定する検査は無い。
- `.ai-context/adr/README.md` の索引に IADR-0641 の行を足す。

## 上げた版

| パッケージ | 前（範囲 → 解決） | 後（範囲 → 解決） |
| --- | --- | --- |
| `vitest` | `^3.2.7` → 3.2.7 | `^5.1.0` → 5.1.0 |
| `@vitejs/plugin-react` | `^4.3.2` → 4.7.0 | `^6.2.1` → 6.2.1 |
| `vite` | `^6.4.3` → 6.4.3 | `^8.3.6` → 8.3.6（plugin-react 6 の peer） |
| `@rolldown/plugin-babel` | — | `^0.3.0` → 0.3.0（**新規**。マクロの展開） |
| `@babel/core` | （plugin-react 4 の推移依存） | `^7.29.0` → 7.29.7（**直接宣言**。`@rolldown/plugin-babel` の peer） |
| `@lingui/core`・`@lingui/react` | `^6.6.0` → 6.7.0 | `^6.12.0` → 6.12.0 |
| `@lingui/cli`・`@lingui/babel-plugin-lingui-macro` | `^6.6.0` → 6.7.0 | `^6.12.0` → 6.12.0 |
| `@lingui/format-po` | `^6.12.0` → 6.12.0 | 変えない |

- lockfile は npm で作った（手で版を書いていない）。手順: 上の範囲を package.json へ書き、lock から対象（vite・vitest・vite-node・`@vitest/*`・
  `@vitejs/plugin-react`・`@lingui/*` とその入れ子）の項目だけを外して `npm install`。**lock を消して作り直す形は採らなかった**（無関係の依存まで動くため）。
  `npm install -D <版>` は既存の木の vite 6 に引きずられて ERESOLVE になった。
- 🔴 rolldown の二重化を解いた: そのままだと `@rolldown/plugin-babel` の peer `rolldown` が最新の 1.3.0 でトップへ入り、vite 8.3.6 は `~1.2.16` を入れ子で持つ
  （ネイティブのバイナリが 2 組）。rolldown とその binding・`@oxc-project/types` の項目を外して `npm install --prefer-dedupe` で 1.2.16 の 1 組にした。
  `npm dedupe` は `@esbuild/*` ほか無関係の項目まで動かすので使わなかった。
- `pseudolocale@3.1.0`（`@lingui/cli` の依存）の EBADENGINE（Node `>=24`）の警告は develop から出ており、本 PR で増えたものではない。

## 設定の変更

| ファイル | 前 | 後 | 理由 |
| --- | --- | --- | --- |
| `vitest.config.ts`・`e2e/vite.harness.config.ts` | `react({ babel: { plugins: ['@lingui/babel-plugin-lingui-macro'] } })` | `babel({ plugins: ['@lingui/babel-plugin-lingui-macro'] }), react()` | plugin-react 6 が `babel` オプションを削除した。`@rolldown/plugin-babel` は `enforce: 'pre'` で、Oxc が JSX・TS を変換する前の元のソースへ掛かる |
| `src/features/catalogRegistration.test.ts` | — | 先頭に `/// <reference types="node" />` | vitest 5 の `vitest/globals` が Node の型を連れてこない。tsconfig の `types` へ `node` を足すと画面のコードにも Node の型が見えるので、Node の API を読むこの試験だけで参照する |

- 緩めた規則・外した試験: 無い。

## 受け入れ基準

- [x] develop（b2a2e876d）の上で `npm ci` が通り、typecheck・lint・e2e:typecheck・test（494 件）が緑である（取り込み前の確認）
- [x] 本ブランチで `npm ci` が通る（ERESOLVE が出ない）
- [x] `npm ls @lingui/core` が 6.12.0 の 1 つだけ（入れ子なし）。lockfile の `@lingui/*` はすべて 6.12.0 で入れ子が無い
- [x] vite・vitest・rolldown が lockfile に 1 版ずつ（vite 8.3.6・vitest 5.1.0・rolldown 1.2.16）
- [x] `npm run typecheck`・`npm run lint`・`npm run e2e:typecheck`・`npm run test`（33 files / 494 tests）が通る
- [x] `npm run i18n`（抽出と compile）で `src/locales` に差分が出ない
- [x] `npm run e2e`（Playwright・chromium）133 件が通る。マクロの展開が E2E ハーネスでも効いている
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・文書の検査・`check-commit-messages` が通る

## 検証

- マクロの展開が効いていること: 単体試験は `msg` マクロで定義した文言を ja のカタログの文字列で照合しており、展開されなければ
  `@lingui/core/macro` の実行時の呼び出しで落ちる。494 件通過。E2E も画面の日本語の文言で照合しており 133 件通過。
- E2E のログに出る `Messages for locale "ja" not loaded.` は、ハーネスがカタログを `load` せずに `activate('ja')` する意図した順序
  （IADR-0340 決定 2。カタログは画面の遅延チャンクが連れてくる）による @lingui/core の警告である。Vite 8 がブラウザのコンソールを
  開発サーバーの端末へ転送するようになったので見えるようになった。新しい不具合ではない。
- `format`・`build`・カバレッジの npm スクリプトと初期ロードの大きさの検査は `frontend/` に無い（整形は lint、ビルドとカバレッジ・初期ロードの床は合成先の基盤が持つ）。

## 計画書との差異

- 差異: なし（`MSP/ADR-0031` の Lingui・コンパイル時のマクロ展開は保つ。展開を担うプラグインだけが変わる）。

## 上げられなかったもの

- 無い。3 本とも上げた。
