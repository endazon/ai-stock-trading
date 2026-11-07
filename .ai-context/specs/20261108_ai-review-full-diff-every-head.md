---
title: 作業仕様書 — AI レビューに、同じ PR への 2 回目以後の push でも、この head の全差分を読み直させる（前回のレビューの結果を流用させない）
type: spec
status: in-progress
related_ids: [NFR, IADR-0642, IADR-0190, IADR-0208]
author: claude (Claude Code)
created: 2026-11-08
updated: 2026-11-08
plan_refs: []
---

# 作業仕様書 — 2 回目以後の push でも全差分を読み直させる（NFR）

> 着手 2026-11-08（develop `baf415792`）。判断の記録は IADR-0642。基盤の同じ直し（MSP#2512）の移植。

## 起点

- 基盤の MSP#2509 で、`claude-review`（必須チェック）が 2 回目以後の push で 5 回、検証の下限「変更ファイルは全部読む」の違反で落ちた
  （25/50・44/51・10/55・8/55・1/55）。本文は「前回レビュー以後の最新コミットの差分だけ」「残りは前回までのレビューで読んだ範囲に拠る」と書いた。
- 本リポの `.github/workflows/claude-code-review.yml` も同じ構成である（`track_progress: true`・`use_sticky_comment: true`・
  【検証の下限】の「変更ファイルは全部読む」に前回の結果の扱いが無い）。同じ穴を先に塞ぐ。

## 原因（基盤の run 42394244730 のログで確かめた）

- `track_progress: true` の組み込みプロンプトは、過去のコメント全部（前回までの AI レビュー本文）を `<comments>` に、
  `<trigger_context>pull request synchronize</trigger_context>` を載せて渡す。組み込みプロンプト自体は差分の絞り込みを指示していない。
- prompt の【検証の下限】は前回の結果の流用を禁じていない。レビューは前回以後のコミットだけを自分で選んだ。

## 方式

- prompt の【検証の下限】節の先頭に 1 項目足す（基盤と同文）: 前回のレビュー（スティッキーコメント・過去のコメント）の結果を流用しない。
  push のたびにこの head の `git diff origin/develop...HEAD` の全ファイルを読み直す。前回以後のコミットだけを読むのは下限を満たさない。
  過去のコメントは前回の指摘の解消の確認にだけ使い、M・確認の根拠・未読の理由に使わない。M はこのセッションで読んだファイルだけ。
- 経緯は送信されない YAML コメント側へ書く。検査器 `check-review-verdict.js` は変えない。必須チェック名・起動条件・`track_progress` も変えない。

## 母集合（規則 9・10）

- 「前回」「前回以後」「過去のコメント」「sticky」「track_progress」で `.github/workflows/claude-code-review.yml`・`scripts/`・`docs/`・`AI_SETUP.md` を走査した。
  prompt に前回のレビューの扱いを書いた箇所は無かった。検査器の挙動は変えないので `scripts/README.md` は誤りにならない。

## 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| AC1 | prompt の【検証の下限】節に、流用の禁止・push のたびの全ファイルの読み直し・前回以後だけの禁止・M の数え方がある | `scripts.repo.test.js`（IADR-0642） |
| AC2 | 陰性対照: それぞれの文を消す・節を消すと赤になる | 同 |
| AC3 | `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が exit 0 | 手元・CI |
| AC4 | ワークフローの YAML が構文として正しい | 手元 |
