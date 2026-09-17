---
title: IADR-0346 1 日あたりの発注金額上限・段階資金・保有建玉数は、約定に加えて当日承認した未終端の新規建て注文の残数量を算入する。見送りは注文アクティビティの終端として射影する
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-10, FR-19, FR-20, ADR-0009, ADR-0016, ADR-0040, IADR-0005, IADR-0008, IADR-0018, IADR-0067, IADR-0113, IADR-0117, IADR-0130, IADR-0136, IADR-0163, IADR-0211, IADR-0246]
author: endazon (with Claude Code)
created: 2026-09-18
updated: 2026-09-18
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
related_specs:
  - ../specs/20260918_829_count-working-entry-orders.md
---

# IADR-0346: 1 日あたりの発注金額上限・段階資金・保有建玉数は、約定に加えて当日承認した未終端の新規建て注文の残数量を算入する。見送りは注文アクティビティの終端として射影する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は planning へ issue で環流する（`feedback.yml` テンプレート）。

- 状態: Accepted
- 日付: 2026-09-18
- 決定者: endazon（[#829](https://github.com/endazon/ai-stock-trading/issues/829)）/ Claude Code（起案）

## 起点・関連

- 計画 FR-10: 1 日あたりの発注金額上限（既定 equity の 150%/日）は「**新規建ての発注代金の合計**で判定し、手仕舞い〔決済〕注文は算入しない」。計画 05_trading-assumptions §5: 保有建玉数の上限 3、Stage 2 は「**発注可能額**をシステム側の統制で 30% に制限する」。
- 症状（#829・2026-09-17・SIMULATE・S2）: AAPL の新規買い（1 回約 28.3 万 USD）が 5 分ごとに承認され、承認・発注の累計は上限（約 170 万 USD）を超えたが、約定済みは一部だけだった。
- 既存の決定: IADR-0130 決定 4（日次枠はゲートとカウンタの両方で新規建てに限定）／ IADR-0005（段階資金は「投入中資金＋当該注文額」）／ IADR-0018（台帳は約定の純射影）／ IADR-0113（moomoo の約定追跡と累積数量の単調 upsert）／ IADR-0067（注文アクティビティの射影 `order_activity`）／ IADR-0117（処理中の決済数量＝承認数量 − 約定累計。**本決定の先例**）／ IADR-0211（見送り `OrderDispatchForgone`）／ IADR-0246（取引日は市場の現地日）／ IADR-0163 決定 2（不在が統制の無効を意味する依存は必須）。
- 作業仕様書: `.ai-context/specs/20260918_829_count-working-entry-orders.md`（母集合・実測）。

## コンテキスト

`PortfolioProjection.Project` は `DailyOrderedAmount`・`InvestedCapital`・`OpenPositionCount` を `trade_fills`（約定）だけから畳み込む。IADR-0113 は「約定が台帳へ届かなければ統制が素通しになる」穴（#270）を**約定を届ける**ことで塞いだが、**届く前の間**は依然として素通しである。paper は即時約定のため露呈せず、moomoo SIMULATE の指値（S2 で逆指値が付かず生き残る）で初めて顕在化した。

実測（2026-09-17 13:00Z 以降の新規建て承認 17 件。仕様書に表）: 取消 10・約定 5・**未終端 2**。未終端 2 件は翌日も `Accepted` のまま残っており、終端イベントが届かない行が実在する。

計画は日次枠を「発注代金」で定義しており、約定額で数えるのは**計画の定義からの乖離**である（計画の誤りではないため環流は不要）。

## 決定

### 決定 1: 未終端の新規建て注文は新しいポート `IWorkingEntryOrderSource` から得る（台帳の契約は変えない）

`IWorkingEntryOrderSource.GetWorkingEntryOrders(approvedAtOrAfter)` は、承認済み（`approved_orders`）・`PositionEffect.Open`・`ApprovedAt >= approvedAtOrAfter` で、**注文アクティビティが終端でない**注文を `WorkingEntryOrder`（DecisionId・銘柄・市場・方向・承認数量・承認価格・`FxRateToBase`・承認時刻）で返す。

- **終端の判定は `order_activity.TerminalAt is not null`**。`Status` ではなく終端時刻で見るのは、遅れて届いた非終端の `OrderExecuted` が `Status` を巻き戻しても `TerminalAt` は消えない（単調）ためである。
- **`order_activity` の行が無い承認は未終端として返す**（射影の到着前＝まだ生きている側へ倒す）。実測で行の欠けは 0 件。
- `IPortfolioLedgerStore` へメソッドを足さない。台帳は約定の純射影（IADR-0018）であり、注文の生死は注文アクティビティ（IADR-0067）の関心である。読み取り側の結合だけを新ポートに閉じる（テスト替え玉 2 件の追随も要らない）。

### 決定 2: 算入は `PortfolioProjection.Project` の純関数内で、約定と同じ入力から残数量を出す

`Project(fills, now, initialCapital, currentPrices, equityHighWaterMark, workingEntries = null)`。各注文について:

- **残数量 ＝ max(0, 承認数量 − 同じ `DecisionId` の約定累計)**。約定累計は**同じ `fills` 列**から数える。約定が届くと約定側が増えて残数量が同じだけ減るため、合計は変わらない＝**二重計上しない**。再配送は台帳（累積の単調 upsert・IADR-0113）と注文アクティビティ（DecisionId 冪等）が既に吸収しており、本関数は状態から毎回導出するので再送の影響を受けない。
- **当日の判定は承認時刻の市場の現地取引日**（`TradeDate(approvedAt, market) == TradeDate(now, market)`。約定と同じ規則・IADR-0246）。moomoo の注文は当日限り（発注に有効期限を指定していない）であり、**終端イベントが届かない行が翌日以降の枠を食い続けることを防ぐ**。窓の下限（`now − 2 日`）は走査量の上限にすぎず、判定は取引日で行う。
- 金額は `残数量 × 承認価格 × FxRateToBase`（基準通貨。約定の `PriceInBase` と同じ換算。承認時レートは約定時レートの近似として台帳に固定済み・IADR-0107）。

| 出力 | 算入 |
| --- | --- |
| `DailyOrderedAmount` | ＋ 残数量の発注代金（計画 FR-10 の定義そのもの） |
| `InvestedCapital` | ＋ 同額（計画 §5 は Stage の上限を「発注可能額」と呼ぶ。IADR-0005 の「投入中資金＋当該注文額」で累計超過を塞いだ穴〔#27〕が、未約定の間は開いたままになるため） |
| `OpenPositionCount` | ＋ **建玉の無い（銘柄, 市場）** の異なり数（既存建玉への建て増しは数を増やさない。建玉キーの粒度は従来のまま） |
| `SymbolsTradedToday` | **変えない**（決定 4） |

`workingEntries` の既定は `null`（＝従来どおり約定だけ）。純関数の既存の呼び出しと既存テストは変わらない。

### 決定 3: `LedgerPortfolioStateProvider` は注文源を**必須依存**で受ける

`LedgerPortfolioStateProvider(ledger, workingEntries, clock, currentPrices?, initialCapital?)`。IADR-0163 決定 2（不在が統制の無効を意味する依存は必須引数にする）に従う——省略可能にすると、配線を削ってもコンパイルが通り、未約定の算入だけが静かに外れる。本番配線は `EfWorkingEntryOrderSource`、テスト・単体実行は `InMemoryWorkingEntryOrderSource`（InMemory の台帳と注文アクティビティを合成）。

### 決定 4: 同日再エントリーの入力（`SymbolsTradedToday`）には算入しない

同日再エントリー禁止（差金決済防止・IADR-0132 決定 5）が塞ぐのは「**決済した同一銘柄を同じ日にもう一度建てる**」ことであり、決済は約定でしか成立しない。未約定の新規建てを加えても防ぐ事象が増えず、同一銘柄の建て増しを承認前に止める過剰拘束だけが増える。フォローアップは起票しない。

### 決定 5: 見送り（`OrderDispatchForgone`）を注文アクティビティの終端として射影する

発注執行が見送った承認（OpenD 不達・損切り価格なし・逆指値非対応・手法不許可。IADR-0211）はブローカーに存在せず、`OrderExecuted` も `OrderCancelled` も出ない。このままでは決定 1 の注文源が**当日中ずっと未終端として返し**、OpenD の再起動中に定時判断が見送られるたびに日次枠が枯れる。

- リスク管理が `OrderDispatchForgone` を購読し（`OrderDispatchForgoneActivityHandler`）、`IOrderActivityStore.RecordForgone(decisionId, symbol, market, side, quantity, occurredAt)` を呼ぶ。
- **状態は `OrderStatus.Rejected`・`TerminalAt = OccurredAt`**。見送りは板に載っておらず、相場操縦検知は `Rejected` を「約定意思の指標にならない」として母集団（約定なし取消）から外す（`OrderActivityRecord.IsCancelledWithoutFill`）。見送りを `Cancelled` にすると**短命の約定なし取消**として見せ玉の嫌疑を積むため採らない。契約（`OrderDispatchForgone`）が「拒否と別集計」と定めるのは FR-05 の約定結果の集計であり、`order_activity` は相場操縦検知の入力にすぎず集計面に現れない。
- **行が無ければ Intent から終端行を作る**（承認の射影より先に見送りが届いても、後着の `RecordPlacement` は既存 DecisionId を無視するため終端が保たれる）。**既に終端の行は変えない**。
- **IADR-0211 との関係**: 同 IADR は「存在しない注文に状態を与えると注文数・拒否数の集計が実態とずれる」として見送りをイベントで表した。その集計（監査台帳の EventType・FR-05 の拒否の別集計）は `order_activity` を読まない（`order_activity` の読み手は相場操縦検知の窓と本決定の注文源だけ。走査で確認）。相場操縦検知の分母（窓内の件数）には見送った承認が**本決定の前から** `Accepted` のまま入っており、状態を `Rejected` にしても件数は変わらない。したがって同 IADR の集計の分離は崩れない。
- **IADR-0211 決定 3（見送った注文の自動リプレイ経路を作らない）は維持する**。リスク管理での `OrderDispatchForgone` の購読は本ハンドラ 1 つに限り、依存は `IOrderActivityStore` だけである（発行・台帳・審査へ届かない）。これを構造テスト（`RejectionSeparationTests`）で固定する——従来の「リスク管理は購読しない」テストは、この限定形へ置き換えた。

## 棄却した案

| 案 | 棄却理由 |
| --- | --- |
| 承認時点で枠を予約し、約定・終端で戻す（予約テーブル） | 状態を 2 か所に持つ。戻し損ね（イベント欠落）が恒久の枠漏れになる。本決定は既存の 2 射影から毎回導出するため、欠落は当日の取引日で自然に消える |
| `order_activity.FilledQuantity` で残数量を出す | 約定累計が台帳と注文アクティビティの 2 か所から来ると、片方だけ更新された瞬間に二重計上または取りこぼしになる。約定分を数えるのと同じ `fills` から引く |
| 終端判定を `Status` で行う | `RecordExecution` は `Status` を無条件に上書きするため、遅着の非終端イベントで生き返る。`TerminalAt` は消えない |
| 承認時刻で窓を切らず、未終端ならいつまでも算入する | 実測で翌日も `Accepted` のまま残る行がある。1 件で以後の全取引日の枠を食う（fail-closed の方向だが、利用者の介入なしに解けない停止になる） |
| 日次枠だけを直し、段階資金・保有建玉数は後続 issue にする | 3 値とも同じ「未約定の間は累計で超過できる」穴（#27 と同型）であり、入力も同じ注文源から出る。分けると同じ事故が段階資金で再発する |
| `approved_orders` に見送り列を足す（マイグレーション） | 注文の生死を台帳へ持ち込む（決定 1 の関心の分離に反する）。`order_activity` は既に終端を表す列を持つ |

## 結果

- 良い点: 日次枠・段階資金・保有建玉数が「発注済みで生きている」注文を数え、指値が溜まっている間に上限を超えて承認し続けることが無くなる。サイジング文脈（`SizingContextService`）の残枠も同じだけ減るため、上限の手前で数量が縮む。取消・失効・見送りは即座に枠を返す。
- 注意点（残余リスク）:
  - **終端イベントが届かない注文は当日中は枠を食う**（fail-closed）。約定追跡の打ち切り（`FillPolling:MaxTrackingHours`）や照会不能が続くと当日の新規建てが止まり得る。翌取引日には算入されない。
  - 注文の訂正（`OrderModified`）で数量が減っても、算入は承認数量で行う（過大＝安全側）。訂正の呼び出し元は現状無い（IADR-0067 決定 6）。
  - 承認→`OrderApproved` の射影（台帳・注文アクティビティ）はメッセージ経由であり、承認直後の数秒は次の審査から見えない。定時判断の周期（5 分）に比べて無視できる。
  - 同一瞬間に複数の判断が並行審査されると、互いの未約定を見ない（審査の直列化は本決定の範囲外。従来の約定ベースでも同じ）。
- 追随: 機能仕様書 FR-10 §統制の入力の前提、テスト仕様書 FR-10（T-10-333〜T-10-338）。
