---
title: 損切り価格の権威データ化（OrderIntent 拡張・台帳永続化で近似を実値化）
type: spec
status: done
related_ids: [FR-03, FR-04, FR-10, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# 仕様書: 損切り価格の権威データ化

> Issue [#83](https://github.com/endazon/ai-stock-trading/issues/83)（`Refs #22`）。市場監視の損切りライン検知に供給する保有ポジション
> （PR #75・IADR-0030）は、損切り価格を **既定比率 3% の近似** で導出していた（権威データが契約に無かったため）。取引判断が決定した
> 損切り価格を発注/約定パイプラインで永続化し、`open-positions` の近似を**実値**に置き換える。

## 起点となる計画書・課題（トレーサビリティ）

- FR-04（取引判断が損切り価格＝ATR 連動 `stopLossDistancePerShare` を決める）、FR-03（市場監視の損切りライン検知）、FR-10（保有・損切り監視）。
- 課題: `OrderIntent`/`OrderApproved`/#63 台帳（`LedgerFill`）に損切り価格が無く、IADR-0030 は 3% 近似で暫定運用していた。
- 関連 IADR: IADR-0018（契約最小化＝本作業で当該項目のみ見直し）、IADR-0030（近似の過渡的措置）。本作業で新規 [IADR-0035](../adr/IADR-0035_stop-loss-authoritative.md)。
- 対象 Issue: #83（`Refs #22`）。

## 対象範囲

### 契約（`Shared.Contracts.Trading.OrderIntent`）

- `OrderIntent` に `decimal? StopLossPrice = null` を追加（**後方互換**・既定 null）。`TradeDecisionMade`→`OrderApproved` を通じて損切り価格を運ぶ。

### 取引判断（`TradeDecisionService`）

- `DecideAsync` で損切り価格を算出し `OrderIntent.StopLossPrice` に設定する。
  - ロング（Buy）: `ReferencePrice − StopLossDistancePerShare`／ショート（Sell）: `ReferencePrice + StopLossDistancePerShare`。

### リスク管理（#63 台帳・射影）

- `ApprovedOrderRow` に `StopLossPrice`（nullable numeric）列を追加（EF マイグレーション）。`EfPortfolioLedgerStore`/`InMemoryPortfolioLedgerStore` で
  承認 Intent の損切り価格を保持・`LedgerFill.StopLossPrice` に補完する。
- `PortfolioProjection.ProjectOpenPositions`: 建玉に **最新の同方向エントリー（新規/建て増し/反転）の損切り価格**を持たせる
  （一部決済では保持・全決済で消滅）。`OpenPosition.StopLossPrice`（nullable）で公開。
- `OpenPositionsService`: 損切り価格が**存在すれば実値**、無ければ従来の**3% 近似にフォールバック**（レガシー建玉・欠損時）。

## 受け入れ基準

CI で緑にする範囲（ユニット・InMemory EF）:
- [x] `TradeDecisionService` が Buy/Sell で `ReferencePrice ∓ StopLossDistancePerShare` を `StopLossPrice` に設定する。
- [x] `OrderIntent.StopLossPrice` が `OrderApproved` 経由で台帳（`ApprovedOrderRow`）に永続化される（EF/InMemory 双方）。
- [x] `ProjectOpenPositions` が最新同方向エントリーの損切り価格を建玉に反映する（建て増しで更新・一部決済で保持・全決済で消滅）。
- [x] `OpenPositionsService` が実値を返し、損切り価格が無い建玉のみ 3% 近似にフォールバックする。
- [x] 既存テストを緑に保つ（`OrderIntent` 追加は後方互換）。

実 API/実コンテナ前提（CI 既定では実行しない）:
- [ ] 実 PostgreSQL でのマイグレーション適用・実パイプライン E2E（#82）。

## 対象外（後続）

- 両建て（ロング/ショート別ロット）別々の損切り管理（現物ネッティングでは net 1 建玉）。損切り価格の履歴・変更追跡。実 LLM 由来の損切り妥当性検証。

## テスト方針

- `TradeDecisionServiceTests`: Buy/Sell の `StopLossPrice` 算出。
- `PortfolioProjectionTests`: `ProjectOpenPositions` の損切り価格（建て増し更新・一部決済保持・反転更新）。
- `OpenPositionsServiceTests`: 実値使用・欠損時 3% 近似フォールバック。
- 台帳ストア（Ef/InMemory）の損切り価格 round-trip。

## 関連仕様

- 連携元: [20260711_position-store-wiring](20260711_position-store-wiring.md)（#22・IADR-0030 の近似）、[20260710_portfolio-projection](20260710_portfolio-projection.md)（#63 台帳）
- 実装ADR: [IADR-0035](../adr/IADR-0035_stop-loss-authoritative.md)／[IADR-0030](../adr/IADR-0030_position-store-sync-api.md)／[IADR-0018](../adr/IADR-0018_portfolio-ledger-projection.md)

## 未決事項

- 実 DB マイグレーション適用・実 E2E（#82）は後続で確定する。両建て別ロット会計は信用有効化（ADR-0007/#50）後。
