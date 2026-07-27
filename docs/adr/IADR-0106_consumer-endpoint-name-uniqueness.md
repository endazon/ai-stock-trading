---
title: IADR-0106 consumer クラス名はキュー名であり、サービス跨ぎで一意にする
type: impl-adr
status: Accepted
related_ids:
  - FR-03
  - FR-10
  - UC-01
  - UC-02
  - ADR-0003
  - IADR-0011
author: claude
created: 2026-07-27
updated: 2026-07-27
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ (ADR-0003 イベント駆動アーキテクチャ)"
---

# IADR-0106: consumer クラス名＝キュー名（サービス跨ぎの一意性）

- 状態: Accepted
- 日付: 2026-07-27
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-03（市場監視・基準値）／FR-10（リスク統制）／UC-01・UC-02（取引判断→発注）
- 関連 ADR: [[ADR-0003]]（イベント駆動アーキテクチャ）／[[IADR-0011]]（MassTransit + RabbitMQ）
- 関連仕様書: `docs/specs/20260727_issue-258_consumer-endpoint-name-collision.md`
- Issue: #258（bug）。増幅要因（MSP 側の重複デプロイ）は endazon/microservices-platform#407

## コンテキストと課題

取引フェーズ2 検証で、`TradeDecisionMade` が `OrderApproved` / `OrderRejected` / error / skipped の
**いずれにも現れず消失**する事象を観測した。RabbitMQ 上で `TradeDecisionMade` キューの **consumers=4**。

原因は **consumer クラス名の衝突**である。

- `RiskManagementService`（承認/拒否の判定）と `MarketMonitorService`（基準値更新）が、それぞれ
  `TradeDecisionMade` を購読する consumer を持ち、**両方とも `TradeDecisionMadeConsumer` という同じクラス名**
  だった（namespace は異なる）。
- 全 Worker は `cfg.ConfigureEndpoints(ctx)` を **`IEndpointNameFormatter` 未設定**で呼ぶ。MassTransit 8.4.1 の
  既定 `DefaultEndpointNameFormatter` は、**エンドポイント名（＝キュー名）を consumer クラス名のみから導き、
  namespace を含まない**（末尾の `Consumer` を落とす）。
- 実測（`DefaultEndpointNameFormatter.Instance.Consumer<T>()` を xUnit で評価）でも、両サービスの consumer が
  ともに **`"TradeDecisionMade"`** を返すことを確認した。観測されたキュー名と一致する。
- 結果、**pub/sub のつもりの 2 サービスが 1 本のキューを共有し competing consumer になる**。RabbitMQ は
  round-robin で配送するため、MarketMonitor 側へ渡った取引判断は `baselineStore.SetBaseline(...)` だけして
  ack され、承認も拒否も error も出さずに**消える**。

これは「クラス名の付け方」の問題ではない。**クラス名がそのまま分散システムのキュー識別子になる**という
MassTransit の既定の帰結であり、命名は機能要件である。他の 38 consumer が無事だったのは、
`*AuditConsumer` / `*NotificationConsumer` / `*LedgerConsumer` / `*ActivityConsumer` と接尾辞で**偶然**
分離されていたためにすぎない。同じ罠は全 consumer に潜在していた。

## 決定

**`IConsumer<T>` 実装のクラス名は、サービスを跨いで一意にする。クラス名には「そのサービスにおける関心事」を
含め、同じイベントを複数サービスが購読する場合でも別々のキューになるようにする。**

1. `MarketMonitorService` の `TradeDecisionMadeConsumer` を **`TradeDecisionMadeBaselineConsumer`** へ改名する
   （キュー名 `TradeDecisionMade` → `TradeDecisionMadeBaseline`）。`RiskManagementService` 側は
   `TradeDecisionMadeConsumer`（キュー名 `TradeDecisionMade`）のまま変更しない。
2. 各サービスのキュー名を xUnit で固定する（`ConsumerEndpointNameTests`）。`DefaultEndpointNameFormatter` を
   直接評価するため、MassTransit の挙動に対する思い込みではなく**実測**で固定される。
3. `scripts/check-consumer-endpoint-names.js` でツリー全体を走査し、サービス跨ぎのキュー名衝突を CI で止める
   （`--self-test` 内蔵・`scripts.test.js` から単体テスト・外部依存ゼロ）。

## 根拠

- **改名は 1 キューしか動かさない**。`RiskManagementService` 側を据え置くため、既存キュー `TradeDecisionMade` は
  そのまま使われ続け、**孤児キュー（consumer の居ないまま滞留するキュー）が発生しない**。新設されるのは
  `TradeDecisionMadeBaseline` の 1 本だけである。
- **`Baseline` は恣意的な接尾辞ではなく、この consumer の関心事そのもの**である（基準値＝baseline の更新）。
  `RiskManagement` 側の関心事（承認/拒否の判定）とは明確に異なり、名前が役割を語る。
- **検査は実行時ではなく静的に行える**。キュー名はクラス名から決まるため、ソース走査で完全に判定できる。
  実クラスタや broker への接続を CI に持ち込まずに再発を止められる。

## 検討した代替案

### 案B: 全サービスに `IEndpointNameFormatter` のサービス毎プレフィックスを設定する（棄却）

`x.SetEndpointNameFormatter(new DefaultEndpointNameFormatter(prefix: "market-monitor", includeNamespace: false))`
のように、サービス毎のプレフィックスで名前空間を分ける。衝突は**構造的に不可能**になる。

**棄却理由**: 効果に対して代償が大きい。**40 本すべてのキュー名が変わる**ため、
稼働中のブローカには旧名のキューが**consumer 不在のまま残り**、exchange へのバインディングも生きているので
**メッセージが滞留し続ける**（ディスクを食い、監視上も紛らわしい）。手動のキュー削除手順が恒久的に必要になる。
一方で得られるものは、静的検査（案A に含む）でも同等に担保できる。40 本のキュー移行を伴う変更は、
実際に名前空間の分離が必要になった時点（例: 同一ブローカに別プロダクトが同居する）で行うべきである。

### 案C: `includeNamespace: true` にする（棄却）

namespace 込みでキュー名を一意化する。**棄却理由**: 案B と同じく全キュー名が変わるうえ、
`AiStockTrading.MarketMonitor.Worker.Composable.Steps.TradeDecisionMade` のような長大で
リファクタリング（namespace 変更）に脆いキュー名になる。

### 案D: 検査のみ入れて改名しない（棄却）

**棄却理由**: 検査は再発を防ぐだけで、**現に発生している取りこぼしを止めない**。

## 影響

- **キュー名**: `MarketMonitorService` の取引判断購読が `TradeDecisionMade` → `TradeDecisionMadeBaseline` へ。
  他 39 consumer のキュー名は不変。`RiskManagementService` のキュー名も不変。
- **デプロイ時**: 新キュー `TradeDecisionMadeBaseline` が自動生成される（MassTransit がバインドまで行う）。
  旧 `TradeDecisionMade` は `RiskManagementService` が引き続き使うため**削除不要**。
  デプロイの瞬間に MarketMonitor 側の in-flight メッセージが旧キューに残った場合、それは
  `RiskManagementService` が処理する（＝承認/拒否は出る。基準値更新のみ取りこぼす）。実弾 OFF・
  基準値は次サイクルのポーリングで復元されるため、安全側であり移行手順は不要と判断する。
- **挙動**: `TradeDecisionMade` は本来意図されたとおり **pub/sub** になり、RiskManagement と MarketMonitor が
  **それぞれ全件**受け取る。取りこぼしはゼロになる。
- **fail-safe / 実弾**: 不変。`UseAiStockTradingRetry`（2s/10s/30s の3回）とデッドレター退避も不変。
- **CI**: `consumer-endpoint-names` ジョブが 1 つ増える（外部依存ゼロ・Node のみ・数秒）。

## 前提（本 ADR の範囲外）

本 ADR は AST 内部のキュー名衝突（原因B）を扱う。取引フェーズ2 検証で観測された consumers=4 のうち残り 2 は、
MSP 側が同じ 3 サービスを `microservices-platform` namespace にも重複デプロイしていたことによる
（endazon/microservices-platform#407・原因A）。**両方が解消して初めて consumers が各 1 になる。**
