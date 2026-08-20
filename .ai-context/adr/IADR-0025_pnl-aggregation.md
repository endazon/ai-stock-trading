---
title: IADR-0025 損益集計は前提条件と取引台帳を入力とする純関数で行い、税は利益にのみ課す
type: impl-adr
status: Accepted
related_ids: [FR-16, FR-17, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# IADR-0025: 損益集計は前提条件と取引台帳を入力とする純関数で行い、税は利益にのみ課す

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-16（数値はコード集計）、FR-17（前提条件）、ADR-0003
- 対象 Issue: [#14](https://github.com/endazon/ai-stock-trading/issues/14)（FR-16 集計スライス）
- 関連する実装仕様書: [20260710_pnl-aggregation](../specs/20260710_pnl-aggregation.md)
- 関連 IADR: [IADR-0021](IADR-0021_trading-assumptions-configuration.md)（前提条件・`CostCalculator`）、[IADR-0018](IADR-0018_portfolio-ledger-projection.md)（平均取得単価法の畳み込み）

## コンテキストと課題

FR-16 は損益・費用・税をコードで集計し LLM に計算させないことを要求する。報告書テンプレートは数値定義（実現損益＝約定代金差額
−費用−源泉徴収税、費用合計＝手数料＋諸費用＋為替スプレッド、評価損益＝(現在値−平均取得単価)×数量）を与える。集計の所有・
入力・税の課し方を決める必要がある。取引台帳（約定）は #63、前提条件（手数料/為替/税率）は #19 にあり、サービス間連携を伴う。

## 検討した選択肢

1. **各サービスが個別に集計する** — 集計定義が分散し不整合。
2. **報告書サービスが集計を所有し、前提条件と取引台帳を入力とする純関数で集計する（採用）** — アーキ概要の「損益集計＝報告書」に
   一致。純関数化で決定的・全面テスト可能。台帳/前提条件の取得元は入力として分離し、実データ連携（#22）と独立に検証できる。

## 決定

**選択肢 2** を採用する。

- **損益集計は報告書サービスの純関数 `PnlAggregator`** で行う。入力は約定列（`PeriodTradeFill`）と前提条件（`TradingAssumptions`）
  ＋任意の現在値。数値は LLM に計算させない（ADR-0003）。
- **平均取得単価法**で約定を畳み込み、実現損益(税引前)＝約定代金差額を求める（IADR-0018 と同方式）。
- **費用合計は `CostCalculator`（#19・IADR-0021）** を再利用する（手数料＋為替スプレッド）。数値定義の一貫性を保つ。
- **税は利益にのみ課す**: 源泉徴収税額 = max(0, 実現損益(税引前) − 費用合計) × 譲渡益税率。損失時は 0。実現損益(税引後・費用込み)
  ＝ 実現損益(税引前) − 費用合計 − 源泉徴収税額（テンプレート数値定義に一致）。
- **評価損益(税引前・参考)** は現在値入力から (現在値 − 平均取得単価)×数量 で算出する。現在値の無い建玉は 0（市場データ連携は後続）。
- **取得元の分離**: 台帳（#63）・前提条件（#19）の取得はサービス間連携（#22）で後続に結線する。本スライスは入力を受け取る純関数と
  照会エンドポイント（前提条件は暫定で `TradingAssumptionsDefaults`）に限定する。

## 理由

- 集計を報告書サービスの純関数に集約することで、テンプレート数値定義との一致を決定的に担保し全面テストできる。
- `CostCalculator` の再利用で費用定義を単一の真実源に保てる。税を利益にのみ課すのは日本の譲渡益課税（源泉徴収）に整合。

## 結果

- 良い影響: 損益・費用・税がテンプレート定義どおりコードで集計され、LLM 非依存で検証可能。
- 悪い影響・トレードオフ: 台帳・前提条件の実データ連携（#22）は後続。前提条件は暫定既定値で、手数料/為替が未登録（0）の間は費用・税が
  過小になる（#19 の実額登録で解消）。評価損益は現在値入力に依存（市場データ連携は後続）。NISA/損益通算/外国税額控除は FR-18 将来拡張。
- フォローアップ: #63 台帳・#19 バージョン付き前提条件の結線（#22）、報告書への集計付与・Markdown 生成・粒度別集計、市場データ連携。
- フォローアップ（重複解消・claude-review）: 平均取得単価法の畳み込みは `RiskManagementService` の `PortfolioProjection.Apply`
  と本 `PnlAggregator.Apply` で重複実装していた（意図的な同方式踏襲）。**→ [IADR-0033](IADR-0033_shared-inventory-fold.md) で
  `Shared.Contracts.Trading.SignedInventory`（純関数）へ集約し解消済み（#77）。**

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0021](IADR-0021_trading-assumptions-configuration.md)（`CostCalculator`）、[IADR-0018](IADR-0018_portfolio-ledger-projection.md)（平均取得単価法）
