---
title: TradeDecisionMade のキュー名衝突を解消し、取引判断の取りこぼしを止める（Issue #258・IADR-0106）
type: spec
status: review
related_ids:
  - FR-03
  - FR-10
  - UC-01
  - UC-02
  - ADR-0013
  - IADR-0010
  - IADR-0014
  - IADR-0106
author: claude
created: 2026-07-27
updated: 2026-07-27
related_specs:
  - "../adr/IADR-0106_consumer-endpoint-name-uniqueness.md"
  - "../adr/IADR-0010_risk-service-layering-and-slicing.md"
  - "../adr/IADR-0014_market-monitor-events-and-boundary.md"
  - "../../docs/functional/FR-10_risk-controls.md"
  - "../../docs/tests/FR-10_risk-guard-core-tests.md"
---

# 仕様書: TradeDecisionMade のキュー名衝突解消（Issue #258）

## 起点となる計画書（トレーサビリティ）

- 要求: FR-03（市場監視・基準値更新）／FR-10（リスク統制＝発注前の決定的検証）。
- ユースケース: UC-01・UC-02（取引判断 → 承認/拒否 → 発注）。
- 決定: [IADR-0106](../adr/IADR-0106_consumer-endpoint-name-uniqueness.md)（本作業で新規）。購読責務の出所は [IADR-0010](../adr/IADR-0010_risk-service-layering-and-slicing.md)（リスク管理の層構成）・
  [IADR-0014](../adr/IADR-0014_market-monitor-events-and-boundary.md)（市場監視のイベント契約と責務境界）。
- メッセージング方針: [[ADR-0013]]（**Wolverine 移行と Kafka 併用に追随**。Accepted 2026-07-25）。
  現行実装の根拠だった platform `ADR-0003`（MassTransit + RabbitMQ）は platform `ADR-0027` により Superseded。
  **本作業は MassTransit の既定命名を前提とするため、Wolverine 移行時に再検証が要る**
  （[IADR-0106](../adr/IADR-0106_consumer-endpoint-name-uniqueness.md)「⚠️ Wolverine 移行時の再検証」）。
- Issue: #258。増幅要因（MSP 側の重複デプロイ）は endazon/microservices-platform#407。

## 背景と問題（実測）

取引フェーズ2 検証で、`TradeDecisionMade` が `OrderApproved` / `OrderRejected` / error / skipped の
**いずれにも現れず消失**する事象を観測した。RabbitMQ 上で `TradeDecisionMade` キューの **consumers=4**。

`RiskManagementService`（承認/拒否の判定）と `MarketMonitorService`（基準値更新）が、どちらも
`TradeDecisionMade` を購読する consumer を持ち、**両方とも `TradeDecisionMadeConsumer` という同じクラス名**
だった。全 Worker は `cfg.ConfigureEndpoints(ctx)` を `IEndpointNameFormatter` 未設定で呼ぶため、
MassTransit 8.4.1 の既定 `DefaultEndpointNameFormatter` が**クラス名のみ（namespace 非包含）**から
キュー名を導き、両サービスが**同一キュー `TradeDecisionMade` を宣言**していた。

**実測での裏取り**: `DefaultEndpointNameFormatter.Instance.Consumer<T>()` を xUnit で評価したところ、
MarketMonitor 側の `TradeDecisionMadeConsumer` に対して **`"TradeDecisionMade"`** を返した。
観測されたキュー名と完全に一致する。

MarketMonitor 側の consumer は `baselineStore.SetBaseline(...)` して ack するだけで何も発行しないため、
round-robin でそちらへ渡った取引判断は承認も拒否も error も出さずに**消える**。これが観測された
「無言の取りこぼし」の正体である。

他の 38 consumer が無事だったのは、`*AuditConsumer` / `*NotificationConsumer` / `*LedgerConsumer` /
`*ActivityConsumer` と接尾辞で**偶然**分離されていたためにすぎず、同じ罠は全 consumer に潜在していた。

## 方針

**`IConsumer<T>` 実装のクラス名は、サービスを跨いで一意にする**（詳細と代替案の棄却理由は [IADR-0106](../adr/IADR-0106_consumer-endpoint-name-uniqueness.md)）。

1. `MarketMonitorService` の `TradeDecisionMadeConsumer` を **`TradeDecisionMadeBaselineConsumer`** へ改名する。
   `RiskManagementService` 側は据え置き（既存キュー `TradeDecisionMade` をそのまま使い続けるため**孤児キューが出ない**）。
2. 各サービスのキュー名を xUnit で固定する（`DefaultEndpointNameFormatter` を直接評価＝実測で固定）。
3. `scripts/check-consumer-endpoint-names.js` でサービス跨ぎの衝突を CI で止める。

## 変更対象

| ファイル | 変更 |
| --- | --- |
| `.../MarketMonitorService.Worker/Composable/Steps/TradeDecisionMadeConsumer.cs` | `TradeDecisionMadeBaselineConsumer.cs` へ改名。命名がキュー識別子である旨をコメントで明記 |
| `.../MarketMonitorService.Worker/Program.cs` | `AddConsumer<>` と説明コメントを改名に追随 |
| `.../MarketMonitorService.Worker.Tests/{MonitorWorkerWebApplicationFactory,PositionStoreSelectionTests}.cs` | 参照を追随 |
| `.../MarketMonitorService.Worker.Tests/TradeDecisionMadeConsumerTests.cs` | `TradeDecisionMadeBaselineConsumerTests.cs` へ改名 |
| `.../MarketMonitorService.Worker.Tests/ConsumerEndpointNameTests.cs` | 新規。キュー名の分離を固定 |
| `.../RiskManagementService.Worker.Tests/ConsumerEndpointNameTests.cs` | 新規。本サービスのキュー名固定＋サービス内一意性 |
| `scripts/check-consumer-endpoint-names.js` | 新規。サービス跨ぎのキュー名衝突を検査 |
| `scripts/scripts.test.js` | 上記検査ロジックの単体テストを追加 |
| `.github/workflows/ci.yml` | `consumer-endpoint-names` ジョブを追加 |
| `docs/adr/IADR-0106_*.md` | 新規（決定） |

## 受け入れ基準（Issue #258 より写像）

| # | 基準 | 検証方法 |
| --- | --- | --- |
| AC1 | RiskManagement と MarketMonitor が別々のキュー名で `TradeDecisionMade` を購読する | `ConsumerEndpointNameTests`（両サービス）。`TradeDecisionMade` / `TradeDecisionMadeBaseline` |
| AC2 | `TradeDecisionMade` を発行すると必ず承認か拒否が発行される（消失ゼロ） | 既存の `TradeDecisionMadeConsumerTests`（RiskManagement）＋ live 検証（#258 手順） |
| AC3 | 同一の `TradeDecisionMade` が MarketMonitor の基準値更新にも取りこぼしなく届く | `TradeDecisionMadeBaselineConsumerTests` |
| AC4 | サービス毎に一意なキュー名前空間が構造的に保証される | `check-consumer-endpoint-names.js`（CI 必須）。修正前ツリーで exit 1・修正後 exit 0 を確認 |
| AC5 | fail-safe 既定・実弾 OFF が不変。retry / デッドレターの挙動を変えない | `UseAiStockTradingRetry` 無変更。取引系既定値は無変更 |
| AC6 | キュー名変更に伴う影響を明記する | 本仕様書「移行と運用影響」＋ [IADR-0106](../adr/IADR-0106_consumer-endpoint-name-uniqueness.md)「影響」 |

## テスト方針（TDD）

1. **実測でハイポセシスを確定**: `DefaultEndpointNameFormatter.Instance.Consumer<TradeDecisionMadeConsumer>()` を
   xUnit で評価し、`"TradeDecisionMade"`（＝RiskManagement 側と同一）が返ることを確認した。
2. **RED**: `check-consumer-endpoint-names.js` を改名前のツリーへ実行し、
   `TradeDecisionMade` の衝突 1 件を検出して exit 1 になることを確認した。
3. **GREEN**: 改名後に exit 0。`ConsumerEndpointNameTests` がキュー名の分離を固定する。

## 移行と運用影響

- 新キュー `TradeDecisionMadeBaseline` はデプロイ時に MassTransit が自動生成・バインドする。
- 旧キュー `TradeDecisionMade` は `RiskManagementService` が引き続き使うため**削除不要・孤児化しない**。
- デプロイ瞬間の in-flight 分が旧キューに残った場合、それは `RiskManagementService` が処理する
  （＝承認/拒否は出る。MarketMonitor の基準値更新のみ取りこぼす）。基準値は次の監視ポーリングで復元され、
  実弾は OFF のため安全側であり、特別な移行手順は不要と判断する。

## 未対応・別課題

- **原因A（MSP 側の重複デプロイ）**: endazon/microservices-platform#407。本 PR とあわせて初めて
  `TradeDecisionMade` の consumers が各 1 になる。**単独では取りこぼしは半減に留まる。**
- 全サービスへの `IEndpointNameFormatter` プレフィックス導入は、40 本のキュー移行を伴うため見送った
  （[IADR-0106](../adr/IADR-0106_consumer-endpoint-name-uniqueness.md) 案B の棄却理由）。同一ブローカに別プロダクトが同居する等、名前空間分離が実際に
  必要になった時点で再検討する。
