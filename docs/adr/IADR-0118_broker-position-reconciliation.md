---
title: IADR-0118 ブローカ実ポジションとの突合は「発注執行が観測を publish・リスク管理が突合」で行い、検知のみに留める
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-09, FR-10, FR-11, UC-02, UC-06, ADR-0002]
author: endazon (with Claude Code)
created: 2026-07-30
updated: 2026-07-30
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/03_moomoo-integration.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
---

# IADR-0118: ブローカ実ポジションとの突合は観測イベント経由で行い、検知のみに留める

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-30
- 決定者: endazon（利用者・#292 起票と設計承認）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-05（発注・注文状態の追跡。**本 ADR は FR-05 の拡張解釈**である＝計画書本文は
  「自 AST が出した注文の状態追跡」を指しており、ブローカ実ポジションとの突合はその自然な延長として扱う。
  乖離が統制の入力（台帳）を狂わせるという意味では FR-10 が直接の根拠になる）、FR-10（リスク統制）、FR-11（監査）、FR-09（通知）、
  [ADR-0002](../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md)（証券会社連携）
- 対象 Issue: [#292](https://github.com/endazon/ai-stock-trading/issues/292)（傘 [#279](https://github.com/endazon/ai-stock-trading/issues/279)）
- 関連する実装仕様書: [20260730_292_broker-position-reconciliation](../specs/20260730_292_broker-position-reconciliation.md)
- 関連 IADR: [IADR-0018](IADR-0018_portfolio-ledger-projection.md)（取引台帳と射影）、
  [IADR-0074](IADR-0074_reservation-reconciliation.md) / [IADR-0092](IADR-0092_reservation-broker-probe-moomoo.md)
  （**注文レベル**の予約リコンサイル＝本 ADR が区別する対象）、
  [IADR-0113](IADR-0113_moomoo-fill-polling.md)（約定伝播ポーラー）、
  [IADR-0117](IADR-0117_owner-position-close-path.md)（owner 決済経路）

## 背景・課題

AST がブローカを見る経路は 2 つあり、**どちらも「AST が出した注文」しか見ていない**。

- `OrderFillPoller`（IADR-0113）: 対象は `executed_orders` の非終端行。
- `OrderReservationReconciler`（IADR-0074/0092）: 突合キーは clientOrderId（remark = DecisionId）。

したがって、**moomoo アプリからの手動売却・外部約定・AST が知らない取引**は取引台帳に一切反映されず、
台帳とブローカ実ポジションの乖離（drift）は検知されないまま恒久化する。台帳は統制（`SameDayReentry`・
日次発注上限・段階資金上限・建玉数上限）の唯一の入力であるため、乖離はそのまま統制の誤判定になる。

ブローカ実ポジションを照会する経路はコード上どこにも存在しない（`IBrokerAdapter` は発注・状態照会・取消のみ）。
moomoo SDK には `TrdGetPositionList` があるが、`MMApiMoomooTradeClient.OnReply_GetPositionList` は
**空実装で応答を捨てていた**。

## 検討した選択肢

1. **発注執行が建玉を観測して publish、リスク管理が台帳と突合する**（採用）。
2. **発注執行がリスク管理の `GET /risk-controls/open-positions` を s2s 照会して自分で突合する**。
3. **リスク管理が発注執行へ建玉照会の同期エンドポイントを叩く**（方向だけ逆）。
4. **`OrderFillPoller` / `OrderReservationReconciler` に相乗りさせる**。

## 決定

**選択肢 1 を採る。** 具体的には次の 5 点を決める。

### 決定 1: 経路は「観測を publish → リスク管理が突合」

発注執行サービスの常駐 `BrokerPositionSnapshotService` が定期的に建玉を照会し、
`BrokerPositionsObserved(Positions, ObservedAt)` を発行する。リスク管理の `BrokerPositionsObservedConsumer` が
取引台帳の射影（`PortfolioProjection.ProjectOpenPositions`）と突き合わせ、乖離があれば
`PositionReconciliationDrift` を発行する（→ 監査・Discord 通知）。

理由: (1) ブローカ接続は発注執行に閉じている、(2) 台帳の権威はリスク管理にある、(3) 発注執行サービスは
**HTTP クライアント／s2s 配線を一切持たない**ため、選択肢 2 は認証サーフェスの新設を伴う、
(4) #164（バックテスト verdict の供給）で採った「s2s ではなくイベント射影」の流儀に一致する。

### 決定 2: 建玉照会は `IBrokerAdapter` ではなく新ポート `IBrokerPositionSource`

`IBrokerAdapter` に足すと全アダプタ（ペーパーを含む）へ実装を強いる。分離すれば、実装しないアダプタでは
DI に本ポートが現れず、常駐そのものが登録されない＝**paper では 1 度も照会が起きない**（構造的な非干渉）。

契約の中核は **`null`（照会不能＝不明）と空列（建玉ゼロ）の厳格な区別**。取り違えると「ブローカは何も
持っていない」と誤断定し、台帳の全建玉が乖離として報告される。moomoo 実装は**いずれかの市場の照会が
失敗したら例外**を送出し（部分列挙を返さない）、アダプタが `null` へ倒す（IADR-0092 と同型の要請）。

### 決定 3: 比較は符号付き数量のみ

キーは `(Symbol, Market)`、値は符号付き数量（+ ロング / − ショート）。台帳の `OpenPosition`（方向 × 正の数量）を
ブローカ表現へ揃えてから比較する。乖離は `BrokerOnly` / `LedgerOnly` / `QuantityMismatch` の 3 種。

**平均取得単価は判定に使わない。** 手数料・端数・為替で必ずズレるため、使えば恒常的に鳴り続け、本当の乖離が埋もれる。

### 決定 4: 是正しない（検知・記録・通知のみ）

自動で建玉を合わせにいく実装はしない。外部要因の乖離に対して**自律的に発注する経路**を作ることになり、
安全側でない。解消は利用者が IADR-0117 の決済経路を使うか、ブローカ側で操作するかの人手の判断に委ねる。
（テストで `PositionReconciliationDrift` 発行時に `OrderApproved` が 1 件も出ないことを固定する。）

### 決定 5: 雑音の抑制は構成キーではなく構造で行う

`PositionDriftTracker`（インメモリ・シングルトン）が 2 つの雑音を落とす。

- **連続 N 回（既定 2）同一シグネチャ**で観測された乖離だけを報告する。発注してから約定が台帳へ届くまでの
  間、台帳とブローカは**正当にズレる**。1 回の観測で通知すると通常運行のたびに鳴る。
- **前回報告した内容と同一なら再報告しない**（解消されない乖離で 10 分ごとに Discord を叩かない）。
  解消したら報告済みを忘れる＝同じ乖離が再発したら再び報告する。

シグネチャは順序非依存の正準形にする（列挙順はブローカ応答に依存し、順序差を「内容が変わった」と誤認すると
連続条件が永久に満たされない）。`N` は構成キーにしない（下げれば雑音・上げれば検知が遅れるだけで、運用で触る値ではない）。

## 根拠

### なぜ #141 / IADR-0092 に相乗りさせないのか（選択肢 4 を採らない理由）

問い・粒度・突合キー・権威・是正のすべてが異なる。

| | #141 予約リコンサイル | 本 ADR 建玉突合 |
| --- | --- | --- |
| 問い | 「この予約は実際に発注されたか」 | 「私の帳簿はブローカと一致しているか」 |
| 粒度 | 注文 1 件 | 銘柄別の建玉残高 |
| 突合キー | clientOrderId（remark = DecisionId） | (Symbol, Market) |
| 対象 | **AST が出した注文だけ** | **AST が知らない約定を含む全部** |
| 権威 | ブローカ（不明は Indeterminate） | 双方を並べる（どちらも正としない） |
| 是正 | 予約を終端化する | しない |

**補完関係であって代替ではない。** #141 が全部緑でも、手動売買由来の乖離は 1 件も検出できない。

### なぜ既定 `true` なのか（既定オフ慣行からの意図的逸脱）

IADR-0113 と同じ理由。副作用が**読み取り照会のみ**で、発注・訂正・取消を 1 つも増やさない。検知器を既定オフで
出荷することは「乖離が見えない状態」を既定にすることを意味する。paper では構造的に登録されないため、既定 ON で
挙動が変わるのは moomoo 経路だけであり、その変化こそが本 ADR の目的である。停止は
`Reconciliation__Positions__Enabled=false` の 1 環境変数で足りる。

### 状態をインメモリに置く理由と、その前提

> **2026-07-31 追記: 本節の決定は [IADR-0121](IADR-0121_position-drift-state-durable.md) により置き換えられた**
> （[#305](https://github.com/endazon/ai-stock-trading/issues/305)）。追跡状態は DB 単一行＋並行トークン
> （`position_drift_state`）へ移り、単一レプリカ前提は解消された。本 ADR の他の決定（決定 1〜5 の判定意味論・
> 是正しない方針）は不変であり、「Migration 無し」だけが更新される。以下は当時の判断の記録として残す。

乖離の権威は**毎回の観測**であって履歴ではない。再起動後は連続条件を数え直し、乖離が継続していれば
1 度だけ再報告される。監査は `MessageId` で重複を識別でき、通知も 1 通で済むため、専用テーブルを持つ価値がない。

**ただしこれは「リスク管理サービスが単一レプリカである」ことに依存する。** 現行の
`deploy/helm/ai-stock-trading/templates/deployment.yaml` は `replicas: 1` 固定であり、`BrokerPositionsObserved` は
単一の競合コンシューマ（1 Pod）でのみ処理される。将来レプリカを増やすと、同一シグネチャの連続観測が Pod 間に
分散して「連続 2 回」条件が実質的に満たされなくなり、**乖離が恒久的に未報告のまま**になり得る（受け入れ基準
「乖離が定期的に検知され、監査・通知へ届く」を静かに損なう向きの縮退である）。

リスク管理サービスを水平スケールする際は、本トラッカーを durable な重複排除（`IWithdrawalNotificationStore`
と同型の DB 単一行）へ置き換えること。本 ADR は単一レプリカ前提のもとでの決定である。
——この置き換えを実施したのが IADR-0121 である。

## 影響・追随

- **実弾ゲート（閂 0〜4）に差分ゼロ。** 増えるのは読み取り照会のみ。SIMULATE 限定・実弾 OFF は不変。
- DB スキーマ変更なし（Migration 無し）。Helm / values / compose / `.env.example` は不変。
  （**IADR-0121 で更新**: 追跡状態の durable 化により `position_drift_state` テーブル 1 つが追加された。
  Helm / values / compose / `.env.example` は引き続き不変。）
- `Shared.Contracts` にイベント 2 件（`BrokerPositionsObserved` / `PositionReconciliationDrift`）と
  型 2 件（`BrokerPositionSnapshot` / `PositionDriftItem`）を追加する。契約ガード 3 点
  （baseline / URN 固定 / 監査 Consumer）に追随済み。
- moomoo の建玉写像（`TrdCommon.Position` → `MoomooPositionSnapshot`）は protobuf 依存のため、既存の
  注文写像と同じく **live 検証に委ねる**（単体テストは SDK 非依存の層＝アダプタ以降を固定する）。
- 為替・信用取引の建玉表現は現段階（現物のみ）では扱わない。信用有効化時に `PositionSide` の写像を再確認すること。
- 決済（IADR-0117）で乖離を解消した場合、次の観測で自然に消える（トラッカーが報告済みを忘れる）。

## 代替案を採らなかった理由

- 選択肢 2（発注執行が s2s 照会）: 発注執行サービスに HTTP クライアント・Keycloak s2s・helm env の新規サーフェスを
  作ることになる。読み取り 1 本のために認証面を増やす価値がない。
- 選択肢 3（リスク管理が発注執行を照会）: 発注執行に建玉照会の HTTP エンドポイントを新設する必要があり、
  ブローカ状態を外部へ晒す面が増える。イベント 1 本で足りる。
- 選択肢 4（既存リコンサイルへ相乗り）: 上表のとおり対象集合も権威も是正方針も異なる。1 機構に混ぜると
  「不明を Indeterminate に倒す」（注文レベル）と「双方を並べる」（建玉レベル）という相反する fail-safe が同居する。
