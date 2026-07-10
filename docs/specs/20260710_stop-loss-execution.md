---
title: 損切りの機械執行（StopLossTriggered 購読 → LLM 迂回で Close 注文発行）
type: spec
status: review
related_ids: [FR-10, FR-03, UC-02, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/04_workflows/02_event-driven-trading.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# 仕様書: 損切りの機械執行

> Issue [#12](https://github.com/endazon/ai-stock-trading/issues/12) の **Slice C**。市場監視（#10）が発行する
> `StopLossTriggered` を購読し、**LLM を迂回して**決済（Close）注文を機械的に発行する（ADR-0003）。イベント契約は
> #10 Slice A（PR #57）で確定済み。責務境界は [IADR-0014](../adr/IADR-0014_market-monitor-events-and-boundary.md)
> （市場監視=検知／リスク管理=執行）。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-10（リスク統制・損切り）、FR-03（損切りライン検知の連携先）
- ユースケース（UC）: UC-02（価格変動トリガー取引の損切り分岐）
- 業務フロー: `04_workflows/02_event-driven-trading.md`（損切りは AI 判断を経由せず機械的に決済・kill switch 中でも必ず実行）
- ADR: ADR-0003（損切りは機械的に執行・AI を経由しない）
- 関連 IADR: [IADR-0014](../adr/IADR-0014_market-monitor-events-and-boundary.md)、[IADR-0010](../adr/IADR-0010_risk-service-layering-and-slicing.md)、本作業で新規 [IADR-0015](../adr/IADR-0015_stop-loss-mechanical-close.md)
- 対象 Issue: #12（Slice C）

## 目的・背景

Slice A/B でリスク管理の判定・ホスト・永続化・認可を実装し、#10 で損切りイベントを発行できるようにした。残る Slice C は、
`StopLossTriggered` を購読して決済（Close）注文を機械的に発行する経路を実装する。損切りは資産保全の最後の砦であり、
ADR-0003・ワークフロー 02 により **kill switch 起動中・日報未確定・LLM 障害中でも必ず実行**する。したがって通常の
発注前スクリーニング（エントリー制約）を通さず、**無条件に Close 承認を発行**する。

## 対象範囲

### アプリケーション（`RiskManagementService.Application`）

- `StopLossExecutionService.BuildCloseApproval(StopLossTriggered)` — 損切りイベントから決済（Close）の `OrderApproved` を
  組み立てる純粋関数（[IADR-0015](../adr/IADR-0015_stop-loss-mechanical-close.md)）:
  - 決済方向 `Side` = 建玉方向の反対（`PositionSide == Buy` → `Sell` / `Sell` → `Buy`）。
  - `Mode` = 現行段階の動作モード（`settings.Stage.Mode`）。`ProductType` = `Cash`（現物のみ有効な現段階。信用有効化時は要拡張）。
  - `Quantity` = `ev.Quantity`、`Price` = `ev.Price`（検知時点の現在値）、`PositionEffect` = `Close`。
  - `OrderApproved(new DecisionId, intent, Quantity, clock.UtcNow)` を返す。
  - **スクリーニング（RiskEvaluator）を通さない**。損切りは無条件執行（kill switch・ロックアウト・相場操縦ガードで止めない）。

### Worker（`RiskManagementService.Worker`）

- `Composable/Steps/StopLossTriggeredConsumer.cs`（`IConsumer<StopLossTriggered>`）— `StopLossExecutionService` で Close の
  `OrderApproved` を組み立て、`context.Publish` する。損切り実行を情報ログに残す（FR-11・監査/通知の起点）。
- `Program.cs` に `AddConsumer<StopLossTriggeredConsumer>()` と `StopLossExecutionService` の DI 登録を追加する。

## 受け入れ基準

CI で緑にする範囲（ユニット＋MassTransit テストハーネス）:
- [ ] `StopLossTriggered`（Buy 建て）を受けると、決済 `Sell`・`PositionEffect.Close`・同数量の `OrderApproved` を発行する。
- [ ] `Sell` 建て（ショート）は決済 `Buy` になる。
- [ ] `Mode` は現行段階の動作モードを用いる。
- [ ] 損切りはスクリーニングを通さず無条件に発行される（kill switch 起動中でも発行される）。
- [ ] 既存テスト（現行数）を緑に保つ。

実コンテナ前提（CI 既定では実行しない・Testcontainers）:
- [ ] RabbitMQ 経由の `StopLossTriggered` → `OrderApproved`（Close）→ 発注執行の E2E。

## 対象外（後続）

- 発注執行サービス（#13）による実際の Close 発注（本 Slice は `OrderApproved`(Close) の発行まで）。
- 損切り実行の Discord 通知（FR-09・#15）・監査ログ永続化（FR-11・#17）。本 Slice は情報ログにとどめる。
- 信用（margin）有効化時の `ProductType` 供給（`StopLossTriggered` への追加 or ポジションストア連携）。

## テスト方針

- `StopLossExecutionService` は純粋関数として単体検証（方向・効果・数量・モード）。
- `StopLossTriggeredConsumer` は MassTransit `ITestHarness`＋インメモリ設定ストアで発行を検証。kill switch 起動中でも
  発行されること（無条件）を固定する。

## 関連仕様

- 先行: [20260709_risk-management-application](20260709_risk-management-application.md)（Slice A）、[20260710_risk-management-worker](20260710_risk-management-worker.md)（Slice B）
- 連携元: [20260710_market-monitor-core](20260710_market-monitor-core.md)（`StopLossTriggered` 発行元）
- 実装ADR: [IADR-0015](../adr/IADR-0015_stop-loss-mechanical-close.md)

## 未決事項

- 信用有効化時の `ProductType`・建玉の正確な決済数量（部分決済）の扱いは #04/#05（建玉効果の注文分解 #50）と連携して確定する。
- 損切り実行の通知（#15）・監査（#17）連携は後続。
