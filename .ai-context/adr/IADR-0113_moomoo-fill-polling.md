---
title: IADR-0113 非同期に約定するブローカーの約定状態は短周期ポーリングで追跡し、台帳は OrderId 単位の単調 upsert で受ける
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-10, FR-12, ADR-0002]
author: endazon (with Claude Code)
created: 2026-07-29
updated: 2026-07-29
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/03_moomoo-integration.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
---

# IADR-0113: 約定状態は短周期ポーリングで追跡し、台帳は OrderId 単位の単調 upsert で受ける

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-29
- 決定者: endazon（利用者・#270 起票と設計承認）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-05（発注執行）、FR-10（リスク統制）、FR-12（ペーパートレード）、
  ADR-0002（計画リポ）（証券会社連携）
- 対象 Issue: [#270](https://github.com/endazon/ai-stock-trading/issues/270)
- 関連する実装仕様書: [20260729_270_moomoo-fill-polling](../specs/20260729_270_moomoo-fill-polling.md)
- 関連 IADR: [IADR-0018](IADR-0018_portfolio-ledger-projection.md)（取引台帳と射影）、
  [IADR-0057](IADR-0057_order-dispatch-idempotency.md)（発注冪等化・at-most-once）、
  [IADR-0074](IADR-0074_reservation-reconciliation.md)（滞留リコンサイル・既定無効）、
  [IADR-0092](IADR-0092_reservation-broker-probe-moomoo.md)（moomoo 実照会プローブ）、
  [IADR-0111](IADR-0111_broker-tier-selection.md)（ブローカー階層・閂 0）

## 背景・課題

paper（`PaperBrokerAdapter`）は発注即 `Filled` を返す。一方 moomoo は発注時に `Submitted`（未約定）を返し、
約定は後から非同期に成立する。`OrderExecutionService` は発注応答をそのまま `executed_orders` に保存して
`OrderExecuted` を発行し、**その後この注文の状態を観測する経路が存在しない**。

帰結として、`Status=Accepted / FilledQuantity=0` の `OrderExecuted` はリスク管理の
`OrderExecutedLedgerConsumer` に無視され、`trade_fills` は 0 行のままになる。`PortfolioProjection` は
`trade_fills` のみから `SymbolsTradedToday` / `DailyOrderedAmount` / 建玉 / `InvestedCapital` を導出するため、
台帳は「まだ何も取引していない」状態に留まり、`SameDayReentry` も金額系上限も次サイクルで**素通し**になる。

実測（2026-07-28 経路B ローカル k8s・SIMULATE）: 5 分間隔で 2 回発注した後も `dailyOrderRemaining` は
¥170,000,000 のまま、`trade_fills` は 0 行。paper では即時終端のため露呈しない構造的欠落である。

既存の照会経路は 2 つあるがどちらも通常運用の追跡には使えない。

- `OrderAmendmentService`: 訂正・取消の同期経路。moomoo 選択時はそもそも配線しない（実弾側に訂正・取消の口を作らない）。
- `MoomooReservationBrokerProbe`（IADR-0074/0092）: **滞留 `Reserved` の事後救済**。既定無効・巡回下限 1 時間・
  滞留閾値 24 時間クランプ。対象は「発注したか不明な予約」であり、「発注済みで約定待ちの注文」ではない。

## 検討した選択肢

1. **約定状態の短周期ポーリング**（issue 案 1）— 非終端の `executed_orders` を短周期で `GetOrderAsync` し、
   変化を台帳へ伝播する。ブローカーの実状態が唯一の権威で、取消・失効・部分約定が自然に表現できる。
2. **`Accepted` 時点で枠を消費する**（issue 案 2）— 台帳の意味を「約定実績」から「発注実績」へ変える。
   取消・拒否・失効時の戻しが必要になり、戻しの取りこぼしが**恒久的な枠の目減り**として蓄積する。
   実現損益・建玉・平均取得単価は結局約定でしか作れないため、台帳が二重の意味を持つ。
3. **リコンサイルの下限クランプを moomoo 経路で緩める**（issue 案 3）— 最小差分だが、二重発注防止のために
   意図的に置いた下限（IADR-0074）を「約定追跡」という別目的で緩めることになり、`Reserved` の在庫が
   高頻度で走査される。滞留救済と通常追跡という異なる要件を 1 つの機構に相乗りさせる。

## 決定

**選択肢 1 を採る。** さらに、台帳側の受け口を「約定があれば載せる ＋ `OrderId` 単位の単調 upsert」に改める。

### 1. ポーラー（発注執行サービス内）

- 照会は既存ポート `IBrokerAdapter.GetOrderAsync` のみ。moomoo 固有型は Application 層に持ち込まない。
- Application（`OrderFillPoller`・純オーケストレーション）と Worker（`OrderFillPollingService`・`IBus` 発行）の
  2 層に分ける。リコンサイル（`OrderReservationReconciler` / `OrderReservationReconciliationService`）と同じ構成。
- 対象は `executed_orders` のうち **非終端**（`Accepted` / `PartiallyFilled`）かつ `MaxTrackingHours` 以内の記録。
- 遷移の扱い: 状態変化または約定数の増加でのみ記録更新＋`OrderExecuted` 発行。不変なら何もしない。
  **照会が `null`・例外なら何も書かない**（ブローカー状態を推測しない・次回巡回で再試行）。約定数は巻き戻さない。
- 既定 `FillPolling:Enabled=true` / `IntervalSeconds=30`（5〜3600 クランプ）/ `MaxTrackingHours=24` / `BatchSize=200`。

### 2. 台帳の受け口（リスク管理）

- `OrderExecutedLedgerConsumer` の条件を `Status == Filled` から **`FilledQuantity > 0`** に変える。
- `AppendFill` は `OrderId` 主キーの**単調 upsert**（新規は追加・累積約定数が増えたときだけ更新・それ以外は無視）。

### 3. スコープの境界

- `Shared.Contracts` 不変・新規イベント無し（`OrderExecuted` を再利用）・**DB スキーマ変更無し**（Migration 無し）。
- SIMULATE / 実弾 OFF は不変。閂 0〜4（IADR-0111 / IADR-0016 / IADR-0056 / IADR-0060）に差分ゼロ。
  増えるブローカー呼び出しは**状態照会（読み取り）のみ**で、発注・訂正・取消の呼び出しは 1 つも足さない。

## 根拠

### なぜ台帳が「累積値の単調 upsert」なのか

moomoo の `FillQty` / `FillAvgPrice` は注文に対する**累積値**であり、約定ごとの差分ではない。したがって
「1 注文 = 1 行・値は最新の累積」が唯一の忠実な写像になる。差分行として追記する形にすると、
`OrderId` 一意という既存の冪等キー（IADR-0018）を壊し、MassTransit 再配送・複数巡回・複数レプリカのいずれでも
二重計上が起こり得る。単調条件（増加時のみ更新）により、順序が前後した配送や部分列挙で数量が巻き戻ることもない。

`Status == Filled` を条件にしていた既存実装は、全量約定しか起こらない paper を前提にしていた。
`FilledQuantity > 0` へ改めることで、**部分約定のまま取消・失効した注文**（`Cancelled` ＋ 約定あり）が
台帳から丸ごと落ちる過少計上も同時に解消する（これはポーラー導入前からリコンサイル経路に存在した欠落）。

### なぜ既定 `true` なのか（既定オフ慣行からの意図的逸脱）

本リポジトリは新規のバックグラウンド処理を既定オフで出荷してきた（retention・reconciliation・撤退評価など）。
本ポーラーはその慣行から外れる。理由は 3 つ。

1. これは**統制が実効するための必要条件**であり、既定オフは「統制が効かない状態」を既定として出荷することを意味する。
   fail-safe の向きは「動かさない」ではなく「約定を統制へ届ける」側にある。
2. 副作用が読み取り照会に限られる。発注・訂正・取消は増えず、照会不達は「書かない・据え置く」に倒れる。
3. paper では二重に到達しない（`Program.cs` は moomoo 選択時のみ登録し、かつ paper は非終端記録を作らない）。
   既定 ON で挙動が変わるのは moomoo 経路だけであり、その変化こそが #270 の修正である。

停止は `FillPolling__Enabled=false` の 1 環境変数で足りる。`docker-compose.yml` / Helm values には設定点を
足さない（既定で正しく動くため。本番 values の描画はバイト等価のまま）。

### リコンサイルとの関係（相乗りさせない理由）

対象集合が交わらない。リコンサイラは `dispatch_reservations` の `Reserved`（発注したか不明）を、
ポーラーは `executed_orders`（発注済み・確定済み予約）を見る。目的も時間尺度も異なる
（前者＝二重発注を招かない事後救済・時間単位、後者＝通常運行の状態追跡・秒単位）。
両者が同一 `OrderId` の `OrderExecuted` を発行しても、台帳の単調 upsert により結果は冪等である。

## 影響・追随

- 1 注文あたり `OrderExecuted` が複数回発行され得る（受付 → 部分 → 全量）。監査は `MessageId` 冪等のため
  時系列として正しく増える（FR-11）。通知は状態遷移ごとに 1 通（全量約定のみ Info・他は Warning＝既存整形のまま）。
  これは既にリコンサイル経路で起き得た形であり、新しい下流契約は導入しない。
- `executed_orders` は追記専用ではなくなる（既存行の状態・約定数を更新する）。`DecisionId` 1:1 は不変で、
  `OrderExecutionService` 相1（既存結果の再発行）は更新後の最新状態を返す。
- 取引日境界は JST 固定のまま（[#249](https://github.com/endazon/ai-stock-trading/issues/249) に残置）。
  日跨ぎで約定した注文の当日帰属は終端化時刻に依存する。
- moomoo の当日照会（`GetOrderList`）で見つからない注文（翌日以降に履歴へ移動した等）は `null` となり、
  `MaxTrackingHours` 経過後に追跡対象から外れる。以降は滞留として人手／リコンサイルの領分に戻る。

## 代替案を採らなかった理由

- 案 2（発注ベースの枠消費）: 台帳の意味が二重化し、取消・拒否の戻し漏れが恒久的な枠の目減りとして蓄積する。
  建玉・実現損益・平均取得単価は結局約定からしか作れず、統制だけ別の入力を持つと乖離の検出が難しくなる。
- 案 3（リコンサイルのクランプ緩和）: 二重発注防止のために置いた下限を別目的で緩めることになり、
  滞留救済と通常追跡という異なる要件が 1 機構に混ざる。緩和は `Reserved` 在庫の高頻度走査も招く。
