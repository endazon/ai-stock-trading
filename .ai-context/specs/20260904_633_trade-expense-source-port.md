---
title: 取引の経費明細の取得ポートを定義し、取れないことを本番で記録する（段1）
type: spec
status: draft
related_ids: [FR-11, FR-16, UC-07, ADR-0016, ADR-0027, IADR-0226, IADR-0183, IADR-0300]
author: claude
created: 2026-09-04
updated: 2026-09-04
plan_refs: [FR-11, FR-16, UC-07, ADR-0016, ADR-0027]
---

# 仕様書: 取引の経費明細の取得ポートを定義し、取れないことを本番で記録する（段1・#633）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/ai-stock-trading/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-11（取引記録・監査ログ。経費区分と建玉単位の紐づけ）／FR-16（報告書の数値はコード集計）
- ユースケース（UC）: UC-07（監査ログの参照）
- 画面（SC）: なし
- 関連 ADR: ADR-0016 決定15（経費区分 7 種・建玉単位で紐づけられること・「集計は後から作れても記録は
  遡って復元できない」）／ADR-0027 決定2（建玉ごとに積み、銘柄・口座へは合算で導出する）／
  決定4（取得できなかったぶんを 0 として積まない）／NFR-10（業務台帳・監査証跡は 7 年保持）
- 関連する実装ADR: IADR-0226（区分 7 種の置き場・建玉紐づけ・保存先＝監査台帳・**供給は範囲外**）／
  IADR-0183（借株料の記録側。計上できた日と料率が取れなかった日を**別の型・別のイベント**へ分けた前例）

## 目的・背景

issue #633（由来: #204 実装監査 更新版 2026-09-02）が検出した欠落を、**2 段に分けたうちの段 1** として直す。

実測（本作業の着手時に再確認した）:

- `new TradeExpenseRecorded(` は**全 8 件がテストコードの中**であり、本番コードからの発行は 0 件である。
- `TradeExpenseLedger` にも本番コードからの参照が無い（テストのみ）。
- 受け皿（契約イベント `TradeExpenseRecorded`・`event-schemas.baseline.json` の登録・AuditService の
  `TradeExpenseRecordedAuditHandler` と `AuditEntryFactory.From`）は**すべて既に在る**。
- 7 区分のうち実費が供給されている区分は 1 つも無い。`OrderExecuted` は手数料を運ばず、
  moomoo アダプタの `MMApiMoomooTradeClient.OnReply_GetOrderFee` は**空実装**である（実口座待ち）。

IADR-0226 決定7 は「供給（実費を積み始めること）は範囲外」と明示して遮断した。その遮断は正しかったが、
**遮断した結果が本番のどこにも現れていない**。現状は「経費を照会する経路そのものが存在しない」のであって、
「照会したが 1 件も無かった」でも「照会できなかった」でもない。この 3 つが外から区別できないことが
issue #633 の実体である。

## 段の切り方（この分割は確定・変更しない）

| 段 | 内容 | 本 PR |
| --- | --- | --- |
| **段 1** | 実費用の取得**ポート**を定義し、既定実装を「取得不能（`Unavailable`）を返す no-op」とする。約定のたびに本番で照会が走り、**取れなかったこと（7 区分すべて `LineCount` = 0）が記録される**ようにする | **本 PR** |
| 段 2 | 実費用の取得（`OnReply_GetOrderFee` の実装・`TradeExpenseRecorded` の実発行・二重計上の防止） | **別 PR**（moomoo 実口座待ち） |

段 2 に手を出さない理由は、値を発明しないためである。`OnReply_GetOrderFee` の応答仕様（区分の粒度・
通貨・手数料と諸費用の切れ目）は実口座でしか確かめられず、ここで概算（`CostCalculator`）を実費として
積むと ADR-0027 が塞いだ「表示されている数字が何を意味するか誰も答えられない」状態へ戻る。

## 対象範囲

- 対象（すべて `backend/Services/OrderExecutionService/`）:
  - `Features/OrderExecution/RecordTradeExpenses/IOrderExpenseSource.cs`（新規）
    — 経費明細の取得ポート・照会結果の型（`OrderExpenseLookup`）・照会の入力（`OrderExpenseQuery`）
  - `Features/OrderExecution/RecordTradeExpenses/TradeExpenseRecordingService.cs`（新規）
    — 約定 1 件に対して照会し、発行すべき `TradeExpenseRecorded` と**建玉の 7 区分集計**を返す
  - `Infrastructure/ExternalServices/UnsuppliedOrderExpenseSource.cs`（新規）
    — 既定実装（**常に `Unavailable`**）
  - `Program.cs`（DI 登録）・`Infrastructure/Steps/OrderApprovedHandler.cs`・
    `Hosted/OrderFillPollingService.cs`（呼び出しの結線）
  - `Tests/Features/OrderExecution/RecordTradeExpenses/TradeExpenseRecordingServiceTests.cs`（新規）
  - `Tests/Infrastructure/ExternalServices/UnsuppliedOrderExpenseSourceTests.cs`（新規）
- 対象外（＝入れないもの。理由つき）:
  - **契約イベントの新設**（`TradeExpenseUnavailable` 等）。理由は後述「設計 §4」。
    `event-schemas.baseline.json`・`EventTypeDiscovery`・AuditService の全数レジストリは**触らない**。
  - **専用の永続テーブル**（`trade_expenses`）。IADR-0226 決定4 が「同じ事実の権威が 2 つになる」ため
    禁じている。段 2 でも作らない。
  - **報告書の費用表示を概算から実績へ切り替えること**。issue #633 本文が「別途判断する」としており、
    `PnlAggregator` の `CostCalculator` 経路は 1 行も触らない。
  - **moomoo の `OnReply_GetOrderFee` の実装**（段 2）。
  - **`docs/functional/` と `docs/tests/`**。網羅裁定（`docs/README.md`）の必須範囲は
    FR-10 / FR-12 / FR-15 / FR-19 / FR-20 であり、**FR-11 / FR-16 は必須範囲に含まれない**
    （作業仕様書と xUnit テストを正の記録とする）。

## 設計

### 1. ポート（`IOrderExpenseSource`）

```
Task<OrderExpenseLookup> GetOrderExpensesAsync(OrderExpenseQuery query, CancellationToken ct)
```

`OrderExpenseQuery` は約定 1 件を指す（`OrderId` / `DecisionId` / `Symbol` / `Market` / `ExecutedAt`）。
返す `OrderExpenseLookup` は次の 2 状態**だけ**を持つ。

| 状態 | 生成 | 意味 |
| --- | --- | --- |
| 供給された | `OrderExpenseLookup.Supplied(lines)` | 照会できた。`lines` が空なら「照会できて 1 件も無かった」 |
| 取得できない | `OrderExpenseLookup.Unavailable(reason)` | 照会できなかった。**金額の欄を持たない** |

🔴 **`Unavailable` に金額の欄を作らない。** これは IADR-0183（借株料）が
`BorrowFeeAccrued` と `BorrowFeeAccrualUnavailable` を別の型へ分けたのと同じ規律である。
1 つの型に畳んで金額を null 許容にすると、「未供給を 0 として合計へ混ぜる」経路が型の上で表現可能になる。
`Supplied` からしか `Lines` を取り出せないようにし、`Unavailable` で `Lines` を読むと例外で落とす。

### 2. 既定実装（`UnsuppliedOrderExpenseSource`）

**常に `Unavailable`** を返す。理由文には「ブローカーから経費明細を照会する実装が無い（段 2）」を持たせる。

🔴 **空の `Supplied` を返さない。** 空を返すと「照会できて費用が 1 円も無かった」と読め、段 2 の結線を
忘れた期間がそのまま「費用なし」で通る（`UnsuppliedBorrowFeeRecordSource` が同じ理由で空の
`BorrowFeeRecord` ではなく `null` を返している）。

### 3. 記録（`TradeExpenseRecordingService`）

約定 1 件（`OrderExecuted`）に対して次を行う。

1. `FilledQuantity` が 0 なら**何もしない**（約定していない注文に経費は発生していない）。
2. `IExecutedOrderStore.FindByDecisionId` で建玉の一次識別子 `(Symbol, Market)` を得る
   （`OrderExecuted` は銘柄を運ばない。**発注記録が権威**であり、ここで別に持ち回らない）。
   記録が見つからなければ推測せずに打ち切る（fail-safe）。
3. ポートへ照会する。ポートが例外を投げても**発注執行を止めない**（握って「取得できない」へ倒す）。
4. 結果を `TradeExpenseRecordingOutcome` として返す。
   - `Supplied` → 明細 1 行につき `TradeExpenseRecorded` を 1 本。
   - `Unavailable` → **イベントは 0 本**。理由を保持する。
5. いずれの場合も、`TradeExpenseLedger.SummarizePosition(lines, symbol, market)` で
   **7 区分ぶんの集計**を作って返す。段 1 では常に **7 区分すべてが `LineCount` = 0** になる。

🔴 **区分できない費用を既存区分へ丸めない。** 本サービスは区分を推定しない。区分は明細
（`TradeExpense.Category`）としてポートから来るものだけであり、`Commission` を既定にする分岐は無い。

### 4. 記録先（なぜ新しい契約イベントを作らないか）

「取れなかった」を専用の契約イベント（`TradeExpenseUnavailable`）にはしない。

- 借株料（IADR-0183）が未供給を専用イベントにしたのは、**建玉 × 取引日という決まった母集合**があり、
  「この日は計上できなかった」が後から日別に照合できる値だからである。経費の未供給には対応する
  母集合が無い（ブローカーが何区分を返す予定なのかは実口座で確かめるまで分からない＝段 2）。
  母集合の分からない否定形を 7 年保持の監査台帳へ 1 約定につき 1 本積むと、**復元できるのは
  「実装が無かった」という実装側の事実だけ**であり、取引の事実ではない。
- 監査台帳へ入る「経費が 1 件も無い」は、`TradeExpenseRecorded` が 0 本であることで既に表現されている。
  `TradeExpenseLedger` が 7 区分を `LineCount` = 0 で返す契約（IADR-0226 決定5）がその読み取り側であり、
  本 PR はその集計を**本番の経路で作る**ことで否定形を成立させる。
- 段 1 の観測は**構造化ログ**（`OrderApprovedHandler` / `OrderFillPollingService`）で行う。理由と
  建玉 `(Symbol, Market)`・7 区分すべてが 0 件であることを 1 行に載せる。

### 5. 結線（本番で必ず走ること）

約定を観測する既存の 2 点へ結線する。**新しい定期実行・新しいフラグ・新しいストアを作らない。**

| 結線先 | いつ走るか |
| --- | --- |
| `Infrastructure/Steps/OrderApprovedHandler` | 発注が即時約定したとき（内蔵 paper・即時 Filled） |
| `Hosted/OrderFillPollingService` | 後から約定が成立したとき（moomoo。発注時は Accepted のため必ずこちら） |

いずれも `OrderExecuted` を発行した**直後**に呼ぶ。段 1 の既定ではイベントが 0 本のため
`PublishAsync` は 1 度も呼ばれず、**発行の面での挙動は不変**である（増えるのはログ 1 行だけ）。

🔴 **段 2 の前提**: 供給が始まった時点で `TradeExpenseRecorded` の発行は**二重計上の防止**が要る
（メッセージ再配送・約定追跡の複数巡回で同じ約定を 2 度観測し得る）。重複排除の鍵は
`TradeExpense.SourceId` である。段 2 の PR はこれを実装するまで供給を有効にしてはならない。
本 PR は発行点にこの注意をコメントとして残す。

## 実装タスク

1. `IOrderExpenseSource` / `OrderExpenseQuery` / `OrderExpenseLookup` を新設する。
2. `UnsuppliedOrderExpenseSource`（常に `Unavailable`）を新設し DI の既定にする。
3. `TradeExpenseRecordingService` を新設する。
4. `OrderApprovedHandler` と `OrderFillPollingService` から呼ぶ。
5. テストを追加する（下記）。
6. 実装ADR（IADR-0300）を書き、`.ai-context/adr/README.md` の索引へ 1 行足す。

## テスト方針

`backend/Services/OrderExecutionService/Tests/`（xUnit v3 + AwesomeAssertions）。起点 ID
（FR-11 / FR-16 / ADR-0016 / ADR-0027）をコメントに残す。

- 既定実装は**常に**「取得できない」を返す（空の `Supplied` を返さない＝否定形）。
- `Unavailable` から `Lines` を読むと例外で落ちる（型で「0 として混ぜる」経路を塞いだことの固定）。
- 未供給のとき: 発行イベントは 0 本、集計は**7 区分すべてが `LineCount` = 0**（受け入れ基準の否定形）。
- 未供給のとき: 区分が `Commission` などへ丸められていない（**7 区分どれも金額 0・件数 0**）。
- 供給されたとき: 明細 1 行につき 1 本発行され、`(Symbol, Market)` の建玉へ紐づき、
  `TradeExpenseLedger.SummarizeByPosition` で集計できる。
- 約定していない（`FilledQuantity` = 0）注文は照会しない（ポートが 1 度も呼ばれない）。
- 発注記録が見つからない場合は推測せず打ち切る（fail-safe）。
- ポートが例外を投げても発注執行を止めない（fail-safe）。

## 受け入れ基準（issue #633 のうち段 1 が担う範囲）

- [x] 経費明細の取得ポートが存在し、既定は「取得できない」を返す no-op である
- [x] 約定 1 件ごとに本番の経路で照会が走り、取れなかったことが（建玉単位・7 区分 `LineCount` = 0 で）記録される
- [x] 記録は `(Symbol, Market)` の建玉単位で紐づき、`TradeExpenseLedger.SummarizeByPosition` で集計できる
- [x] 否定形: 区分が取得できない費用を `Commission` などの既存区分へ丸めない
- [x] 否定形: 明細が 0 件の建玉について 7 区分を `LineCount` = 0 で返す
- [x] 起点 ID コメント付きのテストを添える
- [ ] （段 2）約定 1 件に対し発生した経費区分ぶんの `TradeExpenseRecorded` が publish され、監査台帳へ記録される

## 母集合の取り方（走査と、除外したものとその理由）

| 軸 | 走査 | 結果 |
| --- | --- | --- |
| 発行元 | `new TradeExpenseRecorded(` | 8 件・**すべてテスト**（AuditService の 3 ファイル） |
| 集計の利用 | `TradeExpenseLedger` | 本番参照 0 件（AuditService のテスト 2 ファイルのみ） |
| ブローカーの費用照会 | `GetOrderFee` | `MMApiMoomooTradeClient.cs:575` の**空実装 1 件のみ** |
| 概算の費用 | `EstimateOneWayCost` | `PnlAggregator` ほか。**本 PR の対象外**（issue が「別途判断」と明記） |

除外したものと理由:

- **`ReportService`**: 費用表示の切り替えは issue が範囲外と明記している。
- **`RiskManagementService` の `BorrowFeeAccrualService`**: 借株料は既に別経路（ADR-0027）を持つ。
  段 1 で経費台帳へ二重に流すと、同じ事実が 2 系統になる。
- **`event-schemas.baseline.json` / `EventTypeDiscovery` / AuditService**: 新しい契約イベントを
  作らないため、触る必要が無い（触っていないことが「受け皿は既に在る」ことの裏返しである）。

## 計画書との差異

無し。ADR-0016 決定15・ADR-0027 決定2/決定4 の範囲内であり、IADR-0226 決定7 が範囲外とした「供給」も
本 PR では開けない（段 2）。
