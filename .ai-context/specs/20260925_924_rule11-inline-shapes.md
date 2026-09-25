---
title: 規則 11 の 3 つの形と表の軸を必読規約へ inline 化し、同じ量以上の重複を削る
type: spec
status: accepted
related_ids: [NFR, IADR-0377, IADR-0409]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs: []
---

# 仕様書: 規則 11 を必読規約だけで守れるようにする（#924）

## 起点

- #924（PR #917 の監査の実測）。`.claude/rules/traceability.repo.md` の規則 11 は「3 通りの形で実測する」
  「表を出せるまで形を確定しない」と課すが、**どの 3 通りか・表の軸**は別紙
  `docs/how-to/correction-population-and-window-procedure.md` にしか無い。規則 9・10 は必読側だけで自足する。
- issue のコメントは 0 件（2026-09-25 時点）。

## 予算の実測（issue の数字を転記せず引き直した。規則 10）

`node scripts/check-reading-budget.js`（origin/develop 560e46a4 から切った作業ツリー）:

| 集合 | 着手前 | 着手後 |
| --- | --- | --- |
| Claude Code（`CLAUDE.md` ＋ `.claude/rules/*.md`） | 46,055（90.0%） | **45,361（88.6%）** |
| うち `traceability.repo.md` | 12,555 | 11,861 |
| AGENTS.md 系（合算しない） | 7,378 | 7,378（触らない） |
| Copilot | 2,937 | 2,937（触らない） |

warn のしきい値は `0.9 × 51,200 = 46,080`。issue の「残り 25 バイト」は着手時点でも正しかった（46,080 − 46,055）。

## 案の比較

| # | 案 | 予算 | 自足 | 評価 |
| --- | --- | --- | --- | --- |
| 1 | 3 つの形と表の軸を条文へ inline 化し、同じ量以上の重複を削る | 差し引き減 | する | **採用** |
| 2 | 規則 11 を「別紙を読め」の誘導にし、自足を諦めると明記 | ほぼ ±0 | しない | 採らない。守れない条文が残る |
| 3 | 予算を見直す | 増 | する | 採らない。予算の正本は計画リポの運用ガイド §8 で、実装側では動かせない |

## 決定（詳細は IADR-0409）

1. 規則 11 の条文へ 3 つの形（後の端だけ／前の端だけ／両端の突き合わせ。例 `min(前, 後)`）と
   表の軸（行＝形・列＝プローブ・升目＝期待どおりか）を書く（約 +350 バイト）。
   形は一般形で書く —— 当日の適用例 `20260925_923_775` は develop 先端／`HEAD^1`／マージベースという
   `min` でない両端の形を採っているため、`min` だけを書くと条文と適用例が食い違う。
2. 削る先（約 −1,040 バイト）は、**凍結記録が全文を持つ経緯・実測値**に限る（内容は失われない）:
   - 「IADR の欠番受容」の理由節の実測値 → IADR-0280 の本文 33〜48 行が全数を持つことを確認した。
   - 「検査の置換点（`check-plan-id-qualification.js`）」の経緯段落 → IADR-0262（決定 1）、IADR-0189 の
     2026-08-28 追記、`scripts/check-plan-id-qualification.js` 冒頭注記の 3 箇所が持つことを確認した（必読側を含め 4 重だった）。
   - 規範（欠番を埋めない・再利用禁止・既定を直書き・`AST` を含む理由・env で上書き可）は必読側に残す。
3. 別紙は条文と重なる文（「🔴 この表を出せるまで、是正の形を確定しない」）を削り、表を実例の結果として位置づけ直す。
   trace ブロックへ IADR-0409・本仕様書・#924 を足す。
4. IADR-0377 へ日付つき追記（決定 5 の一部を改めた旨）を足し、索引行にも追記ブロックを足す（原文は残す）。

削る候補として見送ったもの: 「起点 ID の種別」節のレンジ更新履歴（約 1.5 KB）。検査器が読む節であり、
並行するレンジ更新 PR と衝突しやすいので本件では触らない。

## 母集合（規則 9〜11 / 規則 6）

- **規則 9**（誤りになる側の文字列で全走査。`git grep -n -I`、パスで絞らない）:
  - `実例と 3 通りの表` → `.claude/rules/traceability.repo.md:68` の 1 件のみ → **直す**。
  - `3 通りの表` / `3 通りの形` → 規則ファイル・別紙・IADR-0377・索引行（IADR-0344 / 0377）・
    仕様書（820・888-894・923_775）・`scripts/check-test-traceability.js:584`・`scripts/scripts.repo.test.js:1108`。
    別紙以外は凍結記録か、個別の適用例の表を指しており、本変更で誤りにならない → 据え置き。
  - `規則 11` を引くファイル 11 件（IADR-0377・索引・仕様書 6 件・別紙・scripts 2 件）→ 番号は変わらないので据え置き。
  - 削る段落の文を引用している箇所: `2,339 箇所` は IADR-0280（出典そのもの）のみ、`素の実行（env なし）` は
    IADR-0189・`check-plan-id-qualification.js`（出典側）のみ。`check-plan-id-qualification.js:14` は
    「規則ファイルの検査の置換点に値と根拠を記録している」と書く → 根拠（非対称を塞ぐ）は残すので誤りにならない。
  - `traceability.repo.md` を読む検査器（`check-test-traceability.js`・`lib/plan-ranges.js`・`check-commit-messages.js`）は
    「起点 ID の種別（固有）」節だけを読む。本件はその節を触らない。
- **規則 10**（自分の新しい記述）: 予算の数字は IADR-0377 に「着手後 46,055」とあるが凍結記録なので据え置き、
  新しい値は IADR-0409 と IADR-0377 の追記に書く。別紙の見出し「3 通りの表」は実例の表を指すので据え置く。
  導出値（差分 −694、余白 719 = 46,080 − 45,361）は計算し直した。
- **規則 11**（窓）: 該当しない（時間差を扱う是正ではない）。

## 受け入れ基準

- [x] 規則 11 の条文だけで、3 つの形と表の軸が読める。
- [x] `check-reading-budget.js` の Claude Code 母集合が着手前（46,055）以下で、warn にならない。
- [x] 削った内容は IADR-0280 / IADR-0262 に在る。別紙へのリンクが切れない（`check-doc-links.js`）。
- [x] 文書系検査がすべて緑。

## 検証

`check-reading-budget` / `check-trace-blocks` / `check-doc-links` / `check-cross-repo-refs` /
`check-plan-id-qualification` / `check-test-traceability` / `check-adr-index-addendum-loss` /
`check-adr-index-sync --range=origin/develop..HEAD` / `check-commit-messages` / `gen-knowledge-graph --check`。
