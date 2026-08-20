---
title: IADR-0007 証券会社拒否は OrderStatus.Rejected で表し、リスク事前拒否（OrderRejected イベント）と区別する
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-12, FR-10, FR-11]
author: endazon (with Claude Code)
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
---

# IADR-0007: 証券会社拒否は OrderStatus.Rejected で表し、リスク事前拒否（OrderRejected イベント）と区別する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-09
- 決定者: endazon（利用者）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-05（発注執行・注文状態追跡）、FR-12（ペーパートレード）、FR-10/FR-11（リスク統制・監査）
- 関連する実装仕様書: [20260709_paper-broker-validation](../specs/20260709_paper-broker-validation.md)
- 対象 Issue: #30
- 対象コード: [`OrderStatus.cs`](../../backend/Shared/AiStockTrading.Shared.Contracts/Trading/OrderStatus.cs)、
  [`PaperBrokerAdapter.cs`](../../backend/Shared/AiStockTrading.Shared.Infrastructure/Composable/Adapters/Broker/PaperBrokerAdapter.cs)、
  [`OrderRejected.cs`](../../backend/Shared/AiStockTrading.Shared.Contracts/Events/OrderRejected.cs)

## コンテキストと課題

2 点の契約・実装の穴があった（Issue #30）。

1. `PaperBrokerAdapter` が不正な注文（数量 0 以下・価格 0 以下）も検証せず `Filled` にしていた。FR-12 は
   「判断・記録・報告のフローは実発注と完全に同一」を要求しており、実ブローカーなら拒否される注文がペーパーで
   成功する差異は Stage 0/1 の検証価値を損なう。
2. `OrderStatus` に証券会社による拒否（Rejected）がなかった。moomoo 実発注では資金不足・値幅制限等で
   ブローカー側拒否が普通に発生するが、それを表現・追跡・通知する状態がなかった。

「拒否」にはリスク管理サービスによる**発注前の事前拒否**（`OrderRejected` イベント。注文はブローカーへ到達
しない）と、注文がブローカーへ到達した後の**証券会社拒否**（終端状態）の 2 種があり、両者を区別する必要がある。

## 検討した選択肢

1. **不正注文で例外を投げる** — 呼び出し側が try/catch を要し、実発注の「拒否も一つの終端状態」という
   フローと非対称になる。監査ログに注文実体が残らない
2. **不正注文を `OrderStatus.Rejected` の終端注文として返す** — 実ブローカーの拒否と同じ表現で、注文実体が
   記録され、`OrderExecuted`（Status 付き）で下流に伝えられる。判断・記録・報告フローが実発注と同一になる

## 決定

選択肢 2 を採用する。

- `OrderStatus` に `Rejected`（証券会社拒否）を追加する。
- `PaperBrokerAdapter.PlaceOrderAsync` は `Quantity > 0 && Price > 0` を検証し、満たさない注文を約定させず
  `Rejected`（FilledQuantity=0, AveragePrice=0）の終端注文として記録・返却する（例外にしない）。
- `Rejected` は終端状態とし、`CancelOrderAsync` は `Filled`/`Cancelled` と同様に取消不可とする。
- **区別の明文化**: `OrderStatus.Rejected` = ブローカーが注文到達後に拒否した終端状態。
  `Events.OrderRejected` = リスク管理サービスが発注前に拒否し、注文がブローカーへ到達しなかったこと。
  両者は別事象であり、監査ログ（FR-11）・通知（FR-09）で別々に扱う。

## 理由

- ペーパーと実発注のフローを同一に保つ（FR-12）ことが Stage 0/1 の検証価値の前提。拒否も終端状態の一つとして
  同じ経路で記録・通知することで、後続の moomoo アダプタと下流処理を差し替えだけで済ませられる。
- 事前拒否と証券会社拒否を型で区別することで、監査・通知の意味づけが曖昧にならない。

## 結果

- 良い影響: 不正注文がペーパーで約定しなくなり、証券会社拒否を状態として追跡・通知できる。moomoo アダプタは
  同じ `OrderStatus.Rejected` を用いればよい。
- 悪い影響・トレードオフ: ペーパーの検証は最小限（数量・価格の正値のみ）。値幅制限・資金不足等の実ブローカー
  固有の拒否理由はペーパーでは再現しない（実アダプタ実装時に拡張）。拒否理由コードの体系化は後続で検討。
- フォローアップ: 発注執行サービス実装時に、`OrderExecuted` の Status 別ハンドリング（Rejected の通知・監査）を実装する。

## 関連

- Supersedes: なし
- Superseded by: なし
