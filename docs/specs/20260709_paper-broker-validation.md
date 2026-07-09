---
title: PaperBrokerAdapter の入力検証と証券会社拒否状態（OrderStatus.Rejected）
type: spec
status: review
related_ids: [FR-05, FR-12, ADR-0002]
author: endazon (with Claude Code)
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
---

# 仕様書: PaperBrokerAdapter の入力検証と証券会社拒否状態

> Issue #30 の是正。ペーパーブローカーが不正注文を検証せず約定させる問題と、`OrderStatus` に証券会社拒否が
> ない問題を解消する作業仕様。

## 起点・課題

- 起点 ID: FR-05（発注執行・注文状態追跡）、FR-12（ペーパートレード）、ADR-0002（証券会社選定）
- 対象 Issue: #30
- 課題:
  1. `PaperBrokerAdapter.PlaceOrderAsync` が数量 0 以下・価格 0 以下の注文もそのまま `Filled` にする。
     実ブローカーなら拒否される注文がペーパーで成功し、Stage 0/1 の検証価値を損なう（FR-12 の「フロー同一」に反する）。
  2. `OrderStatus` に証券会社による拒否（Rejected）がない。moomoo 実発注では資金不足・値幅制限等の
     ブローカー拒否が普通に発生するが、表現・追跡・通知する状態がない。

## 対象範囲

- `OrderStatus.Rejected`（証券会社拒否）を追加する。
- `PaperBrokerAdapter.PlaceOrderAsync` に数量・価格の検証を追加し、不正注文を約定させず `Rejected` の終端注文
  として記録・返却する（例外にしない）。`Rejected` を終端状態として `CancelOrderAsync` の取消不可に含める。
- 証券会社拒否（`OrderStatus.Rejected`）とリスク事前拒否（`Events.OrderRejected`）の区別を IADR で明文化する。
- 上記の単体テスト。
- 対象外: moomoo アダプタ実装、拒否理由コードの体系化、値幅制限・資金不足等の実ブローカー固有の再現。

## 受け入れ基準

- [x] 数量 0 以下・価格 0 以下の注文がペーパーで約定せず `Rejected`（FilledQuantity=0, AveragePrice=0）になる
- [x] 正常な注文は従来どおり `Filled` になる
- [x] `Rejected` 注文は終端状態として取消できない
- [x] 証券会社拒否とリスク事前拒否の区別が IADR で明文化される
- [x] `dotnet build` / `dotnet test` が全緑

## テスト方針

- 数量・価格の不正パターン（0・負）を `[Theory]` で網羅し、`Rejected`・FilledQuantity=0・取消不可を固定する。

## 計画書との差異

- 差異なし。FR-05 の注文状態追跡（受付・約定・失注・取消）に「証券会社拒否」を追加し、FR-12 の
  「ペーパーと実発注のフロー同一」を強化する是正。
