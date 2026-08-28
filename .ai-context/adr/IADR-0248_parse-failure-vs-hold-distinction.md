---
title: IADR-0248 LLM 構造化出力の解析不能と見送り（Hold）を区別して記録する（挙動は Hold のまま不変）
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-11, ADR-0003, IADR-0039, IADR-0104, IADR-0248]
author: claude (Claude Code)
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# IADR-0248: 解析不能と見送りを区別して記録する

- 状態: Accepted
- 日付: 2026-08-28
- 決定者: claude（起票 #337。#290 を吸収）

## 起点・関連

- 関連する計画書 ID: FR-04（判断根拠の必須記録）・FR-11（監査）・ADR-0003（不確実なら取引しない）
- 関連する実装仕様書: [`20260828_337_trading-cycle-and-screening.md`](../specs/20260828_337_trading-cycle-and-screening.md)

## コンテキストと課題

`TradeDecisionParser` は解析不能・不正出力・LLM の見送りを**すべて同一の Hold**（根拠文字列
「解析不能**または**見送り」）へ潰していた——この文字列自体が #290 の指摘（区別しない）の実装である。

両者の挙動（取引しない）は同じでよいが、意味が違う。**解析不能は出力の形の問題**（プロンプト・
モデル改定・ゲートウェイの退行を示す信号）であり、**見送りは LLM の判断そのもの**（設計上の正常な
結果・TradeDecisionSkipped の規律と同じ）。混同すると、構造化出力の退行が監査上「見送りが増えた」
としか見えず、原因究明が壊れる。

## 決定

1. **`TradeDecisionParser.ParseDetailed` が失敗種別を返す。** 種別は 空出力 / JSON 抽出不能 /
   JSON 不正 / action 不明 / **値の不変量違反**（価格・損切り幅——解析はできたが成立しない＝幻覚の
   疑い。解析不能系に分類する）。**解析できた上での Hold には Failure が付かない**（対の肯定形）。
2. **安全既定と互換 API は不変。** `Parse` は `ParseDetailed(...).Decision` の互換ラッパであり、
   すべての失敗は従来どおり Hold（取引しない）へ倒れる。区別は記録だけを変える。
3. **記録は FR-11 ログで区別する。** `DecisionOrchestrator` は一次スクリーニング・二次の各票で
   解析不能を Warning（種別・詳細つき）で残し、`OrchestratedDecision` に `UnparseableVotes` /
   `ScreeningUnparseable` を載せて `TradeDecisionService` の FR-11 ログ行へ出す。Hold は
   `TradeDecisionMade` を発行しないため **FR-11 ログが唯一の監査記録**である（IADR-0104 決定6 の
   既存判断に従う。イベント新設はしない——見送り側と粒度を揃える）。
4. **解析不能票は Hold 票として多数決へ入れる**（従来挙動）。件数だけを別に数える——票から除くと
   VoteCount の意味（同一入力 N 回）が壊れ、全票解析不能のときの挙動が未定義になる。

## 結果

- 良い影響: 構造化出力の退行が「解析不能 N 件」として監査ログから直接読める。
- 残余リスク: 記録はログであり台帳（AuditService）ではない。台帳化が要るなら見送り（IADR-0104）と
  併せて別途起こす（片方だけイベント化すると粒度が割れる）。
