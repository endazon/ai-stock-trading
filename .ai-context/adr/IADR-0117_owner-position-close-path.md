---
title: IADR-0117 建玉の手仕舞いは利用者専用の同期経路で受け、統制を通さず既存の注文パスへ載せる
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-10, FR-11, UC-02, UC-06, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-30
updated: 2026-07-30
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md
---

# IADR-0117: 建玉の手仕舞いは利用者専用の同期経路で受け、統制を通さず既存の注文パスへ載せる

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-30
- 決定者: endazon（利用者・#292 起票と設計承認）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-10**（リスク統制。本文に「kill switch・日次損失ロックアウト・一時停止は…いずれも
  手仕舞い（Close）と損切りは止めない」）、FR-05（発注執行）、FR-11（監査ログ）、UC-02 / UC-06、
  ADR-0003（計画リポ）（生成AIは統制を上書きできない）
- 対象 Issue: [#292](https://github.com/endazon/ai-stock-trading/issues/292)（傘 [#279](https://github.com/endazon/ai-stock-trading/issues/279)）
- 関連する実装仕様書: [20260730_292_owner-position-close](../specs/20260730_292_owner-position-close.md)
- 関連 IADR: [IADR-0015](IADR-0015_stop-loss-mechanical-close.md)（損切りの機械執行・スクリーニング迂回の先例）、
  [IADR-0018](IADR-0018_portfolio-ledger-projection.md)（取引台帳と射影）、
  [IADR-0004](IADR-0004_position-effect-entry-scoping.md)（エントリー判定は `PositionEffect`）、
  [IADR-0107](IADR-0107_base-currency-conversion.md)（基準通貨・`FxRateToBase` の引き継ぎ）、
  [IADR-0057](IADR-0057_order-dispatch-idempotency.md)（発注冪等化・DecisionId 予約）、
  [IADR-0113](IADR-0113_moomoo-fill-polling.md)（約定伝播・`OrderId` 単調 upsert）

## 背景・課題

`PositionEffect.Close` を生む経路は `StopLossExecutionService.BuildCloseApproval()` **1 つだけ**で、
`StopLossTriggered`（市場監視が損切りラインへの到達を検知したとき）にしか発火しない。判断由来の発注は
`TradeDecisionService.cs:208` が `PositionEffect.Open` をリテラル固定している（IADR-0004）。

したがって **損切りラインに触れない限り建玉を手仕舞う手段が存在しない**。利益確定も、運用判断による撤退も、
異常時の清算もできない。実害として、#270（約定が台帳へ伝播しなかった期間）に積み上がった過大建玉を
正規手段で清算できず、AI 自動取引を清算済みの状態から再開できない。

## 検討した選択肢

1. **リスク管理サービスに利用者専用の同期エンドポイントを置き、`OrderApproved` を発行する**（採用）。
   損切りの機械執行と同じ層・同じ出力に揃える。
2. **発注執行サービスに決済 API を置く**。ブローカに近い層に置く案。ただし決済の材料（建玉・`FxRateToBase`・
   段階 Mode・現在値）はすべてリスク管理側にあり、s2s で取り直す往復と権威の二重化が生じる。
3. **決済専用のイベントを作り、発注執行が専用に処理する**。既存の `OrderApproved` → 予約 → 発注 → `OrderExecuted`
   とは別の経路を新設する案。台帳・枠回復・通知・監査の受け口を二重に持つことになる。

## 決定

**選択肢 1 を採る。** 具体的には次の 4 点を決める。

### 決定 1: 配置と出力（リスク管理・`OrderApproved` を出す）

`POST /risk-controls/positions/close`（OwnerOnly）で受け、`PositionCloseService` が判定して
**既存の `OrderApproved` を発行する**。以降は 1 行も新しい経路を作らない
（発注執行 → `OrderExecuted` → `trade_fills` → 建玉・実現損益・枠回復 → Discord 通知 → 監査）。

売買方向は**要求に含めない**。建玉方向の反対売買としてサーバが決める（誤方向の指定で建て増しさせない）。
`FxRateToBase` は建玉の加重平均約定時レートを引き継ぎ（IADR-0107）、`StopLossPrice` は持たせない。

### 決定 2: 発注前スクリーニングを通さない

`OrderScreeningService`（`RiskEvaluator`）を経由しない。kill switch・日次損失ロックアウト・一時停止・
取引ガード・段階資金上限のいずれでも手仕舞いは止まらない。

**これは逸脱ではなく FR-10 本文の実装である。** 構造的な保証として `PositionCloseService` は統制ストア
（`IKillSwitchStore` / `ILockoutStore` / `IPauseStore`）を依存に持たず、その事実をテストで固定する。

### 決定 3: 過剰決済ガードは「処理中の決済」を在庫から引く（時間窓つき）

取引台帳は**約定でしか動かない**。決済要求から約定が届くまで建玉数量は減らないため、在庫判定を建玉数量だけで
行うと多重投入で在庫を超える決済（意図しないショート化）を作れてしまう。

`IPortfolioLedgerStore.GetInFlightCloseQuantity(symbol, market, approvedAtOrAfter)` を足し、

```
利用可能数量 = 建玉数量 − Σ max(0, 決済承認数量 − 当該 DecisionId の約定累計)   （窓内の承認のみ）
```

を在庫とする。数量省略（全量指定）は「保有全量」ではなく**この利用可能数量**と解釈する。

**時間窓（既定 30 分）で切る**のが要点。窓が無いと、#270 破損期のような「永久に約定しない滞留承認」が
建玉を**恒久的にロック**し、手仕舞い手段を作ったのに手仕舞えないという最悪の状態になる。窓は構成キーにしない
（運用で触る値ではなく、広げれば二重決済の窓が広がるだけである）。

### 決定 4: 監査イベントを 1 つ足し、承認より先に発行する

`PositionCloseRequested`（`DecisionId` / 銘柄 / 市場 / 方向 / 数量 / 価格 / **Actor** / **Reason** / 時刻）を新設し、
`OrderApproved` より**先に**発行する。`OrderApproved` はアクターも理由も持たないため、これが無いと
「誰が・なぜ建玉を落としたか」が監査台帳に残らない（FR-11）。

順序の根拠は、「起きた操作に監査が無い」より「監査があるのに操作が無い」ほうが安全であること。後者は同一
`DecisionId` の後続イベント（`OrderApproved` / `OrderExecuted`）の不在として**検知できる**が、前者は検知できない。

## 根拠

### なぜ「決済専用の経路」を作らないのか（選択肢 3 を採らない理由）

二重計上を作らない唯一確実な方法が「約定を記録する経路を 1 本に保つ」ことだからである。決済専用の経路を作ると、
台帳・枠回復・通知・監査の受け口がそれぞれ 2 本になり、片方だけ落ちる／両方が同じ約定を計上する事故の面が増える。
既存経路に載せる限り、冪等性は `OrderId` 単位の単調 upsert（IADR-0113）と `DecisionId` 予約（IADR-0057）が
そのまま効く。

### なぜ `DecisionId` を決定的にしないのか

損切り（IADR-0015）は `StopLossTriggered.EventId` から決定的に採る。同一イベントの再配送で二重発注しないためである。
一方、利用者の決済要求は**各要求が独立した注文**であり、「同じ銘柄・同じ数量をもう一度落とす」は正当な操作になり得る。
内容から決定的に導くと、正当な連続決済が黙って冪等に潰される。多重投入に対する防御は決定 3（在庫ガード）が担い、
ネットワーク再送に対する防御は発注執行側の予約が担う。

### なぜ構成キーを 1 つも足さないのか

本経路は**利用者の明示操作でしか動かない**。常駐処理ではないため「既定オフで出荷し、有効化して使う」という
opt-in の型が意味を持たず、無効化スイッチは「手仕舞えない状態を作れるスイッチ」にしかならない。
Helm / values / compose / `.env.example` は不変で、本番描画はバイト等価である。

## 影響・追随

- **実弾ゲート（閂 0〜4）に差分ゼロ。** ブローカ呼び出しは 1 つも増えない（既存の発注パスを通るだけ）。
  SIMULATE 限定・実弾 OFF は不変。
- DB スキーマ変更なし（既存 `approved_orders` × `trade_fills` の読み取りのみ・Migration 無し）。
- 現在値が取得できず `limitPrice` も指定されない場合は 422 で拒否する（価格 0 の注文を投げない）。
  現在値の供給は市況フィード（IADR-0068）に依存するため、供給が無い環境では `limitPrice` 必須になる。
- 決済注文の**訂正・取消の口は作らない**（moomoo 経路に訂正・取消を配線しない既存方針を維持）。
  誤った決済の是正は反対売買（新規建て）であり、統制の対象に戻る。
- **AI は依然として自分で建玉を落とせない。** `TradeDecisionService` の `PositionEffect.Open` 固定は本 ADR の
  対象外で、判断由来の決済は #292 の PR 3/3（IADR-0119）で扱う。
- Discord の `/close` コマンドは本 ADR の対象外（HTTP 経路のみ）。

## 代替案を採らなかった理由

- 選択肢 2（発注執行に置く）: 建玉・換算レート・段階 Mode・現在値がリスク管理側にあり、s2s の往復と権威の二重化を招く。
  発注執行サービスは現状 HTTP クライアント／s2s 配線を持たず、そのためだけに認証サーフェスを増やすことになる。
- 選択肢 3（決済専用イベント）: 約定の記録経路が 2 本になり、二重計上・取りこぼしの面が増える。既存経路に載せれば
  冪等性の担保をそのまま再利用できる。
