---
title: Integration E2E の後片付けで RabbitMQ の接続を閉じる待ちが打ち切られても、試験を赤にせず残りの破棄を続ける（#1128）
type: spec
status: accepted
related_ids: [NFR, IADR-0049, IADR-0208]
author: claude (Claude Code)
created: 2026-10-01
updated: 2026-10-01
---

# 仕様書: Integration E2E の後片付けの打ち切り（#1128）

> 本仕様書は実装着手前に作成する。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR・無採番。CI の信頼性）
- 関連する実装ADR: IADR-0049（実基盤 E2E）／IADR-0208（Integration E2E を PR から後段へ移した）
- 起票: [#1128](https://github.com/endazon/ai-stock-trading/issues/1128)（`ci-failure` の自動起票）

## 観測（実測）

- 失敗した実行は 1 回だけ（run 36742182485・develop 23b73f35・2026-09-30 16:16 UTC）。同じコミットの定時実行（36782070924）と、以後の 0fa8c1bc・0dbca54f は緑。
- 失敗した試験は `TradeExecutionPipelineE2ETests.取引判断が承認され発注執行まで複数サービスを跨いで流れる`。**本体ではなく後片付け**（`DisposeAsync`・同ファイル 88 行）で落ちた:
  `TaskCanceledException` ← `RabbitMQ.Client.Impl.MainSession.SetSessionClosingAsync` ← `AutorecoveringConnection.CloseAsync` ← `Wolverine.RabbitMQ.Internal.ConnectionMonitor.DisposeAsync` ← `WebApplicationFactory.DisposeAsync`。
- つまり **ホストの破棄で、RabbitMQ の接続を閉じる待ちが打ち切られた**。試験の判定（承認→発注執行の流れ）は通っている。
- 加えて、後片付けは「1 つ目のファクトリの破棄が投げると、2 つ目のファクトリ・コンテナの破棄・環境変数の掃除に届かない」形である（コンテナと環境変数が後続の試験へ漏れ得る）。

## 方針

- 共通の後片付け `E2EInfrastructure.DisposeQuietlyAsync(IAsyncDisposable?, string)` を置く。
  **握るのは `OperationCanceledException`（`TaskCanceledException` を含む）だけ**で、標準エラーへ 1 行残して続ける。ほかの例外は従来どおり投げる（後片付けの本物の不具合を隠さない）。
- RabbitMQ に接続するホストを持つ 3 つの fixture（`TradeExecutionPipelineE2ETests`・`OrderExecutionPipelineE2ETests`・`KeycloakOwnerOnlyEndpointE2ETests`）の `DisposeAsync` を、
  ① ホストの破棄をこの関数で行い、② コンテナの破棄と環境変数の掃除を `finally` に置く形にする（1 つ目が投げても残りを必ず行う）。
- 試験の判定は一切変えない（スキップ・無効化・再試行を足さない）。

## 母集合（規則 9）

`git grep -n "WebApplicationFactory" backend/Tests/AiStockTrading.IntegrationTests` と `RabbitMq` の両方を含む fixture を引いた:
`TradeExecutionPipelineE2ETests.cs`・`OrderExecutionPipelineE2ETests.cs`・`KeycloakOwnerOnlyEndpointE2ETests.cs` の 3 本。
Postgres だけの並行試験（`ApprovedOrderTerminalConcurrencyE2ETests` ほか）はホストを持たず RabbitMQ に繋がないので対象外。

## 窓（規則 11）

時間差を扱わない（後片付けの例外の分類だけ）ため該当しない。

## 試験

- `E2EInfrastructureDisposeTests`（Integration の印なし・Docker 不要）:
  - 破棄が `TaskCanceledException` を投げても例外にならない
  - 破棄がほかの例外（`InvalidOperationException`）を投げたら、そのまま投げる
  - null は何もしない

## 残余

- 打ち切りの頻度は 1/数百回で、根は Wolverine / RabbitMQ.Client の閉じ待ち（ランナーの負荷）にある。後片付けでしか起きない限り、判定に影響しない。
