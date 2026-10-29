---
title: IADR-0602 live の E2E は CI のジョブではなく手元の口（npm run e2e:live）で流し、trading 層は Playwright の前に市場の閉場の門を確かめ、前提未整備（exit 2）は赤にしない。実行記録は docs/tests/E2E_LIVE.md の表に貼る（CI の実行場所は #1506 で決める）
type: impl-adr
status: Accepted
related_ids: [NFR, SC-01, SC-02, SC-03, SC-04, IADR-0565, IADR-0588, IADR-0599, MSP:IADR-0569]
author: claude (Claude Code)
created: 2026-10-30
updated: 2026-10-30
plan_refs:
  - MSP#2013 コメント 3 §7（実行の運用・判定の記録）
related_specs:
  - ../specs/20261030_1420_e2e-live-local-run-and-record.md
---

# IADR-0602: live の E2E の手元の実行と実行記録（#1420 / E-62 の一部）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-30
- 決定者: claude が起案。CI のジョブ（self-hosted のランナー）を採らないことは、オーケストレーターの判断（2026-10-30）。

## 起点・関連

- 起票: #1420（E-62。親 #1410）。CI の実行場所の判断は子 issue #1506（blocked:decision）。
- 前提: IADR-0565（層と送らない操作）・IADR-0599（門と復元）・IADR-0588（live の config と storageState）。

## コンテキスト

- live の E2E が当たる先は、オーナーの手元の常設 PoC（エッジ `https://localhost`）である。GitHub-hosted のランナーからは届かない。
- 本リポジトリは public である。PoC のホストに self-hosted のランナーを置くと、外部の PR が自分のワークフローで同じラベルを指定したとき、稼働クラスタと OpenD を持つホストでコードが走り得る。`workflow_dispatch` だけに絞っても防げない。
- E-62 の受け入れ基準は「門が exit 2 のとき job は neutral（赤にしない）」「実行記録に runId・結果・未復元一覧の列がある」である。

## 決定

### 決定 1: CI のジョブは置かない。手元の口 `npm run e2e:live`（`scripts/e2e-live-run.js`）で流す

- 実行場所の判断（何もしない／private リポジトリへ移す／条件つきの self-hosted）は #1506 に分けた。決まるまでは、取引オーナーの storageState を持つ手元の端末から流す。
- 口は `--layer ro|trading`（カンマ区切り。既定 `ro`）を受ける。`E2E_LIVE=1`・`E2E_LAYER`・`E2E_RUN_ID` を与えて `playwright.live.config.ts` を起動する。runId はこの口が決めるので、後から未復元一覧のファイルを引ける。

### 決定 2: trading 層は、Playwright を起動する前に市場の閉場の門を確かめる

- 設計（MSP#2013 §7）は「trading は閉場判定を job 先頭に置く」と書く。手元の口でも同じ順にする（`scripts/e2e-market-closed.js`）。不成立なら Playwright を起動しない（ブラウザもセッションも使わない）。門は globalSetup でもう一度確かめる（二重）。

### 決定 3: 前提未整備（exit 2）は赤にしない

- Playwright の終了コードを、記録の結果と口の終了コードへ写す: 0 → 成功（0）、2 → 前提未整備（0）、それ以外 → 失敗（1）。前提未整備は、成功とも失敗とも区別して記録に書く（skip の緑と混ぜない）。
- trading 層で未復元が残れば、Playwright が通っていても失敗（1）にする。

### 決定 4: 実行記録は `docs/tests/E2E_LIVE.md` の表に人が貼る

- 列は 日付・runId・層・結果・未復元一覧・flaky・イメージの作成日時。口は終わりに表へ貼る 1 行を出す（flaky とイメージの作成日時は人が書く）。
- 未復元一覧は `frontend/test-results/trading-restore-<runId>.json` の残った差分の項目名と、復元の失敗から作る。ファイルが無ければ「書き込みなし」。
- 表へ自動で追記しない（手元の実行の結果をリポジトリへ書くには PR が要る。CI の実行場所が決まったら、そちらで扱う）。

## 結果

- 良い点: 公開リポジトリに稼働環境へ届く経路を作らずに、live の E2E を流す手順と記録の形が揃う。
- 悪い点: 実行と記録が人の手に依る。
- 残余リスク: E-62 の「ジョブで neutral」は満たしていない（#1506 で決めるまで #1420 は閉じない）。
