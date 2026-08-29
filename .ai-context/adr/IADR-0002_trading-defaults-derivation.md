---
title: IADR-0002 TradingDefaults の既定値は全体前提条件からの逆算値として明示する
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-17, FR-19, FR-20]
author: endazon (with Claude Code)
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# IADR-0002: TradingDefaults の既定値は全体前提条件からの逆算値として明示する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-08
- 決定者: endazon（利用者）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-10（リスク統制）、FR-17（前提条件の一元管理）、FR-19（取引ガード）、FR-20（段階ゲート）
- 一次情報: 全体前提条件（`05_trading-assumptions.md` §5）、要求定義（`01_requirements.md`）
- 関連する実装仕様書: [20260708_risk-guard-core](../specs/20260708_risk-guard-core.md)
- 対象コード: [`TradingDefaults.cs`](../../backend/Services/RiskManagementService/Domain/TradingDefaults.cs)

## コンテキストと課題

`TradingDefaults.CreateRiskLimits()` の一部の既定値は、全体前提条件 §5 の既定値表に「数値そのもの」として
記載がなく、初期投入資金（100,000 円）と 1 取引リスク・損切り幅の目安記述からの**実装者による逆算値**である。
どの値が計画書に明記された値で、どの値が逆算値かが不明瞭だと、後日の見直し時に根拠を辿れない。

該当する逆算値:

| 既定値 | 数値 | 逆算根拠 |
| --- | --- | --- |
| `MaxOrderAmount` | 35,000 円 | 1 取引リスク 1% × 損切り幅 3% の目安から 1 ポジション約 3.3 万円 → 切り上げの上限 |
| `MaxDailyOrderAmount` | 100,000 円 | 当日の発注累計は初期投入資金（100,000 円）を超えない、という前提から |
| `MaxOpenPositions` | 3 | 「2〜3 銘柄に分散」の目安の上限側 |

計画書に明記のある値（`DailyLossLimitRatio`=2%、`PerTradeRiskRatio`=0.5〜1%、`MaxDrawdownRatio`=10〜15%、
連敗縮小 3〜5 でサイズ半減）は逆算ではなく、範囲指定に対し保守側を既定として採用したもの。

## 検討した選択肢

1. **逆算値をコード内コメントのみで管理する** — 追跡性が弱く、値の意味が散逸する
2. **逆算値であることと根拠を IADR で明示し、コードから参照させる** — 追跡性が高く、計画への環流判断もしやすい

## 決定

選択肢 2 を採用する。`TradingDefaults` の逆算値は本 IADR に根拠を集約し、コードのヘッダコメントから
本 IADR を参照する。値は `TradingDefaults` の一箇所に集約し、`RiskEvaluatorTests` の境界値テストで固定する。

## 理由

- 逆算値は前提条件の変更（初期資金・リスク方針の見直し）で連動して変わるため、根拠の明示が保守に必須
- 計画書 §5 に数値表として明記されていない点は、`/plan-feedback` で計画側へ「既定値表への追記」を提案できる

## 結果

- 良い影響: 各既定値の出所（計画書明記 / 逆算）が判別でき、見直し時の判断が容易
- 悪い影響・トレードオフ: 前提条件が更新された場合、本 IADR と `TradingDefaults` の両方の更新が必要
- フォローアップ: 逆算値（35,000 / 100,000 / 3）を計画書 §5 の既定値表へ明記する提案を `/plan-feedback` で起票する

## 関連

- Supersedes: なし
- Superseded by: なし
