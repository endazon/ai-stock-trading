---
title: IADR-0034 費用計上の並行 RMW は原子的な台帳メソッド＋月単位アドバイザリロックで直列化する
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
---

# IADR-0034: 費用計上の並行 RMW は原子的な台帳メソッド＋月単位アドバイザリロックで直列化する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-11
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: NFR（費用・LLM 月次上限）、ADR-0001（Database per Service）
- 対象 Issue: [#78](https://github.com/endazon/ai-stock-trading/issues/78)（`Refs #23`）
- 関連する実装仕様書: [20260711_cost-concurrency-lock](../specs/20260711_cost-concurrency-lock.md)
- 関連 IADR: [IADR-0027](IADR-0027_cost-control.md)（費用統制・並行性フォローアップを本 IADR で解消）

## コンテキストと課題

`CostControlService.Record` は before 総計読み取り→追記→after 総計読み取りの 3 呼び出しで LLM 上限のしきい値上方遷移を判定する。
トランザクション/ロックが無いため、並行計上で before/after が他の計上を跨ぎ、`CostThresholdReached` の重複発行/取りこぼしが起こり得る。
`#79` で LLM ゲートウェイからの自動計上が繋がると並行度が上がり顕在化する（IADR-0027 の claude-review フォローアップ）。

## 検討した選択肢

1. **原子的な台帳メソッド＋月単位アドバイザリロック（採用）**: `ICostLedger.Record` が追記と before/after LLM 累計を原子的に返す。
   EF は `pg_advisory_xact_lock(key(month))` をトランザクション内で取得し月単位に直列化。追加テーブル不要・トランザクション終了で自動解放。
2. **`SELECT ... FOR UPDATE` 行ロック**: 月の行をロックするが、当月 0 行のケースでロック対象が無く扱いが煩雑。ロック用ダミー行/テーブルが要る。
3. **トランザクション分離を Serializable に上げる**: 直列化異常時に再試行が要り、呼び出し側の複雑化とスループット低下。個人利用規模には過剰。

## 決定

**選択肢 1** を採用する。

- `ICostLedger.Record(month, category, amount, at)` の戻り値を `LlmCostRecordOutcome(LlmTotalBefore, LlmTotalAfter)` にする。
  **追記と当該月 LLM 累計の before/after を 1 つの原子的操作で返す**。しきい値遷移の判定入力を計上と不可分にする。
- `CostControlService.Record` は outcome の before/after から状態を評価し `CrossedTo` を決める（別々の総計読み取りを廃止）。
- **EfCostLedger**: トランザクション内で `pg_advisory_xact_lock(AdvisoryKey(month))` を取得 → before 読み取り → 追記 → after 読み取り → commit。
  同月への並行計上は直列化され、しきい値遷移は各しきい値につき高々 1 回。`AdvisoryKey` は `"yyyy-MM"` から決定的な bigint（`year*100+month`）を導出し、
  **プロセス跨ぎで安定**（複数レプリカでも同月は同キーで直列化。`string.GetHashCode` はプロセス毎ランダムのため用いない）。
- **非リレーショナル（テストの InMemory EF）**: アドバイザリロック/実トランザクション非対応のため、プロセス内 `lock` で代替直列化する。
- **InMemoryCostLedger**: `Lock` で before→追記→after を直列化して outcome を返す（並行性の意味論を CI で決定的に検証できる）。
- **前提（呼び出し側の契約）**: `Record` はアンビエント（既に開始済みの）トランザクション内から呼ばない。EF 実装が自前でトランザクションを
  開始するため、入れ子になると例外になる。#79 の自動計上連携時に他のユニットオブワークと組み合わせないこと（`ICostLedger.Record` の XML doc に明記）。

## 理由

- しきい値遷移の判定を計上と原子化することで、直列化下では各しきい値の上方遷移が構造的に高々 1 回になる（重複/取りこぼし解消）。
- アドバイザリロックはスキーマ変更・ダミー行が不要で、当月 0 行でも機能し、トランザクション終了で自動解放される。個人利用規模に対し過不足ない。
- 決定的キーによりプロセス跨ぎ（複数レプリカ）でも同月が直列化される。

## 結果

- 良い影響: 並行計上でも `CostThresholdReached` が各しきい値につき高々 1 回。#79 の自動計上連携に対し安全。
- 悪い影響・トレードオフ: 同月計上は直列化されスループットが月単位で制限される（費用計上は低頻度のため許容）。実 DB でのロック実効は
  実コンテナ E2E（#82）でのみ再現可能（CI は InMemory の直列化で意味論を検証）。
- フォローアップ: 実 DB E2E（#82）、#79 の自動計上連携。本 IADR で IADR-0027 の並行性フォローアップを解消する。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0027](IADR-0027_cost-control.md)（本 IADR で並行性フォローアップを完了）
