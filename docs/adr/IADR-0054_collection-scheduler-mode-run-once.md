---
title: IADR-0054 取引サイクルの本番スケジューラは収集の run-once HTTP トリガ＋Collection:Trigger モードで実現する
type: impl-adr
status: Accepted
related_ids:
  - FR-02
  - IADR-0023
  - IADR-0052
author: claude
created: 2026-07-14
updated: 2026-07-14
plan_refs:
  - "../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md (FR-02)"
---

# IADR-0054: 取引サイクルの本番スケジューラ＝run-once HTTP トリガ＋Collection:Trigger モード

- 状態: Accepted
- 日付: 2026-07-14
- 決定者: claude（実装）

## 起点・関連

- 関連計画 ID: FR-02（取引サイクル）／IADR-0023（合流・市場カレンダー・休場ガード）／IADR-0052（CronJob 骨子）
- Issue: #121（本番スケジューラ）
- 仕様: `docs/specs/20260714_121_trade-cycle-cronjob-trigger.md`

## コンテキストと課題

取引サイクルの定時トリガーを in-process ポーリング（IADR-0023）から本番の K8s CronJob へ切替えたい
（#121）。方式として (a) CronJob→HTTP run-once、(b) CronJob→メッセージ発行、(c) 常時 in-process のまま、が候補。
また休場日ガードとの整合が要件。

## 決定

1. **収集に run-once HTTP エンドポイント**（`POST /internal/collection/run-once`）を設け、CronJob（curl）が
   これを叩いて 1 巡回（`RunOnceAsync`）を起動する。既存の最小 HTTP サーフェス（:8080）に載せる。
2. **スケジューラモードを `Collection:Trigger`**（`InProcess` 既定 / `External`）で切替える。External では
   `CollectionPollingService` の in-process 巡回を停止し、起動は run-once のみとする。
3. **fail-safe**: `Collection:Trigger` 未設定＝InProcess（現行動作を維持）。chart は `cronjob.enabled=true` の
   ときのみ情報収集へ `Collection__Trigger=External` を注入し、二重起動を防ぐ。既定は CronJob 無効＝in-process。
4. **休場ガードはトリガ非依存**: 市場カレンダーの開場日ゲート（IADR-0023）は下流 `TradeDecision.
   InformationCollectedConsumer`（`calendar.IsOpen`）にあり、in-process/run-once いずれでも適用される。

## 根拠・トレードオフ

- (a) HTTP run-once は curl の CronJob だけで済み、発行用イメージ/CLI が要らず単純。メッセージ方式(b)は
  発行専用コンポーネントが必要で過剰。→ (a) 採用。
- モードを構成（`Collection:Trigger`）に切り出すことで、dev は in-process のまま・本番のみ External と、
  環境差を values/env で吸収できる（コア改修なし）。
- 休場ガードを下流に一元化することで、トリガを増やしても二重管理にならない。

## 影響

- 追加: `CollectionTrigger` enum ＋ `CollectionOptions.Trigger`、`ExecuteAsync` の External 早期 return、
  run-once エンドポイント、chart の Trigger 注入。回帰テスト 3 本。
- 変更なし: 収集本体（`RunOnceAsync`）・下流の市場カレンダー。
- CronJob の実クラスタ起動確認は #121 の受け入れ（要ユーザ環境）。
