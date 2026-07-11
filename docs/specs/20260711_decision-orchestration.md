---
title: 取引判断の多数決・二段（一次スクリーニング→二次本判断）オーケストレーション（fake LLM でテスト可能なポート抽象）
type: spec
status: review
related_ids: [FR-04, FR-11, UC-01, UC-02, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# 仕様書: 取引判断の多数決・二段オーケストレーション

> Issue [#11](https://github.com/endazon/ai-stock-trading/issues/11)（FR-04）の一部スライス。[IADR-0017](../adr/IADR-0017_trade-decision-structure.md)
> で「対象外（後続）」と明記した **多数決（同一入力複数回実行）** と **二段判断（軽量スクリーニング→高性能本判断）** の
> オーケストレーションロジックを実装する。LLM 依存は既存の `ILlmCompletionClient` ポートで抽象化したまま、CI は fake（順次出力）で
> 緑にする。**実 LLM 連携（platform LLM ゲートウェイ `/complete`・モデル解決）は本スライスの対象外で後続**。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-04（AI 判断・判断根拠記録）、FR-11（プロンプト・入出力・根拠の記録）
- ユースケース（UC）: UC-01（定時サイクル）、UC-02（価格変動トリガー）
- ADR: ADR-0003（方針階層＋独立リスク管理・不確実なら Hold）
- 技術検討: `06_technical/01_architecture-overview.md`
  - L128: 「取引判断は構造化出力（JSON）とし、**同一入力の複数回実行＋多数決で安定化**する」
  - L129: 「判断の二段化: **軽量モデルによるスクリーニング（対象銘柄の絞り込み）→ 高性能モデルによる本判断**の二段構成とし、費用増を抑える」
  - L34: 「モデル選択・二段判断はゲートウェイの構成で行う」→ 本スライスは**モデル識別子をポート経由でゲートウェイに渡す**形にとどめ、実解決は後続
- 関連 IADR: [IADR-0017](../adr/IADR-0017_trade-decision-structure.md)（多数決・二段を後続と明記）、本作業で新規 [IADR-0037](../adr/IADR-0037_decision-orchestration.md)
- 対象 Issue: #11（残スライスあり・**クローズしない**。`Refs #11`）

## 目的・背景

LLM の売買判断は temperature=0 でも再現しない（`06_daytrading-review.md` §3.1 の実証）。ADR-0003 は「不確実なら Hold」を求める。
非決定性への構造的対策として計画書が要求するのが (a) 同一入力の複数回実行＋多数決による安定化、(b) 軽量→高性能の二段化による費用統制である。
本スライスはこの 2 つの**オーケストレーションロジック**を判断サービス内に実装し、fake LLM で決定的に検証する。実 LLM／実モデル解決は
ゲートウェイ側の後続作業として切り分ける。

## 対象範囲

既存 `TradeDecisionService`（`AiStockTrading.TradeDecision.*`）に、判断集約（ドメイン純関数）とオーケストレータ（アプリ層）を追加する。

### ドメイン（`TradeDecisionService.Domain`・純関数）

- `DecisionAggregator.Aggregate(IReadOnlyList<LlmDecision>) : DecisionVoteResult`
  - **多数決**: 最多得票の `TradeAction` を採る。**同数タイは安全側 `Hold`**（ADR-0003「不確実なら取引しない」）。空入力も `Hold`。
  - **数値の集約**: 勝利 action が `Buy`/`Sell` のとき、勝利票の中から**参照価格の中央値（下側中央値）を持つ代表票**をそのまま採用する
    （`ReferencePrice`・`StopLossDistancePerShare`・`Rationale` を一体で採る）。合成値（frankenstein）を作らず、実在する 1 票の
    根拠（FR-11）を保つ。決定的順序（`ReferencePrice`, `StopLossDistancePerShare`, `Rationale`）でソートし中央を選ぶ。
- `DecisionVoteResult(LlmDecision Decision, int TotalVotes, int AgreementVotes)` — 集約結果＋監査用の票数（FR-11）。

### アプリケーション（`TradeDecisionService.Application`）

- `DecisionOrchestrationOptions`（不変レコード）: `VoteCount`（既定 1・1 以上）／`EnableScreening`（既定 false）／
  `PrimaryModel`（軽量・スクリーニング用モデル識別子）／`SecondaryModel`（高性能・本判断用モデル識別子）。
  `Default`＝1 票・スクリーニング無効・モデル未指定（＝**現行挙動と等価**）。
- `DecisionOrchestrator(ILlmCompletionClient, DecisionOrchestrationOptions, ILogger)`:
  - `DecideAsync(screeningPrompt, decisionPrompt, ct) : OrchestratedDecision`
  - **一次スクリーニング**（`EnableScreening` 時）: 軽量モデル（`PrimaryModel`）で 1 回だけ判断。`Hold` なら**二次を呼ばず**打ち切り
    （費用統制）。`ScreenedOut=true` で返す。
  - **二次本判断（多数決）**: 高性能モデル（`SecondaryModel`）で `VoteCount` 回実行し、各出力を `TradeDecisionParser.Parse` して
    `DecisionAggregator.Aggregate` で集約。`OrchestratedDecision` を返す。
- `OrchestratedDecision(LlmDecision Decision, int TotalVotes, int AgreementVotes, bool ScreenedOut)` — 判断サービスは `.Decision` を
  下流（サイジング）に渡し、票数・スクリーニング結果を FR-11 ログに残す。
- ポート `ILlmCompletionClient.CompleteAsync(prompt, model?, ct)`: **モデル識別子の任意引数を追加**（後方互換の既定 `null`）。
  一次/二次で異なるモデルをゲートウェイへ渡すため（実解決はゲートウェイ後続）。
- `TradeDecisionPromptBuilder.BuildScreening(...)`: 銘柄の**絞り込み**用の軽量プロンプト（同一 JSON スキーマを再利用し
  `TradeDecisionParser` を共有）。
- `TradeDecisionService`: LLM 直呼びをやめ、内部で `DecisionOrchestrator` を構成（`DecisionOrchestrationOptions` を任意注入・
  既定は `Default`＝現行挙動）。二次判断の集約結果を用いて既存のサイジング→`TradeDecisionMade` 発行へ繋ぐ。票数・スクリーニングを FR-11 ログ。

### Worker（`TradeDecisionService.Worker`）

- `PlaceholderLlmCompletionClient` と DI をポート新シグネチャに追随（挙動不変・`Hold` 返却）。
- `DecisionOrchestrationOptions` は構成（`Decision:*`）から供給可能にする。未設定なら `Default`（現行挙動）。

## 受け入れ基準

CI で緑にする範囲（ユニット＋fake LLM。実 LLM/実コンテナ不要）:
- [ ] **多数決**: `[Buy,Buy,Sell]` → `Buy`、`[Sell,Sell,Buy]` → `Sell`、`[Buy,Hold,Hold]` → `Hold`。
- [ ] **タイは安全側 Hold**: `[Buy,Sell]`・`[Buy,Buy,Sell,Sell]`・空入力 → `Hold`。
- [ ] **数値集約**: 勝利票の参照価格中央値を持つ代表票の `ReferencePrice`/`StopLossDistancePerShare`/`Rationale` が採られる。
- [ ] **二段・スクリーニング打ち切り**: 一次が `Hold` なら二次 LLM を呼ばない（呼び出し回数で検証）。`ScreenedOut=true`。
- [ ] **二段・通過**: 一次が `Buy`/`Sell` なら二次を `VoteCount` 回呼び、多数決結果を返す。
- [ ] **モデルルーティング**: 一次は `PrimaryModel`、二次は `SecondaryModel` をポートへ渡す（fake で記録・検証）。
- [ ] `VoteCount` 回、二次プロンプトで LLM が呼ばれる（呼び出し回数の検証）。
- [ ] **既定は現行挙動と等価**: `Default`（1 票・スクリーニング無効）で既存 `TradeDecisionServiceTests` が全て緑のまま。
- [ ] 既存テスト（現行数）を緑に保つ（`dotnet format` 準拠・警告ゼロ）。

実 LLM/実コンテナ前提（CI 既定では実行しない・後続）:
- [ ] platform LLM ゲートウェイ経由の実モデル解決（一次=軽量／二次=高性能）。
- [ ] RabbitMQ E2E（Testcontainers・#24）。

## 対象外（後続）

- 実 LLM クライアント（platform `/complete` HTTP）・実モデル解決（ゲートウェイ構成）。
- RAG（#8）・費用統制の実計測（#23/#79）・確定済み日報/保有の実データ供給（#14/#12/#13）。
- スクリーニングの高度化（複数銘柄の一括絞り込み・銘柄ローテーション）。本スライスは 1 銘柄の一次→二次直列。

## テスト方針

- `DecisionAggregator` は純関数として単体検証（多数決・タイ→Hold・数値集約・空入力）。fake 不要。
- `DecisionOrchestrator` は**順次出力の fake LLM**（呼び出しごとに異なる出力を返し、`(prompt, model)` を記録）で検証。
  非決定性（票の割れ）・スクリーニング打ち切り・モデルルーティング・呼び出し回数を決定的に確認する。
- `TradeDecisionService` の既存テストは `Default` オプションで**挙動不変**を確認（回帰防止）。
- `TradeDecisionPromptBuilder.BuildScreening` は絞り込み文言・JSON スキーマ再利用を確認。

## 関連仕様

- 上位: [20260710_trade-decision-core](20260710_trade-decision-core.md)（Slice A・本スライスの基盤）
- 実装ADR: [IADR-0037](../adr/IADR-0037_decision-orchestration.md)、[IADR-0017](../adr/IADR-0017_trade-decision-structure.md)

## 未決事項

- `VoteCount` の既定値・二段のしきい値・モデル識別子の実値は費用統制（#23/#79）と実 LLM 導入時に確定する。本スライスは構成可能な
  ポイントを用意するにとどめる。
