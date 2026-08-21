---
title: IADR-0033 符号付き在庫・平均取得単価法の畳み込みを Shared.Contracts.Trading の純関数へ集約する
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-16, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/01_architecture-overview.md
---

# IADR-0033: 符号付き在庫・平均取得単価法の畳み込みを Shared.Contracts.Trading の純関数へ集約する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-11
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-10（リスク管理の射影）、FR-16（報告書の損益集計）、ADR-0001（Database per Service）
- 対象 Issue: [#77](https://github.com/endazon/ai-stock-trading/issues/77)（`Refs #7`）
- 関連する実装仕様書: [20260711_inventory-fold-dedup](../specs/20260711_inventory-fold-dedup.md)
- 関連 IADR: [IADR-0018](IADR-0018_portfolio-ledger-projection.md)（PortfolioProjection）、[IADR-0025](IADR-0025_pnl-aggregation.md)（PnlAggregator・重複解消フォローアップ）

## コンテキストと課題

符号付き在庫・平均取得単価法の畳み込みが 2 箇所に重複実装されている（意図的な同方式踏襲・IADR-0025 claude-review フォローアップ）。

- `RiskManagementService.Application`: `PortfolioProjection.Apply`（#63 台帳射影）
- `ReportService.Domain`: `PnlAggregator.Apply`（損益集計）

両者は論理的に同一で、方式変更時に片側だけ更新されると会計がドリフトする。単一情報源へ集約する必要があるが、
Database per Service（ADR-0001）を崩さず、過剰な共通化も避ける必要がある。

## 検討した選択肢

1. **`Shared.Contracts.Trading` に純関数として置く（採用）** — 両サービスが既に `Shared.Contracts.Trading`（`TradeSide`/`Market`）を
   参照済み。新規プロジェクト・DB 越境なしで共有でき、最小差分。`Trading` は既に `OrderIntent.Notional` 等の軽い純ドメインロジックを含む。
2. **新規共有ドメインライブラリを作る** — 分離は綺麗だが、1 つの小さな純関数に対しプロジェクト追加（csproj/slnx/Directory.Build）が過剰。
3. **一方のサービス Domain が所有し他方が参照** — サービス間のドメイン結合（例: 報告書→リスク管理ドメイン）を生み、責務が不明瞭。

## 決定

**選択肢 1** を採用する。

- `Shared.Contracts.Trading` に `SignedInventory`（静的・純関数）を置く。
  - `InventoryLot(int Quantity, decimal AverageCost)`＝符号付き在庫（+ ロング / − ショート）と平均取得単価。
  - `InventoryFillResult(InventoryLot Lot, decimal RealizedPnl, bool Reduced)`＝適用後ロット・減少分の実現損益・在庫が減少したか。
  - `Apply(InventoryLot current, int signedQuantity, decimal price) -> InventoryFillResult`（純関数・不変・`ref` を用いない）。
- `PortfolioProjection`（実現損益のみ使用）・`PnlAggregator`（実現損益＋減少フラグ使用）の私有 `Apply` を削除し、`SignedInventory.Apply` を呼ぶ。**挙動は不変**。
- **限定**: 集約は畳み込みロジック（1 約定の在庫適用）のみ。サービス固有の集計（実現損益の合計・費用/税・当日境界・連敗・当日発注累計など）は各サービスに残す（過剰共通化を避ける）。

## 理由

- 既存の参照関係（両者 → `Shared.Contracts.Trading`）を使い、新規プロジェクト・DB 越境なしで単一情報源化できる。
- 純関数のため決定的・全面テスト可能で、方式変更時のドリフトを構造的に防げる。
- 純関数（`ref` なし・不変レコード）にすることで呼び出し側の意図が明確になり、共有による結合を最小化する。

## 結果

- 良い影響: 平均取得単価法の畳み込みが単一情報源になり、リスク管理と報告書の会計整合が保証される（片側ドリフト解消）。
- 悪い影響・トレードオフ: `Shared.Contracts` に軽いドメインロジックが増える（`Trading` の既存方針＝軽い純ドメイン primitives の範囲内）。
- フォローアップ: なし（本 IADR で IADR-0025 のフォローアップ「畳み込みの集約」を解消する）。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0018](IADR-0018_portfolio-ledger-projection.md)、[IADR-0025](IADR-0025_pnl-aggregation.md)（本 IADR で重複解消フォローアップを完了）
