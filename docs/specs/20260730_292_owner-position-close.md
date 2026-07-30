---
title: 建玉の owner 決済経路（部分/全量・正規の注文パス・統制で止めない）
type: spec
status: review
related_ids: [FR-05, FR-10, FR-11, UC-02, UC-06, ADR-0003, ADR-0007]
author: endazon (with Claude Code)
created: 2026-07-30
updated: 2026-07-30
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0007_risk-control-authority.md
---

# 仕様書: 建玉の owner 決済経路

> 利用者指示・設計承認（2026-07-30）。[#292](https://github.com/endazon/ai-stock-trading/issues/292) の
> **PR 1/3**。本 PR は「利用者が建玉を手仕舞う経路」に閉じる。
> ブローカ突合は PR 2/3、判断由来の決済（AI の出口）は PR 3/3。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）:
  - **FR-10**（リスク統制）— 本文に「kill switch・日次損失ロックアウト・一時停止は…**いずれも手仕舞い（Close）と
    損切りは止めない**」と明記されている。本 PR の統制迂回はこの要求の実装であり、新規の裁定ではない。
  - FR-05（発注・注文状態追跡）／FR-11（監査ログ）
- ユースケース: UC-02（価格変動起点の取引）・UC-06（利用者による統制操作）
- ADR: [ADR-0007](../../planning/projects/ai-stock-trading/07_adr/ADR-0007_risk-control-authority.md)（統制の権限＝
  変更操作は利用者のみ）、[ADR-0003](../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md)
- 関連 IADR: [IADR-0015](../adr/IADR-0015_stop-loss-mechanical-close.md)（損切りの機械執行・スクリーニング迂回の先例）／
  [IADR-0018](../adr/IADR-0018_portfolio-ledger-projection.md)（取引台帳と射影）／
  [IADR-0004](../adr/IADR-0004_position-effect-entry-scoping.md)（エントリー判定は `PositionEffect`）／
  [IADR-0107](../adr/IADR-0107_base-currency-conversion.md)（基準通貨と `FxRateToBase` の引き継ぎ）／
  [IADR-0057](../adr/IADR-0057_order-dispatch-idempotency.md)（発注冪等化）／
  本作業で新規 [IADR-0117](../adr/IADR-0117_owner-position-close-path.md)
- 対象 Issue: [#292](https://github.com/endazon/ai-stock-trading/issues/292)（`Refs #292`）・
  傘 [#279](https://github.com/endazon/ai-stock-trading/issues/279)

## 現状（この変更の直前・実コードで確定）

| 面 | 実態 |
| --- | --- |
| `PositionEffect.Close` の発生源 | `StopLossExecutionService.BuildCloseApproval()` **のみ**（`StopLossTriggered` 購読時） |
| 判断由来の発注 | `TradeDecisionService.cs:208` が `PositionEffect.Open` を**リテラル固定**（IADR-0004） |
| 利用者の決済手段 | **無い**。`/risk-controls` は kill switch・pause・設定・段階ゲートのみ |
| 帰結 | 損切りラインに触れない限り建玉は永久に手仕舞いできない。#270 破損期に積み上がった過大建玉を正規手段で清算できない |

## 目的

1. 利用者（owner）が保有建玉を**部分／全量**決済できる。
2. 決済は**正規の注文パス**（`OrderApproved` → 発注執行 → ブローカ）を通り、約定・台帳・枠回復・通知まで既存経路に載る。
3. 在庫を超える決済・多重投入による**過剰決済（意図しないショート化）を構造的に拒否**する。
4. kill switch・日次損失ロックアウト・一時停止で**阻害されない**（FR-10）。
5. 「誰が・いつ・何を・なぜ」決済したかが監査に残る（FR-11）。

## 設計

### 1. 配置: リスク管理サービス（`PositionCloseService`）

決済の材料（台帳の建玉・`FxRateToBase`・段階 Mode・現在値キャッシュ）はすべて Risk にある。発注執行側へ置くと
同じ情報を s2s で取り直すことになる。損切りの機械執行（`StopLossExecutionService`）と**同じ層・同じ出力**
（`OrderApproved`）に揃える。

```
PositionCloseService.Request(PositionCloseCommand, actor) -> PositionCloseOutcome
    PositionCloseCommand = { Symbol, Market, Quantity?, LimitPrice?, Reason }
    PositionCloseOutcome = { Rejection, OrderApproved?, PositionCloseRequested? }
    PositionCloseRejection = None | PositionNotFound | InvalidQuantity | ExceedsAvailable | PriceUnavailable
```

判定順序（純粋・DB 書き込みなし）:

```
positions = PortfolioProjection.ProjectOpenPositions(ledger.GetFills())
pos       = positions[(Symbol, Market)]                    ; 無ければ PositionNotFound
inFlight  = ledger.GetInFlightCloseQuantity(Symbol, Market, now - InFlightWindow)
available = pos.Quantity - inFlight                        ; <= 0 なら ExceedsAvailable
quantity  = Quantity ?? available                          ; <= 0 なら InvalidQuantity
                                                           ; > available なら ExceedsAvailable
price     = LimitPrice ?? currentPrice[(Symbol, Market)]    ; 非正・欠損なら PriceUnavailable
side      = pos.Side == Buy ? Sell : Buy                    ; 建玉方向の反対売買
intent    = OrderIntent(..., Cash, settings.Stage.Mode, quantity, price, PositionEffect.Close,
                        StopLossPrice: null, FxRateToBase: pos.FxRateToBase)
```

- **`Side` はサーバが建玉方向から決める。** クライアントに方向を持たせない（誤方向指定で建て増しさせない）。
- **数量省略＝全量**は「保有数量」ではなく **`available`**（処理中を除いた残り）。処理中がある状態での全量指定が
  在庫超過にならない安全側の解釈。
- `FxRateToBase` は建玉の加重平均約定時レート（`ProjectOpenPositions` が導出）を引き継ぐ。引き継がないと決済レグ
  だけ未換算で台帳に積まれ、基準通貨の実現損益が桁で狂う（IADR-0107・損切り決済と同一規則）。
- `StopLossPrice` は `null`。決済注文に損切り価格は無い（IADR-0035 の「一部決済では既存の損切りを保持」に従い、
  射影側で建玉の損切りが維持される）。

### 2. 過剰決済ガード（本設計の要）— `GetInFlightCloseQuantity`

取引台帳は**約定でしか動かない**。決済要求を投げてから約定が台帳に届くまでの間、建玉数量は減らない。
したがって在庫チェックを「台帳の建玉数量」だけで行うと、**二重送信で意図しないショートを作れてしまう**
（現物のみ有効な現段階では、ブローカ側で拒否されるか、あるいは受理されて建玉が反転する）。

`IPortfolioLedgerStore` に読み取りメソッドを 1 つ足す:

```csharp
/// 指定銘柄について、approvedAtOrAfter 以降に承認された Close 注文の「未約定数量」合計を返す。
int GetInFlightCloseQuantity(string symbol, Market market, DateTimeOffset approvedAtOrAfter);
```

- 未約定数量 = `承認数量 − 当該 DecisionId の約定数量合計`（負にはクランプしない＝ `Math.Max(0, …)`）。
- **時間窓（既定 30 分）で切る。** 窓が無いと、#270 破損期のような「永久に約定しない古い承認」が決済を
  **恒久的にブロック**する。窓を過ぎた承認は「もう成立しない」とみなし在庫を解放する。
  窓は `PositionCloseService` のコンストラクタ引数（既定 30 分）で、構成キーは持たせない
  （運用で触る値ではなく、触れるようにすると二重決済の窓を広げられてしまうため）。
- 新規テーブル・新規列・Migration は**無い**（既存の `approved_orders` × `trade_fills` の読み取りのみ）。

### 3. エンドポイント（OwnerOnly）

```
POST /risk-controls/positions/close
{ "symbol": "AAPL", "market": 1, "quantity": null, "limitPrice": null, "reason": "…" }
→ 202 { decisionId, symbol, market, side, quantity, price, mode }
```

| 事象 | 応答 |
| --- | --- |
| 受理 | **202 Accepted**（約定は後から非同期に成立するため 200 ではない） |
| `symbol` / `market` / `reason` 欠如 | 400（`market` は `Market?` で受ける。非 nullable enum は省略時に暗黙 0＝日本市場へ束縛されるため） |
| 該当建玉なし | 404 |
| 数量・価格の不正、在庫超過 | 422 |

- 認可は既存の `owner` サブグループ（`AiStockTradingAuthPolicies.OwnerOnly`）。サービストークンには**開かない**
  （生成AI・自動処理が建玉を落とせないようにする＝ADR-0007 の最小権限）。
- **`OrderScreeningService` を通さない。** 損切りの機械執行と同型（IADR-0015）。kill switch・pause・
  日次損失ロックアウト・取引ガード・段階資金上限のいずれでも止まらない。根拠は FR-10 本文。

### 4. 発行と監査

受理時に 2 イベントを**この順**で発行する（同一 `DecisionId` で相関）。

1. `PositionCloseRequested(DecisionId, Symbol, Market, Side, Quantity, Price, Actor, Reason, RequestedAt)` — 新規。
   `OrderApproved` は**アクターも理由も持たない**ため、これが無いと「誰が・なぜ落としたか」が監査に残らない（FR-11）。
2. `OrderApproved(DecisionId, Intent, Quantity, ApprovedAt)` — 既存。以降は完全に既存経路。

順序の根拠: 「起きた操作に監査が無い」より「監査があるのに操作が無い」ほうが安全（後者は同一 `DecisionId` の
後続イベント不在で検知できる）。

以降は**新しい経路を一切作らない**:

```
OrderApproved ─▶ OrderApprovedLedgerConsumer（Risk 台帳へ Intent 記録）
              └▶ OrderApprovedConsumer（発注執行・予約 → ブローカ SELL）
                 └▶ OrderExecuted ─▶ OrderExecutedLedgerConsumer（trade_fills・建玉減・実現損益・枠回復）
                                  ├▶ OrderExecutedNotificationConsumer（Discord 通知）
                                  └▶ OrderExecutedAuditConsumer（監査）
```

二重計上が起きないのは、決済が**既存の約定記録経路にしか載らない**ため（`OrderId` 単位の単調 upsert・IADR-0113）。

### 5. 構成

**構成キーを一切足さない。** 常時有効（利用者の明示操作でしか動かないため、opt-in ゲートは摩擦にしかならない）。
Helm / values / compose / `.env.example` は**不変**。

## 影響範囲

| 対象 | 変更 |
| --- | --- |
| `Shared.Contracts` | `PositionCloseRequested`（新規イベント 1 件） |
| `Shared.Contracts.Tests` | `event-schemas.baseline.json` ＋ URN 固定 `[InlineData]` 追加 |
| `RiskManagementService.Application` | `PositionCloseService`（新規）、`IPortfolioLedgerStore.GetInFlightCloseQuantity`（追加）、`InMemoryPortfolioLedgerStore` 追随 |
| `RiskManagementService.Worker` | `EfPortfolioLedgerStore` 追随、`POST /risk-controls/positions/close`、DI 登録 |
| `AuditService` | `AuditEntryFactory.From(PositionCloseRequested)` ＋ Consumer（カバレッジテストが強制） |
| DB スキーマ / Migration | **無し**（既存テーブルの読み取りのみ） |
| Helm / compose / values / `.env.example` | **不変** |
| 実弾ゲート（閂 0〜4） | **不変**（新しいブローカ呼び出しは 1 つも足さない。既存の発注パスを通るだけ） |

## テスト（受け入れ基準の写像）

| # | 観点 | テスト |
| --- | --- | --- |
| 1 | 全量決済 | ロング 100 → `Sell` / 数量 100 / `PositionEffect.Close` / `FxRateToBase` 引き継ぎ |
| 2 | 部分決済 | `quantity=40` → 40 のみ。建玉は残る |
| 3 | ショート建玉 | `Sell` 建て → `Buy` で手仕舞う |
| 4 | 建玉なし | `PositionNotFound` |
| 5 | 数量不正 | `0` / 負 → `InvalidQuantity` |
| 6 | 在庫超過 | 保有 100 に `quantity=101` → `ExceedsAvailable` |
| 7 | 多重投入 | 未約定 Close 60 が窓内 → 保有 100 でも `available=40`。`quantity=50` は `ExceedsAvailable`、省略時は 40 |
| 8 | 古い滞留の解放 | 窓外（31 分前）の未約定 Close は `available` を減らさない |
| 9 | 部分約定の反映 | 承認 60・約定 20 → 未約定は 40 として数える |
| 10 | 価格 | `limitPrice` 指定＞現在値。両方無い／非正 → `PriceUnavailable` |
| 11 | Mode | `settings.Stage.Mode` が Intent に載る |
| 12 | 相関 | `PositionCloseRequested.DecisionId == OrderApproved.DecisionId` |
| 13 | 認可 | 未認証 401／`trading-service` ロール 403／`trading-owner` 202 |
| 14 | **統制で止まらない** | kill switch 起動中・pause 中でも 202 で受理され `OrderApproved` が発行される（FR-10） |
| 15 | 入力検証 | `market` 省略で 400（暗黙 0 に束縛されない）／`reason` 空で 400 |
| 16 | HTTP 写像 | 建玉なし 404／在庫超過 422 |
| 17 | 台帳 | `GetInFlightCloseQuantity` が Ef / InMemory の両実装で同一意味論（`Open` 効果は数えない・他銘柄は数えない） |
| 18 | 監査 | `PositionCloseRequested` に監査 Consumer が存在（カバレッジテスト）／Summary に actor・理由・数量が入る |
| 19 | 契約 | URN 固定・後方互換 baseline に新イベントが登録されている |

## 受け入れ基準（`docs/DEFINITION_OF_DONE.md` と併せて）

- [ ] owner が建玉の全量／部分決済を要求でき、在庫超過・多重投入が構造的に拒否される
- [ ] kill switch／ロックアウト／pause で決済が阻害されない
- [ ] 決済の要求（誰が・いつ・何を・なぜ）が監査へ残る
- [ ] 決済の約定が既存経路で台帳へ届き、Discord 通知される（新規経路を作らない）
- [ ] SIMULATE 限定・実弾 OFF が不変。Helm / values / Migration に差分が無い
- [ ] `dotnet build` / `dotnet test` / `dotnet format` が green・CI / gitleaks が green

## スコープ外

- **ブローカ実ポジションとの突合**（PR 2/3・IADR-0118）
- **判断由来の決済＝AI の出口**（PR 3/3・IADR-0119）。本 PR の後も `TradeDecisionService` は `PositionEffect.Open`
  固定のままで、AI は自分で建玉を落とせない
- Discord `/close` コマンド（HTTP 経路のみ。Bot 経路は別 issue）
- 決済注文の訂正・取消（moomoo 経路には訂正・取消の口を配線しない既存方針を維持）
