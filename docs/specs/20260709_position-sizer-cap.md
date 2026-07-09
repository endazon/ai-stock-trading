---
title: ポジションサイジングの金額上限キャップ（サイジング→拒否ループの解消）
type: spec
status: review
related_ids: [FR-10, UC-01, UC-02, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# 仕様書: ポジションサイジングの金額上限キャップ

> Issue #29 の是正。`PositionSizer` のリスク予算ベース株数が 1 注文金額上限・資金を超過し、
> `RiskEvaluator` で必ず拒否される（サイジング→拒否ループ）問題を解消する作業仕様。

## 起点・課題

- 起点 ID: FR-10（リスク統制・ポジションサイジング）、UC-01/UC-02（取引サイクル）、ADR-0003（AI ガードレール）
- 対象 Issue: #29
- 課題: `PositionSizer.CalculateQuantity` は「リスク予算 ÷ 損切り幅」のみで株数を返す。損切り幅が浅いと
  想定金額が `MaxOrderAmount`・資金を系統的に超過し、`RiskEvaluator` で必ず拒否される。取引機会の空振りと
  監査ログのノイズ。IADR-0003 は「上限との突き合わせを誰がやるか」を未定義だった。

## 対象範囲

- `PositionSizer` に `CalculateCappedQuantity(capital, perTradeRiskRatio, stopLossDistancePerShare,
  referencePrice, maxOrderAmount, availableCapital, sizeFactor)` を追加する。リスク予算基準の株数と、
  金額上限（1 注文金額上限・利用可能資金の小さい方）を参照価格で割った株数の小さい方を返す。
- IADR-0003 に「金額上限とのキャップは呼び出し側がサイジング時に行う」責務を追記する。
- 上記の単体テスト。
- 対象外: `RiskEvaluator` の変更（責務不変）、取引判断サービスの結線（後続スライス）。

## 設計判断

- ADR-0003 / IADR-0003 の責務分担（サイジング＝判断側、上限検証＝リスク側）を維持する。`RiskEvaluator` は
  変更しない。呼び出し側がサイジング時にキャップを適用するための primitive を `PositionSizer` に足すのみ。
- `availableCapital` には段階資金上限の残枠（`CapitalCap - InvestedCapital`。IADR-0005）等を渡す想定。
- 参照価格が正でない注文は 0 株（見送り）とする。

## 受け入れ基準

- [ ] リスク予算基準が金額上限内なら従来どおりの株数を返す
- [ ] 損切り幅が浅い場合、想定金額（株数 × 参照価格）が 1 注文金額上限を超えない株数にキャップされる
- [ ] 利用可能資金が 1 注文金額上限より小さい場合は資金基準でキャップされる
- [ ] 参照価格がゼロ以下なら 0 株を返す
- [ ] `dotnet build` / `dotnet test` が全緑

## テスト方針

- キャップ適用の境界（リスク予算基準 vs 金額上限基準の切替点）と、価格 0 以下の見送りを `[Fact]` で固定する。

## 計画書との差異

- 差異なし。IADR-0003 の責務境界を保ったまま、サイジング primitive にキャップ機能を追加する是正。
