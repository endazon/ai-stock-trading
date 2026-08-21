---
title: 撤退（withdrawal）の定期評価ドライバ
type: work
status: Draft
related_ids: [FR-20, FR-11, FR-09, UC-06, ADR-0008, IADR-0070, IADR-0082, IADR-0083]
issue: 166
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/  # FR-20（段階ゲート）/ FR-11（監査）/ FR-09（通知）
  - planning:projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# 作業仕様書: 撤退（withdrawal）の定期評価ドライバ（#166）

## 目的 / 背景

段階ゲート（#20・PR #163・[IADR-0070](../adr/IADR-0070_stage-gate-persistence-and-approval.md)）は、撤退（差し戻し）基準の評価と
自動安全側を `StageGateService.EvaluateWithdrawal()` として実装済みである（撤退基準到達かつ `HaltNewEntries` なら kill switch を
自動起動し、降格提案 `ProposedStage` を返す。実降格は行わず承認付き遷移を要する）。しかし、これを**周期的に駆動する常駐処理
（ドライバ）が無い**。本作業は撤退の定期評価ドライバ（`BackgroundService`）を Risk に追加する。設計判断は [IADR-0083](../adr/IADR-0083_withdrawal-evaluation-driver.md)。

## スコープ

- Risk Worker に `WithdrawalEvaluationService : BackgroundService` を追加し、`EvaluateWithdrawal` を定時駆動する。
- 市場休場ガード: `IBusinessCalendar` に `IsBusinessDay(DateOnly)` を追加（現状 `NextBusinessDay` のみ）。`IClock.Today` で判定。
- 多重起動ガード: 単一 `BackgroundService` の逐次 `PeriodicTimer`（オーバーラップなし）で構造的に担保。
- 安全既定: 既定**無効**（opt-in・`QuoteRefreshService`／#141 リコンサイルの前例）＋間隔構成可（既定 300 秒）。
- 通知（#15 連携）＋監査（#167 整合）: 新イベント `WithdrawalTriggered`（primitive・Risk.Domain 非依存）を追加し、
  **新規に kill switch を自動起動した瞬間のみ**発行。NotificationService に Consumer＋Formatter、AuditService に Consumer＋Factory を追随。
- `event-schemas.baseline.json`（IADR-0079）を新イベント追加で再生成。

### 非スコープ（後続・フォローアップ issue 起票）

- **Stage 1（ペーパー）の説明不能乖離＝非停止の降格提案通知**。durable な重複排除状態が別途必要なため分離（本 PR では非通知・副作用なし）。
- 実 `StagePerformance`（実 DD・統制違反・スリッページ実績）の供給（バックテスト verdict／実 DD の s2s・#82 系）。本 issue は未供給時 fail-safe で非発火。
- 実コンテナ E2E（#82 系）。本作業はユニット + MassTransit テストハーネスで担保する。

## 設計（要点）

- **ドライバは `QuoteRefreshService` の実績パターンに準拠**: `PeriodicTimer` で定時、巡回ごとに DI スコープを作成（scoped な
  `StageGateService`／stores を解決）、例外は捕捉して次周期へ縮退（fail-safe・1 巡回の失敗で常駐を落とさない）。`RunOnceAsync` を
  public にしてユニットテスト可能にする。
- **発火＝新規停止時のみ通知（durable 冪等）**: 巡回で `EvaluateWithdrawal` を呼ぶ前に kill switch 状態を読み、`Triggered &&
  HaltNewEntries && 直前は未起動` のとき（＝今巡回で自動起動した）だけ `WithdrawalTriggered` を発行する。既に起動済みなら再発行しない
  （kill switch 状態が durable な重複排除鍵＝プロセス再起動でも冪等）。`EvaluateWithdrawal` の既存冪等挙動（起動済みなら再起動しない）を踏襲。
- **#167 との整合（二重記録の回避）**: 撤退は段階を遷移させない（提案に留める）ため `StageTransitioned` を発行しない。`WithdrawalTriggered`
  は別イベントで、Risk 専有の kill switch 変更履歴（`SettingsChangeLog`・バス非経由）とも中央監査上で役割が異なる。二重記録にならない。
- **契約は primitive**: `ProposedStage`（int）・`Reason`（string）・`HaltNewEntries`（bool）・`OccurredAt`（DateTimeOffset）。`Shared.Contracts → Risk.Domain` の依存逆転を避ける。
- **発注審査ホットパスに触れない**: ドライバは背景巡回で局所 stores を読むのみ。同期発注審査（`OrderScreeningService`）の経路は不変。

## 受け入れ基準（issue #166）→ テスト写像

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 定時に `EvaluateWithdrawal` を実行する常駐処理を追加（市場休場ガード） | `WithdrawalEvaluationServiceTests`（休場日は評価スキップ・営業日は実行）・`WeekendBusinessCalendarTests`（`IsBusinessDay`） |
| 2 | 撤退基準到達時に kill switch 自動起動＋降格提案を通知（#15） | `WithdrawalEvaluationServiceTests`（新規停止で `WithdrawalTriggered` 発行・提案段階/理由/停止フラグを含む）・Notification Consumer/Formatter テスト |
| 3 | 実 `StagePerformance` を入力（供給は別 issue・未供給時 fail-safe 非発火） | `WithdrawalEvaluationServiceTests`（既定実績＝非発火・副作用なし・非発行） |
| 4 | 非発火時は副作用なし・冪等（起動済みなら再起動しない） | `WithdrawalEvaluationServiceTests`（既に停止済み→再発行/再起動なし）・`StageGateServiceTests`（既存冪等）不変 |
| 5 | 市場休場・多重起動のガード | `WithdrawalEvaluationServiceTests`（休場日スキップ）＋逐次 `PeriodicTimer` による多重防止（設計） |
| 6 | 新イベント追加で監査 Consumer 追随・後方互換 | `AuditConsumerCoverageTests`（緑）・`AuditEventConsumersTests`／`AuditEntryFactoryTests`（写像）・`EventBackwardCompatibilityTests`（違反ゼロ）・baseline 差分 |

## 検証

- `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx` 緑。
- `dotnet format` 差分なし。`nullable` 有効・警告ゼロ。
- 実基盤依存（実 DD 供給・実コンテナ）は本 PR 非対象＝ユニット + ハーネスで切り分け。
