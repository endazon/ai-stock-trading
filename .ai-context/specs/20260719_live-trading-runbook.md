---
title: 実弾解禁 Runbook 作成（docs-only）
type: work
status: draft
related_ids:
  - FR-05
  - FR-20
  - ADR-0002
  - IADR-0016
  - IADR-0056
  - IADR-0060
author: endazon (with Claude Code)
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
---

# 作業仕様書: 実弾（`TrdEnv_Real`）解禁 Runbook の作成

> 本作業は **docs のみ**。コード・設定の既定値・IADR 連番は変更しない。実弾は有効化しない（フラグ off のまま）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-05**（発注執行）、**FR-20**（段階ゲート）
- ユースケース（UC）: なし（運用手順のため）
- 画面（SC）: なし
- 関連 ADR: [IADR-0016](../adr/IADR-0016_safe-broker-execution.md)（安全既定・二重ゲート）、
  [IADR-0056](../adr/IADR-0056_moomoo-simulate-poc-complete-real-gated.md)（§3 実弾解禁前提）、
  [IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md)（決定 5＝第三の閂）、計画 ADR-0002（broker-selection）
- 計画書リンク: `planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md`

## 目的・背景

実弾解禁の切替（config キー・前提・go-live 手順・切り戻し）を一箇所に集約し、**切替を容易化しつつ文書化**する。
現状は三重の閂（[IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md) 決定 5）で実弾を塞いでおり、本作業は
その状態を変えない手順書の整備である。

## 対象範囲

- 対象: [`docs/operations/live-trading-cutover-runbook.md`](../../docs/operations/live-trading-cutover-runbook.md) の新設、
  [`docs/operations/operations.md`](../../docs/operations/operations.md) からの相互リンク、`docs/README.md` への `runbook` 種別の登録。
- 対象外: コード・設定の既定値変更、新規 IADR の起票、`docs/adr/README.md`・IADR 連番、
  #141 の Risk/OrderExecution コード、および並行の #141 セッションが起票予定の IADR-0092
  （**本 PR 時点では未起票**の将来番号。#141 の自動リコンサイル実装は既存の IADR-0074 に対応する。両者に触れない）。

## 設計

- 実キー名・ゲート挙動を実コード（`BrokerFactory.cs` / `MoomooBrokerOptions.cs` /
  `MMApiMoomooTradeClient.cs` / `Program.cs`）で裏取りしてから記述する。
- 「単一 config フリップ」は**解禁 IADR が閂 2・3 を緩めた後の目標像**として記し、現状は起動時停止すると明記する
  （事実に基づく・誇張しない）。
- 状態の単一情報源は `operations.md` に置き、Runbook は手順に特化する。

## 受け入れ基準

- [x] 実弾解禁 Runbook が config キー・三重の閂・解禁前チェックリスト・go-live 手順・切り戻し・安全警告を含む
- [x] 実コードで裏取りしたキー名・ゲート挙動と一致する
- [x] コード・設定の既定値を変更しない（実弾は off のまま）
- [x] `check-doc-links` が通る（相対リンク切れなし）
- [x] コミット件名・PR タイトルが計画 ID 規約（`docs(...)`）に適合する

## テスト方針

docs のため単体テストは無い。`node scripts/check-doc-links.js` によるリンク健全性と、
`check-commit-messages.js` による件名規約の CI 検査で担保する。

## 計画書との差異

- 差異: なし（実弾解禁の意思決定そのものは別 IADR に属し、本作業はその手順書の整備に留まる）

## 未決事項

- 実弾解禁を可能にする実装 ADR（`IADR-XXXX`）は未起票。本 Runbook はその ADR 承認後の手順書として先行整備する。
