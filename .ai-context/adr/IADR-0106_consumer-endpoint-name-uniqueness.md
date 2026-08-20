---
title: IADR-0106 consumer クラス名はキュー名であり、サービス跨ぎで一意にする
type: impl-adr
status: Superseded
related_ids:
  - FR-03
  - FR-10
  - UC-01
  - UC-02
  - ADR-0013
  - IADR-0010
  - IADR-0014
author: claude
created: 2026-07-27
updated: 2026-08-04
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0013_messaging-follow-wolverine-kafka.md
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md
---

# IADR-0106: consumer クラス名＝キュー名（サービス跨ぎの一意性）

- 状態: **Superseded**（[IADR-0129](./IADR-0129_wolverine-messaging-topology.md) が置き換えた・2026-08-04）
- 日付: 2026-07-27
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-03（市場監視・基準値）／FR-10（リスク統制）／UC-01・UC-02（取引判断→発注）
- 関連 ADR:
  - [[ADR-0013]]（メッセージング基盤の **Wolverine 移行と Kafka 併用に追随する**。Accepted 2026-07-25）
    — 現時点で有効なメッセージング方針。本 ADR は後述「⚠️ Wolverine 移行時の再検証」で従属関係を明記する。
  - platform `ADR-0027`（Wolverine 移行）／platform `ADR-0028`（RabbitMQ + Kafka 併用）
    — 現行実装の根拠だった platform `ADR-0003`（MassTransit + RabbitMQ）は `ADR-0027` により **Superseded**。
  - [IADR-0010](./IADR-0010_risk-service-layering-and-slicing.md)（リスク管理サービスの層構成）／[IADR-0014](./IADR-0014_market-monitor-events-and-boundary.md)（市場監視のイベント契約と責務境界）
    — 本 ADR が扱う 2 サービスの購読責務の出所。
- 関連仕様書: `docs/specs/20260727_issue-258_consumer-endpoint-name-collision.md`
- Issue: #258（bug）。増幅要因（MSP 側の重複デプロイ）は endazon/microservices-platform#407

> **注記（既存の誤参照を踏襲しない）**: 各 Worker の `Program.cs` にある
> `// ADR-0003, IADR-0011: MassTransit（RabbitMQ）` というコメントは既存の誤参照である。
> 本ユニットの `ADR-0003` は「生成AIの売買判断を方針階層とリスク管理で拘束する」（AI ガードレール）であり、
> `IADR-0011` は「基盤ランタイム Foundation は最小移植」であって、いずれもメッセージングの決定ではない
> （意図されていたのは platform `ADR-0003`＝現 `ADR-0027`）。本 ADR ではこれを踏襲せず、上記の正しい参照を用いる。
> 既存コメントの一括是正は本 PR のスコープ外（別途対応）。

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

## ⚠️ Wolverine 移行時の再検証（[[ADR-0013]] 追随）

**本 ADR の決定と `scripts/check-consumer-endpoint-names.js` は、MassTransit の
`DefaultEndpointNameFormatter` の挙動（キュー名＝`Consumer` 接尾辞を落とした consumer クラス名・
namespace 非包含）を前提としている。この前提は Wolverine 移行で失われる。**

[[ADR-0013]]（Accepted 2026-07-25）により、本ユニットは基盤の Wolverine 移行（platform `ADR-0027`）と
RabbitMQ + Kafka 併用（platform `ADR-0028`）へ追随することが確定している。Wolverine はキュー／エンドポイントの
命名規約が MassTransit と異なるため、移行時には以下を**必ず再検証**すること。

- 本 ADR の「クラス名＝キュー名」という前提が Wolverine でも成り立つか。成り立たない場合、
  `TradeDecisionMadeBaselineConsumer` への改名がキュー分離として機能し続けるか。
- `check-consumer-endpoint-names.js` の判定規則（`endpointNameOf`＝末尾 `Consumer` を落とす）を
  Wolverine の命名規約へ更新するか、あるいは検査自体を Wolverine の仕組み（明示的なエンドポイント宣言等）へ
  置き換えるか。**規則が変わったまま検査だけ残すと、通っているのに守られていない状態になる。**
- `ConsumerEndpointNameTests`（両サービス）が `DefaultEndpointNameFormatter` を直接参照しているため、
  MassTransit 依存の除去に伴い書き換えが必要になる。

なお [[ADR-0013]] は「フォローアップ: 実装リポジトリで移行を Issue 化し、基盤側の移行スケジュールと同期する」
と定めているが、本 PR 時点で本リポジトリに Wolverine 移行の Issue は起票されていない（確認済み）。
移行 Issue を起票する際は、本節を移行作業のチェック項目として取り込むこと。

## 前提（本 ADR の範囲外）

本 ADR は AST 内部のキュー名衝突（原因B）を扱う。取引フェーズ2 検証で観測された consumers=4 のうち残り 2 は、
MSP 側が同じ 3 サービスを `microservices-platform` namespace にも重複デプロイしていたことによる
（endazon/microservices-platform#407・原因A）。**両方が解消して初めて consumers が各 1 になる。**

## 関連（本 ADR の失効・2026-08-04 追記）

- **Superseded by: [IADR-0129](./IADR-0129_wolverine-messaging-topology.md)**（Wolverine 移行のトポロジ設計）。
  本 ADR の決定は MassTransit の `DefaultEndpointNameFormatter`（キュー名＝`Consumer` 接尾辞を落とした
  consumer クラス名・namespace 非包含）を前提とする。#354（[[ADR-0013]]）の Wolverine 移行により
  **キュー名の導出にクラス名が一切関与しなくなった**ため、本 ADR の対策（クラス名をサービス跨ぎで
  一意にする）は現行では効力を持たない。上の「⚠️ Wolverine 移行時の再検証」が求めた再検証は
  #354 で実施され、キュー名の一意性は `<ServiceName>.<メッセージ型名>` の `ServiceName` へ帰着した
  （[IADR-0129](./IADR-0129_wolverine-messaging-topology.md) 決定 1）。`scripts/check-consumer-endpoint-names.js` の旧規則は #354 第 3 段階で撤去した。
- **本文は当時の記録として原文のまま据え置く**（#258 の原因分析と代替案の検討は、現在の設計を読む上での
  文脈として価値がある）。本節と状態欄のみを追記・更新した。
