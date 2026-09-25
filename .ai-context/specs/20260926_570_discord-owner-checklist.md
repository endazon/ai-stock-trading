---
title: Discord Bot の実機確認を利用者が 1 回の窓で打てる形に並べる（#570・#565）
type: spec
status: accepted
related_ids: [FR-07, FR-08, FR-09, FR-10, FR-14, FR-19, UC-03, IADR-0240, IADR-0274, IADR-0408, IADR-0423]
author: claude (Claude Code)
created: 2026-09-26
updated: 2026-09-26
plan_refs: []
---

# Discord Bot の実機確認を利用者が 1 回の窓で打てる形に並べる

## 背景

#570 の残件（`/report` の冪等確定・`/gfv clear`・第 2 アカウントの陰性試験）と #565 の残件（確定報告書の本文が RAG 検索に当たること）は、
どちらも利用者が Discord で打つコマンドを待っている。A-7a の再測定手順 ①〜⑤ は項目ごとに書かれており、#992 の `/status`
（口座未照会時の上限「不明」表示）が入っていない。利用者の指示（2026-09-26・blocked:human の引き取り）で、AI にできる読み取り専用の
再確認を行い、利用者の打鍵手順を 1 枚に並べる。

🔴 **ハードリミット**: Discord の操作、稼働クラスタへの書き込み（exec・書き込みを伴う port-forward）は行わない。本番の OwnerOnly 操作を
Bot を経由せずに代行しない。

## 実測（2026-09-26・`kubectl logs` / `get` のみ）

- notification-service（起動 13:41 UTC）: `Gateway Connecting → Connected → Ready`、`スラッシュコマンドを登録しました（guild=1519273423510179953）`。
  17:33 に `Server requested a reconnect` → `Resumed previous session`。起動時に `A Ready handler is blocking the gateway task` の警告 1 回
  （Ready でコマンド 8 件を順に登録。約 20 秒）。
- 18:10:49 UTC: `ReportConfirmed` を 1 回処理し Webhook へ `204`。
- report-service 18:10:49 UTC: **`確定報告書 daily-2026-09-26 は本文が空のため KB へ本文を送りません`** → `POST /documents` は 201。
  report-service の起動（13:41）以後に自動生成のログは無く、日付は土曜の日報で生成時刻より前。**手で作られた会話（PUT は本文を持たない）が確定された**可能性が高い。
  確定者は audit と通知チャンネルの「確定」通知に載るが、AI は API の読み出し（トークン・port-forward が要る）を行っていないため特定していない。
- MSP の `CreateDocumentRequest` は `string? Body = null` を持ち、AST の `HttpKnowledgeBaseWriter.CreateDocumentBody` も `Body` を送る（#565 のコード側の残件は無い）。

## 変更

- `docs/operations/discord-live-check-runbook.md` を新設: AI の再確認結果、手順 1〜6（`/status`・補完・`/report approve` の 2 度押し・`/gfv clear`・
  `/drift adopt`・第 2 アカウント）と期待する応答、手順 3 で選ぶ報告書（自動生成のドラフト）、AI が引き取るログ確認、失敗時の分岐。
- `docs/blocked-tasks.md` A-7a に追記。

冪等確定の合否は「2 回目の応答文面」ではなく「確定の通知が 1 通だけ・`ReportConfirmed` の処理が 1 回だけ」で判定する。
`EfReportStore.Confirm` は確定済みなら版を見ずに `Transitioned: false`（200）を返し、Bot は成功時に同じ文面を返すため、
#570 の 2026-09-23 コメントの「2 回目が『既に確定済み』で弾かれれば合格」は文面の予想として正確でない。

## 受け入れ基準

- [x] 利用者が打つコマンド・期待する応答・記録することが 1 枚で分かる
- [x] #992 の `/status` と #995 の `/drift adopt` の確認を含む
- [x] AI が引き取るログ確認のコマンドが書いてある（読み取りのみ）
- [ ] 利用者が手順 1〜6 を実行する（#570 の残件）
- [ ] 本文つきの報告書が 1 件確定され、AI が RAG 検索で当たることを確かめる（#565 の残件）
