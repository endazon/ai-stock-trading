---
title: SC-02 テーマを外すボタンのアクセシブルネームを可視文言に合わせる（axe 4.14 の label-content-name-mismatch）と @axe-core/playwright 4.14.0 への更新
type: spec
status: accepted
related_ids: [SC-02, NFR]
author: claude (Claude Code)
created: 2026-11-12
updated: 2026-11-12
---

# 作業仕様書: SC-02 テーマを外すボタンの Label in Name（SC-02）

> 着手 2026-11-12（develop `d4ebaeb38`）。ブランチ `fix/SC-02-theme-remove-label-in-name`。
> Dependabot #1629（`@axe-core/playwright` 4.13.0 → 4.14.0）の置き換え。

## 起点

- Dependabot #1629 の CI で、axe 4.14 が SC-02 に新たに `label-content-name-mismatch`（WCAG 2.5.3 Label in Name）を出した。
  対象は `button[aria-label="暗号資産を外す"]`（`ThemeConcentrationForm.tsx`）。可視文言は「テーマを外す」、
  アクセシブルネームは「<テーマ名>を外す」で、**名前が可視文言を含まない**。
- 落ちたのは `frontend/e2e/a11y.spec.ts` の 4 件（SC-02 画面・REAL 切替モーダル・監視銘柄の削除確認・取引ガードの緩和確認）。
  いずれも SC-02 を描画した状態で axe を回すため、同じ 1 要素が 4 件に現れる。

## 方針

- **既知違反（`KNOWN_AXE_VIOLATIONS`）へ足さず、直す。**
- アクセシブルネームを `<テーマ名>のテーマを外す`（名前未入力時は `テーマ N のテーマを外す`）にする。可視の「テーマを外す」を
  部分文字列として含み、どのテーマかの区別も保つ。
- `@axe-core/playwright` を `npm install -D -E` で 4.14.0 へ（既存の完全一致 pin を保つ。`axe-core` も ~4.14.0 へ追随）。
- i18n カタログを `npm run i18n` で再生成する（`を外す` が消え `のテーマを外す` が入る）。

## 母集合（同型の取りこぼし。規則 9: 記憶でなく走査）

`grep -rn -A4 "aria-label" frontend/src --include=*.tsx`（テストを除く。`.ts` に aria-label は無い）で 39 箇所。
可視文言と aria-label を両方持つ**ボタン**は 3 件で、違反はうち 1 件:

| 箇所 | 可視文言 | aria-label | 判定 |
| --- | --- | --- | --- |
| `ThemeConcentrationForm.tsx`（テーマ行） | テーマを外す | `<名前>を外す` | **違反 → 是正** |
| `ThemeConcentrationForm.tsx`（銘柄行） | 外す | `<銘柄>をテーマから外す` | 含む（適合） |
| `HighVolatilityForm.tsx`（明示指定行） | 外す | `<銘柄>（<市場>）の明示指定を外す` | 含む（適合） |

残り 36 件は `form` / `Table` / `section`（Panel・Note）/ ダイアログの aria-label で、Label in Name の対象（可視ラベルを持つウィジェット）ではない。

単体テストで当該ボタンを名前で引くものは無かった（`grep -rn "外す" frontend/src --include=*.test.*`。名前で引くのは上表の適合 2 件だけ）。
回帰を固定するため `RiskSettingsPage.sessionAndThemeControls.test.tsx` に 1 件足す。

## 受け入れ基準

- [x] SC-02 のテーマを外すボタンのアクセシブルネームが可視の「テーマを外す」を含む（単体テスト）
- [x] `@axe-core/playwright` 4.14.0 で `e2e/a11y.spec.ts` が全件緑（手元実走）。是正前のコンポーネントでは同 e2e が
      `label-content-name-mismatch button[aria-label="暗号資産を外す"]` で赤になることを確かめた（検出力の確認）
- [x] `KNOWN_AXE_VIOLATIONS` は空のまま
- [x] vitest（sc02）・`npm run lint`・`npm run typecheck`・`npm run e2e:typecheck` が緑
