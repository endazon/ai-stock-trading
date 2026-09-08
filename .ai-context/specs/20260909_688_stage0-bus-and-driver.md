---
title: 仕様書: Stage 0 判定を駆動するバス基盤の新設（BacktestService）
type: spec
status: draft
related_ids: [FR-15, FR-20, ADR-0008, ADR-0023, ADR-0033, IADR-0089, IADR-0105, IADR-0129, IADR-0157, IADR-0259, IADR-0276, IADR-0281, IADR-0304, IADR-0310]
author: endazon (with Claude Code)
created: 2026-09-09
updated: 2026-09-09
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0023_us-daily-ohlc-history-source.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0033_stage0-evaluation-target-is-ai-decision-replay.md
---

# 仕様書: Stage 0 判定を駆動するバス基盤の新設（BacktestService）

> 対象 issue: [#688](https://github.com/endazon/ai-stock-trading/issues/688)（親 [#632](https://github.com/endazon/ai-stock-trading/issues/632)）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-15**（バックテストを実弾投入前の必須ゲート＝Stage 0 とする）・**FR-20**（段階的展開のゲート）
- ユースケース（UC）: UC-06（設定変更・段階遷移承認）
- 画面（SC）: なし
- 関連 ADR: **ADR-0008**（段階ゲートとバックテスト）・ADR-0023 決定5（米国株日足 OHLC 履歴源）・
  **ADR-0033**（Stage 0 の評価対象は AI 判断そのもの。記録・再生方式。2026-09-05 裁定＝環流 planning#533 の回答）
- 関連 IADR: IADR-0089（verdict はイベントで供給し Risk が read-modify-write で射影）・IADR-0105 / IADR-0157（過去データ源の安全既定）・
  IADR-0129（Wolverine/RabbitMQ 共通配線）・IADR-0259 / IADR-0276 / IADR-0289（VSA と `Hosted/`）・
  IADR-0281 / IADR-0304（空売りの観測と StrategyId）・**IADR-0310**（本作業の実装 ADR）
- 計画書リンク: 隣接クローンを読み取り専用で参照（`project-planning/projects/ai-stock-trading/`）

## 目的・背景

`BacktestService` は Stage 0 判定（`Stage0GateService`）と verdict の契約写像（`BacktestEvaluatedFactory`）を持ちながら、
**Wolverine/RabbitMQ の参照が無く、`BacktestEvaluated` を publish する経路が無い**。受け側の
`BacktestEvaluatedProjectionHandler`（RiskManagementService）は購読を配線済みだが、**一通も届いていない**。
本作業は「経路（バス基盤＋駆動）を通す」ことに限る。**評価対象そのもの（本番戦略）は実装しない。**

ADR-0033（2026-09-05 裁定）は評価対象を **取引判断サービスの AI 判断そのもの**と定め、記録・再生方式で評価すると決めた。
同 ADR の「統制と現在の実現手段」表は、記録器・記録再生戦略が未実装である現状に対する暫定手段として
**「Stage 0 の verdict を実運用として生成しない（#688 はプレースホルダ戦略での動作確認に留める）」**と明記している。
本作業はこの指示のとおりに作る。

## 対象範囲

- 対象:
  - `BacktestService` への Wolverine/RabbitMQ 配線（他サービスと同型＝共通ヘルパ 1 行）
  - Stage 0 判定を駆動する定時常駐（`Hosted/Stage0EvaluationService`）。**既定は無効**（fail-safe）
  - 空データ時の fail-closed（判定を走らせず、合格 verdict を出さない）
  - プレースホルダ戦略（`PlaceholderStrategy`）と、その verdict を**不合格固定**にする純関数
  - publish → RMS 射影 → 昇格拒否の維持を、Wolverine のテストハーネスで確認するテスト
- 対象外:
  - 本番戦略（AI 判断の記録・再生。ADR-0033 決定1・2）の実装
  - 実 RabbitMQ / 実 OpenD を用いた E2E（[#82](https://github.com/endazon/ai-stock-trading/issues/82) に残置）
  - `Backtest:BarData:Provider` の既定変更（`none` のまま。ADR-0023 決定5 の未確認 2 点が未了）
  - 試行台帳・PBO 行列・ウォークフォワード OOS の実供給（本番戦略が要る。プレースホルダでは作れない）

## 設計

### 1. バス基盤

`BacktestService.csproj` へ `WolverineFx.RabbitMQ` / `WolverineFx.RuntimeCompilation` を追加し、`Program.cs` で

```csharp
builder.Host.UseWolverine(opts => opts.UseAiStockTradingRabbitMq(ServiceName, builder.Configuration["RabbitMq:ConnectionString"]));
```

の 1 行だけを呼ぶ（キュー名・fan-out・再試行・DLQ の選択肢をサービス側に持たない。IADR-0129 決定4）。
本サービスは**発行専用**でありハンドラを持たない（`InformationCollectionService` と同型）。

### 2. 駆動（Hosted）

`Hosted/Stage0EvaluationService`（`BackgroundService`）。`Hosted/` に置くのは IADR-0276 / IADR-0289 追記1
（`BackgroundService` は `Hosted/`。HTTP 端点を持たないサービスは `Features/<集約>/<操作>/` を作らない）に従う。

- 構成 `Backtest:Stage0`（`Stage0EvaluationOptions`）。**`Enabled` 既定 `false`**。無効なら `ExecuteAsync` は即 return し、
  巡回もバー取得も publish も**一切行わない**。
- 有効時は `IntervalSeconds`（既定 86,400＝日次・下限 60）で `RunOnceAsync` を回す。1 巡回の例外は握りつぶしてログする。
- `RunOnceAsync` の流れ:
  1. 構成の銘柄（`Symbols`）から PIT ユニバースを作り、評価期間 `[今日-LookbackDays, 今日]` を導く。
  2. `MaterializedBarDataSource.LoadAsync` で実過去データ源からバーを 1 回だけ取得する（既定 provider は `none`＝0 本）。
  3. **バーが 0 本なら判定を走らせず**、不合格固定の verdict（`NoHistoricalBars` ＋ `PlaceholderStrategy`）を publish する。
  4. バーがあれば `BacktestRunner` でプレースホルダ戦略を走らせ（駆動経路の実走）、
     `Stage0DriverVerdict.PlaceholderRun(cutoffSatisfied)` で**不合格固定**の verdict を作る。
  5. `BacktestEvaluatedFactory.From(...)` を通して `BacktestEvaluated` を組み、`IMessageBus.PublishAsync` する。

### 3. 不合格固定（プレースホルダ）

`Features/Backtest/EvaluateStage0Gate/Stage0DriverVerdict`（純関数）。**合格を作れる口を持たない**
（`Stage0GateResult(Passed: false, ...)` を直接組み、`Stage0GateEvaluator` を呼ばない）。
`Stage0GateCheck` へ `NoHistoricalBars` / `PlaceholderStrategy` の 2 値を足し、未達理由を `FormatFailedChecks()`
の単一情報源に載せる（`FailedChecks` 文字列の作り方を分岐させない）。
`StrategyId` は `placeholder/no-op`（プレースホルダであることが受け手から判る）。

### 4. 空データの fail-closed（最重要の否定形）

`DataCutoffPolicy.IsAllAfterCutoff` は**空を真空的に真**と返す（＝空でも検証条件①だけは満たすように見える）。
`Program.cs` の従来コメントが指していた論点はこれである。駆動側は**バーが 0 本のとき判定そのものを走らせない**ことで埋める。
加えて、カットオフ日が未構成なら `DataCutoff` を未達として載せる（ADR-0033 決定3・カットオフ日の供給元が未整備であるため）。

## 受け入れ基準

- [x] 本番構成（Wolverine/RabbitMQ の同型配線）で駆動経路が実行され `BacktestEvaluated` が publish される（テストハーネスで捕捉・実 RabbitMQ 不要）
- [x] RMS の `IStagePerformanceStore` に `BacktestPassed` が記録される（プレースホルダでは `false`）
- [x] **否定形（最重要）**: 過去データが空のとき合格 verdict を出さない（判定を走らせない）
- [x] **否定形**: Stage 0 未合格でも昇格できる経路が生まれていない（`BacktestPassed=false` は従来どおり昇格を止める）
- [x] **否定形**: プレースホルダ戦略の verdict が本番の合否として記録されない（`Passed=false` 固定・理由が読める）
- [x] 既定構成では駆動が起動しない（fail-safe）
- [x] 起点 ID コメント（FR-15 / FR-20 / ADR-0008）付きのテスト。統制系の 3 点セット（境界値・プロパティベース・否定形）

## テスト方針

| 観点 | テスト |
| --- | --- |
| 純関数（不合格固定） | `Stage0DriverVerdictTests`: **プロパティベース**（カットオフ充足・バー有無の全組合せで `Passed` が真にならない）・境界値（理由の並び）・否定形 |
| 駆動（空データ） | `Stage0EvaluationServiceTests`: バー 0 本 → `Passed=false` かつ `NoHistoricalBars` を含む verdict を publish |
| 駆動（バーあり） | 同: バーがあってもプレースホルダでは `Passed=false`・`StrategyId` がプレースホルダ |
| 既定無効 | 同: `Enabled=false` の `StartAsync` で publish が 0 通 |
| 経路 | `Stage0DriverToRiskProjectionTests`: publish した verdict を RMS ハンドラへ流し、`IStagePerformanceStore.BacktestPassed=false` を確認し、`StageGateService` の昇格が `BacktestNotPassed` で拒否され続けることを確認 |
| 配線 | `BacktestWorkerWiringTests`: 駆動の登録・既定無効・自己申告 |

## 計画書との差異

- 差異: あり（**計画どおりの暫定**）。ADR-0033 が定めた評価対象（AI 判断の記録・再生）は未実装であり、本作業は
  プレースホルダ戦略で駆動経路のみを通す。これは ADR-0033「統制と現在の実現手段」表が #688 に対して明示した暫定手段そのものである。
  **本作業の verdict は go-live の判断材料にならない**（`Passed=false` 固定）。

## 未決事項

- 記録・再生方式（ADR-0033 決定2）の実装 issue が未起票（本作業の対象外）。
- LLM 学習カットオフ日の供給元が計画側に未登録（ADR-0033 決定3）。構成キー `Backtest:Stage0:LlmTrainingCutoff` を用意したが、
  値が入るのは計画側の登録後である。**未構成のときは `DataCutoff` を未達として載せる**（合格側へ倒さない）。
