---
title: AST 取引台帳とブローカ実ポジションの定期突合（検知・記録のみ・是正しない）
type: spec
status: review
related_ids: [FR-05, FR-09, FR-10, FR-11, UC-02, UC-06, ADR-0002]
author: endazon (with Claude Code)
created: 2026-07-30
updated: 2026-07-30
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/03_moomoo-integration.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
---

# 仕様書: ブローカ実ポジションとの定期突合

> 利用者指示・設計承認（2026-07-30）。[#292](https://github.com/endazon/ai-stock-trading/issues/292) の
> **PR 2/3**（PR 1/3 = owner 決済経路の上に積む）。判断由来の決済は PR 3/3。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-05（発注・注文状態の追跡）／FR-10（リスク統制）／FR-11（監査）／FR-09（通知）
- ADR: [ADR-0002](../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md)（証券会社連携）
- 関連 IADR: [IADR-0018](../adr/IADR-0018_portfolio-ledger-projection.md)（取引台帳と射影）／
  [IADR-0074](../adr/IADR-0074_reservation-reconciliation.md)・[IADR-0092](../adr/IADR-0092_reservation-broker-probe-moomoo.md)
  （**注文レベル**の予約リコンサイル＝本 PR と区別する対象）／
  [IADR-0113](../adr/IADR-0113_moomoo-fill-polling.md)（約定伝播ポーラー）／
  [IADR-0117](../adr/IADR-0117_owner-position-close-path.md)（owner 決済経路・PR 1/3）／
  本作業で新規 [IADR-0118](../adr/IADR-0118_broker-position-reconciliation.md)
- 対象 Issue: [#292](https://github.com/endazon/ai-stock-trading/issues/292)（`Refs #292`）・
  傘 [#279](https://github.com/endazon/ai-stock-trading/issues/279)

## 現状（この変更の直前・実コードで確定）

| 面 | 実態 |
| --- | --- |
| `OrderFillPoller`（#270） | 照会対象は `executed_orders` の**非終端行のみ**＝ **AST が発注した注文だけ** |
| `OrderReservationReconciler`（#141） | 照合キーは clientOrderId（remark）＝これも **AST が出した注文だけ** |
| ブローカ実ポジションの照会 | **どこにも無い**。`IBrokerAdapter` は発注・状態照会・取消のみ |
| moomoo 側の手動売却・外部約定 | AST 台帳に**一切反映されない**。乖離は検知されないまま恒久化する |
| SDK の対応 | `MMApiMoomooTradeClient.OnReply_GetPositionList` は**空実装で応答を捨てている** |

## 目的

1. AST 台帳（`trade_fills` の射影）とブローカ実ポジションの乖離を**定期的に検知**する。
2. 乖離を**監査へ記録し、Discord へ通知**する（FR-11 / FR-09）。
3. **是正はしない**（検知・記録のみ）。
4. 一過性の未反映（発注直後・約定ポーリング待ち）で**誤検知しない**。
5. paper（内蔵擬似約定）では**構造的に無害**である。

## 設計

### 1. 経路: OrderExecution が観測を publish、Risk が突合する

```
OrderExecution.Worker                    (bus)                  Risk.Worker
BrokerPositionSnapshotService  ──BrokerPositionsObserved──▶  BrokerPositionsObservedConsumer
  └ IBrokerPositionSource                                      ├ PortfolioProjection.ProjectOpenPositions（台帳）
     ├ moomoo: TrdGetPositionList                              ├ PositionDriftDetector（純関数）
     └ paper : 実装なし → 常駐が起動時に自己停止              └ PositionDriftTracker（連続 N 回・シグネチャ dedup）
                                                                  └ PositionReconciliationDrift ─▶ Audit / Notification
```

**方向をこうする理由**: (1) ブローカ接続は発注執行サービスに閉じている、(2) 台帳の権威はリスク管理にある、
(3) 発注執行サービスは HTTP クライアント／s2s 配線を**一切持たない**ため、逆方向（発注執行が Risk を照会）にすると
認証サーフェスを新設することになる、(4) #164 で採った「s2s ではなくイベント射影」の流儀に一致する。

### 2. ブローカ側（発注執行サービス）

新ポート `IBrokerPositionSource`（`Shared.Contracts.Ports`）:

```csharp
/// 現在の建玉一覧。照会不能（未対応・不達・応答異常）は null（＝**不明**）を返す。空列は「建玉ゼロ」を意味する。
Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken cancellationToken = default);
```

- **`null`（不明）と空列（建玉ゼロ）を厳格に区別する。** 取り違えると「ブローカは何も持っていない」と誤断定し、
  台帳の全建玉が乖離として報告される（#141 が `null`／例外を区別した理由と同型）。
- `BrokerPositionSnapshot(Symbol, Market, Quantity, AverageCost)`。`Quantity` は**符号付き**（+ ロング / − ショート）で、
  台帳射影（`OpenPosition.Side` × `Quantity`）と同じ表現に揃える。
- 実装は `MoomooBrokerAdapter`（`IBrokerAdapter, IClientOrderIdBroker, IBrokerPositionSource`）。
  `IMoomooTradeClient.GetPositionsAsync` が全対応市場（US/JP）を `TrdGetPositionList` で列挙する。
  **いずれかの市場の照会が失敗したら例外**を送出し、アダプタが `null`（不明）へ倒す（部分列挙を「全部」と誤らない）。
- `PaperBrokerAdapter` は本ポートを実装しない → paper では `IBrokerPositionSource` が DI に存在せず、常駐は
  起動時に自己停止する（**構造的な非干渉**）。

常駐 `BrokerPositionSnapshotService`（既定 10 分・60〜3600 秒クランプ）は照会結果を
`BrokerPositionsObserved(Positions, ObservedAt)` として発行する。`null`（不明）なら**何も発行しない**。

### 3. 突合（リスク管理）

`PositionDriftDetector.Detect(ledger, broker)`（純関数・`RiskManagementService.Domain`）:

- キーは `(Symbol, Market)`。両側の**符号付き数量のみ**を比較する。
- 平均取得単価は文脈として載せるが**判定には使わない**（手数料・端数・為替で必ずズレるため）。
- 乖離の種類:

| Kind | 条件 | 意味 |
| --- | --- | --- |
| `BrokerOnly` | 台帳 0・ブローカ ≠ 0 | AST が知らない建玉（手動売買・外部約定） |
| `LedgerOnly` | 台帳 ≠ 0・ブローカ 0 | 台帳にだけある建玉（約定の誤計上・ブローカ側の決済） |
| `QuantityMismatch` | 双方 ≠ 0 で不一致 | 数量のズレ |

### 4. 誤検知と通知過多の抑制（config を足さずに構造で解く）

`PositionDriftTracker`（シングルトン・インメモリ）:

- **連続 N 回（既定 2）同一シグネチャで観測された乖離だけを報告する。** 発注直後〜約定ポーリング反映までの
  一過性のズレを弾く。1 回の観測で通知すると、通常運行のたびに乖離が鳴る。
- **前回**報告したシグネチャと同一なら再報告しない（10 分ごとに Discord を叩かない）。
- 乖離が解消したら報告済みシグネチャを消す（同じ乖離が再発したら再び報告する）。
- 状態はプロセス内のみ。再起動後に 1 度だけ再報告され得るが、監査上は無害（重複は `MessageId` で識別できる）。
- `N` は構成キーにしない（`PositionCloseService` の窓と同じ方針＝運用で触る値ではない）。

### 5. 是正しない

自動で建玉を合わせにいく実装は**しない**。外部要因の乖離に対して自律的に発注する経路を作ることになり、
安全側でない。乖離の解消は、利用者が [IADR-0117](../adr/IADR-0117_owner-position-close-path.md) の決済経路を
使うか、ブローカ側で操作するかの人手の判断に委ねる。

### 6. #141 / IADR-0092 との関係（相乗りさせない）

| | #141 `OrderReservationReconciler` | 本 PR `PositionReconciliation` |
| --- | --- | --- |
| 問い | 「この予約は実際に発注されたか」 | 「私の帳簿はブローカと一致しているか」 |
| 粒度 | 注文 1 件 | 銘柄別の建玉残高 |
| 突合キー | clientOrderId（remark = DecisionId） | (Symbol, Market) |
| 対象 | **AST が出した注文だけ** | **AST が知らない約定を含む全部** |
| 権威 | ブローカ（不明は Indeterminate） | 双方を並べる（どちらも正としない） |
| 是正 | 予約を終端化する | しない（報告のみ） |

**補完関係であって代替ではない。** #141 が全部緑でも、手動売買由来の乖離は 1 件も検出できない。

### 7. 構成

| キー | 既定 | 意味 |
| --- | --- | --- |
| `Reconciliation:Positions:Enabled` | `true` | 建玉スナップショットの定期発行（発注執行） |
| `Reconciliation:Positions:IntervalSeconds` | `600` | 巡回間隔（60〜3600 にクランプ） |

**既定 `true` は意図的な逸脱**（IADR-0113 と同じ理由）。副作用は**読み取り照会のみ**で、発注・訂正・取消を
1 つも増やさない。検知器を既定オフで出荷することは「乖離が見えない状態を既定にする」ことを意味する。
paper では `IBrokerPositionSource` が存在せず構造的に無害。Helm / values は**触らない**（既定で正しく動く）。

## 影響範囲

| 対象 | 変更 |
| --- | --- |
| `Shared.Contracts` | `BrokerPositionSnapshot` / `PositionDriftItem` / `PositionDriftKind`（Trading）、`BrokerPositionsObserved` / `PositionReconciliationDrift`（Events・新規 2 件） |
| `Shared.Contracts.Tests` | baseline ＋ URN 固定の追随 |
| `OrderExecutionService.Worker` | `IMoomooTradeClient.GetPositionsAsync` ＋ `MMApiMoomooTradeClient` 実装（`OnReply_GetPositionList` を `Complete` へ）、`MoomooBrokerAdapter : IBrokerPositionSource`、`BrokerPositionSnapshotService` ＋ options、DI 配線 |
| `RiskManagementService.Domain` | `PositionDriftDetector`（新規・純関数） |
| `RiskManagementService.Application` | `PositionDriftTracker`（新規・インメモリ） |
| `RiskManagementService.Worker` | `BrokerPositionsObservedConsumer` ＋ DI/購読登録 |
| `AuditService` / `NotificationService` | 新規 2 イベントの Consumer ＋ 写像・整形 |
| DB スキーマ / Migration | **無し** |
| Helm / compose / values / `.env.example` | **不変** |
| 実弾ゲート（閂 0〜4） | **不変**（増えるのは読み取り照会のみ。発注・訂正・取消を 1 つも足さない） |

## テスト（受け入れ基準の写像）

| # | 観点 | テスト |
| --- | --- | --- |
| 1 | 一致 | 台帳とブローカが一致していれば乖離ゼロ |
| 2 | `BrokerOnly` | ブローカにだけある建玉を検出（**手動売買由来の drift**） |
| 3 | `LedgerOnly` | 台帳にだけある建玉を検出 |
| 4 | `QuantityMismatch` | 数量差を検出し、双方の数量を載せる |
| 5 | 符号 | ショート建玉（台帳 `Sell`）が負の数量としてブローカと突合される |
| 6 | 単価非依存 | 平均取得単価だけが違う場合は乖離としない |
| 7 | 市場の区別 | 同一コードの別市場を別建玉として扱う |
| 8 | 連続 N 回 | 1 回目は報告しない／2 回連続で同一なら報告する |
| 9 | 通知抑制 | 同一シグネチャの継続は再報告しない／内容が変われば再報告する |
| 10 | 解消と再発 | 乖離が消えた後に同じ乖離が再発したら再び報告する |
| 11 | 不明の扱い | `IBrokerPositionSource` が `null` を返したら**何も発行しない**（空列と区別） |
| 12 | 例外 | 照会例外で常駐が落ちない（次回巡回で再試行） |
| 13 | paper 非干渉 | `IBrokerPositionSource` 未登録なら常駐が自己停止し 1 度も照会しない |
| 14 | 間隔クランプ | 0・負・巨大値が 60〜3600 に収まる |
| 15 | 監査・通知 | 新規 2 イベントに監査 Consumer が存在（カバレッジテスト）／通知文に銘柄と両数量が入る |
| 16 | 契約 | URN 固定・baseline に新イベントが登録されている |

## 受け入れ基準（`docs/DEFINITION_OF_DONE.md` と併せて）

- [ ] AST 台帳とブローカ実ポジションの乖離が定期的に検知され、監査・通知へ届く
- [ ] 一過性の未反映で誤検知しない（連続 N 回）／同一乖離で通知が繰り返されない
- [ ] 照会不能（不明）を「建玉ゼロ」と取り違えない
- [ ] 是正（自動発注）を行わない
- [ ] paper で構造的に無害。SIMULATE 限定・実弾 OFF・Helm / values / Migration が不変
- [ ] `dotnet build` / `dotnet test` / `dotnet format` が green・CI / gitleaks が green

## スコープ外

- **乖離の自動是正**（安全側でないため行わない）
- 平均取得単価・実現損益の突合（数量のみ）
- moomoo 以外のブローカの建玉照会（プロバイダ追加時にアダプタで実装する）
- 判断由来の決済（PR 3/3・IADR-0119）
