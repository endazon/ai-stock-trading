---
title: フロントエンドの ESLint を 10 系へ上げ、develop の frontend の赤（#1572 による npm ci の ERESOLVE）を直す
type: spec
status: done
related_ids: [NFR, IADR-0640, IADR-0288, IADR-0290, MSP:ADR-0066]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs: []
---

# 仕様書: フロントエンドの ESLint を 10 系へ上げる（NFR）

> 本仕様書は実装着手前に作成する。実装の判断は IADR-0640。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（依存の保守）。非機能 `NFR`（計画に該当番号は無い）。
- 関連 ADR: `MSP/ADR-0066` 決定 3（feature 境界の機械強制。「`eslint-plugin-import`（または同等）」）・IADR-0288 決定 4・IADR-0290。
- 起点: Dependabot の #1572（`@eslint/js` 9.39.5 → 10.0.1）が 2026-11-07 12:48Z に develop へマージされた（1e89cc106）。
  eslint 本体は `^9.11.1`（解決 9.39.5）のままで、`@eslint/js@10` の peer `eslint@^10` を満たせず、develop の CI の
  `frontend` と `frontend-e2e` が `npm ci` の ERESOLVE で赤になった。利用者の裁定（2026-11-07）は「今 10 系へ上げる」。

## 対象範囲

- 対象: `frontend/package.json`・`frontend/package-lock.json`・`frontend/eslint.config.js`、10 系で増えた規則が検出したコード 1 件。
- 対象外: 基盤（MSP）の lint（合成時は基盤の設定で lint される。基盤は ESLint 9 のまま）。lint 以外の依存の更新。

## 母集合（規則 9: lint 一式を記憶で挙げず、package.json と設定の import から引いた）

```sh
grep -nE '"(eslint[^"]*|@eslint/[^"]*|typescript-eslint|globals)"' frontend/package.json
grep -n "^import" frontend/eslint.config.js
grep -rnE "eslint-plugin-import|import/no-restricted-paths|tseslint\.config" --exclude-dir=node_modules --exclude=package-lock.json .
```

- 設定が使う plugin は `eslint-plugin-import`・`eslint-plugin-react-hooks`・`eslint-plugin-react-refresh` と `typescript-eslint` の 4 つ。
  **`eslint-plugin-jsx-a11y` は本ユニットでは使っていない**（基盤だけが持つ）ので対象外。
- 文書の言及: IADR-0288・IADR-0290・作業仕様書（いずれも凍結記録で書き換えない）。`.ai-context/adr/README.md` の索引に IADR-0640 の行を足す。

## 上げた版

| パッケージ | 前（範囲 → 解決） | 後（範囲 → 解決） | ESLint 10 の peer |
| --- | --- | --- | --- |
| `eslint` | `^9.11.1` → 9.39.5 | `^10.14.0` → 10.14.0 | — |
| `@eslint/js` | `^10.0.1` → 10.0.1（#1572） | 変えない | `^10.0.0` |
| `typescript-eslint` | `^8.72.1` → 8.72.1 | 変えない（既に対応） | `^8.57.0 \|\| ^9.0.0 \|\| ^10.0.0` |
| `eslint-plugin-react-hooks` | `^5.1.0-rc.0` → 5.2.0 | `^7.1.1` → 7.1.1 | `… \|\| ^10.0.0` |
| `eslint-plugin-react-refresh` | `^0.4.12` → 0.4.26 | `^0.5.7` → 0.5.7 | `^9 \|\| ^10` |
| `eslint-plugin-import` | `^2.32.0` → 2.32.0 | **外す** | **無い**（`^2 … \|\| ^9`。2.32.0 が最新） |
| `eslint-plugin-import-x` | — | `^4.18.0` → 4.18.0（**新規**） | `^8.57.0 \|\| ^9.0.0 \|\| ^10.0.0` |
| `eslint-import-resolver-typescript` | `^4.4.4` → 4.4.5 | `^4.4.5` → 4.4.5 | `*` |
| `globals` | `^15.9.0` → 15.15.0 | `^17.14.0` → 17.14.0 | — |

- lockfile は npm で作った（手で書いていない）。手順: 移行前の lock から `npm uninstall eslint-plugin-import eslint-import-resolver-typescript`
  （develop の lock は peer が壊れているためこの一歩だけ `--legacy-peer-deps`）→ `npm install -D <上の版>`。
  **lock を消して作り直す形は採らなかった**——react・lingui・playwright など lint と無関係の依存まで 334 項目が動いたため。
  採った形で動いたのは lint 一式とその推移依存（追加 19・削除 128〔多くは `eslint-plugin-import` の依存〕・変更 44）だけである。
- 🔴 `eslint-import-resolver-typescript` は `eslint-plugin-import` を peerOptional に持つため、resolver を残したまま
  `eslint-plugin-import` を消すと lock に peer の項目が残り、`npm ci` が ERESOLVE で落ちる（実測）。resolver ごと一度外してから入れ直した。

## 設定の変更（`frontend/eslint.config.js`）

| 変更 | 前 | 後 | 理由 |
| --- | --- | --- | --- |
| 規則名（やむを得ない変更） | `import/no-restricted-paths` | `import-x/no-restricted-paths` | plugin の移行（IADR-0640 決定 2）。ゾーン・`basePath`・メッセージは同じ |
| plugin の登録 | `plugins: { import: importPlugin }` | `plugins: { 'import-x': importX }` | 同上 |
| resolver の設定 | `'import/resolver': { typescript: { project } }` | `'import-x/resolver-next': [createTypeScriptImportResolver({ project })]` | import-x の新しい resolver の口。`project` は同じ `tsconfig.standalone.json` |
| 設定の入口 | `tseslint.config(...)` | `defineConfig(...)`（`eslint/config`） | `tseslint.config` は非推奨。`extends` は `defineConfig` が支える |

- 緩めた規則: **無い。** 移行前後の `--print-config` の差（`src/features/index.ts`・`e2e/a11y.spec.ts`・`vitest.config.ts`）は、
  上の改名 1 件と**増えた規則だけ**である: `no-unassigned-vars`・`no-useless-assignment`・`preserve-caught-error`（`@eslint/js` 10 の recommended）、
  `react-hooks/{config,error-boundaries,gating,globals,immutability,preserve-manual-memoization,purity,refs,set-state-in-effect,set-state-in-render,static-components,use-memo}`（error）・
  `react-hooks/{incompatible-library,unsupported-syntax}`（warn）。消えた規則・弱まった規則は無い。
- flat config はもともと採用済み（eslintrc は使っていない）。10 系で廃止された設定項目の使用は無かった。

## 増えた規則が検出したコード

- `src/features/sc02-risk-settings/components/RiskSettingsPage.tsx`（取引ガードの危険確認）: `useEffect` で `setConfirmDanger(false)` を呼んでいた
  （`react-hooks/set-state-in-effect`）。同じファイルの `guardSignature` と同じ「前回のシグネチャを state に持ち、描画中に比較・調整する」形へ直した。
  挙動は厳しい側へ倒れる（effect 版は危険リストが変わった描画の commit 後まで古い確認が残った）。既存の単体テスト
  `RiskSettingsPage.guard.test.tsx`（危険を足すと確認が外れる）が通ることで確かめた。

## 受け入れ基準

- [x] `npm ci` が通る（ERESOLVE が出ない）
- [x] `npm run lint` が 0 error・0 warning（移行前と同じ 108 ファイル）
- [x] `import-x/no-restricted-paths` が効いている——違反の探りで移行前と同じ 5 件の error（下の §検証）
- [x] `npm run typecheck`・`npm run test`・`npm run i18n`（カタログ差分なし）・`npm run e2e:typecheck`・`npm run e2e` が通る
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が通る

## 検証

- 違反の探り（一時ファイル。検証後に削除）: `src/features/sc01-settings/__probe.ts`（`../sc02-risk-settings/...` の import・`react-router-dom`・
  `@foundation/api/apiClient` の `apiFetch`）と `src/lib/zz/__probe.ts`（`../../features/...` と `@ai-stock-trading/features/...`）。
  移行前（1e89cc106~1 を `npm ci`）と移行後で、どちらも **5 problems (5 errors)**、規則・位置・メッセージが一致した。
- `npm run test`: 33 files / 494 tests passed。`npm run e2e`: 133 passed。
- `format`・`build` の npm スクリプトは `frontend/` に無い（整形は lint、ビルドは合成先の基盤が持つ）。

## 計画書との差異

- 差異: なし（`MSP/ADR-0066` は「`eslint-plugin-import`（または同等）」を許す。IADR-0288 決定 4 の規則名は IADR-0640 で改める）。

## 10 系に未対応で残したもの

- 無い。`eslint-plugin-import`（2.32.0・ESLint 10 を peer に持たない）は残さず `eslint-plugin-import-x` へ置き換えた。
