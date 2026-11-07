---
title: IADR-0640 フロントエンドの ESLint を 10 系へ上げ、feature 境界の規則を eslint-plugin-import から eslint-plugin-import-x へ移す
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0288, IADR-0290, MSP:ADR-0066]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs: []
---

# IADR-0640: フロントエンドの ESLint を 10 系へ上げ、import 境界の規則を eslint-plugin-import-x へ移す

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-11-07
- 決定者: 利用者の裁定（2026-11-07「今 10 系へ上げる」）／ Claude Code（起案・実装）

## 起点・関連

- 非機能 `NFR`（依存の保守。計画に該当番号は無い）。
- 原因: Dependabot の #1572（`@eslint/js` を 10.0.1 へ）が develop へマージされ、eslint 本体が `^9.11.1`（解決 9.39.5）のままだったため
  `@eslint/js@10` の peer（`eslint@^10`）が満たせず、develop の CI（frontend / frontend-e2e）の `npm ci` が ERESOLVE で落ちた。
- 一部を改める先: IADR-0288 決定 4（feature 境界を `import/no-restricted-paths` で強制する）——**規則の名前だけ**を改める。
  計画側の根拠 `MSP/ADR-0066` 決定 3 は「`eslint-plugin-import`（または同等）」と同等品を許している。
- 実装仕様書: [`.ai-context/specs/20261107_frontend-eslint-10.md`](../specs/20261107_frontend-eslint-10.md)

## コンテキストと課題

- `@eslint/js` だけを 9 系へ戻すと、Dependabot が同じ PR を出し続け、メジャー版の追随が先送りになる。利用者は 10 系へ上げると裁定した。
- 10 系へ上げるとき、`frontend/` の lint 一式のうち **`eslint-plugin-import` 2.32.0（最新。2025-06 以降の公開なし）だけが ESLint 10 を peer に持たない**
  （`^2 || … || ^9`）。他は 10 系に対応した版がある（typescript-eslint 8.72.1・eslint-plugin-react-hooks 7.1.1・eslint-plugin-react-refresh 0.5.7）。
- `eslint-plugin-import` は本ユニットで `no-restricted-paths`（feature 間 import の禁止・層の向き）1 規則だけに使っている。
  この規則は**解決できた import しか検査しない**ため、resolver が効かないと静かに 0 件検査になる（IADR-0288 決定 4）。

## 決定

1. **ESLint 本体・`@eslint/js` を 10 系にそろえる**（eslint `^10.14.0`・`@eslint/js` `^10.0.1`）。typescript-eslint・react-hooks・react-refresh・globals は 10 系に対応した最新版へ上げる。
2. **`eslint-plugin-import` を外し、`eslint-plugin-import-x`（`^4.18.0`。peer に `eslint@^10` を持つ後継の fork）へ移す。**
   規則名は `import/no-restricted-paths` → `import-x/no-restricted-paths`、resolver の設定は
   `import/resolver`（`typescript: { project }`）→ `import-x/resolver-next`（`createTypeScriptImportResolver({ project })`）。**ゾーン・`basePath`・メッセージは変えない。**
3. **peer の不一致を `overrides` や `--legacy-peer-deps` で黙らせて `eslint-plugin-import` を残す形は採らない。** 実行時に動くかが保証されず、
   壊れたときに「規則が 0 件検査になる」形で静かに効かなくなる恐れがあるため。
4. **規則は緩めない。** 10 系で増えた規則（`@eslint/js` recommended の `no-unassigned-vars`・`no-useless-assignment`・`preserve-caught-error`、
   react-hooks 7 recommended の React Compiler 系の規則）はそのまま有効にし、検出された箇所はコードを直す。
5. 設定の入口を `tseslint.config()`（typescript-eslint で非推奨）から ESLint 本体の `defineConfig()`（`eslint/config`）へ移す。

## 検討した選択肢

| 選択肢 | 採否 | 理由 |
| --- | --- | --- |
| `@eslint/js` を 9 系へ戻す | 不採用 | 利用者の裁定（今 10 系へ上げる）に反する。Dependabot が同じ更新を出し続ける |
| `eslint-plugin-import` を残し `overrides` で peer を上書き | 不採用 | 決定 3 |
| `eslint-plugin-import-x` へ移す | **採用** | 同じ規則・同じ設定項目を持ち、ESLint 10 を正式に支える |
| plugin を namespace `import` で登録して規則名を保つ | 不採用 | 実体が別の plugin なのに名前で旧 plugin に見え、読み手を誤らせる |

## 結果

- 移行前（ESLint 9・`eslint-plugin-import`）と移行後で、同じ違反の探り（feature → feature・shared → features〔相対とエイリアス〕・採用外パッケージ・`apiFetch`）が
  **同じ 5 件の error** を出すことを実測した（仕様書 §検証）。resolver は効いている。
- 増えた規則で検出されたのは 1 件（`react-hooks/set-state-in-effect`。sc02 の取引ガードの危険確認）。描画中に調整する形へ直し、挙動は厳しい側へ倒れる。

## 残余リスク

- 基盤（MSP）はまだ ESLint 9・`eslint-plugin-import` である。合成時は基盤の設定が本ユニットを lint するため、本決定は基盤の lint に影響しない。
  基盤が 10 系へ上げるときは同じ置き換えが要る（本リポに追随義務は無い）。
