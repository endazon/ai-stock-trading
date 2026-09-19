---
title: 手仕舞いを成行で出せるようにし、板に残った手仕舞いを利用者が取り消せるようにし、失効した手仕舞いを通知する
type: spec
status: accepted
related_ids: [FR-05, FR-09, FR-10, FR-11, FR-19, UC-02, UC-06, ADR-0002, ADR-0003, ADR-0013, ADR-0016, ADR-0040, IADR-0018, IADR-0057, IADR-0067, IADR-0092, IADR-0113, IADR-0117, IADR-0129, IADR-0210, IADR-0211, IADR-0335, IADR-0342, IADR-0346, IADR-0347, IADR-0357]
author: endazon (with Claude Code)
created: 2026-09-19
updated: 2026-09-19
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10「kill switch・日次損失ロックアウト・一時停止はいずれも手仕舞い〔Close〕と損切りは止めない」・FR-11 監査・FR-09 通知)
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md (UC-06 利用者による建玉の手仕舞い)
---

# 仕様書: 手仕舞いの成行・取消の口・失効の通知（#847）

## 起点

- #847（bug・**稼働環境で実測**。2026-09-18 23:13 JST）。#768（`OrderAmendmentDispatcher` に呼び出し元が無い）を含む。
- 症状: 利用者が建玉 3,381 株の手仕舞いを要求した。手仕舞い API は `limitPrice` を省くと**その時点の現在値**で
  売り指値を出す。発注時 334.09 だったが直後に 333.59 まで下げ、**約定しないまま板に残った**
  （23:13 / 23:18 とも `filled=0 status=0(Accepted) planned=334.09`・評価損益 −5,133 USD）。
- 板に残った注文が数量を占めるため（`ExceedsAvailable`）**指値を変えて出し直せず**、
  発注執行に**取消のエンドポイントが無い**ため、利用者は moomoo アプリから手で取り消すしかなかった。
- 当日注文のため、約定しなければ引け後に失効して**黙って建玉が残る**。

## 射程（issue の 3 点。すべて本 PR）

1. **手仕舞いに成行を選べるようにする。** 現在値の指値は、下落局面＝手仕舞いが要る場面でこそ置いていかれる。
2. **板に残った手仕舞い注文を取り消す経路。** 利用者専用（OwnerOnly）・理由必須・監査に誰が・なぜが残る。
   `OrderAmendmentDispatcher.CancelAsync`（実装済み・DI 登録のみで呼び出し元が無い＝#768）を本 PR で配線する。
3. **引け跨ぎで失効した手仕舞いを検知して利用者へ知らせる。**

**射程外**: #864（Close 発注前のブローカー実建玉突合・`DispatchApprovedOrder/` 配下）／
#852（見送りが在庫をロックする・`IPortfolioLedgerStore` の `MarkTerminal` 周り）／
注文の**訂正**（`ModifyAsync`）の駆動元（#768 のもう半分。本 PR は取消だけを配線する）。

## 起点の前提のうち、実測で異なっていた点

- issue 本文と親タスクは「S1 のソフトウェア逆指値が成行で決済する設計（`SoftwareStopExecutor`）」を引くが、
  **`SoftwareStopExecutor` という型はリポジトリに存在しない**（`grep -rn "SoftwareStop" backend --include=*.cs`）。
  S1（`StopLossExecutionMethod.SoftwareStop`）は**未実装**（#820）で、現状は S0 と同じ扱いである
  （`StopLossExecutionMethod.cs` の XML ドキュメントが自認）。
- 実在する「成行で手仕舞う」設計は **`IProtectiveOrderBroker.PlaceMarketOrderAsync`**（IADR-0210）であり、
  保護逆指値が成立しないときの建玉解消（`OrderExecutionAppService.CloseUnprotectedPositionAsync` と
  `ProtectiveStopGuard.ReplaceOrCloseAsync`）が呼んでいる。**本 PR はこの既存の口へ揃える**
  （新しいブローカー呼び出しを 1 つも増やさない）。
- 「失効した手仕舞いが通知されない」も厳密には違い、`OrderExecuted` は
  `NotificationFormatter.From(OrderExecuted)` で Warning になる。ただし文面は
  `約定 Expired 数量0@0（OrderId=…・DecisionId=…）` だけで、**それが手仕舞いだったことも、建玉が残っていることも
  書かれていない**。本 PR が足すのはそこ（「何株の手仕舞いが流れて、何株が残ったか」）である。

## 決定（詳細は IADR-0357）

### A. 成行の手仕舞い —— `limitPrice` 省略時の既定を成行にする

| 要求 | 発注種別 | 備考 |
| --- | --- | --- |
| `limitPrice` 省略（`marketOrder` 省略） | **成行**（既定が変わる） | #847 の事故そのものの形。ここを直さないと同じ事故が再発する |
| `limitPrice` 指定 | 指値 | 従来どおり |
| `marketOrder: true` ＋ `limitPrice` 指定 | **400**（矛盾） | 黙ってどちらかを捨てない |
| `marketOrder: false` ＋ `limitPrice` 省略 | 現在値の指値 | 旧既定を明示的に選べる退避口 |

要求本文の項目名は **`marketOrder`** である —— `market` は既に市場（Japan / UnitedStates）が使っているためである。

- `OrderIntent` に `bool MarketOrder = false` を足す（末尾・既定 false＝**従来どおり指値**）。
  既存の全生成点は既定に倒れるため、**エントリー・保護レグ・判断由来の決済は 1 バイトも変わらない**。
  `approved_orders` は `OrderIntent` の列を明示写像しており本値を持たないため、**Migration は無い**
  （本値は発注時にしか意味を持たない）。
- `OrderExecutionAppService` は `PositionEffect.Close` かつ `MarketOrder` かつブローカーが
  `IProtectiveOrderBroker` のとき `PlaceMarketOrderAsync` で送る。それ以外は従来経路（1 バイトも変わらない）。
  - 能力が無いブローカーへ届いた成行は**指値（参照価格）で発注し Warning を残す**。手仕舞いを止めない側へ倒す
    （FR-10）。実在するアダプタ（paper / moomoo）は 2 つとも能力を持つため到達しない。
- 参照価格（`OrderIntent.Price`）は成行でも載せる（台帳・監査・通知・paper の約定価格が使う）。
  現在値が取れないときは**成行に限り**建玉の平均取得単価へ倒す（`OpenPosition.AverageEntryPrice`＝台帳が
  持つ実在の値。捏造しない）。**指値では従来どおり `PriceUnavailable`（422）**。
  - moomoo は成行に価格を載せない（`MoomooOrderKind.Market` は `PlaceCoreAsync` で丸めも発注前検証の
    価格チェックも通らない）ため、参照価格は送信内容に影響しない。

### B. 取消の明示的な経路（#768 の配線）

- `POST /risk-controls/positions/close/cancel`（**OwnerOnly**・`decisionId` と `reason` 必須）を足す。
  手仕舞い（`POST /risk-controls/positions/close`）と同じ層・同じ権限・同じ 202 の形に揃える。
- 検証は台帳で行う: 承認が無い → 404／`PositionEffect.Close` でない → 422（エントリーの取消はこの口の役目でない）。
- 監査は `PositionCloseCancellationRequested`（`DecisionId` / **Actor** / **Reason** / 時刻）を発行して残す。
  `PositionCloseRequested` と同型であり、監査台帳の受け口も同型（`AuditEntryFactory` ＋ 専用ハンドラ）。
- 発注執行が同イベントを購読し、`OrderAmendmentDispatcher.CancelAsync(decisionId, reason)` を呼ぶ
  ——**これが #768 の「呼び出し元」である**。
- 🔴 **「確実に取り消せた」と「取消を送ったが結果が不明」を混同しない。**
  `OrderCancelled` は取引台帳で **`MarkTerminal` → 在庫の押さえを解く引き金**である（IADR-0117 改定 1/4）。
  ブローカーが取消**要求**を受理しても、注文はまだ生きていることがある（moomoo は
  `Cancelling_Part`/`Cancelling_All`（12/13）を経て `Cancelled_All`（15）になり、その間に約定し得る。
  12/13 を非終端へ倒す配慮は IADR-0117 改定 3 が既に入れている）。
  - したがって `OrderAmendmentService.CancelAsync` は取消の送信後に**注文状態を照会して確認**し、
    **確認できた取消（`AbandonsUnfilledRemainder`＝`Cancelled` / `Expired` / `Rejected`）のときだけ**
    `OrderCancelled` を発行する。
  - 確認できないとき（照会が `null`＝不明／まだ非終端／`Filled`）は**発行しない**。在庫は押さえたままになり、
    本当の終端は既存の約定追跡（`OrderFillPoller`・30 秒周期）が観測して
    `OrderExecuted(Status=Cancelled)` として届ける（IADR-0117 改定 4 の経路）。**新しい経路を作らない。**
  - 取消そのものが失敗した（例外）ときは記録もイベントも作らない（IADR-0067 の既存規律）。
- 取消の口は **`IBrokerAdapter.CancelOrderAsync`** を使う（**moomoo も実装済み**。
  `MoomooBrokerAdapter.CancelOrderAsync` → `client.CancelOrderAsync`）。
  `IOrderAmendmentBroker`（ペーパー専用）は**訂正（`ModifyOrderAsync`）専用として残す**
  ——IADR-0067 が型で塞いだのは「実 OpenD へ `TrdModifyOrder` を配線していない」ことであり、
  取消は当初から `IBrokerAdapter` に在って moomoo が実装している（`ProtectiveStopGuard` も
  `OrderExecutionAppService` も既にそれを呼んでいる）。
- DI: `OrderAmendmentService` / `OrderAmendmentDispatcher` を **moomoo 構成でも登録する**
  （従来は `if (!brokerSelection.IsMoomoo)`）。`IOrderAmendmentBroker` は任意依存にし、
  無い構成での `ModifyAsync` は `NotSupportedException`（**取消だけが moomoo で使える**）。
- `UnwiredDiRegistrationTests.KnownUnwired` から `OrderExecutionService/OrderAmendmentDispatcher` を外す
  （外し忘れは「実体を失った項目」で赤くなる）。

### C. 失効した手仕舞いの通知

- `IPortfolioLedgerStore.MarkTerminal` の戻り値を `void` → **`bool`**（**初めて終端を記録したときだけ true**）にする。
  単調・冪等の意味論は 1 バイトも変えない。再配送で通知が繰り返されないための冪等キーがこれである。
- 取引台帳のハンドラ（`OrderExecutedLedgerHandler` / `OrderCancelledLedgerHandler`）は、
  **初めて終端を記録した** ＆ 承認が `PositionEffect.Close` ＆ **未約定残 > 0** のとき
  `PositionCloseAbandoned`（銘柄 / 市場 / 方向 / 承認数量 / 約定累計 / 残 / 終端状態 / 時刻）を発行する。
- 通知は既存経路に載せる（`NotificationFormatter` ＋ `NotificationService` のハンドラ）。
  文面は「**手仕舞いが約定しないまま終わり、建玉が N 株残っている**」と、**残った建玉が無保護なら**
  そのまま翌日へ持ち越すことを書く。重大度は **Warning**
  （Critical は「実際に統制が破れた」事象＝保護喪失・損切り到達に取っておく。埋もれさせない）。
- 監査にも残す（`AuditEntryFactory` ＋ 専用ハンドラ。`AuditConsumerCoverageTests` が全イベントに要求する）。

［2026-09-19 追記 / #847・フェーズ末監査］**決定 C に 2 点を足した**（詳細は IADR-0357 決定 4）。

- 🔴 **残数量は台帳の約定累計だけでは出せない（到着順序）。** 取消の確認 → `OrderCancelled` は即座に発行される
  のに対し、部分約定は約定追跡（30 秒周期）経由で届く。**取消が約定を追い越すのが通常**であり、台帳だけで
  数えると部分約定ぶんを二重に数える（実測: 承認 3,381・約定 1,000 で残が 2,381 ではなく 3,381）。
  終端の記録は単調なので**訂正通知も出ない**。`OrderCancelled` へ `ObservedFilledQuantity`（取消を確認した
  照会が返した累積約定数）を足し、受け手は台帳の累計との `Math.Max` を採る。
  **Risk からブローカーを引く案は採らない**（同期照会をホットパスへ持ち込まない規律・IADR-0018 / IADR-0117）。
- 🔴 **`MarkTerminal` の「初回だけ true」は並行では成立していなかった。** `OrderCancelled` と `OrderExecuted` は
  **別キュー＝並行実行**であり、EF 実装は read-then-write の TOCTOU である。実測では、200 試行の反復で多数観測された —— 🔴 **比率は実行環境の並行度に依存するため絶対数は書かない**。
  `approved_orders.TerminalAt` を並行トークンにする（移行は DDL 無しの空 Up/Down）。
  **`order_activity` の同名列には付けない**（あちらは無条件上書きが正しい）。
  根本原因は**冪等をインメモリ実装だけで測っていたこと**であり、EF 側に並行の回帰テストを置いた。

［2026-09-19 追記 / #847・2 巡目監査］さらに 2 点。

- 🔴 **発行側（観測した約定数をイベントへ載せる経路）がテストで固定されていなかった。** 受け手側は値を
  手で渡しており、発行側を `0` 固定へ戻す変異が **6 テストプロジェクトで赤ゼロ**だった（＝直した当の数字を
  リファクタで黙って元へ戻せる状態）。確認に使う偽ブローカーへ累積約定数を注入して固定した（T-10-589）。
- 🔴 **並行トークンの検証をインメモリ DB プロバイダだけで回すのも同じ限界を持つ。** 本トークンは
  **元の値が必ず NULL** で、`WHERE "TerminalAt" IS NULL` へ翻訳されることに依存する（素直な等値述語だと
  常に 0 行更新＝終端が一度も記録されず在庫が永久に解放されない）。実 PostgreSQL の結合テストを足した（T-10-590）。
- **書き手を数えない書き方へ改めた。** #872 が `MarkForgone` を第 2 の書き手として足すため、
  「本列を書く唯一の操作」という前提はじきに偽になる。トークンは列に付くので全書き手に効き、
  裏返すと**全書き手が競合例外を捕まえねばならない**。その旨をコード側の注記に書いた。
- **副作用として保護レグ（逆指値）の終端でも発行される**。承認行は `approved_orders` に同じ形で載り、
  台帳からは owner の手仕舞いと区別できないためである。**受容する**——保護レグが未約定残を残して終端になった
  状態は「無保護の建玉が残っている」ことそのものであり、黙らせてよい事象ではない。
  常駐ガードの Critical（`ProtectiveStopCoverageLost`）が別途出るため、こちらは Warning に留める。

## 変更するファイル（母集合。誤りの側の語で走査してから列挙した）

走査: `grep -rn "PlaceMarketOrderAsync\|MarkTerminal\|OrderAmendmentDispatcher\|IOrderAmendmentBroker\|LimitPrice" backend --include=*.cs`

| 層 | ファイル | 変更 |
| --- | --- | --- |
| 契約 | `Shared.Contracts/Trading/OrderIntent.cs` | `MarketOrder` を末尾に追加（既定 false） |
| 契約 | `Shared.Contracts/Events/PositionCloseCancellationRequested.cs` | 新規 |
| 契約 | `Shared.Contracts/Events/PositionCloseAbandoned.cs` | 新規 |
| リスク管理 | `Features/RiskManagement/ClosePosition/{Endpoint,PositionClose,PositionCloseService}.cs` | 成行の選択・価格解決 |
| リスク管理 | `Features/RiskManagement/CancelPositionClose/{Endpoint,PositionCloseCancellationService}.cs` | 新規（取消の口） |
| リスク管理 | `Features/RiskManagement/RiskControlEndpoints.cs` | 新エンドポイントの登録 |
| リスク管理 | `Features/RiskManagement/IPortfolioLedgerStore.cs` ＋ 2 実装 | `MarkTerminal` を `bool` へ |
| リスク管理 | `Infrastructure/Steps/{OrderExecutedLedgerHandler,OrderCancelledLedgerHandler}.cs` | 失効の発行 |
| リスク管理 | `Program.cs` | 取消サービスの DI |
| 発注執行 | `Features/OrderExecution/AmendOrder/OrderAmendmentService.cs` | 取消の確認・`IBrokerAdapter` 化 |
| 発注執行 | `Infrastructure/Steps/OrderAmendmentDispatcher.cs` | **確認できたときだけ**発行 |
| 発注執行 | `Infrastructure/Steps/PositionCloseCancellationHandler.cs` | 新規（#768 の呼び出し元） |
| 発注執行 | `Features/OrderExecution/DispatchApprovedOrder/OrderExecutionAppService.cs` | 成行の分岐（**最小限**。#864 と同居） |
| 発注執行 | `Program.cs` | 訂正・取消の DI を moomoo でも登録 |
| 監査 | `Domain/AuditEntryFactory.cs`・`Infrastructure/Steps/AuditEventHandlers.cs` | 新イベント 2 件 |
| 通知 | `Features/Notifications/NotificationFormatter.cs`・`Infrastructure/Steps/NotificationHandlers.cs` | 失効の通知 |
| 横断 | `Tests/AiStockTrading.Architecture.Tests/UnwiredDiRegistrationTests.cs` | 既知の未結線から 1 行外す |
| 文書 | `docs/functional/FR-10_risk-controls.md` | 決済経路の表・取消の口 |

**#864 との衝突回避**: `DispatchApprovedOrder/` は `OrderExecutionAppService.cs` の**発注 1 箇所だけ**を触る
（相 3 のブローカー呼び出しの分岐）。同ディレクトリの他ファイルは触らない。
**#852 との衝突回避**: 在庫の解放ロジック（`GetInFlightCloseQuantity` の算式・`MarkTerminal` の意味論）は
**変えない**。変えるのは `MarkTerminal` の**戻り値だけ**である。

## 🔴 壊してはいけないもの（着手前に確認し、完了前に再実行する）

`T-10-402` / `T-10-403` / `T-10-406` / `T-10-407` / `T-10-408`（「二重決済でショート化しない」を固定する既存テスト）が
緑のままであること。本 PR が近づくのは次の 2 点であり、どちらも**押さえを解く側へは倒さない**。

1. 取消の結果が不明なときに `OrderCancelled` を出さない（＝在庫を戻さない）。
2. `MarkTerminal` の戻り値を足すだけで、記録する条件（`AbandonsUnfilledRemainder`・単調・冪等・
   相関する承認が無ければ書かない）は 1 バイトも変えない。

## 統制が効かなくなる範囲（IADR-0357 にも書く）

- 成行の手仕舞いも `PositionEffect.Close` であり、発注前スクリーニング（`RiskEvaluator.cs:23` の
  `isEntry = PositionEffect == Open` / `OrderScreeningService.cs:40`）を通らない。
  **成行では約定価格の上限が無い**ため、kill switch も段階資金上限も日次損失ロックアウトも効かないまま
  「いくらで約定するか分からない売り」が出る。指値の手仕舞いには少なくとも価格の床があった。
- これは FR-10 本文（手仕舞いと損切りは止めない）の実装であって逸脱ではないが、**指値から成行へ既定を
  動かすことで「統制が効かない経路」が実質的に広がる**。取引額の上限で守られていたわけではないが、
  「置いていかれて約定しない」という事実上のブレーキが外れる。これは #847 が**意図した**変更である。
- 数量の上限（在庫ガード＝`GetInFlightCloseQuantity`）は従来どおり効く。**建玉を超える成行は作れない。**

## 受け入れ基準 → テスト

| 受け入れ基準 | テスト |
| --- | --- |
| 下落局面でも手仕舞いが成立する経路がある（成行） | `PositionCloseServiceTests`（既定が成行・矛盾指定の拒否・参照価格の解決）／`OrderExecutionServiceMarketCloseTests`（`PlaceMarketOrderAsync` へ送る）／`PositionCloseEndpointTests` |
| 未約定の手仕舞いを利用者が取り消せる（監査に誰が・なぜが残る） | `PositionCloseCancellationEndpointTests`（OwnerOnly・理由必須・404/422）／`PositionCloseCancellationHandlerTests`（#768 の配線）／`OrderAmendmentServiceTests`（**確認できたときだけ**発行）／`UnwiredDiRegistrationTests` |
| 失効した手仕舞いが通知される | `PositionCloseAbandonedTests`（発行・冪等・到着順序）／`EfPortfolioLedgerMarkTerminalConcurrencyTests`（並行）／`NotificationTemplateGoldenTests` |
