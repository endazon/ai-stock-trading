---
title: 報告書サービス FR-16 Slice（損益集計のコア・PnlAggregator 純関数）
type: spec
status: review
related_ids: [FR-16, FR-17, FR-06, FR-07, ADR-0003, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/04_report-templates.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# 仕様書: 損益集計のコア（PnlAggregator）

> Issue [#14](https://github.com/endazon/ai-stock-trading/issues/14)（FR-16・Must）の集計スライス。報告書テンプレート
> （`04_report-templates.md` の数値定義）に従い、**損益・費用・税をコードで集計**する純関数 `PnlAggregator` を実装する
> （数値は LLM に計算させない）。前提条件（#19・`CostCalculator`/税率）と取引台帳（約定列）を入力とする。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-16（テンプレート準拠・数値はコード集計）。関連 FR-17（前提条件バージョン）・FR-06/07（報告書）
- 技術検討: `04_report-templates.md`「数値の定義（全報告書で統一）」、`05_trading-assumptions.md` §4（概算費用関数）
- ADR: ADR-0003（数値はコードで集計）、ADR-0001（新規サービス）
- 関連 IADR: 本作業で新規 [IADR-0025](../adr/IADR-0025_pnl-aggregation.md)。前提条件は [IADR-0021](../adr/IADR-0021_trading-assumptions-configuration.md) を参照
- 対象 Issue: #14（FR-16 集計スライス。[IADR-0024](../adr/IADR-0024_report-confirmation-and-policy.md) の後続）

## 目的・背景

報告書 Slice A（#69）は確定管理と方針テキストを実装したが、テンプレートの数値（損益・費用・税）は未集計だった。FR-16 は
「数値はコードで集計し LLM に計算させない」を要求する。本スライスは損益集計の核となる純関数を実装する。取引台帳の実データ源
（#63 の約定台帳連携）と #19 のバージョン付き前提条件取得はサービス間連携（#22）の後続に切り分け、本スライスは**入力を受け取る
純関数と照会エンドポイント**に限定する。

## 対象範囲（報告書サービス `ReportService`）

- **Domain**（`ConfigurationService.Domain` を参照＝前提条件 `TradingAssumptions`・概算費用関数 `CostCalculator`）:
  - `PeriodTradeFill`（集計入力の約定: 銘柄・市場・方向・建玉効果・数量・約定単価・約定時刻）。
  - `PnlSummary`（集計結果: 実現損益[税引前]・費用合計・源泉徴収税額・実現損益[税引後・費用込み]・評価損益[税引前]・約定件数・決済件数）。
  - `PnlAggregator`（純関数）: 約定列を平均取得単価法で畳み込み、テンプレート数値定義どおりに集計する。
    - 実現損益(税引前) = Σ（約定代金差額＝(決済単価 − 取得単価)×決済数量×方向符号）。
    - 費用合計 = Σ `CostCalculator.EstimateOneWayCost`（手数料＋為替スプレッド）。
    - 源泉徴収税額 = max(0, 実現損益(税引前) − 費用合計) × 譲渡益税率（利益にのみ課税）。
    - 実現損益(税引後・費用込み) = 実現損益(税引前) − 費用合計 − 源泉徴収税額。
    - 評価損益(税引前・参考) = Σ 建玉（現在値 − 平均取得単価）×数量（現在値は入力。無い銘柄は 0）。
- **Worker**: `POST /reports/pnl-summary`（OwnerOnly・body＝約定列＋任意の現在値）→ `PnlSummary`。前提条件は暫定で
  `TradingAssumptionsDefaults`（#19 のバージョン付き前提条件取得は #22 後続）。

## 受け入れ基準

CI で緑にする範囲（ユニット＋WebApplicationFactory）:
- [ ] 利益決済で実現損益(税引前)・費用・税・実現損益(税引後)がテンプレート定義どおり算出される。
- [ ] 損失決済では源泉徴収税額 0・実現損益(税引後)＝税引前−費用（負値）になる。
- [ ] 費用合計が `CostCalculator`（前提条件の手数料・為替スプレッド）と一致する。
- [ ] 評価損益は現在値入力から (現在値 − 平均取得単価)×数量 で算出され、現在値の無い建玉は 0 扱い。
- [ ] `POST /reports/pnl-summary` が OwnerOnly（401/403）で集計サマリを返す。
- [ ] 既存テストを緑に保つ。

実 API/実コンテナ前提（CI 既定では実行しない）:
- [ ] #63 台帳・#19 前提条件を実接続した期間集計の E2E。

## 対象外（後続）

- 取引台帳（約定列）の実データ源＝#63 の約定台帳連携（本スライスは約定列を入力で受ける）。
- #19 のバージョン付き前提条件の取得（本スライスは既定値）。両者はサービス間連携（#22）で結線する。
- NISA 非課税区分・損益通算/繰越・外国税額控除（FR-18 将来拡張）。為替の実レート円換算（前提条件は率近似・IADR-0021）。
- 報告書への集計サマリの永続付与・テンプレート Markdown 生成・週報/月報の粒度別集計。

## テスト方針

- `PnlAggregator` は純関数として単体検証（利益/損失/費用/税/評価損益/複数銘柄）。
- エンドポイントは `ReportWorkerWebApplicationFactory`（TestAuthHandler）で OwnerOnly・集計結果を検証。

## 関連仕様

- 先行: [20260710_report-confirmation](20260710_report-confirmation.md)（報告書 Slice A）
- 参照: [20260710_configuration-assumptions](20260710_configuration-assumptions.md)（前提条件・`CostCalculator`）
- 実装ADR: [IADR-0025](../adr/IADR-0025_pnl-aggregation.md)

## 未決事項

- 台帳連携・前提条件取得（#22）、報告書への集計付与・Markdown 生成・粒度別集計は後続スライスで確定する。
