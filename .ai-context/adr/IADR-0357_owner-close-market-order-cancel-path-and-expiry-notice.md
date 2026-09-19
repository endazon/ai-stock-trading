---
title: IADR-0357 利用者の手仕舞いは既定を成行にし、板に残った手仕舞いを取り消す口を利用者へ開き、未約定残を残して終わった手仕舞いを通知する — 取消は「確認できた終端」だけを在庫解放の引き金にする
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-09, FR-10, FR-11, FR-19, UC-02, UC-06, ADR-0002, ADR-0003, ADR-0013, ADR-0016, ADR-0040, IADR-0018, IADR-0057, IADR-0067, IADR-0074, IADR-0092, IADR-0113, IADR-0117, IADR-0129, IADR-0210, IADR-0211, IADR-0335, IADR-0342, IADR-0346, IADR-0347, IADR-0350]
author: claude (Claude Code)
created: 2026-09-19
updated: 2026-09-19
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10「kill switch・日次損失ロックアウト・一時停止はいずれも手仕舞い〔Close〕と損切りは止めない」・FR-11 監査・FR-09 通知)
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md (UC-06 利用者による建玉の手仕舞い)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# IADR-0357: 手仕舞いの既定を成行にし、取消の口を開き、失効を通知する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-19
- 決定者: claude（起票 #847。利用者レビューは PR で受ける）

## 起点・関連

- 関連する計画書 ID: **FR-10**（リスク統制。本文に「…いずれも手仕舞い（Close）と損切りは止めない」）、
  FR-05（発注執行）、FR-09（通知）、FR-11（監査）、FR-19（取引ガード）、UC-02 / UC-06、ADR-0003
- 対象 Issue: [#847](https://github.com/endazon/ai-stock-trading/issues/847)（**稼働環境で実際に人が困った不具合**）。
  [#768](https://github.com/endazon/ai-stock-trading/issues/768)（`OrderAmendmentDispatcher` に呼び出し元が無い）を内包する。
- 関連する実装仕様書:
  [20260919_847_exit-market-order-cancel-and-expiry-notice](../specs/20260919_847_exit-market-order-cancel-and-expiry-notice.md)
- 関連 IADR: [IADR-0117](IADR-0117_owner-position-close-path.md)（手仕舞いの経路そのもの。本 ADR はその**出口**を強化する）、
  [IADR-0210](IADR-0210_broker-side-stop-loss-unification.md)（`PlaceMarketOrderAsync`＝成行手仕舞いの既存の口）、
  [IADR-0067](IADR-0067_order-lifecycle-telemetry.md)（訂正・取消の配管とポート分割）、
  [IADR-0057](IADR-0057_order-dispatch-idempotency.md)（DecisionId 予約）、
  [IADR-0113](IADR-0113_moomoo-fill-polling.md)（約定追跡＝終端の権威）、
  [IADR-0335](IADR-0335_unwired-di-registration-detection.md)（未結線の台帳）

## 背景・課題

稼働環境で実測（2026-09-18 23:13 JST）。利用者が建玉 3,381 株の手仕舞いを要求した。手仕舞い API は
`limitPrice` を省くと**その時点の現在値**で売り指値を出す。発注時 334.09 だったが直後に 333.59 まで下げ、
**約定しないまま板に残った**（23:13 / 23:18 とも `filled=0 status=0(Accepted) planned=334.09`。評価損益 −5,133 USD）。

この 1 件に 3 つの欠陥が重なっている。

1. **下落局面ほど外せない。** 現在値の指値は、価格が下げ続けるかぎり置いていかれる。
   **手仕舞いが必要な場面（下落）でこそ効かない**という筋の悪さがある。
2. **出し直せない。** 板に残った注文が数量を占めるため（`ExceedsAvailable`）、指値を変えた再要求が弾かれる。
   **発注執行に取消の口が無く**、利用者は moomoo アプリから手で取り消すしかなかった。
   取消の機能自体は実装済みで（`MoomooBrokerAdapter.CancelOrderAsync` → `OrderAmendmentService` →
   `OrderAmendmentDispatcher.CancelAsync`）、**呼び出せる口だけが無い**（#768）。
3. **当日注文は引け後に失効し、黙って建玉が残る。** `OrderExecuted(Status=Expired)` は届くが、通知の文面は
   「約定 Expired 数量0@0」だけで、**それが手仕舞いだったことも、何株が残ったかも書かれていない**。
   無保護の建玉（ADR-0040 の S2）はそのまま翌日へ持ち越される。

### 起点の前提のうち、実測で異なっていた点

issue と親タスクは「S1 のソフトウェア逆指値が成行で決済する設計（`SoftwareStopExecutor`）に揃える」と書くが、
**`SoftwareStopExecutor` という型は存在しない**（`grep -rn "SoftwareStop" backend --include=*.cs`）。
S1（`StopLossExecutionMethod.SoftwareStop`）は**未実装**（#820）で、現状は S0 と同じ扱いである。
実在する「成行で手仕舞う」設計は `IProtectiveOrderBroker.PlaceMarketOrderAsync`（IADR-0210）であり、
本 ADR はそちらへ揃える（**ブローカー呼び出しを 1 つも増やさない**）。

## 検討した選択肢

1. **`limitPrice` 省略時の既定を成行にする**（採用）。事故の形そのもの（省略して指値が出た）を直す。
2. **`marketOrder: true` を足し、既定は現在値の指値のまま**。後方互換だが、**同じ事故が再発する**
   ——利用者は下落局面で「新しいフラグを思い出す」ことを要求される。
3. **手仕舞い要求に「既存の未約定の手仕舞いを置き換える」意味を持たせる（訂正発注）**。
   `IOrderAmendmentBroker.ModifyOrderAsync` は**実 OpenD へ配線されていない**（IADR-0067）ため、
   moomoo では実装不能である。加えて #847 は「取消の明示的な経路を必ず用意すること」を受け入れ基準にしている。

## 決定

**選択肢 1 を採り、取消の明示的な口（選択肢 3 ではない）を開き、失効を通知する。** 4 点を決める。

### 決定 1: 手仕舞いの既定を成行にする（`limitPrice` 省略＝成行）

| 要求 | 発注種別 |
| --- | --- |
| `limitPrice` 省略（`marketOrder` 省略） | **成行**（既定が変わる） |
| `limitPrice` 指定 | 指値（従来どおり） |
| `marketOrder: true` ＋ `limitPrice` 指定 | **400**（矛盾。黙ってどちらかを捨てない） |
| `marketOrder: false` ＋ `limitPrice` 省略 | 現在値の指値（旧既定を選べる退避口） |

- 要求本文の項目名は **`marketOrder`** である（`market` は既に「市場（Japan / UnitedStates）」が使っている）。
- `OrderIntent` へ `bool MarketOrder = false` を**末尾に**足す（既定 false＝従来どおり指値）。
  **エントリー・保護レグ・判断由来の決済は 1 バイトも変わらない。**
  `approved_orders` は `OrderIntent` の列を明示写像しており本値を持たないため **Migration は無い**
  （本値は発注時にしか意味を持たない。射影が読み戻さないことは意図した限界である）。
- 発注執行は `PositionEffect.Close` かつ `MarketOrder` かつブローカーが `IProtectiveOrderBroker` のとき
  `PlaceMarketOrderAsync` で送る。**成行の能力が無い発注先では指値（参照価格）で送る**——見送らない。
  手仕舞いを止めないほうが重い（FR-10）。実在するアダプタ（内蔵 paper / moomoo）は 2 つとも能力を持つ。
- **参照価格は成行でも載せる**（台帳・監査・通知・内蔵 paper の約定価格が使う）。現在値が取れないときは
  **成行に限り**建玉の平均取得単価へ倒す——台帳が持つ実在の値であり、**0 を載せない・値を捏造しない**。
  指値では従来どおり `PriceUnavailable`（422）である。
  moomoo は成行注文に価格も発火価格も載せない（`MoomooOrderKind.Market`）ため、参照価格は送信内容に影響しない。

### 決定 2: 取消の明示的な口を利用者へ開く（#768 の配線）

- `POST /risk-controls/positions/close/cancel`（**OwnerOnly**・`decisionId` と `reason` 必須・202）。
  手仕舞い（`POST /risk-controls/positions/close`）と同じ層・同じ権限・同じ形に揃える。
  **サービストークンには開かない**（生成AI・自動処理が板の注文を消せないようにする＝ADR-0003）。
- 判定は台帳だけを見る: 承認が無い → 404／`PositionEffect.Close` でない → 422。
  **「既に終端か」は判定しない** ——台帳の終端は**確認できたものだけ**が立っており（IADR-0117 改定 1/3）、
  立っていないことは生死を意味しない。先回りして弾くと「終端が届いていないだけの注文を取り消せない」を作る。
- 監査は `PositionCloseCancellationRequested`（`DecisionId` / 銘柄 / 市場 / **Actor** / **Reason** / 時刻）で残す。
  `PositionCloseRequested` と同型であり、`OrderCancelled` はアクターを持たないため、これが
  「誰が・なぜ板の注文を消したか」の唯一の証跡になる（FR-11）。
- 発注執行が同イベントを購読し（`PositionCloseCancellationHandler`）、
  `OrderAmendmentDispatcher.CancelAsync` を呼ぶ。**これが #768 の「呼び出し元」である。**
  `UnwiredDiRegistrationTests.KnownUnwired` から当該行を外した（外し忘れは「実体を失った項目」で赤くなる）。
- **取消は `IBrokerAdapter.CancelOrderAsync` で行う。** IADR-0067 が `IOrderAmendmentBroker` をペーパー専用に
  して型で塞いだのは「実 OpenD へ `TrdModifyOrder`（**訂正**）を配線していない」ことであり、**取消は当初から
  `IBrokerAdapter` に在って moomoo が実装している**（`ProtectiveStopGuard` も `OrderExecutionAppService` も
  既に呼んでいる）。したがって DI も全構成で登録し、**訂正（`ModifyAsync`）だけ**が
  `IOrderAmendmentBroker` を要して無い構成では `NotSupportedException` になる（型と実行時の二重の遮断）。

### 決定 3: 🔴 「確実に取り消せた」と「取消を送ったが結果が不明」を混同しない

`OrderCancelled` は取引台帳で `MarkTerminal` → **在庫の押さえを解く引き金**である（IADR-0117 改定 1/4）。
**ブローカーが取消要求を受理しても、注文はまだ生きていることがある** —— moomoo は
`Cancelling_Part`/`Cancelling_All`（12/13。IADR-0117 改定 3 が**非終端**へ倒している）を経て
`Cancelled_All`（15）になり、その間に約定し得る。

したがって:

- `OrderAmendmentService.CancelAsync` は取消の送信後に**注文状態を照会して確認**し、
  確認できた終端（`AbandonsUnfilledRemainder`＝`Cancelled` / `Expired` / `Rejected`）のときだけ
  `OrderCancelled` を組み立てる。`OrderAmendmentDispatcher` はそれを受けて**確認できたときだけ発行する**。
- 確認できないとき（照会が `null`＝不明／まだ非終端／`Filled`）は**発行しない**。
  在庫は押さえたままになり、本当の終端は既存の約定追跡（`OrderFillPoller`・30 秒周期）が観測して
  `OrderExecuted(Status=Cancelled)` として届ける（IADR-0117 改定 4 の経路）。**新しい経路を作らない。**
- 取消そのものが失敗した（例外）ときは記録もイベントも作らない（IADR-0067 の既存規律）。
  発注執行のハンドラは例外を握らず、Wolverine の再試行（2s/10s/30s）→ `_error` キューへ落とす
  ——**握って正常終了すると利用者は「消えた」と思い込む。**
- **取消を送ったこと自体は無条件に記録する**（`order_lifecycle`）。記録することと在庫を戻すことは別であり、
  後者だけが確認を要する。
- 応答（202）の本文にも「証券会社で取り消せたことが確認できるまで建玉を押さえ続ける」と書く。

**もし確認せずに発行していたら何が起きるか**（本 ADR がここまで書く理由）: 建玉 3,381 に対する手仕舞いが
取消要求の直後に全量約定した場合、`OrderCancelled` → `MarkTerminal` で処理中が 0 になり、約定が台帳へ届く前に
利用者の再要求が 3,381 株を**もう一度**売れる。結果は 3,381 株のショートである。
これは IADR-0117 が 4 巡の監査で塞ぎ続けてきた穴そのものである。

### 決定 4: 未約定残を残して終わった手仕舞いを通知する

- `IPortfolioLedgerStore.MarkTerminal` の戻り値を `void` → **`bool`**（**初めて終端を記録したときだけ true**）にする。
  **記録する条件は 1 バイトも変えていない**（`AbandonsUnfilledRemainder`・単調・冪等・相関する承認が無ければ
  書かない）。足したのは戻り値だけであり、これが**通知の冪等キー**になる（再配送で撃ち直さない）。
- 台帳のハンドラ（`OrderExecutedLedgerHandler` / `OrderCancelledLedgerHandler`）は、初めて終端を記録した
  ＆ 承認が `PositionEffect.Close` ＆ 未約定残 > 0 のとき `PositionCloseAbandoned` を**カスケード送信**する。
  発行元が台帳なのは、承認 Intent（建玉効果・銘柄・方向）と約定累計を持つのが台帳だけだからである
  ——`OrderExecuted` も `OrderCancelled` も運ばない。
- 通知は既存経路（`NotificationFormatter` ＋ `NotificationService` のハンドラ）に載せる。文面は
  「手仕舞いが `<終端>` で終了・約定 N・**未決済 M**・建玉はこの数量ぶん残っている・逆指値なしならこのまま翌日へ」。
  **重大度は Warning**（Critical は「実際に統制が破れた」事象＝保護喪失・損切り到達に取っておく。
  ただし Info にもしない——手仕舞えなかった建玉が実在する）。
- 監査にも残す（`AuditEntryFactory` ＋ 専用ハンドラ。`AuditCycleCompletenessTests` が全イベントに要求する）。
- 🔴 **本イベントは在庫の押さえに一切関与しない**（通知と監査のためだけに存在する）。
  判定を誤っても二重決済は生じず、生じるのは通知の過不足だけである。
- 🔴 **残数量は「台帳の約定累計」だけでは出せない（到着順序）。** 取消の確認 → `OrderCancelled` は発注執行が
  **即座に**発行するのに対し、部分約定は約定追跡（`OrderFillPoller`・30 秒周期）経由で台帳へ届く。
  **取消が約定を追い越すのが通常**であり、台帳の累計だけで数えると部分約定ぶんを未決済として二重に数える
  （実測: 承認 3,381・約定 1,000 のとき残が 2,381 ではなく **3,381**）。しかも終端の記録は単調なので
  **あとから約定が届いても訂正通知は出ない** —— 誤った数字がそのまま残る。
  - **是正**: `OrderCancelled` に **`ObservedFilledQuantity`**（取消を確認した照会が返した累積約定数）を足す。
    取消の確認は**注文状態の照会そのもの**なので、この値は既に手元にある——**Risk からブローカーを引かない**
    （同期照会をホットパスへ持ち込まない規律＝IADR-0018 / IADR-0117。issue の助言 1 の literal な形は採れない）。
    受け手は台帳の累計との **`Math.Max`** を採る（過小報告しない・遅着の台帳更新で数字が戻らない＝単調）。
  - **`OrderExecuted` 側は直す必要が無い** —— そちらは `FilledQuantity` を運び、ハンドラが
    `AppendFill` を済ませて**から** `MarkTerminal` を呼ぶため、台帳は既に最新である（IADR-0117 改定 2/4 の順序）。
  - 🔴 **運搬経路そのものをテストで固定する（T-10-589）。** 受け手側の検証は値を**手で渡して**いたため、
    「発行側が本当に載せているか」は 1 件も観測していなかった —— 実測で、発行側を `0` 固定へ戻す変異が
    **6 テストプロジェクトで赤を 1 件も出さなかった**。**この PR が直した当の数字を、リファクタで黙って
    元へ戻せる状態だった。** テスト仕様書自身の規律「不変条件は、それを壊す経路そのものを通すテストでしか
    固定できない」に従い、確認に使う偽ブローカーへ累積約定数を注入できるようにして発行側を固定した。
- 🔴 **「初回だけ true」は並行しても成立しなければ意味が無い。** `OrderCancelled` と `OrderExecuted` は
  Wolverine の**別キュー＝並行実行**であり（IADR-0129 決定 1「1 サービス内 1 イベント型 = 1 キュー」）、
  同じ承認の終端を同時に運ぶのはまさに #847 のシナリオである。
  `EfPortfolioLedgerStore.MarkTerminal` は「読む → `null` か検査する → 代入する → `SaveChanges`」の
  **TOCTOU** であり、実測では「初回」が 2 回成立する試行が 200 試行の反復で多数観測される（比率は実行環境の並行度に依存するため絶対数は書かない）（＝失効通知が二重に出る）。
  - **是正**: `approved_orders.TerminalAt` を **並行トークン**（`IsConcurrencyToken`）にする。
    負けた側の UPDATE は 0 行になり `DbUpdateConcurrencyException` になるので、`MarkTerminal` が捕まえて
    `false` を返す（＝通知しない）。**在庫の押さえは壊れない**——どちらが勝っても `TerminalAt` は同じ意味に
    落ち着く冪等な書き込みである。**PostgreSQL の DDL は 1 行も要らない**（移行は
    `AddApprovedOrderTerminalConcurrencyToken`。Up / Down は**意図的に空**で、モデルとスナップショットを
    一致させるためだけに置く）。
  - 🔴 **`order_activity` の同名列には付けない。** あちらは `EfOrderActivityStore.RecordCancellation` が
    **無条件に上書きする**規約であり（IADR-0117 改定 1 の赤字）、トークンを付けると正常な上書きが競合例外になり、
    射影の終端判定（`EfWorkingEntryOrderSource`）が壊れる。
  - 🔴 **トークンは列に付くので、書き手が増えるたび全員に効く。裏を返すと、全員が catch を持たねばならない。**
    本 ADR の時点で `approved_orders.TerminalAt` を書くのは `MarkTerminal` 1 つだけだが、
    **見送りの終端（#872 の `MarkForgone`）が第 2 の書き手として同じ列を同じ read-then-write で書く**。
    catch を持たない書き手が居ると、例外がハンドラを貫通して Wolverine の再試行 → error キューへ至り、
    **その終端の記録そのものが落ちる**（見送りなら在庫が解放されない＝#852 の実害の再発）。
    したがってコード側の注記は**書き手を数えない形**で書いた ——
    「この列を書く操作はいずれも `TerminalAt is not null` を見て単調に倒れ、
    競合例外を捕まえて『書かなかった』を返す。新しい書き手を足すときは catch も足す」。
    🔴 **「唯一の書き手」を前提にした説明は、次の書き手が現れた瞬間に黙って偽になる**
    ——本 PR の根本原因（主張だけがあって実装ごとに測っていなかった）と同じ型である。
  - 🔴 **根本原因は「冪等をインメモリ実装だけで測っていた」ことである。** `InMemoryPortfolioLedgerStore` は
    `ConcurrentDictionary.TryUpdate` の CAS で元から正しく、そちらのテストだけが緑だった。
    **実装が 2 つあるなら、不変条件は実装ごとに測る**（T-10-580 を EF 側に置いた理由）。
  - 🔴 **さらに、EF の検証をインメモリ DB プロバイダで回すことにも同じ限界がある。**
    本トークンは**元の値が必ず NULL** であり、EF が `WHERE "TerminalAt" = @original` ではなく
    **`WHERE "TerminalAt" IS NULL`** を組み立てることに依存している（SQL では `NULL = NULL` が真にならないため、
    素直な等値述語だと**常に 0 行更新 → 常に競合例外 → 終端が一度も記録されない**という、
    **在庫が永久に解放されない**最悪の壊れ方になる）。**インメモリ provider は SQL を発行しないので確かめられない。**
    実 PostgreSQL の結合テスト（T-10-590・`AiStockTrading.IntegrationTests`。先例は
    `PositionDriftStateConcurrencyE2ETests`）を置き、**否定形（負けは 1 回だけ）と肯定形（勝者は書けて在庫が解ける）を
    対で**固定した —— 述語が壊れると否定形だけは緑のまま通るためである。

## 🔴 統制が効かなくなる範囲（本 PR が広げるもの）

- **成行の手仕舞いも `PositionEffect.Close` であり、発注前スクリーニングを通らない**
  （`RiskEvaluator` の `isEntry = PositionEffect == Open` / `OrderScreeningService`）。kill switch・
  日次損失ロックアウト・一時停止・取引ガード・段階資金上限のいずれも掛からない。
  これは IADR-0117 決定 2 のとおり FR-10 本文の実装であって逸脱ではない。
- **新しく失われるのは「価格の床」である。** 指値の手仕舞いには、意図しない価格では約定しないという
  事実上のブレーキがあった（それが #847 では「約定しない」という害として現れた）。成行にはそれが無く、
  **いくらで約定するか分からない売りが統制の外で出る。** これは #847 が**意図した**変更であり、
  「置いていかれて手仕舞えない」ほうが重いという裁定である。
- **残るブレーキは数量の上限だけ**である。在庫ガード（`GetInFlightCloseQuantity` による
  「建玉 − 処理中の決済」）は従来どおり効き、**建玉を超える成行は作れない**（テストで固定）。
- **取消の口は「注文を消す」統制外の操作を利用者へ開く。** 開く先は OwnerOnly のみで、
  サービストークン（生成AI・自動処理）には開かない。理由は必須で、監査に誰が・なぜが残る。
- 残余リスク:
  - `OrderIntent.MarketOrder` は台帳へ永続化しない。`FindApprovedIntent` が返す Intent は常に
    `MarketOrder = false` であり、**「この決済は成行だったか」を後から台帳だけでは言えない**
    （言えるのは発注執行の記録と監査の `PositionCloseRequested`）。列を足す migration に見合う用途が無い。
  - `PositionCloseAbandoned` は**保護レグ（逆指値）の終端でも発行される**。承認行は `approved_orders` に
    同じ形で載り、台帳からは owner の手仕舞いと区別できないためである。**受容する** ——
    保護レグが未約定残を残して終端になった状態は「無保護の建玉が残っている」ことそのものであり、
    黙らせてよい事象ではない。常駐ガードの Critical（`ProtectiveStopCoverageLost`）が別途出るため、
    こちらは Warning に留めて重大度の階層を壊さない。
  - 取消が「不明」で終わったとき、利用者へ能動的な通知は出ない（Warning ログのみ）。
    押さえは残るので安全側だが、**滞留の解消は約定追跡に依る**（IADR-0117 改定 6 の残余リスクと同型）。
  - **失効通知の約定数は「取消を確認した瞬間の観測」である。** その直後に約定が成立していれば、
    通知の「未決済 N 株」は依然として多めに出る（`Math.Max` は過小報告を防ぐが、未来の約定は読めない）。
    台帳は約定が届き次第正しくなるが、**終端の記録は単調なので訂正通知は出ない**。
    数字を鵜呑みにせず建玉を確認してほしい、という趣旨は通知文面の「建玉を確認してください」が担う。
  - 🔴 **内蔵 paper では、参照価格へ倒した成行手仕舞いの実現損益がちょうど 0 になる。**
    `PaperBrokerAdapter.PlaceMarketOrderAsync` は `intent.Price` で即時約定するため、現在値が取れずに
    平均取得単価へ倒した場合、**建値と同値で決済したことになる**。市況フィードが落ちている間の
    paper / バックテストの損益指標が静かに歪む（moomoo は価格を送らないので実約定価格が入り、影響しない）。
    **手仕舞いを止めないことを優先した代償**であり、Stage 判定は SIMULATE の約定だけで集計する（FR-20 /
    IADR-0149 決定 1）ため合否には混入しない。
  - **ローリング更新の最中に発行された `OrderCancelled` は `ObservedFilledQuantity` を持たない**
    （新しい版の受け手が古い版の発行を読むと既定 0 になる）。0 は台帳の累計へフォールバックするので
    **1 巡目と同じ挙動に戻るだけ**であり、悪化はしない（残数量が多めに出得るだけで、在庫には影響しない）。
    入れ替わりが終われば自然に解消するため、版の切り分けも移行手順も置かない。
  - **建玉の平均取得単価が 0 以下の場合は、成行でも `PriceUnavailable`（422）で落ちる**
    （`ResolvePrice` の `> 0m` 条件）。「成行なら止めない」には**この例外がある**。
    倒し先が無い以上、価格 0 の記録を台帳へ残すより拒否するほうが安全であり、
    平均取得単価が 0 以下の建玉は台帳の破損を意味する（正常系では作れない）。

## 根拠

### なぜ既定を変えるのか（選択肢 2 を採らない理由）

#847 の事故は「**利用者が `limitPrice` を省いた**」ことで起きた。省略の意味を変えなければ、同じ操作が
同じ結果になる。新しいフラグを足すだけの案は、**下落局面という最悪のタイミングで利用者に正しい
オプションを思い出すことを要求する**。既定は「よく使われる操作が安全側に倒れる」ように置く。

既定を変える代償（後方互換の破れ）は測れる範囲だった —— 本エンドポイントの呼び出し元は
**テストと文書だけ**であり（`grep -rn "positions/close"` で全追跡ファイルを走査。BFF にも画面にも
Discord にも呼び出しは無い）、いずれも本 PR で引き直した。旧既定は `marketOrder: false` で選べる。

### なぜ訂正発注（選択肢 3）を採らないのか

`IOrderAmendmentBroker.ModifyOrderAsync` は**実 OpenD へ配線されていない**（IADR-0067 が型で塞いでいる）。
moomoo で訂正はできないため、「手仕舞い要求に既存の未約定を置き換える意味を持たせる」は稼働環境では
成立しない。加えて #847 は「**取消の明示的な経路を必ず用意すること**（アプリ操作に逃がさない）」を
受け入れ基準にしており、訂正は代替にならない。

### なぜ在庫の解放ロジックを触らないのか

#852 が `IPortfolioLedgerStore` / `MarkTerminal` 周りで「見送りが在庫をロックする」を実装中であり、
かつ IADR-0117 が 4 巡の監査でこの一帯の不変条件を固めた直後である。本 PR が触るのは
**`MarkTerminal` の戻り値だけ**で、記録する条件も算式も 1 バイトも変えていない。

## 影響・追随

- **実弾ゲート（閂 0〜4）に差分ゼロ。** 新しいブローカー呼び出しは 1 つも増えない
  （成行は `PlaceMarketOrderAsync`＝IADR-0210 で既に在る口、取消は `CancelOrderAsync`＝IADR-0067 以前から在る口）。
  SIMULATE 限定・実弾 OFF は不変である。
- **DB の列は 1 つも増えない。** `OrderIntent.MarketOrder` は `approved_orders` の列に写さない。
  ただし **Migration は 1 本入る** —— `approved_orders.TerminalAt` を並行トークンにするモデル変更のためで、
  `AddApprovedOrderTerminalConcurrencyToken` の **Up / Down は意図的に空**である（PostgreSQL の DDL を伴わない。
  置かないと次の `migrations add` へこの注釈が黙って混ざる）。
  🔴 **`dotnet ef migrations has-pending-model-changes` は並行トークンの追加を検出しなかった**（実測）。
  差分が DDL を生まないためであり、**この検査だけではモデルとスナップショットの乖離は見つからない**。
- **構成キーを 1 つも足さない**（IADR-0117 と同じ理由——利用者の明示操作でしか動かず、
  無効化スイッチは「手仕舞えない状態を作れるスイッチ」にしかならない）。Helm / values / compose は不変。
- **既存契約 `OrderCancelled` へ 1 項目を足す**（`ObservedFilledQuantity`・既定 0＝加算のみで後方互換）。
  発行元は取消の配管 1 箇所だけで、既存の購読側（台帳・注文アクティビティ・監査）は読まなくても壊れない。
- **新しいキューは 2 本増える**（`PositionCloseCancellationRequested` / `PositionCloseAbandoned`）。
  監査サービスは契約イベント全数を購読するため、購読対象が 2 型増える（IADR-0129 の
  `codegen write` 済みイメージで読むため、起動時の実行時コンパイルは増えない）。
- **BFF には出さない。** 既存の手仕舞い（`/positions/close`）も BFF プロキシに無く、
  取消だけを出すのは非対称である。画面からの操作は別 issue の射程とする。
- **#864（`DispatchApprovedOrder/` でのブローカー実建玉突合）との同居**: 本 PR が
  `OrderExecutionAppService` で触ったのは**相 3 のブローカー呼び出し 1 箇所だけ**である。
- **#852（`MarkTerminal` 周りの在庫解放）との同居**: 戻り値だけを足し、意味論は変えていない。

## 代替案を採らなかった理由

- **成行の能力が無いブローカーでは手仕舞いを見送る**: 建玉が残る。FR-10 が止めないと定める操作を
  「能力が無い」という実装都合で止めることになる。指値へ倒して Warning を残す。
- **取消の結果が不明なときも `OrderCancelled` を出し、後から訂正する**: `MarkTerminal` は**単調**であり
  （最初の終端が真）、後から取り消せない。IADR-0117 改定 1 の規約を壊す。
- **`OrderStatus` へ「取消進行中」を足す**: IADR-0117 改定 6 と同じ理由で採らない ——
  `OrderStatus` は証券会社に存在する注文の状態の集合であり、`Cancelling_*` は既に `Accepted`（非終端）へ
  写っている。足せば通知・監査・射影・ペーパーアダプタまで面が広がる。
- **失効の検知に新しい常駐処理を置く**: `OrderFillPoller`（既定有効・24 時間追跡）が既に終端を観測しており、
  足りなかったのは「それが手仕舞いだった」と言える場所だけだった。常駐を増やさない。
