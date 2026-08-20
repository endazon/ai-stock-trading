---
title: IADR-0036 含み損益は現在値入力から純関数で算出し、DD はピーク入力から算出する（実供給は後続）
type: impl-adr
status: Accepted
related_ids: [FR-10, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# IADR-0036: 含み損益は現在値入力から純関数で算出し、DD はピーク入力から算出する（実供給は後続）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-11
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-10（リスク統制・日次損失上限/最大DD）、ADR-0003
- 対象 Issue: [#81](https://github.com/endazon/ai-stock-trading/issues/81)（`Refs #12`）
- 関連する実装仕様書: [20260711_unrealized-pnl-valuation](../specs/20260711_unrealized-pnl-valuation.md)
- 関連 IADR: [IADR-0008](IADR-0008_daily-loss-limit-basis.md)（日次損失＝実現＋含みの合算）、[IADR-0018](IADR-0018_portfolio-ledger-projection.md)（射影・当該項目 0 の暫定）

## コンテキストと課題

`PortfolioProjection` は `UnrealizedPnl`/`DrawdownRatio` を市場データ未連携のため 0 で返しており、IADR-0008 の「日次損失＝実現＋含みの合算」
判定が実現分のみ（含み過小）、`DrawdownRatio ≥ MaxDrawdownRatio` の新規建て拒否が無効だった。時価評価の算出ロジックを CI 緑で先に用意する必要がある。

## 決定

- **含み損益（`UnrealizedPnl`）は現在値入力から純関数で算出する**: `Σ 建玉 (現在値 − 平均取得単価) × 符号付き数量`。現在値の無い建玉は 0
  （フォールバック）。報告書 `PnlAggregator` の評価損益と同一の式（符号付き）で、日次損失の含み合算（IADR-0008）を活性化する主目的。
- **ドローダウン（`DrawdownRatio`）はピーク入力から純関数で算出する**: `peak > 0 ? max(0, (peak − 現在エクイティ) / peak) : 0`。
  現在エクイティ = `Capital + DailyRealizedPnl + UnrealizedPnl`。**真のピーク（高値更新の履歴）は状態を要し純関数では持てない**ため、
  ピークは**入力**として受け、ピークの追跡・永続化はホスト/状態管理（#22/#82）の後続に切り分ける。
- **`PortfolioProjection.Project`** に `currentPrices`・`equityHighWaterMark` を追加（nullable・既定 null）。**既定では従来どおり 0**
  （production 現挙動を保持）。現在値・ピークの**実供給**（市場データ源・エクイティ追跡）は #22/#82 の後続。

## 理由

- 含み損益は現在値さえあれば純粋・決定的に算出でき、IADR-0008 の安全判定（含み合算）を最小変更で活性化できる。
- DD のピークは本質的に状態（時系列の高値更新）を要するため、純関数の責務は「ピークが与えられたときの DD 算出」に限定するのが honest。
  ピーク追跡を分離することで、算出ロジックを CI 緑で検証し、状態管理は実基盤連携（#22/#82）に切り分けられる。
- 引数 nullable・既定 null により後方互換（現挙動 0 を保持）で段階移行できる。

## 結果

- 良い影響: 含み損益・DD の算出ロジックが純関数として整い、全面テスト可能。現在値/ピークを供給すれば IADR-0008 の含み合算・DD 判定が活性化する。
- 悪い影響・トレードオフ: 本スライス単体では production 挙動は 0 のまま（実供給が未結線）。DD のピーク追跡は状態管理が別途必要。
  現在値欠損の建玉は含み 0 に倒れる（鮮度は供給側 #22 で担保）。
- フォローアップ: 現在値の実供給（日次終値/為替・#22/#82）、エクイティ・ハイウォーターマークの永続追跡、`LedgerPortfolioStateProvider` 結線、
  報告書 `PnlAggregator` との評価入力共有・重複整理。

## 関連

- Supersedes: なし（IADR-0018 の「当該項目 0」を算出ロジック導入で更新）
- Superseded by: なし
- 関連: [IADR-0008](IADR-0008_daily-loss-limit-basis.md)（含み合算）、[IADR-0018](IADR-0018_portfolio-ledger-projection.md)（射影）
