---
title: IADR-0027 費用統制は専用サービスが月次費用台帳を持ち、純関数で間隔延長/停止を判定する
type: impl-adr
status: Accepted
related_ids: [NFR, FR-17, FR-09, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# IADR-0027: 費用統制は専用サービスが月次費用台帳を持ち、純関数で間隔延長/停止を判定する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: NFR（費用）、FR-17（月次費用上限＝前提条件）、FR-09（停止通知）、ADR-0001
- 対象 Issue: [#23](https://github.com/endazon/ai-stock-trading/issues/23)（Slice A）
- 関連する実装仕様書: [20260710_cost-control](../specs/20260710_cost-control.md)
- 関連 IADR: [IADR-0021](IADR-0021_trading-assumptions-configuration.md)（`MonthlyCostLimits`）、[IADR-0020](IADR-0020_notification-safe-outbound.md)（通知）、[IADR-0019](IADR-0019_audit-log-service.md)（追記専用台帳パターン）
  - 補足: `cost_entries` は月×カテゴリで**追記専用**の台帳であり、構造的には監査ログ（IADR-0019）の追記専用パターンに近い。IADR-0012 の
    「単一行 JSON＋Version 楽観排他」パターンは踏襲しない（専有 DB＝ADR-0001 準拠の点のみ共通）。

## コンテキストと課題

NFR は LLM 費用に月次上限を設け、超過時に定時サイクル間隔を自動延長する（05 §6: LLM 15,000 円・80% で間隔延長・100% で停止）。
費用計測の仕組みが無いと Stage 0 運用開始時から費用暴走リスクがある。費用の集計・統制判定をどこが所有し、どう間隔延長/停止を
表現するかを決める必要がある。実 LLM（#11 プレースホルダ）・poller（#9/#21）はまだ費用を報告/参照していない。

## 検討した選択肢

1. **各サービスが個別に費用を数える** — 集計が分散し月次合算・統制判定が不整合。
2. **専用の費用統制サービスが月次費用台帳を持ち、純関数で統制判定する（採用）** — 費用の集計・上限判定・しきい値通知を一元化。
   純関数化で判定が決定的・全面テスト可能。費用源（LLM 計測）・poller への配線は入力/照会として分離し、実データ連携（#22）と
   独立に検証できる。

## 決定

**選択肢 2** を採用する。

- **新規サービス `CostControlService`**（Domain + Application + Worker）が月次費用台帳（`cost_entries`・専有 DB）を所有する。
- **統制判定は純関数 `CostGovernor`**: 月内 LLM 累計と `MonthlyCostLimits.Llm` から `ratio` を求め、`Normal`（<80%）／
  `Throttled`（≥80% <100%・間隔倍率 2）／`Halted`（≥100%・停止）を返す。しきい値・倍率は 05 §6 に従う。
- **しきい値通知はイベント駆動**: 状態が上方に遷移（Normal→Throttled→Halted）したときのみ `CostThresholdReached` を発行し、通知
  サービスが Discord 通知する（各サービスは Discord を直接呼ばない・IADR-0020）。同一状態内の追加計上では発行しない。
- **費用÷資金レビュー**（`CostReview`・FR-16）を提供し、月報の費用レビューに供給できるようにする（供給配線は後続）。
- **費用源・poller 配線の分離**: 実 LLM 費用計測（platform ゲートウェイ）・poller への間隔延長/停止の配線・#19 のバージョン付き
  上限取得はサービス間連携（#22）で後続に結線する。本スライスは費用計上を API/イベントで受け、統制判定を照会 API で提供する
  （上限は暫定で `TradingAssumptionsDefaults.CostLimits`）。

## 理由

- 費用の集計・上限判定・通知を専用サービスに一元化することで、月次合算と統制判定の一貫性・追跡性を保てる。
- 純関数の判定は決定的で全面テスト可能。しきい値通知をイベント化することで通知・poller など購読者を疎結合に追加できる。

## 結果

- 良い影響: LLM 費用が 80%/100% で間隔延長/停止する判定が成立し、費用暴走を構造的に抑えられる。費用÷資金レビューも供給できる。
- 悪い影響・トレードオフ: 実 LLM 費用計測・poller への配線・#19 上限取得は後続（#22）。上限が暫定既定値で、実 LLM 未実装の間は
  計上源が無い（費用計上 API を用意するに留まる）。EUR→円換算は前提条件の率近似。
- フォローアップ: 実 LLM 費用計測（platform ゲートウェイ）、poller（#9/#21）への間隔延長/停止の配線、#19 バージョン付き上限取得、
  月報への費用レビュー供給（#14）。
- フォローアップ（並行性・claude-review）: `CostControlService.Record` の read-modify-write（before→記録→after で上方遷移を検知）は
  トランザクション/行ロックを取らず、並行計上で `CostThresholdReached` の二重発行・遷移検知漏れが起こり得た。
  **→ [IADR-0034](IADR-0034_cost-concurrency-lock.md) で原子的な台帳メソッド（`ICostLedger.Record` が before/after を返す）＋月単位
  アドバイザリロックにより解消済み（#78）。**

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0021](IADR-0021_trading-assumptions-configuration.md)（`MonthlyCostLimits`）、[IADR-0020](IADR-0020_notification-safe-outbound.md)（通知）
