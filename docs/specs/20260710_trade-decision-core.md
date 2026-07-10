---
title: 取引判断サービスのコア（トリガー購読・LLM判断ポート・構造化出力・PositionSizer 結線・TradeDecisionMade 発行）
type: spec
status: review
related_ids: [FR-04, FR-07, FR-10, FR-11, UC-01, UC-02, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# 仕様書: 取引判断サービスのコア

> Issue [#11](https://github.com/endazon/ai-stock-trading/issues/11)（FR-04）の **Slice A**。トリガー（`PriceMovementDetected`）
> を購読し、確定済み日報の方針＋リスク制約＋文脈で LLM 判断を行い、構造化出力を解析、`PositionSizer` で数量確定して
> `TradeDecisionMade` を発行する中核ループを実装する。**LLM 依存は `ILlmCompletionClient` ポートで抽象化**し、CI は fake で
> 緑にする（実 LLM ＝ platform LLM ゲートウェイ `/complete` は後続）。多数決・二段判断・RAG・費用関数は後続スライス。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-04（AI 判断・判断根拠記録）、FR-07（確定前方針では取引しない）、FR-10（サイジングはリスク予算・上限内）、FR-11（プロンプト・入出力・根拠の記録）
- ユースケース（UC）: UC-01（定時サイクル）、UC-02（価格変動トリガー）
- ADR: ADR-0003（方針階層＋独立リスク管理・AI はガードレールを上書きしない）
- 関連 IADR: [IADR-0003](../adr/IADR-0003_position-sizing-responsibility.md)（サイジング責務は判断サービス）、[IADR-0004](../adr/IADR-0004_position-effect-entry-scoping.md)（PositionEffect 設定）、本作業で新規 [IADR-0017](../adr/IADR-0017_trade-decision-structure.md)（判断サービス構成・LLM 抽象・安全既定）
- 対象 Issue: #11（Slice A）

## 目的・背景

取引パイプラインの上流（トリガー → AI 判断 → 発注意図）を実装する。ADR-0003 の「確定済み日報の方針とリスク制約の範囲内でのみ
判断」に従い、確定済み日報が無ければ取引しない（FR-07）。LLM の判断は構造化出力（JSON）で受け、`PositionSizer`（IADR-0003）で
必ず数量を確定してから `OrderIntent` を組み立て、`TradeDecisionMade` を発行する。LLM は非決定的なため、この Slice では
`ILlmCompletionClient` ポートで抽象化し、CI は fake で検証する（実 LLM・多数決・二段判断・費用統制は後続）。

## 対象範囲

新規サービス `TradeDecisionService`（`AiStockTrading.TradeDecision.*`）。ドメイン／アプリ／Worker の三層。

### ドメイン（`TradeDecisionService.Domain`）

- `TradeAction`（`Buy` / `Sell` / `Hold`）。
- `LlmDecision`（`Action`・`Rationale`・`ReferencePrice`・`StopLossDistancePerShare`）— LLM の構造化判断。
- `TradeDecisionParser.Parse(json)` — LLM の JSON 出力を `LlmDecision` に解析する。不正・欠損は `Hold`（安全側＝取引しない）。

### アプリケーション（`TradeDecisionService.Application`・`RiskManagementService.Domain` を参照）

- ポート: `ILlmCompletionClient`（プロンプト → 生成テキスト）／`IDailyPolicyProvider`（確定済み日報の方針。無ければ取引しない）／
  `ISizingContextProvider`（資金・リスク設定・段階/日次残枠・連敗/DD）／`IClock`。
- `TradeDecisionPromptBuilder.Build(context)` — 方針・トリガー・文脈からプロンプトを構築する。
- `TradeDecisionService.DecideAsync(PriceMovementDetected)`:
  1. **確定済み日報が無ければ `null`（取引しない・FR-07）**。
  2. プロンプト構築 → `ILlmCompletionClient.CompleteAsync` → `TradeDecisionParser.Parse`。
  3. `Hold`/解析不能なら `null`（取引しない）。
  4. `Buy`/`Sell` は **`PositionSizer.CalculateCappedQuantity`**（IADR-0003）で数量確定。`availableCapital` は
     **段階資金残枠（`CapitalCap − InvestedCapital`）と日次発注残枠（`MaxDailyOrderAmount − DailyOrderedAmount`）の小さい方**
     （IADR-0017 で確定）。`sizeFactor` は `PositionSizer.GetSizeFactor(連敗, DD, limits)`。
  5. 数量 0 以下は `null`（見送り）。それ以外は `OrderIntent`（`PositionEffect.Open`・IADR-0004）を組み立て、
     `TradeDecisionMade(new DecisionId, intent, rationale, clock.UtcNow)` を返す。
  6. プロンプト・LLM 出力・根拠を記録する（FR-11。ログ。永続監査は #17 連携）。

### Worker（`TradeDecisionService.Worker`）

- `PriceMovementDetectedConsumer`（`IConsumer<PriceMovementDetected>`）→ `DecideAsync` → 非 null なら `TradeDecisionMade` を `Publish`。
- **安全既定のプレースホルダ**: `PlaceholderLlmCompletionClient`（`Hold` を返す＝取引しない）／`PlaceholderDailyPolicyProvider`
  （方針なし＝取引しない）／`PlaceholderSizingContextProvider`（`TradingDefaults`＋保有ゼロ）。実 LLM（platform `/complete`）・
  実データ（日報 #14／保有 #12・#13）は後続で差し替える。初回利用時に 1 回警告する。
- 実行時基盤は test-support shim（本番非使用・IADR-0013）。ヘルスのみ（照会 API は後続）。永続化は本 Slice では設けない
  （判断は都度・ステートレス。監査永続は #17）。

## 受け入れ基準

CI で緑にする範囲（ユニット＋fake LLM＋MassTransit テストハーネス）:
- [ ] 確定済み日報が無い場合は `TradeDecisionMade` を発行しない（FR-07）。
- [ ] LLM が `Hold`／解析不能を返す場合は発行しない。
- [ ] `Buy`/`Sell` の発注意図は**必ず `PositionSizer` 経由で数量確定**される（IADR-0003 結合テスト）。
- [ ] 数量 0（リスク予算/上限で見送り）の場合は発行しない。
- [ ] 発行時、`OrderIntent.PositionEffect` は `Open`（IADR-0004）。
- [ ] `TradeDecisionParser` が構造化 JSON を解析し、不正は `Hold` に倒す。
- [ ] 既存テスト（現行数）を緑に保つ。

実 LLM/実コンテナ前提（CI 既定では実行しない）:
- [ ] platform LLM ゲートウェイ（`POST /complete`）経由の実判断（後続）。
- [ ] RabbitMQ E2E（Testcontainers・#24）。

## 対象外（後続）

- 実 LLM クライアント（platform `/complete` HTTP）、多数決（同一入力複数回）・二段判断（軽量→本判断）・RAG（#8）・費用統制（NFR/#23）。
- 定時サイクル（#21）トリガー。本 Slice は `PriceMovementDetected` のみ。
- 実データ供給（確定済み日報 #14、保有・段階/日次残枠 #12/#13）。本 Slice はプレースホルダ／テストスタブ。
- 判断根拠の永続監査（#17）。本 Slice はログ。

## テスト方針

- `TradeDecisionParser` は純粋関数として単体検証（正常 JSON・不正 → Hold）。
- `DecideAsync` は fake（LLM/方針/サイジング文脈）で検証。**サイジング結合テスト**（数量が `PositionSizer.CalculateCappedQuantity`
  と一致・`availableCapital` の min 選択・PositionEffect=Open）を含める（IADR-0003）。
- `PriceMovementDetectedConsumer` は MassTransit `ITestHarness` で `TradeDecisionMade` 発行を検証。

## 関連仕様

- 連携元: [20260710_market-monitor-core](20260710_market-monitor-core.md)（`PriceMovementDetected`）
- 連携先: [20260709_risk-management-application](20260709_risk-management-application.md)（`TradeDecisionMade` を購読・検証）
- 実装ADR: [IADR-0017](../adr/IADR-0017_trade-decision-structure.md)、[IADR-0003](../adr/IADR-0003_position-sizing-responsibility.md)、[IADR-0004](../adr/IADR-0004_position-effect-entry-scoping.md)

## 未決事項

- 実 LLM のプロンプト設計・モデル選択・多数決回数・二段判断のしきい値は後続（費用統制 #23 と連動）。
- 確定済み日報・保有・段階/日次残枠の実データ供給は #14/#12/#13 連携で確定。
