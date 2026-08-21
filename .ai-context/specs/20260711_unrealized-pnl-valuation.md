---
title: 含み損益・ドローダウンの時価評価（純関数スライス）
type: spec
status: done
related_ids: [FR-10, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# 仕様書: 含み損益・ドローダウンの時価評価（純関数スライス）

> Issue [#81](https://github.com/endazon/ai-stock-trading/issues/81)（`Refs #12`）。リスク管理 `PortfolioProjection` は `UnrealizedPnl`/
> `DrawdownRatio` を市場データ未連携のため 0 で返していた（IADR-0008/0018）。本スライスは**現在値入力を受けて含み損益・DD を算出する
> 純関数**を実装し CI 緑で完結させる。現在値の**実供給**（市場データ源）は #22/#82 の後続に切り分ける。

## 起点となる計画書・課題（トレーサビリティ）

- FR-10（リスク統制）。IADR-0008: 日次損失上限は `DailyRealizedPnl + UnrealizedPnl` の合算で判定。`DrawdownRatio ≥ MaxDrawdownRatio` で新規建て拒否。
- 課題: `PortfolioProjection` が `UnrealizedPnl`/`DrawdownRatio` を 0 で返すため、日次損失判定は実現分のみ（含み分過小）・DD 判定は無効。
- 関連 IADR: IADR-0008（含み損合算）、IADR-0018（射影・当該項目 0 の暫定）。本作業で新規 [IADR-0036](../adr/IADR-0036_unrealized-pnl-valuation.md)。
- 対象 Issue: #81（`Refs #12`）。

## 対象範囲

### リスク管理ドメイン計算（`PortfolioValuation`・純関数）

- `UnrealizedPnl(positions, currentPrices)`: Σ 建玉 `(現在値 − 平均取得単価) × 符号付き数量`。現在値の無い建玉は 0（フォールバック）。
- `DrawdownRatio(equityHighWaterMark, currentEquity)`: `peak > 0 ? max(0, (peak − currentEquity) / peak) : 0`。

### 射影（`PortfolioProjection.Project`）

- 引数に `currentPrices`（`(Symbol,Market)→現在値`・nullable）と `equityHighWaterMark`（nullable）を追加（**後方互換**・既定 null）。
- `UnrealizedPnl = PortfolioValuation.UnrealizedPnl(建玉, currentPrices)`。`currentEquity = Capital + DailyRealizedPnl + UnrealizedPnl`。
  `DrawdownRatio = PortfolioValuation.DrawdownRatio(equityHighWaterMark, currentEquity)`。
- 既定（null）では従来どおり `UnrealizedPnl=0`/`DrawdownRatio=0`（production の現挙動を保持）。

## 受け入れ基準

CI で緑にする範囲（純関数ユニット）:
- [x] `PortfolioValuation.UnrealizedPnl`: ロング含み益/含み損・ショート・現在値欠損（0）・符号を正しく算出する。
- [x] `PortfolioValuation.DrawdownRatio`: ピークからの下落率（下限 0）・ピーク未指定/非正は 0・下落なしは 0。
- [x] `PortfolioProjection.Project`: `currentPrices` から含み損益を反映し、`equityHighWaterMark` から DD を算出する。
- [x] `Project` の既定（引数省略）は従来どおり `UnrealizedPnl=0`/`DrawdownRatio=0`（回帰）。

実 API/実コンテナ前提（CI 既定では実行しない・#22/#82）:
- [ ] 現在値（日次終値/為替評価）の実供給と `LedgerPortfolioStateProvider` からの結線。
- [ ] エクイティ・ハイウォーターマーク（資金ピーク）の永続追跡（複数日・状態管理）。

## 方式・トレードオフ（明示）

- **含み損益**は現在値入力から純粋に算出できる（IADR-0008 の「含み合算」を活性化する主目的）。現在値欠損の建玉は 0 に倒す（過小・安全側は日次損失が過小評価にならないよう #22 で鮮度担保）。
- **DrawdownRatio** は「資金ピークからの」トレーリング指標であり、真のピーク（高値更新の履歴）は**状態を要する**（純関数では持てない）。本スライスは
  **ピークを入力**として受けて DD を算出する純関数に限定し、ピークの追跡・永続化は #22/#82（ホスト/状態管理）の後続に切り分ける。
- 現在値・ピークの**実供給**は本スライス対象外。`LedgerPortfolioStateProvider` は当面 null を渡し production 現挙動（0）を保持する。

## テスト方針

- `PortfolioValuationTests`: 含み損益（ロング益/損・ショート・欠損・符号）と DD（ピーク下落・ピーク無し・下落なし）を純関数で検証。
- `PortfolioProjectionTests`: `Project` へ現在値/ピークを与えたときの `UnrealizedPnl`/`DrawdownRatio`、引数省略時の 0（回帰）。

## 関連仕様

- 連携元: [20260710_portfolio-projection](20260710_portfolio-projection.md)（#63 射影・IADR-0018）
- 実装ADR: [IADR-0036](../adr/IADR-0036_unrealized-pnl-valuation.md)／[IADR-0008](../adr/IADR-0008_daily-loss-limit-basis.md)

## 未決事項

- 現在値/ピークの実供給・結線（#22/#82）、市場別取引日境界、報告書 `PnlAggregator` との評価入力共有は後続で確定する。
