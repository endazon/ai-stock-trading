---
title: 平均取得単価法の畳み込み重複を共有ドメインへ集約
type: spec
status: done
related_ids: [FR-10, FR-16, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
---

# 仕様書: 平均取得単価法の畳み込み重複を共有ドメインへ集約する

> Issue [#77](https://github.com/endazon/ai-stock-trading/issues/77)（`Refs #7`）。符号付き在庫・平均取得単価法の畳み込みが
> `RiskManagementService` の `PortfolioProjection.Apply` と `ReportService` の `PnlAggregator.Apply` に**重複実装**されている
> （意図的な同方式踏襲・IADR-0025 claude-review フォローアップ）。方式変更時の片側ドリフトを避けるため単一情報源へ集約する。

## 起点となる計画書・課題（トレーサビリティ）

- FR-10（リスク管理の保有・実現損益射影）/ FR-16（報告書の損益集計）。両者が同一の在庫会計を用いる。
- ADR-0001（Database per Service）: **純関数の共有のみ**とし、サービス跨ぎの DB 参照は作らない。
- 関連 IADR: IADR-0018（PortfolioProjection）・IADR-0025（PnlAggregator・重複解消フォローアップ）。本作業で新規 [IADR-0033](../adr/IADR-0033_shared-inventory-fold.md)。
- 対象 Issue: #77（`Refs #7`）。

## コンテキストと課題

`PortfolioProjection.Apply(ref pos, signedQ, price) -> decimal Realized` と `PnlAggregator.Apply(ref pos, signedQ, price) -> (decimal Realized, bool Reduced)`
は**論理的に同一**（建て増し＝加重平均・実現なし／反対方向＝減少分に実現損益計上／反転）。片方だけ変更すると会計がドリフトする。

## 対象範囲

### 共有（`AiStockTrading.Shared.Contracts.Trading`）

- `SignedInventory`（静的・**純関数**）＋ `InventoryLot(int Quantity, decimal AverageCost)` ＋ `InventoryFillResult(InventoryLot Lot, decimal RealizedPnl, bool Reduced)`。
  - `Apply(InventoryLot current, int signedQuantity, decimal price) -> InventoryFillResult`。両呼び出し元の要求（実現損益・減少フラグ・更新後ロット）を満たす。
  - 配置理由: 両サービスが既に `Shared.Contracts.Trading`（`TradeSide`/`Market`）を参照済みで、新規プロジェクト・DB 越境を作らずに共有できる（IADR-0033）。

### 呼び出し元の差し替え（挙動不変）

- `PortfolioProjection.Project` / `ProjectOpenPositions`: 私有 `Apply` を削除し `SignedInventory.Apply` を用いる（実現損益は既存どおり、減少フラグは未使用）。
- `PnlAggregator.Aggregate`: 私有 `Apply` を削除し `SignedInventory.Apply` を用いる（実現損益＋減少フラグ）。

## 受け入れ基準

- [x] `SignedInventory.Apply` が建て（0→建玉）・建て増し（加重平均・実現0）・一部決済（実現計上・在庫減）・全決済（0）・反転（実現＋新規建て）を正しく畳み込む。
- [x] `PortfolioProjection` / `PnlAggregator` が `SignedInventory` を用い、私有 `Apply` を持たない（単一情報源）。
- [x] 既存の `PortfolioProjectionTests` / `PnlAggregatorTests` / 依存テストがすべて緑（**挙動不変**）。
- [x] 過剰な共通化はしない（畳み込みロジックの単一情報源化に限定・サービス固有の集計は各サービスに残す）。

## 対象外

- サービス固有の集計（実現損益の合計・費用/税・当日境界・連敗など）は各サービスに残す。DB 越境・新規共有プロジェクトは作らない。

## テスト方針

- `SignedInventoryTests`（`Shared.Infrastructure.Tests`・Contracts を参照）で純関数の各分岐を直接検証。
- 既存の射影/集計テストで呼び出し元の挙動不変を担保（回帰）。

## 関連仕様

- 連携元: [20260710_portfolio-projection](20260710_portfolio-projection.md)（#63 射影）、[20260710_report-confirmation](20260710_report-confirmation.md)（PnlAggregator）
- 実装ADR: [IADR-0033](../adr/IADR-0033_shared-inventory-fold.md)／[IADR-0018](../adr/IADR-0018_portfolio-ledger-projection.md)／[IADR-0025](../adr/IADR-0025_pnl-aggregation.md)
