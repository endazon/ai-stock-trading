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
- RabbitMQ に接続するホストを持つ 4 つの fixture（`TradeExecutionPipelineE2ETests`・`OrderExecutionPipelineE2ETests`・`KeycloakOwnerOnlyEndpointE2ETests`・`ServiceTokenSyncQueryE2ETests`）の `DisposeAsync` を、
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

［2026-10-01 追記 / #1128］独立監査（NO-GO）の是正:

- **母集合の漏れ**: 上の「3 本」は誤りで、`ServiceTokenSyncQueryE2ETests`（リスク管理・報告書・費用統制の 3 ホストが実 RabbitMQ に接続する）が漏れていた。
  引き直し: `git grep -lE "RabbitMq(Container|Builder)|Testcontainers.RabbitMq" -- backend` と `git grep -lE "UseRabbitMq|RabbitMq__ConnectionString" -- 'backend/Tests/**'` はどちらも同じ 4 本。
  各サービスの単体試験のファクトリ（`backend/Services/*/Tests/*WebApplicationFactory.cs` 10 本）はすべて `DisableAllExternalWolverineTransports()` を呼ぶので対象外。4 本目にも同じ形を当てた。
- **環境変数の掃除**: コンテナの破棄が投げても環境変数を消すよう、4 本とも `DisposeInfrastructureAsync` の中に `finally` を 1 段足した（`DisableTestParallelization` なので残ると後続の試験クラスへ漏れる）。
- **握る範囲**: 握るのは打ち切り全般（ホストの停止の打ち切りも含む）。どの段で打ち切られたかを追えるよう、標準エラーへ型名だけでなく例外全体（スタック）を出す。素の `OperationCanceledException` の試験を足した（監査の変異 M4 を殺す）。
- **残余（追加）**: 打ち切られた接続は半開きで残り得る。Testcontainers の経路（CI）では直後にブローカのコンテナごと破棄されるので後続へ影響しない。外部注入（`E2E_*`・ローカル専用）で共有するブローカでは consumer が残り、次の試験クラスのメッセージを奪い得る（変更前も同じで、変更前は赤になるだけだった）。
- **残余（追加）**: 外側の `finally` が投げると、内側の OCE 以外の例外は失われる（試験は赤のまま。原因の手がかりだけが減る）。
