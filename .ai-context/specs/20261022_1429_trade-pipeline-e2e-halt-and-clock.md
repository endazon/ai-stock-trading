---
title: 取引パイプラインの統合 E2E を、取引停止の照会と閉場前の締め切りから切り離す（Integration E2E の develop 赤）
type: spec
status: done
related_ids: [NFR, FR-10, IADR-0561, IADR-0567]
author: claude (Claude Code)
created: 2026-10-22
updated: 2026-10-22
---

# 仕様書: 取引パイプラインの統合 E2E を、取引停止の照会と閉場前の締め切りから切り離す

> 本仕様書は実装着手前に作成した（着手 2026-10-22。AST `origin/develop` `66a6e0ee`＝#1432 のマージ後）。

## 起点（トレーサビリティ）

- 起点: #1429（Integration E2E が develop で赤）。試験の決定性＝ `NFR`。
- 赤くなった試験: `backend/Tests/AiStockTrading.IntegrationTests/TradeExecutionPipelineE2ETests.cs` の 2 件
  （`取引判断が承認され発注執行まで複数サービスを跨いで流れる` / `同一イベントは購読する全サービスへ届く_fan_outがcompeting_consumerへ退行していない`）。
  いずれも「発注が執行されない（null）」。最後の緑 run 39631534010（`d9a27318`）、最初の赤 run 39635369598（`2f2f608e`＝#1427）。
- 関係する統制: 送る直前の取引停止の照会（FR-10, IADR-0567）、閉場前の新規建ての締め切り（FR-10, IADR-0561）。
- IADR: 作らない（試験側だけの是正。製品コード・本番の構成は変えない）。計画との矛盾: なし。

## 原因

1. **常時（#1427 以降）**: 発注執行は送る直前に取引停止状態をリスク管理へ照会する（IADR-0567）。`RiskManagement:BaseUrl` が未設定だと
   `UnconfiguredTradingHaltSource` が「不明」を返し、新規建てを送らない（fail-closed）。#1427 は `OrderExecutionPipelineE2ETests` に
   `NotHaltedSource` を差し込んだが、2 Worker を同居させる `TradeExecutionPipelineE2ETests` には差し込んでいない。
   2 試験とも OrderApproved が発注執行に届いても執行されない。
2. **時刻依存（#1399 以降）**: リスク管理の審査は閉場の 60 分前以降の新規建てを `PreCloseEntryCutoff` で拒否する（IADR-0561）。
   本試験は実時計で米国株（AAPL）の TradeDecisionMade を流すため、19:00Z〜04:00Z 付近（米国の閉場 60 分前〜米国東部の日付の終わり）で
   審査が拒否する。fan-out 試験は OrderApproved を直接発行し審査を通らないため、この原因の影響は 1 件目だけである。

## 方式

- 発注執行ホスト: `ConfigureTestServices` で `ITradingHaltSource` を `OrderExecutionPipelineE2ETests.NotHaltedSource`（既存）へ差し替える。
- リスク管理ホスト: `ConfigureTestServices` で審査の `IClock` を `EntryScreeningSerializationE2ETests.OutsidePreCloseCutoffClock`（既存。
  締め切りの窓の中なら同じ市場ローカルの日の締め切り 1 分前へ戻す）へ差し替える。
- 新しい補助は作らない。既存 2 型の可視性を `private` → `internal` にして再利用する（同アセンブリの試験間の共有に限る）。
- 統制は迂回しない: 情報収集の観測・基準資金は従来どおり実ブローカ経由で整える。取引停止の照会と締め切りの判定そのものは
  それぞれ `OrderExecutionPipelineE2ETests` 側の配線試験・`RiskEvaluator` の単体試験の検証対象であり、本試験の対象ではない。

## 母集合（規則 9・10）

### 引き方

1. `backend/Tests/AiStockTrading.IntegrationTests/*.cs` の全 17 ファイルを列挙し、ファイルごとに
   `WebApplicationFactory<`・`new TradeDecisionMade`・`new OrderApproved`・`OrderScreeningService|ScreenAsync`・`ITradingHaltSource`・
   `IClock|OutsidePreClose` の出現を数えた。
2. 取引停止の照会に依存する＝発注執行ホストで OrderApproved を執行させる試験。締め切りに依存する＝リスク管理の審査
   （ホスト経由または `OrderScreeningService` の直接組み立て）を実時計で通す試験。

### 本 PR で直す

| クラス | 依存 | 対処 |
| --- | --- | --- |
| `TradeExecutionPipelineE2ETests` | 取引停止の照会（2 件）・締め切り（1 件目） | 上の方式 |

### 既に対処済み

| クラス | 依存 | 対処 |
| --- | --- | --- |
| `OrderExecutionPipelineE2ETests` | 取引停止の照会 | #1427 が `NotHaltedSource` を差し込み済み。審査を通らない（OrderApproved を直接発行） |
| `EntryScreeningSerializationE2ETests` | 締め切り | #1396 が `OutsidePreCloseCutoffClock` で審査を組み立て済み。発注執行は使わない |

### 除外（理由）

| クラス | 理由 |
| --- | --- |
| `ServiceTokenSyncQueryE2ETests` / `KeycloakOwnerOnlyEndpointE2ETests` | リスク管理ホストは起動するが、読み取りの同期照会（`/risk-controls/open-positions` ほか）の認可だけを見る。審査・執行を通らない |
| `ApprovedOrderTerminalConcurrencyE2ETests` / `LedgerStopLineWideningConcurrencyE2ETests` / `PositionDriftStateConcurrencyE2ETests` / `SettingsChangeHistoryTransactionE2ETests` | ストアを実 DB で直接叩く。審査・執行のどちらも通らず、時刻は引数で渡す |
| `MoomooAdapterFakeOpenDIntegrationTests` | ブローカアダプタ単体。審査・取引停止の照会を通らない |
| `E2EInfrastructure*` / `RequiredServices*` / `RabbitMqTopologyProbe` / `AssemblyInfo` | 基盤の補助とその試験 |

## 受け入れ基準

- [x] 修正前の `TradeExecutionPipelineE2ETests` が手元（実 Testcontainers）で 2 件とも赤（null）になる（原因 1 の再現）。
- [x] 審査の時計を締め切りの窓の中へ固定する一時変異で 1 件目が赤になる（原因 2 の再現・差し替え口が効くことの確認）。変異は戻した。
- [x] 修正後に 2 件とも緑。`OrderExecutionPipelineE2ETests`・`EntryScreeningSerializationE2ETests` も緑のまま（可視性の変更の影響なし）。
- [x] 製品コードを変えない。`dotnet format --verify-no-changes` が通る。
