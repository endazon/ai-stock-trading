---
title: IADR-0017 取引判断サービスは LLM をポートで抽象化し、方針/LLM 不在は取引しない安全既定・サイジングは残枠 min で行う
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-07, FR-10, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# IADR-0017: 取引判断サービスの構成（LLM 抽象・安全既定・残枠 min サイジング）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-04（AI 判断）、FR-07（確定前方針では取引しない）、FR-10（サイジング）、ADR-0003（方針階層＋独立リスク管理）
- 対象 Issue: [#11](https://github.com/endazon/ai-stock-trading/issues/11)
- 関連する実装仕様書: [20260710_trade-decision-core](../specs/20260710_trade-decision-core.md)
- 関連 IADR: [IADR-0003](IADR-0003_position-sizing-responsibility.md)（サイジング責務）、[IADR-0004](IADR-0004_position-effect-entry-scoping.md)（PositionEffect）、[IADR-0013](IADR-0013_platform-foundation-testsupport-shim.md)（shim）

## コンテキストと課題

取引判断サービスは LLM（非決定的）で売買判断を行い、`PositionSizer` で数量を確定して `TradeDecisionMade` を発行する。
LLM 連携（platform LLM ゲートウェイ）・確定済み日報（#14）・保有/残枠（#12/#13）はいずれも未実装または外部依存で、CI で
実行できない。資金を扱うため、これらが揃わない状態で誤って発注意図を作らない安全設計が要る。また `PositionSizer` は
`RiskManagementService.Domain` にあり（IADR-0003）、サイジングの `availableCapital` に段階資金残枠だけでなく日次発注残枠も
含めるかが未決だった（#11 コメント）。構成方針を決める必要がある。

## 検討した選択肢

1. **LLM/データを直結で実装** — CI 不能・外部依存で壊れやすく、未確定入力で発注意図を作る危険。
2. **LLM・外部データをポートで抽象化し、安全既定（不在なら取引しない）＋ fake で CI 緑** — `ILlmCompletionClient` /
   `IDailyPolicyProvider` / `ISizingContextProvider` を定義。実 LLM（platform `/complete`）・実データは後続で差し替える。

## 決定

選択肢 2 を採用する。

- **LLM 抽象**: `ILlmCompletionClient.CompleteAsync(prompt)`（生成テキスト）で抽象化。プロンプト構築
  （`TradeDecisionPromptBuilder`）と構造化解析（`TradeDecisionParser`）は判断サービス内に置き単体テストする。実 LLM は
  platform LLM ゲートウェイ `POST /complete`（`CompletionRequest(Prompt, MaxTokens, Model)`）を呼ぶ HTTP 実装で、後続。
- **安全既定（取引しない）**: (a) 確定済み日報が無ければ取引しない（FR-07）。(b) LLM が `Hold`／解析不能なら取引しない。
  (c) 数量 0（リスク予算/上限で見送り）なら取引しない。ホストのプレースホルダ（LLM=Hold・方針=なし）により、実 LLM/実データが
  揃うまで**発注意図を一切作らない**。
- **サイジング（IADR-0003）**: `PositionSizer.CalculateCappedQuantity` を用い、`availableCapital` に
  **段階資金残枠（`CapitalCap − InvestedCapital`）と日次発注残枠（`MaxDailyOrderAmount − DailyOrderedAmount`）の小さい方**を
  渡す（#11 コメントの未決を確定）。`sizeFactor` は `PositionSizer.GetSizeFactor(連敗, DD, limits)`。発注意図は
  `PositionEffect.Open`（新規建て・IADR-0004）で組み立てる。
- **`PositionSizer` の参照**: `TradeDecisionService.Application` は `RiskManagementService.Domain`（`PositionSizer`・
  `RiskLimitSettings`・`TradingDefaults`）を参照する。サイジングは判断側の責務（IADR-0003）でありロジックの単一情報源を
  複製しないため。将来サイジングを共有ドメインへ切り出す余地は残す。
- **対象外（後続）**: 多数決（同一入力複数回）・二段判断（軽量→本判断）・RAG（#8）・費用統制（#23）・定時サイクル（#21）。

## 理由

- LLM・外部データをポート化し安全既定に倒すことで、未確定入力で誤発注意図を作らずに CI で中核ロジックを検証できる。
- 残枠 min の採用は、段階資金上限と日次発注上限の両方を発注前に尊重し、`RiskEvaluator` での二重拒否（サイジング→拒否ループ）を
  避ける（IADR-0003 追記の趣旨）。
- `PositionSizer` を参照するのは、サイジングロジックを判断サービスへ複製せず単一情報源に保つため。

## 結果

- 良い影響: 実 LLM/実データ未了でも中核ループ（トリガー→判断→サイジング→発行）を CI で緑にでき、安全（取引しない既定）。
  サイジング結合（IADR-0003 フォローアップ）をテストで担保。
- 悪い影響・トレードオフ: `TradeDecisionService.Application` が `RiskManagementService.Domain` に依存する（サービス間の
  ドメイン結合）。将来サイジングを共有ドメイン化する場合の移動候補。実判断は実 LLM 実装まで動かない（安全側で意図どおり）。
- フォローアップ: 実 LLM クライアント（`/complete`）・多数決・二段判断・RAG・費用統制・定時サイクル・実データ供給・監査永続。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0003](IADR-0003_position-sizing-responsibility.md)、[IADR-0004](IADR-0004_position-effect-entry-scoping.md)
