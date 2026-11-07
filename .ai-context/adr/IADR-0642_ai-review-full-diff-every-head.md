---
title: IADR-0642 AI レビューは同じ PR への 2 回目以後の push でも、この head の全差分を読み直す。前回のレビューの結果を流用しない（prompt で定め、試験で固定する。検査器は緩めない）
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0190, IADR-0208]
author: claude (Claude Code)
created: 2026-11-08
updated: 2026-11-08
plan_refs: []
---

# IADR-0642: 2 回目以後の push でも全差分を読み直す

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-08
- 決定者: claude（依頼はオーケストレーター 2026-11-08）

## 起点・関連

- 起点: 基盤の MSP#2509 で、`claude-review` が 2 回目以後の push で 5 回続けて検証の下限の違反で落ちた。基盤の直しは MSP#2512。
- 前提の実装 ADR: [IADR-0190](./IADR-0190_review-verdict-gate.md)（判定の検査）・[IADR-0208](./IADR-0208_ci-pr-latency-reduction.md)（検証の下限の節）。変えない。
- 作業仕様書: [20261108_ai-review-full-diff-every-head](../specs/20261108_ai-review-full-diff-every-head.md)。

## コンテキストと課題

- `track_progress: true` の組み込みプロンプトは、過去のコメント（前回までの AI レビュー本文）を `<comments>` に、
  `pull request synchronize` を `<trigger_context>` に載せる（基盤の run 42394244730 のログで確認。本リポも同じアクション・同じ設定）。
- prompt の【検証の下限】は「変更ファイルは全部読む」だけで、前回の結果の流用を禁じていない。基盤では、レビューが前回以後のコミットだけを読み、
  残りを「前回までのレビューで読んだ」として下限を割った。前回の本文がその読み方を書くため、回を追って範囲が狭まった（10→8→1 ファイル）。

## 決定

1. prompt の【検証の下限】節の先頭に、基盤と同文の 1 項目を足す: 前回のレビュー（スティッキーコメント・過去のコメント）の結果を流用しない。
   push のたびに、この head の `git diff origin/develop...HEAD` の全ファイルを読み直す。前回以後のコミットだけを読むのは下限を満たさない。
   過去のコメントは前回の指摘の解消を確かめる手掛かりにだけ使い、M・確認の根拠・未読の理由に使わない。M はこのセッションで読んだファイルだけ。
2. `scripts.repo.test.js` が節の中の 5 つの語句を固定し、消すと赤にする（陰性対照つき）。
3. `check-review-verdict.js` は変えない。必須チェック名・起動条件・`track_progress` / `use_sticky_comment` も変えない。

## 代替案

- **`track_progress: false` にして過去のコメントを渡さない**: 採らない。組み込みプロンプトと投稿の経路が変わり、検査器が読む投稿の形と進捗の表示が同時に変わる。
  prompt の指示で効かなければ次の手として残す。
- **検査器で「前回以後」の語を落とす**: 採らない。既に M + K < N で落ちる。足りないのは正しく読ませる指示である。

## 結果

- 良い: 2 回目以後の push でも下限を満たす読み方が明文になる。文が消えれば試験が赤になる。
- 悪い: prompt が 6 行増える。毎 push で全差分を読むため、大きい PR では時間が増える（ジョブの上限 20 分の内）。
- 残余: prompt は指示であり強制ではない。流用したときは従来どおり検査器が落とす（緑で通らない）。
