---
title: 実 LLM 費用計測の実装スライス（LlmCostIncurred イベント計上）— Issue #79
type: spec
status: draft
related_ids:
  - FR-04
  - NFR
  - IADR-0027
  - IADR-0031
  - IADR-0034
  - IADR-0055
author: claude
created: 2026-07-15
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR: 費用統制)
related_specs:
  - "20260714_79_llm-egress.md（#125・計測入力となる /complete 応答）"
  - "../adr/IADR-0055_llm-cost-metering-event.md（本スライスの設計・Proposed）"
---

# 仕様書: 実 LLM 費用計測の実装スライス（Issue #79）

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-04**（AI 判断）／**NFR**（費用統制）
- 関連 IADR: **IADR-0055**（本スライスの設計・イベント計上）／IADR-0027（費用統制）／IADR-0031（poller の照会）／
  IADR-0034（原子的計上・月内直列化）
- Issue: [#79](https://github.com/endazon/ai-stock-trading/issues/79)（実 LLM 費用計測と poller 配線）の「実費用計測」スコープ

## 目的・背景

#125 で取引判断の LLM は platform ゲートウェイ `POST /complete` を呼ぶようになった。その応答トークンを
月次 LLM 費用として `CostControlService` へ計上する。`POST /costs/record` は **OwnerOnly** でサービストークンでは
403 になるため、**イベント計上**（`LlmCostIncurred`）を用いる（IADR-0055 決定1）。

## 対象範囲

**対象（本 PR）**
- `Shared.Contracts.Events.LlmCostIncurred`（`Amount`[円]・`At`。Category=Llm は事象名で含意）。
- trade-decision: `ILlmUsageReporter`＋`LlmUsage`（ポート）／`NoOpLlmUsageReporter`（**既定＝publish しない**）／
  `LlmPricing`（純関数・Domain）／`PublishingLlmUsageReporter`（単価適用＋publish・Worker）／
  `HttpLlmCompletionClient` の reporter 配線（応答 `InputTokens`/`OutputTokens` を計測入力にする）。
- cost-control: `IProcessedMessageStore`（**MessageId 重複排除**・`ICostLedger` とは別物）＋`InMemory`/`Ef` 実装＋
  `ProcessedMessageRow`＋migration／`LlmCostIncurredConsumer`（計上＋しきい値遷移時 `CostThresholdReached` 発行）／
  MassTransit 消費者登録。
- audit: `LlmCostIncurredAuditConsumer`＋`AuditEntryFactory.From(LlmCostIncurred)`。
  **FR-11/IADR-0019「全ドメインイベントを監査台帳へ記録」の担保**（`AuditConsumerCoverageTests` が新規イベントの
  購読漏れを検知するため、新契約イベント追加に伴い必須）。相関は `llm-cost:{yyyy-MM}` の決定的 GUID
  （注文相関を持たないため。`CostThresholdReached` と同系・IADR-0026）。

**対象外**
- **poller 配線**（`GET /costs/state` の間隔延長/停止適用）＝#79 の残スコープ（本 PR では扱わない）。
- #19 バージョン付き月次上限の取得（現状 `DefaultCostLimitsProvider`）。

## 設計（IADR-0055 に従う）

- **計測点は egress**（決定3）: `HttpLlmCompletionClient` が成功応答のトークンを `ILlmUsageReporter` へ渡す。
  既定は `NoOpLlmUsageReporter`（publish しない＝fail-safe）。Worker が `PublishingLlmUsageReporter` を配線する。
- **単価は構成**（決定2）: `LlmPricing:InputPer1kTokens` / `OutputPer1kTokens`（円・**既定 0**）。
  費用 = 入力/出力トークン ÷1000 × 単価。未設定=0 円＝計上しても統制に影響しない（安全既定）。金額 0 でも
  **計上経路は通す**（経路の健全性を保つ・IADR-0055 根拠）。
- **冪等性**（決定5）: 消費者は `context.MessageId` を `IProcessedMessageStore` で重複排除する。
  既処理なら no-op。計上が失敗した場合は**マークを戻して**（`Unmark`）再配信で再試行できるようにする
  （マークのみ残って計上が欠落するのを避ける）。既存 `AuditConsumerHelper.MessageId` と同系の再送耐性パターン。
- **トランザクション入れ子回避／ユニットオブワーク分離**（決定4・IADR-0034）: 消費者は outbox を使わず
  `EfCostLedger` の月内直列化に委ねる。さらに `EfProcessedMessageStore` は**操作ごとに専用の短命 `DbContext`**
  を生成し、台帳（scoped `DbContext`）と **ChangeTracker を共有しない**。共有すると、計上の `SaveChanges` 失敗で
  `Added` のまま残った計上行を `Unmark` の `SaveChanges` が道連れで確定させ得る（claude-review 🔴 指摘）。
- **計測は best-effort**: reporter の失敗は LLM 応答を壊さない（例外を捕捉しログのみ）。取引判断（FR-04）を
  費用計測の失敗で Hold に倒すのは過剰なため。計上漏れは at-least-once 再配信で緩和される。

## 受け入れ基準

- [ ] `LlmPricing` が トークン×単価を正しく算出し、**単価未設定（0）で 0 円**になる（純関数テスト）
- [ ] `NoOpLlmUsageReporter` 既定では publish されない（fail-safe）
- [ ] `PublishingLlmUsageReporter` が単価適用のうえ `LlmCostIncurred` を publish する（harness）
- [ ] `HttpLlmCompletionClient` が成功応答のトークンを reporter へ渡す／reporter 例外が応答を壊さない
- [ ] `LlmCostIncurredConsumer` が `CostCategory.Llm` で計上し、しきい値上方遷移時に `CostThresholdReached` を発行する
- [ ] **同一 `MessageId` の再配信で二重計上しない**（回帰テスト・決定5）
- [ ] 計上失敗時はマークを戻し、再配信で計上できる

## テスト方針

- 単体（xUnit＋FluentAssertions）: 単価純関数／reporter（NoOp・Publishing）／HttpLlmCompletionClient（fake handler）／
  消費者（InMemory ledger＋InMemory processed store。MassTransit テストハーネス）。
- 冪等性は「同一 MessageId で 2 回 Consume → 計上 1 回」を固定する。
- **EF 重複排除ストアは実 `DbContext`（InMemory provider）で検証**し、「台帳の未確定変更を巻き込まない
  （ChangeTracker 非共有）」を回帰テストで固定する（`EfProcessedMessageStoreTests`）。フェイク
  （`ThrowingCostLedger`）では検出できない層のため。
- 実 PostgreSQL に対する統合 E2E（決定4 のアドバイザリロック込みの確認）は #82 の E2E 基盤で別途実施する
  （本リポの統合 E2E は Docker 依存で CI 分離・IADR-0049）。

## 計画書との差異

- なし（IADR-0055 の実装。#79 の残スコープ＝poller 配線は後続）。

## 未決事項

- poller 配線（#79 残）・#19 バージョン付き上限。
- 計上とマークの完全な原子性（同一トランザクション化）は IADR-0055 決定4（outbox 不使用・入れ子回避）と
  トレードオフ。本 PR は「マーク→計上→失敗時 Unmark」で実用上の再試行可能性を担保する。
