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
- lockfile の `@lingui/*` は 6.12.0 の 1 版、vite・vitest・rolldown も 1 版ずつ。

## 残余リスク

- **基盤（MSP）は合成時に `pnpm.overrides` で vite `^6.4.3`・vitest `^3.2.7` に固定している**（本ユニットは基盤の pnpm workspace の一員）。
  基盤が本リポの submodule を進めると、基盤の lockfile では本ユニットの devDependencies の `@vitejs/plugin-react@6`・`@rolldown/plugin-babel`
  の peer（vite 8）が満たされない組になる（pnpm は既定で警告に留める）。合成時の試験は基盤の `vitest.config.ts`（plugin-react 4 の
  `babel` オプション）で走るので、本ユニットの設定ファイルは使われず、実行には影響しない。試験のコードの変更は型の参照 1 行だけで、
  vitest 3 でも通る。基盤がこの組を嫌うときの判断は基盤側にある（本リポに追随義務は無い）。
- `@rolldown/plugin-babel` は 0.x であり、マイナー版で破壊的変更があり得る。Dependabot の更新ごとに単体・E2E で確かめる。
