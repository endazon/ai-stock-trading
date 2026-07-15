---
title: IADR-0055 実 LLM 費用計測はイベント（LlmCostIncurred）で計上する（HTTP /costs/record は OwnerOnly のため使わない）（Proposed）
type: impl-adr
status: Proposed
related_ids:
  - FR-04
  - IADR-0027
  - IADR-0031
  - IADR-0034
author: claude
created: 2026-07-14
updated: 2026-07-14
plan_refs:
  - "../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md (NFR: 費用統制)"
---

# IADR-0055: 実 LLM 費用計測はイベント（LlmCostIncurred）で計上する（Proposed）

- 状態: **Proposed**（設計確定。実装は #79 の後続スライス・#125 の LLM egress に積む）
- 日付: 2026-07-14
- 決定者: claude（実装・起案）

> 採番注記: develop 最新は IADR-0051。IADR-0052〜0054 は in-flight（PR #123 / feat/122）が採番済みのため、
> 衝突回避として本 IADR は 0055 を用いる（AST adr README の並行採番ルールに従う）。

## 起点・関連

- 関連 ID: FR-04（AI 判断）／IADR-0027（費用統制）／IADR-0031（poller の間隔延長/停止照会）／IADR-0034（原子的計上）
- Issue: #79（実 LLM 費用計測と poller 配線）の残スコープ「実費用計測」
- 依存: #125（LLM egress・`HttpLlmCompletionClient`。/complete 応答の InputTokens/OutputTokens が計測入力）

## コンテキストと課題

#125 で取引判断の LLM は platform ゲートウェイ `POST /complete` を呼ぶようになり、応答に
`InputTokens/OutputTokens` が返る。これを月次 LLM 費用として `CostControlService` へ計上したい（#79）。
だが調査で次が判明:

1. **`CostControlService` の計上口 `POST /costs/record` は `OwnerOnly`**（`RequireAuthorization(OwnerOnly)`）。
   サービス（trade-decision）の client_credentials トークンは `trading-service` ロールで、**OwnerOnly を満たさず 403**。
   → サービスからの HTTP 自動計上は現状不可。
2. `CostControlService` に**イベント消費者は無い**（HTTP のみ）。
3. **単価（¥/トークン）モデルが無い**（`/costs/record` は金額を受けるだけ）。
4. `ICostLedger` は「トランザクションを開始するため入れ子で例外。#79 自動計上連携時に留意」と明記。

## 決定

1. **イベント計上を採用する**。新契約イベント `LlmCostIncurred(Category=Llm, Amount[円], At)` を
   `Shared.Contracts.Events` に追加し、`CostControlService` に消費者を足して `svc.Record(Llm, amount)` する。
   HTTP `OwnerOnly` を避け、内部メッセージング（認可はネットワーク/メッシュ層）で計上する。既存の
   `CostThresholdReached` 発行と対称。
2. **単価は構成で持つ**。trade-decision に `LlmPricing:InputPer1kTokens` / `OutputPer1kTokens`（¥・**既定 0**）。
   費用 = 入力/出力トークン×単価。**fail-safe: 未設定=0 円＝計上しても統制に影響しない**（安全既定）。
3. **計測点は egress**。`HttpLlmCompletionClient` は成功応答のトークンを `ILlmUsageReporter` へ渡す
   （既定 `NoOpLlmUsageReporter`＝publish しない）。Worker は `PublishingLlmUsageReporter`（単価適用＋
   `LlmCostIncurred` publish）を配線する。egress とメッセージングを疎結合に保つ。
4. **トランザクション入れ子回避**（課題4）: 消費者内の `Record` は MassTransit の transactional outbox を
   使わない構成とし、`EfCostLedger` の月内直列化（アドバイザリロック）に委ねる。統合テストで確認する。
5. **冪等性**（再配信対策）: MassTransit の at-least-once（再試行/再配信）で `LlmCostIncurred` が重複配信され得る。
   費用は月次累計のため二重計上は統制判定を誤らせる。実装スライスで **メッセージ ID による重複排除**（消費済み
   `MessageId` を台帳に記録し既処理なら no-op。IADR-0026 の決定的 UUID 方針と整合）を入れる。単価 0 既定では
   影響は無害だが、実単価投入時に効くよう最初から冪等にする。

## 根拠・トレードオフ

- イベント計上は OwnerOnly 制約を自然に回避し、疎結合・再試行（デッドレター）に載る。HTTP へ OwnerOrService を
  足す案は認可面の緩和になり、費用計上の書き込み口を広げるため不採用。
- 単価既定 0 は「計測経路を通しても金額 0＝無害」で、実単価は運用で投入（fail-safe）。

## 影響（実装スライス・#79 後続 / #125 に積む）

- 追加: `Shared.Contracts.Events.LlmCostIncurred`、`ILlmUsageReporter`＋`NoOp`/`Publishing`、`LlmPricing`（純関数）、
  `CostControlService` の `LlmCostIncurredConsumer`＋登録、`HttpLlmCompletionClient` への reporter 配線。
- テスト: 単価純関数／reporter（harness で publish 検証）／消費者（InMemory ledger で計上検証）。
- 未着手（さらに後続）: #19 バージョン付き月次上限の取得（現状 `DefaultCostLimitsProvider`）。

## 代替案

- **HTTP /costs/record を OwnerOrService へ緩和**: 認可面の緩和・書き込み口拡大。→ 不採用。
- **ゲートウェイ側で費用エミット**（MSP LlmGateway → AST）: クロスユニット・MSP 改修。→ 不採用（AST 内で完結）。
