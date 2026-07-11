---
title: 費用計上の並行 read-modify-write に行ロック/トランザクションを導入
type: spec
status: review
related_ids: [NFR, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
---

# 仕様書: 費用計上の並行 read-modify-write を原子化する

> Issue [#78](https://github.com/endazon/ai-stock-trading/issues/78)（`Refs #23`）。`CostControlService.Record` の
> read-modify-write（計上前状態→追記→計上後状態でしきい値の上方遷移を検知）は現状トランザクション/行ロックを取らず、
> `#79`（実 LLM 費用の自動計上）で並行計上が繋がるとしきい値通知（`CostThresholdReached`）の重複/取りこぼしが起こり得る。
> 計上と before/after 読み取りを月単位で原子化し、しきい値遷移を高々 1 回に保つ。

## 起点となる計画書・課題（トレーサビリティ）

- NFR（費用）: LLM 月次上限 80% 間隔延長・100% 停止（05_trading-assumptions §6）。しきい値到達で `CostThresholdReached` を発行。
- ADR-0001（Database per Service）。関連 IADR: IADR-0027（費用統制・並行性フォローアップ）。本作業で新規 [IADR-0034](../adr/IADR-0034_cost-concurrency-lock.md)。
- 対象 Issue: #78（`Refs #23`）。

## コンテキストと課題

`CostControlService.Record` は `ledger.GetMonthlyTotal(before)` → `ledger.Record(append)` → `ledger.GetMonthlyTotal(after)` の
**3 呼び出し**で上方遷移を判定する。並行計上でこれらが交錯すると、before/after が他の計上を跨ぎ、`CostThresholdReached` が
**重複発行**または**取りこぼし**になり得る（IADR-0027 の claude-review フォローアップ）。

## 対象範囲

### Application（`CostControlService`）

- ポート `ICostLedger.Record` の戻り値を `LlmCostRecordOutcome(decimal LlmTotalBefore, decimal LlmTotalAfter)` にする
  （**追記と当該月 LLM 累計の before/after を原子的に返す**）。呼び出し側は before/after から遷移を判定する。
- `CostControlService.Record` は `ledger.Record` の outcome から before/after 状態を評価し `CrossedTo` を決める（別々の総計読み取りを廃止）。
  非 LLM 計上は before==after（LLM 累計不変）で `CrossedTo` は null になる。

### Adapters

- `InMemoryCostLedger`: `Lock` で RMW（before 読み取り→追記→after 読み取り）を直列化して outcome を返す（テストで並行性の意味論を検証可能）。
- `EfCostLedger`（Worker）: **トランザクション＋月単位の PostgreSQL アドバイザリロック**（`pg_advisory_xact_lock`・トランザクション終了で自動解放）で
  並行計上を月単位に直列化し、before/after を原子的に返す。非リレーショナル（テストの InMemory EF）はプロセス内ロックで代替する。

## 受け入れ基準

CI で緑にする範囲（ユニット・InMemory ledger の並行性）:
- [ ] `ICostLedger.Record` が LLM 累計の before/after を返す。
- [ ] `CostControlService.Record` が outcome から遷移を判定し、しきい値通知（`CrossedTo`）が上方遷移時のみ返る（挙動不変・回帰）。
- [ ] `InMemoryCostLedger` の並行計上（N 並列 Record）でしきい値遷移が **80%/100% それぞれ高々 1 回**（重複/取りこぼしなし）。
- [ ] `EfCostLedger.Record` が before/after を返し、月・カテゴリ別集計は従来どおり（InMemory EF）。

実 API/実コンテナ前提（CI 既定では実行しない）:
- [ ] 実 PostgreSQL でのアドバイザリロックによる並行計上の直列化（真の行/ロック競合は実 DB でのみ再現・#82）。

## 対象外（後続）

- 実 LLM 費用計測・poller 配線（#79）。#82 実コンテナ E2E での実 DB ロック検証。分散環境の複数レプリカ検証。

## テスト方針

- `InMemoryCostLedger`/`CostControlService` を N 並列 `Record` で走らせ、`CrossedTo==Throttled`/`Halted` が各々ちょうど 1 回であることを検証（Lock で直列化・決定的）。
- `EfCostLedger.Record` の before/after 戻り値・集計を InMemory EF で検証（アドバイザリロックの実効は実 DB=#82）。

## 関連仕様

- 連携元: [20260710_cost-control](20260710_cost-control.md)（#23・CostControlService/CostGovernor）
- 実装ADR: [IADR-0034](../adr/IADR-0034_cost-concurrency-lock.md)／[IADR-0027](../adr/IADR-0027_cost-control.md)

## 未決事項

- 実 DB でのロック検証・#79 の自動計上連携は後続で確定する。
