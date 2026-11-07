---
title: IADR-0641 フロントエンドを vite 8・@vitejs/plugin-react 6・vitest 5 へ上げ、Lingui のマクロの展開を @rolldown/plugin-babel へ移す
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0338, IADR-0340, MSP:ADR-0031]
author: claude (Claude Code)
created: 2026-11-08
updated: 2026-11-08
plan_refs: []
---

# IADR-0641: フロントエンドを vite 8・plugin-react 6・vitest 5 へ上げ、Lingui のマクロの展開を @rolldown/plugin-babel へ移す

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-11-08
- 決定者: オーケストレーターの指示（Dependabot #1598・#1600・#1601 を通る形の 1 本へまとめる）／ Claude Code（起案・実装）

## 起点・関連

- 非機能 `NFR`（依存の保守。計画に該当番号は無い）。
- 起点: Dependabot の #1598（vitest 5.1.0）・#1600（@vitejs/plugin-react 6.2.1）・#1601（@lingui/react 6.12.0）。
- 実装仕様書: [`.ai-context/specs/20261108_frontend-deps-vitest-plugin-react-lingui.md`](../specs/20261108_frontend-deps-vitest-plugin-react-lingui.md)

## コンテキストと課題

- plugin-react 6 は peer に `vite@^8` を持ち（7 以下は非対応）、**`babel` オプションを削除した**。本ユニットは Lingui の `msg` マクロ
  （`@lingui/core/macro`）を `react({ babel: { plugins: ['@lingui/babel-plugin-lingui-macro'] } })` で展開していた（`MSP/ADR-0031`）。
  6 系では同じ書き方が効かず（オプションごと無くなった）、展開されないマクロは実行時に例外を投げる。
- plugin-react 6 の移行の案内は、Babel の変換を `@rolldown/plugin-babel` へ渡す形である。
- vitest 5 は vite `^6.4 || ^7 || ^8` を peer に持ち、vite 8 と組める。`vitest/globals` は Node の型を連れてこなくなった。
- `@lingui/*` は `@lingui/cli` が同じ版で互いを固定して依存するため、1 つだけ上げると lockfile に入れ子の別版ができる。

## 決定

1. **vite を `^8.3.6`、`@vitejs/plugin-react` を `^6.2.1`、vitest を `^5.1.0` へ上げる。**
2. **Lingui のマクロの展開は `@rolldown/plugin-babel`（`^0.3.0`）に `@lingui/babel-plugin-lingui-macro` を渡して行う。**
   `vitest.config.ts` と `e2e/vite.harness.config.ts` の両方を `plugins: [babel({ plugins: [...] }), react()]` にそろえる。
   同プラグインは `enforce: 'pre'` で、Oxc が JSX・TS を変換する前の元のソースへ掛かる。`@babel/core` は peer なので直接宣言する。
3. **`@lingui/*`（core・react・cli・babel-plugin-lingui-macro・format-po）は同じ版（6.12.0）にそろえ、lockfile に入れ子の別版を作らない。**
   **ただし実行時の依存（`@lingui/core`・`@lingui/react`）の宣言の範囲は `^6.6.0` のまま据え置き、lockfile の解決だけを 6.12.0 にする。**
   範囲は「基盤（MSP）が合成時に持つ版を満たす」ことを条件にする（基盤は `^6.6.0` で 6.6.0 に解決している）。範囲を `^6.12.0` に上げると、
   基盤の pnpm は本ユニットへ 6.12.0 の別の実体を割り当て、基盤の試験の準備が `activate` した実体と本ユニットの画面が読む実体が別になる（下の §基盤での実測）。
4. **Node の型は、Node の API を読む試験のファイルだけが `/// <reference types="node" />` で参照する。** tsconfig の `types` へ `node` を足すと、
   ブラウザで動く画面のコードにも Node の型が見えるため採らない。
5. **rolldown は vite が使う版の 1 組にそろえる**（`@rolldown/plugin-babel` の peer を別版で入れない）。

## 検討した選択肢

| 選択肢 | 採否 | 理由 |
| --- | --- | --- |
| plugin-react を 4 系に据え置き、#1600 を閉じる | 不採用 | Dependabot が同じ更新を出し続け、vite 8 への追随が先送りになる。上げる道が公式にある |
| `overrides` / `--legacy-peer-deps` で vite 6 のまま plugin-react 6 を入れる | 不採用 | peer の要件（vite 8）を満たさず、`babel` オプションも効かないので壊れたまま入る |
| マクロをやめて `@lingui/vite-plugin` 等の別経路にする | 不採用 | 基盤（合成時）は Babel のマクロで展開しており、同じプラグインを使い続けるのが最小の変更 |
| `@rolldown/plugin-babel` に移す | **採用** | plugin-react 6 の公式の移行先。同じ Babel のプラグインをそのまま渡せる |
| tsconfig の `types` に `node` を足す | 不採用 | 決定 4 |

## 結果

- 単体試験 494 件・E2E 133 件・i18n の再生成（差分なし）が通る。マクロの展開は単体・E2E の両方で効いている（仕様書 §検証）。
- lockfile の `@lingui/*` は 6.12.0 の 1 版（宣言の範囲は core・react が `^6.6.0`、cli・macro・format-po が `^6.12.0`）、vite・vitest・rolldown も 1 版ずつ。

## 基盤での実測

2026-11-08。基盤 develop（95ace5d05）の作業ツリーで submodule `src/ai-stock-trading` を差し替え、`src/` で
`pnpm install --no-frozen-lockfile` → `pnpm run typecheck`・`lint`・`test`・`build` を流した（基盤の lock の差は基盤 develop の lock から）。

| 本ユニットの pin | `pnpm install` | typecheck | lint | test（170 files / 2,172 件） | build |
| --- | --- | --- | --- | --- | --- |
| 基盤の現在の pin（c38b33019） | `--frozen-lockfile` で通る | 通る | 通る | 通る（AST の 33 files を含む） | 通る |
| 本リポ develop（b2a2e876d。本 PR の親） | 通る（lock +736 / −57 行） | **落ちる** | 通る | **5 files / 11 件落ちる** | **落ちる** |
| 本 PR の初版（af7d5c33c。`@lingui/core`・`react` が `^6.12.0`） | 通る（lock +1,291 / −89 行） | 落ちる（develop と同じ 2 件） | 通る | **26 files / 283 件落ちる** | 落ちる（develop と同じ） |
| 本 PR（範囲を `^6.6.0` へ戻した形） | 通る（lock +1,267 / −86 行。develop の pin からの差は 562 行） | 落ちる（develop と同じ 2 件） | 通る | 5 files / 11 件落ちる（**develop と同じ集合**） | 落ちる（develop と同じ） |

- 初版で増えた 272 件の失敗の出力には `Lingui: Attempted to call a translation function without setting a locale.` が 281 回出る（develop の pin・修正後の形では 0 回）。基盤の `@lingui/core@6.6.0` と
  本ユニットの `@lingui/core@6.12.0` の 2 つの実体ができたことによる。範囲を `^6.6.0` に戻すと基盤の lock は本ユニットにも 6.6.0 を割り当て、消える。
- **vite 8・plugin-react 6・`@rolldown/plugin-babel`・vitest 5 は基盤の合成を壊さない。** 基盤の `pnpm.overrides` が vite を 6.4.3・vitest を 3.2.7 に
  固定し（plugin-react 6 も vite 6.4.3 に結び付けて入る。peer の警告は出なかった）、合成時の試験・build は基盤の設定で走る。
- develop の時点で既にある赤（本 PR と無関係。基盤が pin を進めると本 PR の有無に依らず出る）:
  - test 11 件: `No QueryClient set` ほか。本ユニットの `@tanstack/react-query` の範囲 `^5.105.3` を基盤の lock の 5.101.4 が満たさず、実体が 2 つになる。
  - typecheck・build: 基盤の `platform/frontend/e2e` の `Page` 型の不一致（`@playwright/test` 1.62.1 と 1.65.0 の 2 版。本ユニットの `^1.64.1`〔#1602〕から）。

## 残余リスク

- **基盤（MSP）は合成時に `pnpm.overrides` で vite `^6.4.3`・vitest `^3.2.7` に固定している**（本ユニットは基盤の pnpm workspace の一員）。
  本ユニットの設定ファイル（plugin-react 6・plugin-babel）は合成時に使われない。実測で基盤の typecheck・lint・test・build に新しい赤を足さないことを確かめた（§基盤での実測）。
- **実行時の依存の範囲は、基盤が持つ版を満たす範囲に留める**（決定 3）。Dependabot が `@lingui/core`・`@lingui/react` を上げると範囲も上がるので、
  その PR は基盤の版（`platform/frontend` の宣言と lock）と突き合わせてから入れる。`@tanstack/react-query`（`^5.105.3`）・`@playwright/test`（`^1.64.1`）は
  すでにこの条件を破っており、基盤が pin を進めると上の赤が出る（本 PR の対象外）。
- `@rolldown/plugin-babel` は 0.x であり、マイナー版で破壊的変更があり得る。Dependabot の更新ごとに単体・E2E で確かめる。
