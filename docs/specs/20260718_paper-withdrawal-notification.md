---
title: 撤退の非停止（ペーパー乖離）降格提案の通知
type: work
status: Draft
related_ids: [FR-20, FR-11, FR-09, UC-06, ADR-0008, IADR-0070, IADR-0083, IADR-0085]
issue: 189
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/  # FR-20（段階ゲート）/ FR-11（監査）/ FR-09（通知）
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# 作業仕様書: 撤退の非停止（ペーパー乖離）降格提案の通知（#189）

## 目的 / 背景

撤退の定期評価ドライバ（#166・PR #190・[IADR-0083](../adr/IADR-0083_withdrawal-evaluation-driver.md)）は、
`StageGateService.EvaluateWithdrawal()` を定時駆動し、**新規に kill switch を自動起動した瞬間のみ** `WithdrawalTriggered`
（`HaltNewEntries=true`）を発行する。重複排除は kill switch 状態（DB 永続）を鍵にしており、これは実弾段階（Stage 2/3）の
実 DD 超過による自動停止経路にのみ効く。

`StageGate.AssessWithdrawal` にはもう 1 つの撤退経路がある: **Stage 1（ペーパー）でバックテスト乖離が説明不能**の場合
（`Triggered=true` / `HaltNewEntries=false` / `ProposedStage=Stage0`）。これは kill switch を起動しないため上記の durable な
重複排除鍵が使えず、#166 のドライバでは**通知していない**（巡回ごとに通知するとスパムになるため意図的に見送り）。本作業は
この非停止経路について、**巡回ごとの重複通知を避けつつ**（durable な「通知済み」状態）、`WithdrawalTriggered`
（`HaltNewEntries=false`）を 1 回だけ発行する。設計判断は [IADR-0085](../adr/IADR-0085_paper-withdrawal-notification-dedup.md)。

## スコープ

- Stage 1 ペーパー乖離（`Triggered && !HaltNewEntries`）の降格提案について、ドライバの巡回で `WithdrawalTriggered`
  （`HaltNewEntries=false`）を**新規発生時に 1 回だけ**発行する。
- **durable な重複排除**: 最後に通知した撤退提案のシグネチャ（Reason＋ProposedStage）を DB 単一行に永続化する
  （`IWithdrawalNotificationStore` ＋ EF 実装 `withdrawal_notification` テーブル）。in-memory は再起動／multi-instance で
  破綻するため不可（IADR-0083 代替案）。
- **#166 の `EvaluateWithdrawal` 経路と整合**: 停止経路（`NewlyEngaged`）と非停止経路（シグネチャ照合）で二重通知しない。
  `EvaluateWithdrawal`／`StageGateService` は不変（手動評価 EP は非停止経路で副作用ゼロ・非発行のため触れる必要がない）。
- **既存 `WithdrawalTriggered` を再利用**（新イベント無し）。Notification/Audit のフォーマッタは `HaltNewEntries=false`
  （"提案のみ"／Warning）を既に整形済み。監査 Consumer 追随不要・`AuditConsumerCoverageTests` 影響なし。
- 安全既定: ドライバ既定**無効**は不変。判定不能・非乖離・解消時は非発火（fail-safe）。

### 非スコープ（後続・別 issue）

- 実 `StagePerformance`（ペーパー乖離の実測 `PaperDeviationExplained`）の供給（別 issue・#82 系）。未供給時は fail-safe で非発火。
- 実コンテナ E2E（#82 系）。本作業はユニット + MassTransit テストハーネスで担保する。

## 設計（要点・IADR-0085）

- **重複排除はドライバ（`WithdrawalEvaluationService`）が所有する。** 停止経路と異なり非停止経路は kill switch という副作用が
  無く、手動評価 EP（`POST /stage-gate/withdrawal/evaluate`）は非停止経路で副作用ゼロ・非発行のため、#166 が `NewlyEngaged` を
  `EvaluateWithdrawal` 内に閉じ込めて避けた check-then-act 競合はそもそも存在しない。よって `StageGateService`／`EvaluateWithdrawal`
  を変更せず、ドライバ側で「読み → 照合 → 発行 → 保存」を行う（既存テスト群に一切触れない）。
- **巡回ロジック**（`RunOnceAsync`・営業日のみ、`outcome = EvaluateWithdrawal()` の後）:
  - `outcome.NewlyEngaged`（停止経路・既存）→ `WithdrawalTriggered(HaltNewEntries=true)` を発行（不変）。
  - `assessment is { Triggered: true, HaltNewEntries: false }`（非停止経路）→ シグネチャ `s` を算出し、ストアの
    最終通知シグネチャと**異なるとき**だけ `WithdrawalTriggered(HaltNewEntries=false)` を発行して `s` を保存する。
  - 上記いずれの非停止提案も無い（`!Triggered` または停止経路）→ ストアにシグネチャが残っていればクリアする
    （解消後に再乖離したら再通知できるように）。
- **シグネチャ**: `Reason` と `ProposedStage` から算出（`"{Reason}:{(int)ProposedStage}"`）。同一乖離が継続する間は不変＝再通知しない。
  将来 Stage 1 以外の非停止理由が増えても識別できる。
- **durable**: EF 単一行（`SingletonKeys.Id`）。プロセス再起動をまたいでも保持し重複通知しない。DbContext が scoped のためストアも scoped。
- **fail-safe**: 未記録＝シグネチャ null＝「未通知」。ドライバの例外は捕捉して次周期へ縮退（#166 と同じ）。

## 受け入れ基準（issue #189）→ テスト写像

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | Stage 1 ペーパー乖離（非停止）で `WithdrawalTriggered(HaltNewEntries=false, ProposedStage=Stage0)` を発行する | `WithdrawalEvaluationServiceTests`（ペーパー乖離で非停止通知） |
| 2 | 巡回ごとの重複通知を避ける（同一乖離継続中は再通知しない） | `WithdrawalEvaluationServiceTests`（2 回巡回で単一発行） |
| 3 | durable（再起動またぎ冪等）・in-memory 不可 | `EfWithdrawalNotificationStoreTests`（保存/取得/クリア・単一行 upsert）／新 factory 上で 2 ドライバ跨ぎで単一発行 |
| 4 | #166 停止経路と二重通知しない・停止経路は不変 | `WithdrawalEvaluationServiceTests`（Stage 2 停止経路は従来どおり 1 回・非停止ストアに依存しない） |
| 5 | 解消後の再乖離で再通知できる | `WithdrawalEvaluationServiceTests`（乖離→解消→再乖離で 2 回発行） |
| 6 | 安全既定・fail-safe（非乖離・判定不能は非発火） | `WithdrawalEvaluationServiceTests`（既定実績＝非発火）・休場日スキップ（不変） |
| 7 | 新イベント無し＝監査後方互換不変 | `AuditConsumerCoverageTests`（緑・不変）・baseline 差分なし |

## 検証

- `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx` 緑。
- `dotnet format` 差分なし。`nullable` 有効・警告ゼロ。
- 実基盤依存（実 DD／ペーパー乖離の実測供給・実コンテナ）は本 PR 非対象＝ユニット + ハーネスで切り分け。
