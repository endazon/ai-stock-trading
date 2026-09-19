---
title: 終端になった決済承認を「処理中の決済」から除き、取り消した手仕舞いが建玉をロックし続けないようにする
type: spec
status: accepted
related_ids: [FR-05, FR-10, FR-11, FR-19, UC-06, ADR-0003, ADR-0013, IADR-0018, IADR-0057, IADR-0067, IADR-0074, IADR-0092, IADR-0113, IADR-0117, IADR-0129, IADR-0159, IADR-0210, IADR-0211, IADR-0346]
author: endazon (with Claude Code)
created: 2026-09-19
updated: 2026-09-19
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10「kill switch・日次損失ロックアウト・一時停止はいずれも手仕舞い〔Close〕と損切りは止めない」)
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md (UC-06 利用者による建玉の手仕舞い)
---

# 仕様書: 終端になった決済承認を「処理中の決済」から除く（#848）

## 起点

- #848（bug）。2026-09-18 23:2x JST、稼働環境で実測。
- 症状: 利用者が板に残った手仕舞い注文を moomoo アプリで取り消した**後**も、指値を変えた手仕舞いの再要求が
  `ExceedsAvailable`（422）で拒否され続けた。無保護の建玉 3,381 株・含み損 −5,133 USD を抱えた状態であり、
  **損切りが必要な下落局面で手仕舞えない**という最悪の形で露呈した。
- 発注執行の台帳は取消を正しく検知していた（`OrderId=1149564921959476304 Quantity=3381 Filled=0 Status=4(Cancelled)`）。
- 原因: `EfPortfolioLedgerStore.GetInFlightCloseQuantity` が「処理中の決済」を
  **`approved_orders` の承認数量 − `trade_fills` の約定累計**だけで数え、**取消・失効・拒否を一切見ていない**。
  `PositionCloseService` はこの値を在庫から引くため、承認から `DefaultInFlightWindow = 30 分` のあいだ
  **取り消された注文が建玉をロックし続ける**。

## 射程（#848 の射程そのまま）

1. `GetInFlightCloseQuantity` から**終端になった承認を除く**。ただし**終端だと確認できたものだけ**除く。
2. **fail-safe の向きを保つ**。状態が不明な承認は従来どおり「処理中」とみなす（除外し過ぎると二重決済で
   ショート化する）。
3. 終端の情報をリスク管理へ届ける経路を決め、理由を残す（イベントか照会か）。
4. **窓（30 分）で切る既存の設計は残す**（永久に約定しない滞留承認が建玉を恒久ロックするのを防ぐため）。

**射程外**: #847（成行での手仕舞い・取消の口）。本仕様書は「取り消した後に再要求できる」ことだけを直す。

## 終端をどう届けるか（イベント／照会の決定）

**新しいイベントも同期照会も作らない。既に届いている `OrderExecuted.Status` を捨てずに記録する。**

根拠:

- `OrderExecuted` は `OrderStatus Status` を**既に運んでいる**。約定追跡（`OrderFillPoller`）は 30 秒周期で
  ブローカーへ照会し、**終端化したときも `OrderExecuted` を再発行する**（`OrderFillPoller.cs` の
  `statusChanged` 分岐）。リスク管理は `OrderExecutedLedgerHandler` でこれを**既に購読している**。
- ところが同ハンドラは `FilledQuantity <= 0` で早期 return しており、**約定 0 の取消（まさに #848 の事象）を
  丸ごと捨てていた**。つまり終端の情報は既にリスク管理まで届いており、受け取る側が落としていた。
  新設すべき配線は 0 本である。
- 同期照会（発注執行へ s2s で `executed_orders.Status` を引く）は採らない。発注審査・手仕舞い判定の
  ホットパスに他サービスの可用性を持ち込むことになり、IADR-0018 / IADR-0067 が
  「同期契約は Risk 専有 DB への射影で供給する」と繰り返し決めている方針に反する。
- 新イベント（例 `OrderTerminalized`）も採らない。`OrderExecuted` と同じ事実を 2 つの契約で運ぶことになり、
  片方だけ届く事故の面が増える（IADR-0117 が「決済専用の経路を作らない」と決めた理由と同型）。

加えて、明示的な取消の事実 `OrderCancelled`（`OrderAmendmentDispatcher` が発行）も終端として台帳へ届ける。
リスク管理は既に同イベントを購読しており（`OrderCancelledActivityHandler`）、Wolverine のキューは
`ai-stock-trading.risk-management-service.OrderCancelled` が既に存在する。**新しいキューは増えない。**

## なぜ `order_activity` を読まないのか（採らなかった案）

`order_activity` 射影（IADR-0067）は既に `TerminalAt` を持ち、`EfWorkingEntryOrderSource`（IADR-0346 決定 1）は
`approved_orders` を左結合して終端を除いている。同じ結合を `GetInFlightCloseQuantity` でも行えば
**migration 無しで**直せる。それでも採らない理由:

- **母集合が違う。** `order_activity` の行は `OrderApproved` の射影でしか作られない。保護レグ
  （`ProtectiveStopPlaced` / `ProtectiveStopCoverageLost`）の決済 Intent は **`OrderApproved` を流さず**
  台帳へ直接承認行を足す（IADR-0210 決定 2/3。流すと発注執行が二重発注する）。したがって保護レグの決済承認は
  `order_activity` に**行が無く**、終端化しても永久に除外されない。
  保護レグは `PositionEffect.Close` であり `GetInFlightCloseQuantity` の対象に入るため、同じ不具合が残る。
- **台帳が数える承認の終端は、台帳が持つべきである。** 別集約（相場操縦検知の入力）の射影へ依存すると、
  あちらの母集合や保持方針が変わったときに静かに統制が壊れる。

## 方式

### 決定 1: 台帳が承認の終端を持つ（`approved_orders` に 2 列追加）

`ApprovedOrderRow` に `TerminalStatus`（`OrderStatus?`）と `TerminalAt`（`DateTimeOffset?`）を足す。
どちらも nullable で、**null ＝終端だと確認できていない**（列追加前の既存行・終端が未到達の行）。

- **判定に使うのは `TerminalAt` だけ**とする（`EfWorkingEntryOrderSource` と同じ規約）。`TerminalStatus` は
  **診断用**——次に同じ事故を見るとき「取消か・失効か・拒否か」が DB から読めることに価値がある。
  状態だけで判定すると、遅着の非終端イベントで巻き戻り得る。
- **単調**にする。一度立った `TerminalAt` は消さず、後着の終端で上書きもしない（最初の終端が真）。

### 決定 2: `IPortfolioLedgerStore.MarkTerminal(decisionId, status, at)` を足す

- 非終端の `status` は**無視する**（`Accepted` / `PartiallyFilled` で終端を捏造しない）。
- 相関する承認が無ければ**何もしない**（`AppendFill` と同じ。知らない注文の終端は書けない）。
- 既に `TerminalAt` があれば**何もしない**（冪等・単調）。

終端判定の単一情報源として `RiskManagementService.Domain.OrderStatusLifecycle.IsTerminal` を置く
（`Filled` / `Cancelled` / `Rejected` / `Expired`）。既存の `OrderActivityProjection.IsTerminal` は
同じ定義を独自に持っていたため、これへ委譲させて定義が 2 か所で割れないようにする（振る舞いは不変）。

### 決定 3: `GetInFlightCloseQuantity` は `TerminalAt` を持つ承認を除く

```
処理中の決済 = Σ max(0, 決済承認数量 − 当該 DecisionId の約定累計)
               （窓内に承認され、かつ TerminalAt が無いものだけ）
```

- **`TerminalAt` が無い承認は従来どおり全量が処理中**（不明は安全側）。射程 2 の fail-safe はここで保たれる。
- 終端になった承認の未約定残は**二度と約定しない**ため、在庫から引く理由が無い。部分約定のまま取消された
  承認も同じ（約定した分は `trade_fills` を通じて建玉数量へ既に反映されている＝二重に引かない）。

### 決定 4: 終端を書く購読は 2 つ（どちらも既存のキュー）

| 購読 | ハンドラ | 書くもの |
| --- | --- | --- |
| `OrderExecuted` | `OrderExecutedLedgerHandler`（**既存を拡張**） | 終端状態なら `MarkTerminal`。**`FilledQuantity <= 0` の早期 return より前**に行う（#848 の事象そのもの） |
| `OrderCancelled` | `OrderCancelledLedgerHandler`（**新規・同一集約**） | `MarkTerminal(..., Cancelled, CancelledAt)` |

`OrderExecuted` は既に 2 ハンドラ（台帳・注文アクティビティ）が同一チェーンで走る（IADR-0129 決定 10）。
`MarkTerminal` は冪等なので、再試行で両ハンドラが再実行されても意味は保たれる。

### 決定 5: 窓は変えない

`DefaultInFlightWindow = 30 分` は据え置く。終端が**届かない**注文（照会不能・射影の取りこぼし）は依然として
存在し得るため、恒久ロックを防ぐ最後の受け皿として窓が要る（IADR-0117 決定 3 の根拠は今も生きている）。

## 母集合

走査（2026-09-19・本ブランチ作成直後。`git rev-parse --is-shallow-repository` ＝ `false`）:

- 軸 1: `grep -rn "GetInFlightCloseQuantity" backend --include=*.cs`
- 軸 2: `grep -rn ": IPortfolioLedgerStore" backend --include=*.cs`（実装・テスト fake の全数）
- 軸 3: `grep -rln "InFlight\|処理中の決済" backend docs .ai-context`

| 箇所 | 種別 | 扱い |
| --- | --- | --- |
| `Features/RiskManagement/IPortfolioLedgerStore.cs` | 契約 | **変更**（`MarkTerminal` 追加・`GetInFlightCloseQuantity` の doc 更新） |
| `Infrastructure/Persistence/EfPortfolioLedgerStore.cs` | 実装 | **変更**（終端の記録・終端の除外） |
| `Infrastructure/Persistence/InMemoryPortfolioLedgerStore.cs` | 実装 | **変更**（同一の意味論） |
| `Infrastructure/Persistence/PersistenceRows.cs`（`ApprovedOrderRow`） | 行 | **変更**（2 列追加） |
| `Infrastructure/Persistence/Migrations/` | スキーマ | **追加**（`AddApprovedOrderTerminalState`） |
| `Infrastructure/Steps/OrderExecutedLedgerHandler.cs` | 購読 | **変更**（終端の記録を早期 return より前に） |
| `Infrastructure/Steps/OrderCancelledLedgerHandler.cs` | 購読 | **追加** |
| `Domain/OrderStatusLifecycle.cs` | 純関数 | **追加**（終端定義の単一情報源） |
| `Infrastructure/Persistence/OrderActivityProjection.cs` | 純関数 | **変更**（上へ委譲。振る舞い不変） |
| `Features/RiskManagement/ClosePosition/PositionCloseService.cs` | 呼び出し | 変更なし —— 入力の意味が正しくなるだけ。窓もそのまま |
| `Features/RiskManagement/BuyInInferenceService.cs` | 呼び出し | 変更なし —— **同じ向きで正しくなる**。終端した決済は「自らの決済指示」ではないため、未説明の消失から引いてはいけない（IADR-0159 決定 1 の意図どおり） |
| `Tests/.../OpenPositionsServiceTests.cs` / `ShortSellingStatusServiceTests.cs` の `FakeLedger` | テスト | **変更**（契約追加への追随のみ） |
| `docs/functional/FR-10_risk-controls.md`（式 2 か所） | 文書 | **変更**（終端の除外を式へ） |
| `docs/tests/FR-10_risk-controls-tests.md` | 文書 | **変更**（T-10-400〜405 を追加） |
| `docs/tests/FR-10_risk-guard-core-tests.md`（T-10-87 の「9 ケース／10 ケース」） | 文書 | **変更**（導出値。走査ではなく数え直す） |
| `.ai-context/adr/IADR-0117_*.md` ＋ `.ai-context/adr/README.md` | 記録 | **変更**（日付つき追記・索引行） |

除外した理由:

- `AuditService` / `NotificationService` / `ReportService` の `InFlight` 一致は**別語**（保護レグの滞留・
  文面テンプレート）であり、本件の意味の「処理中の決済」ではない。
- `docs/blocked-tasks.md` の一致は強制買戻しの観測に関する別項目。
- `ProtectiveStopGuard`（発注執行）の `InFlight` は保護レグの巡回であり、リスク管理の台帳とは別の集約。

## テスト（T-10-400〜405）

| ID | 固定すること |
| --- | --- |
| T-10-400 | **取消（約定 0）が確認できた決済承認は、30 分を待たずに在庫へ戻る**（#848 の主目的） |
| T-10-401 | 失効・拒否も同じ（終端 3 値）。**部分約定のまま取消**された承認は残数量だけでなく**丸ごと**除く |
| T-10-402 | 🔴 **状態が不明な承認は処理中のまま**（終端イベントが届いていない／非終端の状態しか来ていない）。除外し過ぎない |
| T-10-403 | 🔴 **二重決済でショート化しない**——終端していない決済が在庫を押さえている限り、同量の再要求は拒否される |
| T-10-404 | 終端の記録は**約定 0 でも行われる**（`OrderExecuted` の早期 return より前）・**明示的な取消**でも行われる |
| T-10-405 | 終端は**単調・冪等**（後着の非終端で戻らない・同じ終端の再送で時刻が動かない）・**未知の承認は書かない** |

EF 実装と InMemory 実装の両方へ同じ観点を写像する（既存の
`EfPortfolioLedgerInFlightCloseTests` / `PortfolioLedgerInFlightCloseTests` の対で乖離を検知する慣行に従う）。

## 受け入れ基準

1. 取り消された手仕舞いの数量が、30 分を待たずに在庫へ戻る。
2. 状態が不明な承認は従来どおり処理中として扱う（安全側）。
3. 二重決済でショート化しないことを固定するテストが残る。
4. 新テストが**修正前のコードで落ちる**ことを実測する（対照実験）。
5. `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` と文書検査器が緑。

## 残余リスク

- **終端が届かない注文は依然として窓の満了まで在庫を押さえる**（照会不能・イベント欠落）。これは意図した
  fail-safe であり、窓が最後の受け皿である（決定 5）。
- **訂正（`OrderModified`）による減量は反映しない。** 訂正は終端ではないため、承認数量のまま処理中に数える
  （多めに押さえる＝安全側）。
- `TerminalStatus` は診断専用で、判定には使わない。使いたくなったら**巻き戻りの検討が先**である。

---

## ［2026-09-19 追記 / #848］監査ブロッキング 2 件の是正（PR #851 の初版に対する指摘）

フェーズ末監査が本 PR の初版（head `0a68c0bf`）に**ブロッキング 2 件**を出した。どちらも
**fail-safe の向きが反転していた**箇所であり、本追記でその是正を決める。**上の決定 2・決定 4 を
部分的に改める**（改めた箇所は各項に明記する）。

### B1: 終端の記録が約定の記録より**前**にあり、その間だけ建玉が丸ごと空いて見える

**指摘（実測）**: 全量約定した決済（`OrderExecuted(Status=Filled, FilledQuantity=100)`）の処理中、
`MarkTerminal` が commit 済みで `AppendFill` が未 commit の区間で「建玉 − 処理中」が **100（建玉全量）**になる。

```
[AppendFill 直前] 建玉=100 処理中=0 利用可能=100
対照（約定を先に書く順序） 利用可能=0
```

その瞬間に手仕舞い要求が入ると**同じ 100 株をもう一度売れる**（＝本仕様書が塞いだはずのショート化）。
`EfPortfolioLedgerStore` では `MarkTerminal` と `AppendFill` が**別々の `SaveChanges`** であるため、
この区間は実在する。さらに**非レース版**も再現する —— 矛盾したイベント（`Status=Filled, FilledQuantity=0`）
では `TerminalAt` だけが立ち、**恒久的に**在庫が丸ごと戻る。

**原因の核**: 在庫解放に使う終端へ `Filled` を入れたこと。**`Filled` を終端に含める必要がそもそも無い** ——
全量約定した承認は `max(0, 承認数量 − 約定累計) = 0` で**自然に 0 になる**（決定 3 の式そのもの）。
得るものが無いまま窓を作っていた。

**是正（決定 2 の改定・両方を行う）**:

1. **在庫解放に使う終端から `Filled` を外す。** 終端判定の単一情報源（`Domain.OrderStatusLifecycle`）へ
   **第 2 の述語 `AbandonsUnfilledRemainder`（`Cancelled` / `Rejected` / `Expired`）** を足し、
   `MarkTerminal` はこれで門を張る。`IsTerminal`（`Filled` を含む）は**書き換えない** ——
   `OrderActivityProjection`（IADR-0067・相場操縦検知の生存区間）は `Filled` を終端として必要とするためであり、
   **共有関数ではなく呼び出し側で絞る**。
2. **`MarkTerminal` を `AppendFill` の後へ移す**（`OrderExecutedLedgerHandler`）。
   決定 4 が「`FilledQuantity <= 0` の早期 return より**前**へ置く」としていた形は**採らない**。
   代わりに**早期 return そのものを畳み**、「約定があるときだけ `AppendFill` → そのあと必ず `MarkTerminal`」
   の順にする。約定 0 の取消（#848 の事象）が捨てられないことは変わらず、
   **約定が台帳に載る前に在庫が解放される区間が消える**。

> 🔴 **1 だけでも 2 だけでも塞がらない。** 1 だけなら `Cancelled` と部分約定が同じイベントで届いたときに
> 同じ区間が残り、2 だけなら矛盾イベント（`Filled` かつ約定 0）の恒久的な誤解放が残る。**両方行う。**

### B2: 「状態が不明」が `Rejected` に畳まれ、不明のまま在庫が解放される

**指摘（実測）**: #848 の受け入れ基準 2・本仕様書・PR 本文はいずれも「**終端だと確認できたものだけ除く／
不明は処理中のまま**」と書いているが、**上流の写像が不明を `Rejected` に倒しているため成立していなかった**。

```
OpenD=-1 → Failed → Rejected → 終端=True   ← NONE（不明）
OpenD=4  → Failed → Rejected → 終端=True   ← TIMEOUT（OpenD 定義上「結果未知」）
OpenD=99 → Failed → Rejected → 終端=True   ← 将来の新コード
```

この写像（`MMApiMoomooTradeClient.MapState` の `_ => Failed`・`MoomooBrokerAdapter.MapState` の
`_ => Rejected`）は**本 PR 以前は無害**だった —— 台帳に約定を載せないだけだったからである。
**`Rejected` が在庫解放の引き金になった瞬間に、この既定の向きが fail-safe から fail-open へ反転した。**
`MoomooBrokerAdapter` のコメント「安全側: 不明/失敗は Rejected」は、この時点で事実と食い違っていた。

**是正（決定 2 の補い・写像の側で直す）**:

- **`MoomooOrderState` に `Unknown` を足す**（発注執行サービス内の正規化列挙であり、サービス間契約ではない）。
  `MMApiMoomooTradeClient.MapState(int)` を **`3` / `21` / `22` / `23` → `Failed`**（確認できた失敗）、
  **`-1`（NONE） / `4`（TIMEOUT） / それ以外の未知コード → `Unknown`** へ分ける。
- `MoomooBrokerAdapter.MapState` は **`Unknown` → `OrderStatus.Accepted`**（＝非終端・まだ動く）とする。
  既定（`_`）も `Accepted` へ倒す —— **名前を付けられない状態で在庫を解放しない**。
  `Failed` → `Rejected` は**変えない**（**`Rejected` 自体は在庫解放の対象のままである**。
  発注拒否での解放は #848 の射程内であり、ここを外すと取り消しに続く 2 つ目の恒久ロックを作る）。
- **`12` / `13`（取消進行中）を非終端（`Submitted`）へ倒している既存の配慮は壊さない**（回帰として固定する）。
- 副次的に**正しい方向へ揃う**: `OrderFillPoller` は非終端の記録を引き直し続けるため、
  「不明」は次の巡回で**本当の状態に解決される**。`Rejected` へ畳むと終端化して二度と引き直されなかった。

**`OrderStatus` へ `Unknown` を足す案は採らない。** サービス間契約（`OrderStatus`）の値を増やすと、
通知の文面・監査・射影・ペーパーアダプタまで面が広がる。**不明を落とさない**という目的は、
発注執行サービス内の正規化列挙（`MoomooOrderState`）で分けるだけで達せられる。

### 追加する受け入れ基準

6. **約定が台帳に載る前に在庫が解放されない。** 全量約定の処理中、`AppendFill` の**直前**に観測しても
   処理中の決済は承認数量のままである（B1。`Filled` は在庫解放の終端ではない）。
7. **不明な状態では在庫が解放されない。** OpenD の `-1` / `4` / 未知コードは `Rejected` にならず、
   在庫解放の引き金にならない。`3` / `21` / `22` / `23`（確認できた失敗）は従来どおり `Rejected` であり、
   `12` / `13`（取消進行中）は従来どおり非終端である（B2）。

### 追加するテスト（`T-10-406` から採番。405 以下は本 PR で使用済み）

| ID | 固定すること |
| --- | --- |
| **T-10-406** | 🔴 **約定が台帳に載る前に在庫が解放されない**。`AppendFill` の直前に観測した処理中の決済が承認数量のままであること／`MarkTerminal(Filled)` は無視されること／矛盾イベント（`Filled` かつ約定 0）で在庫が戻らないこと |
| **T-10-407** | 🔴 **不明な状態では在庫が解放されない**。OpenD `-1` / `4` / `99` は `Unknown` → `Accepted`（非終端）であり `Rejected` にならない／`3` / `21` / `22` / `23` は `Rejected` のまま／`12` / `13` は非終端のまま |
| T-10-404（追加ケース） | 保護レグ（`ProtectiveStopPlaced` 由来の承認）でも終端が記録され、処理中から外れる（`order_activity` に行が無い母集合。決定「`order_activity` は読まない」の実測） |

### 非ブロッキングの受け止め

- **保護レグ経路のテスト**を T-10-404 に足した（上表）。監査は自作プローブで「実際には動く」ことを確認済みで、
  欠けていたのは**固定**である。
- **`TerminalAt` という同名列が `order_activity` と `approved_orders` の 2 か所にでき、意味論が違う。**
  新しい方（`approved_orders`）は**単調**（一度立ったら動かさない）、既存の方（`order_activity`）は
  `EfOrderActivityStore.RecordCancellation` が**無条件に上書き**する。**同じ名前で違う規約**であることを
  IADR-0117 の追記へ明記した（どちらかへ寄せる改修は本追記の射程外。射影の意味論を変えると
  相場操縦検知の入力が動く）。

### 母集合（本追記の是正のために引き直したもの）

走査（2026-09-19・`git rev-parse --is-shallow-repository` ＝ `false`）:

- 軸 1: `grep -rn "IsTerminal" backend --include=*.cs`（終端述語の全呼び出し）
- 軸 2: `grep -rn "MarkTerminal" backend docs .ai-context`（新設 API の全参照・文書含む）
- 軸 3: `grep -rn "早期 return" backend docs .ai-context`（**誤りの側の文字列**。決定 4 が定めた順序を
  そのまま書き写した箇所を、順序を変える前に全部引く）
- 軸 4: `grep -rn "OrderStatusLifecycle\|MapState" backend --include=*.cs`

| 箇所 | 扱い |
| --- | --- |
| `Domain/OrderStatusLifecycle.cs` | **変更**（`AbandonsUnfilledRemainder` を追加。`IsTerminal` は不変） |
| `Infrastructure/Persistence/EfPortfolioLedgerStore.cs` / `InMemoryPortfolioLedgerStore.cs` | **変更**（`MarkTerminal` の門を差し替え） |
| `Features/RiskManagement/IPortfolioLedgerStore.cs` | **変更**（`MarkTerminal` の契約 doc） |
| `Infrastructure/Steps/OrderExecutedLedgerHandler.cs` | **変更**（順序を入れ替え・コメントを是正） |
| `Infrastructure/Persistence/OrderActivityProjection.cs` | 変更なし —— `IsTerminal` へ委譲したまま（`Filled` は射影側の終端） |
| `Infrastructure/ExternalServices/IMoomooTradeClient.cs` | **変更**（`MoomooOrderState.Unknown`） |
| `Infrastructure/ExternalServices/MMApiMoomooTradeClient.cs` | **変更**（OpenD コードの写像を「確認できた失敗」と「不明」に分ける） |
| `Infrastructure/ExternalServices/MoomooBrokerAdapter.cs` | **変更**（`Unknown` → `Accepted`・既定を反転・誤ったコメントの是正） |
| `Tests/.../MMApiMoomooTradeClientMappingTests.cs` / `MoomooBrokerAdapterTests.cs` | **変更**（`4` / `-1` を `Failed` と固定していた既存の主張を是正し T-10-407 を足す） |
| `Tests/.../PortfolioLedgerInFlightCloseTests.cs` / `EfPortfolioLedgerInFlightCloseTests.cs` / `PortfolioLedgerConsumersTests.cs` | **変更**（T-10-406・T-10-404 の保護レグ） |
| `docs/tests/FR-10_risk-controls-tests.md` | **変更**（T-10-406 / T-10-407 と T-10-404 の記述の是正） |
| `.ai-context/adr/IADR-0117_*.md` ＋ `.ai-context/adr/README.md` | **変更**（追記・索引行） |

除外した理由:

- `Shared.Infrastructure/.../PaperBrokerAdapter.cs` の終端述語は**内蔵 paper の自前定義**であり、
  ブローカー照会の「不明」という概念を持たない（擬似約定は必ず確定する）。本件の母集合に入らない。
- `OrderExecutionService/Domain/OrderStatusLifecycle.cs` は**発注執行側の同名の純関数**であり、
  約定追跡の「引き直しを止めてよいか」を判定する。`Filled` はそこでは正しく終端である（引き直す意味が無い）。
  在庫解放とは別の問い。
- 変更に含めた（当初「変更不要」と判断しかけたが、軸 3 の走査で捕まえた）: `docs/functional/FR-10_risk-controls.md`
  の**式そのものは変わらない**（「終端になったと確認できていない承認だけを数える」）が、その下の散文が
  終端を「**取消・失効・拒否・全量約定**」と列挙しており、改定 2 で**誤りになった**。是正した。
  🔴 **これが規則 10（是正のたびに「この変更で新たに誤りになる自分の記述」を引き直す）の実例である** ——
  「終端の定義を変える」という変更は、**変更前の語（「全量約定」）で引かないと捕まらない**。

---

## ［2026-09-19 追記 / #848］監査ブロッキング B3 の是正（2 巡目・PR #851 head `94c0da10` に対する指摘）

フェーズ末監査の 2 巡目が**ブロッキング 1 件（B3）**を出した。1 巡目の B2 と**同じ穴の裏側**である ——
B2 は**照会側**の写像（`MoomooOrderState.Unknown` を新設して解消）、B3 は**発注側**の写像であり、
**両方を塞がないと「不明では在庫を解放しない」は成立しない**。

### B3: 発注側の「届いたか不明」が終端 `Rejected` へ畳まれ、在庫解放の引き金になっている

**指摘（実測）**: `MoomooBrokerAdapter.PlaceWithRejectionDetailAsync` の包括 catch が、
**送信後の SDK 例外・応答異常**を終端 `Rejected` に倒していた。ここへ落ちる代表例は `SendAsync` の
返信待ちタイムアウトであり、**`send()` は既に実行済み**である（`MMApiMoomooTradeClient` のコメント自身が
「発注**送信後**の失敗は**届いたか不明**」と書いていた）。

```
[発注側] status=Rejected terminal=True orderId=1b26000f...（Guid.NewGuid の偽 ID）
[台帳]   承認 3,381 → OrderExecuted(Rejected, 約定 0) → 処理中=0（建玉の押さえが解ける）
```

**帰結は 2 つある。**

1. **決済（手仕舞い）**: 手仕舞い注文が証券会社側で生きているかもしれないのに押さえが解け、再要求が通って
   **同じ株数に 2 本の決済が並ぶ＝二重決済でショート化**する。本 PR 以前は 30 分の窓が偶然これを防いでいた
   （それが #848 の不便の正体でもあった）。
2. **エントリー**: `Rejected` は `OrderExecutionAppService` で「終端失敗＝建玉が生じない」と読まれ、
   **保護レグを張らずに正常終了**する。注文が実際には生きていた場合、**無保護の建玉**がそのまま残る。

### 是正（決定 2 の補い・**発注側の**写像で直す。B2 は照会側だった）

**包括 catch を廃し、`BrokerDispatchIndeterminateException` として伝播させる。**

- 新設した例外は `BrokerUnavailableException`（接続確立の失敗＝**確実に未発注**）と**対になる型**で、
  同じ `Shared.Contracts/Ports/` に並べる。契約は「**送信は済んだが結果が確認できない**」だけを運ぶこと。
- `OrderExecutionAppService` は本例外を**明示的に捕捉して Error でログし、そのまま再送出**する。
  ここで行ってよいことは**何もしない**ことだけである ——
  - 予約を**解放しない**（解放すると再配送で二重発注。`BrokerUnavailable` との決定的な違いはここである）
  - 予約を**確定しない**・結果を**保存しない**（**偽の注文 ID による終端記録を作らない**。#842 と同型の指摘）
  - **見送り（`OrderDispatchForgone`）にもしない** —— 見送りは「発注していない」という主張であり、
    ここでそれを主張するのは `Rejected` と同じ誤り（建玉が無いという仮定）である
- 予約は `Reserved` のまま残り、**client order id（remark＝`DecisionId`）による突合**
  （`MoomooReservationBrokerProbe` → `OrderReservationReconciler`）が実状態を
  `Placed` / `NotPlaced` / `Indeterminate` に解決する。発注済みだった場合は**証券会社が採番した本物の
  注文 ID**で `executed_orders` に記録され、`OrderExecuted` が発行される。**予約を勝手に完了させない。**
- **変えない側（#848 の射程を壊さない）**: 発注執行で `OrderStatus.Rejected` を返す箇所は 3 つあり、
  誤っていたのは包括 catch の 1 つだけである。**発注前検証での棄却**（確実に未送信）と
  **`MoomooTradeRequestException`**（`retType != 0`＝**確認できた**非受理）は `Rejected` のままとする
  ——「確認できた拒否」による在庫解放は #848 の射程内であり、外すと 2 つ目の恒久ロックを作る。
  🔴 **［2026-09-19 追記 / #848・B5］この箇条の「`retType != 0`＝確認できた非受理」は誤りだった**（4 巡目監査）。
  確認できた非受理は **`retType == -1`（`Failed`）だけ**である。`-100`（TimeOut）/ `-200`（DisConnect）/
  `-400`（Unknown）/ `-500`（Invalid）は**送信後に返事が読めなかった**ことを SDK が応答の形に包んだ値で、
  B3 と同じ「届いたか不明」である。**誤っていた箇所は 1 つではなく 2 つだった。** 是正は末尾の B5 の節。

> 🔴 **これは「例外を握り潰す fail-safe」が反転していた実例である。** 包括 catch のコメントは
> 「フローを止めない・実弾防止の安全側」と書いていた。**在庫解放の引き金が `Rejected` になった瞬間、
> 『フローを止めない』は『状態を知らないまま押さえを解く』に化けた。**
> B1・B2 と合わせて 3 度目であり、**共通の形は「安全側と書いてある既定が、下流の意味変更で反転する」**である。

### 共有契約 `OrderStatus` へ値を足さない理由

**足さない。** 本件で必要なのは「不明という**状態**を運ぶこと」ではなく「**結果を確認できていないのだから
何も主張しないこと**」である。`OrderStatus` は**証券会社に存在する注文の状態**の集合であり（FR-05）、
存在するかどうかが分からない注文はその集合の要素ではない。値を足すと通知の文面・監査・射影・
ペーパーアダプタまで「不明」を表現する義務が広がるうえ、**`Unknown` という状態を持つ注文記録**を
台帳に作ることになり、偽の注文 ID を残さないという不変条件 3 と正面から衝突する。
**記録を作らずに例外で終わる**方が、意味の上でも面の上でも小さい。B2 が
`MoomooOrderState`（サービス内の正規化列挙）で足りたのと同じ判断である。

### 追加する受け入れ基準

8. **送信後に結果を確認できなかった発注は、在庫解放の引き金を作らない。** 終端の記録
   （`ExecutionRecord` / `OrderExecuted(Rejected)`）が 1 件も生じず、処理中の決済は承認数量のまま押さえられる。
9. **同じとき、「建玉は生じていない」とも仮定しない。** エントリーでも終端失敗として扱わず、
   予約を `Reserved` のまま据え置いて突合の解決に委ねる（偽の注文 ID を残さない）。
   **変えない側**: 発注前棄却と確認できた非受理は従来どおり `Rejected`、確実に未発注は従来どおり見送り。
   ［2026-09-19 追記 / #848・B5］「確認できた非受理」は `retType == -1` に限る（受け入れ基準 13・14。末尾の B5 の節）。

### 追加するテスト（`T-10-408` から採番。407 以下は本 PR で使用済み）

| ID | 固定すること |
| --- | --- |
| **T-10-408** | 🔴 **送信後に結果を確認できない発注を「拒否」とも「建玉なし」とも仮定しない**（13 ケース）。写像（手仕舞い・エントリー・保護レグ・成行・代替注文種別）／**実アダプタを通した結線**（在庫解放の引き金を作らない・建玉なしと仮定しない）／据え置いた予約を突合が本物の注文 ID で解決する・確定できないあいだは据え置いたまま／**変えない側**（確認できた非受理は `Rejected`・確実に未発注は見送りで解放） |

### 母集合（本追記の是正のために引き直したもの）

走査（2026-09-19・`git rev-parse --is-shallow-repository` ＝ `false`）:

- 軸 1: `grep -rn "届いたか不明" backend docs .ai-context`（**誤りの側の概念**。「不明」と書きながら
  拒否へ倒している箇所を、倒し方を変える前に全部引く）
- 軸 2: `grep -rn "送信後" backend docs .ai-context`
- 軸 3: `grep -rn "Rejected に倒\|Rejected へ倒\|Rejected で返し" backend docs .ai-context`
- 軸 4: `grep -rn "OrderStatus.Rejected" backend --include=*.cs`（発注執行で拒否を作る全 3 箇所の同定）

| 箇所 | 扱い |
| --- | --- |
| `Shared.Contracts/Ports/BrokerDispatchIndeterminateException.cs` | **追加**（`BrokerUnavailableException` と対になる契約） |
| `Shared.Contracts/Ports/BrokerUnavailableException.cs` | **変更**（対の型を指す。「従来どおり例外をそのまま伝播」が型を持った） |
| `Infrastructure/ExternalServices/MoomooBrokerAdapter.cs` | **変更**（包括 catch を伝播へ・クラス冒頭の誤った要約を是正・ログ文面） |
| `Infrastructure/ExternalServices/MMApiMoomooTradeClient.cs` | **変更**（分類のコメントが新しい型を指す） |
| `Features/OrderExecution/DispatchApprovedOrder/OrderExecutionAppService.cs` | **変更**（明示的な捕捉＋Error ログ＋再送出。解放も確定も見送りもしないことを本文で固定） |
| `Tests/.../MoomooBrokerAdapterTests.cs` | **変更**（是正前の挙動を肯定していた `client_例外_送信後の失敗_は_Rejected_に倒す_fail_safe` を廃し T-10-408 へ） |
| `Tests/.../MoomooBrokerAdapterAlternativeStopTests.cs` | **変更**（同上・S3 側） |
| `Tests/.../OrderExecutionServiceIndeterminateDispatchTests.cs` | **追加**（アプリケーション層の契約＋実アダプタを通した結線） |
| `Features/OrderExecution/GuardProtectiveStops/ProtectiveStopGuard.cs` | 変更なし —— 例外を既に捕捉しており、**分岐は是正前後で同一**（拒否が返っていたときと同じ「再発注不可→手仕舞い」へ落ちる）。偽 ID の記録と「手仕舞い済み」の誤った主張が消える方向にだけ動く |
| `Features/.../OrderExecutionAppService.PlaceProtectiveStopAsync` | 変更なし —— 同上（拒否と同じ「未受理」分岐へ落ちる。S3 の試行の記録には例外のメッセージが載るため監査に空欄は残らない） |
| `docs/tests/FR-10_risk-controls-tests.md` | **変更**（T-10-408 の追加・対照実験・**T-10-303 / T-10-363 の是正**。どちらも「送信後の失敗は従来どおり拒否へ倒す」と書いており、本是正で**誤りになった**） |
| `docs/operations/broker-execution-paths-runbook.md` | **変更**（ログ文面の行・`OrderId` の形の表。**「記録が無い＝発注していない」ではない**ことを明記し、滞留予約を探す SQL を足した） |
| `.ai-context/adr/IADR-0117_*.md` ＋ `.ai-context/adr/README.md` | **変更**（改定 6・索引行） |
| `.ai-context/adr/IADR-0211_*.md` ＋ 同索引行 | **変更**（同 ADR が列挙した「`Rejected` へ倒す 3 事象」の 3 番目が本是正で**誤りになった**。日付つき追記で訂正） |

除外した理由:

- `PaperBrokerAdapter`（内蔵 paper）: ネットワーク越しの送信が無く、「送ったが結果が分からない」という
  事象そのものが起こり得ない。包括 catch も持たない。**契約 `OrderStatus` を触らなかったため波及もゼロ**である。
- 通知・監査・射影: `OrderStatus` の値域を変えていないため、写る面が 1 つも増えない
  （これが「契約へ値を足さない」ことの実利である）。
- `OrderExecutionService/Domain/OrderStatusLifecycle.cs`: 約定追跡の「引き直しを止めてよいか」の判定であり、
  本件は**そもそも記録を作らない**ため到達しない。

### 残余リスク（本追記で新たに残るもの）

- **保護レグの送信後が不明なとき、「張れなかった」側へ倒す挙動は変えていない**（既存の分岐のまま）。
  逆指値が実際には生きていた場合、エントリーを成行で手仕舞うと**孤立した逆指値**が残り得る。
  是正前（`Rejected` が返っていた）と**同一の挙動**であり本追記で悪化はしないが、
  同型の穴であることは確かなので**別 issue で扱う**（#853。本 PR の射程外＝IADR-0210 の fail-closed 方針の見直しを伴う）。
- **突合で `Placed` と確定したエントリーに、保護レグは張られない**（`OrderReservationReconciler` は
  記録と `OrderExecuted` の発行までしか行わない）。建玉突合（IADR-0118）が乖離として報告する経路は残る。
  これも**別 issue**で扱う（#853）。
- **「不明」は計器に現れない**。`BusinessMetrics` は注文状態と見送り理由で数えており、例外で終わる本経路は
  どちらにも計上されない（Error ログと `_error` キューでのみ観測できる）。計器を足すかは運用で判断する。

### 併せて起票した非ブロッキング（本 PR では直さない）

- **#852**: 見送り（`OrderDispatchForgone`）が取引台帳へ届かず、手仕舞いが 30 分の窓の満了まで処理中のまま残る。
  購読を実測で引いた（リスク管理側は `OrderDispatchForgoneActivityHandler` の 1 本だけで、
  `MarkTerminal` を呼ぶ台帳ハンドラは存在しない）。**射程が違う**ため本 PR では直さない ——
  #848 は「**終端になった**承認を外す」であり、見送りは「そもそも発注していない」という別の事実である。
  是正するときも **fail-safe の向きを守る**こと（外してよいのは「確実に未発注」を意味する理由のときだけ）。
- **#853**: 保護レグの状態が不明／後から判明したときの扱い（上の残余リスク 2 件）。

---

## ［2026-09-19 追記 / #848］監査ブロッキング B4 の是正（3 巡目・PR #851 head `f873a6d3` に対する指摘）

フェーズ末監査の 3 巡目が**ブロッキング 1 件（B4）**を出した。B1〜B3 の是正は確認済みで、
**B4 は B3 の是正そのものが作った穴**である。

### B4: B3 の是正が、保護逆指値ガードの成行手仕舞いを「撃ち直し」に変えた

**指摘（実測）**: `ProtectiveStopGuard.ReplaceOrCloseAsync` の成行手仕舞いは
`catch (Exception ex) when (ex is not OperationCanceledException)` で例外を握り、**`MarkCompleted(stop)` を
呼ばずに** `Remediation.None` で終わる。逆指値の記録は `Active` のまま残り、**次の巡回（既定 30 秒）で
同じ数量の成行手仕舞いをもう一度送る**。

```
indeterminate=False（是正前の形）: 1巡目=(Completed, 成行 1 回)  2巡目=(Completed, 成行 1 回)
indeterminate=True （是正後の形）: 1巡目=(Active,    成行 1 回)  2巡目=(Active,    成行 2 回)
```

B3 以前はアダプタが終端 `Rejected` を**返して**いたためこの catch に例外は来ず、1 回で終わっていた。
B3 が「届いたか不明」を**例外**にした瞬間、この catch の意味は
「確実に失敗した → 次の巡回で再試行してよい」から
「**届いたか不明 → 未発注と仮定して撃ち直す**」へ反転した。
1 巡回ごとに全数量の成行売りが 1 本増える＝**二重決済でショート化**（本 PR が守ろうとしている性質そのもの）。

> 🔴 **上の B3 の母集合の表は、この行を「変更なし——分岐は是正前後で同一」と書いていた。事実と異なる。**
> 同一だったのは**逆指値の再発注**（`PlaceStopOrderAsync` の catch → 手仕舞いへ落ちる）だけであり、
> **成行手仕舞い**（`PlaceMarketOrderAsync` の catch）は「1 回で完了」から「巡回ごとに再送」へ変わっていた。
> 同じ表の `OrderExecutionAppService.PlaceProtectiveStopAsync` の行も、その先の
> `CloseUnprotectedPositionAsync`（成行手仕舞い）の挙動が変わっていたこと
> （`PositionClosed`＋偽 ID の記録 → `None`）を載せていなかった。**下の表で引き直す。**
> 誤りの形は B2・B3 と同じで、**「変更なし」を、誤りの側の語（ここでは `catch (Exception`）で引き直さずに書いた**ことである。

### 是正（決定 2 の補い・**呼び出し側**で直す。B2 は照会側の写像、B3 は発注側の写像だった）

**成行手仕舞いを、エントリーと同じ「予約 → 発注 → 確定」の 3 相（IADR-0057）に載せる。**
ソフトウェア逆指値（S1・PR #830）の決済が採っている作法と同じである。

1. **決定的な DecisionId は既にある**（`ProtectiveStopIds.CloseDecisionId(entry, attempt)`。
   ガードでは `attempt = stop.Attempt + 1` であり、手仕舞いが完了しない限り `stop.Attempt` は進まないため
   **巡回をまたいで同じ値**になる）。足りなかったのは**予約**である —— ガードは
   `IOrderReservationStore` を持っておらず、「送ったかもしれない」をどこにも記録していなかった。
2. **送る前に `TryReserve(closeDecisionId)`。** 取れなければ**送らない**（`Outcome.Unknown`＝据え置き）。
3. **例外を 3 つに分ける**（一括 catch に混ぜない）:

   | 例外 | 意味 | 予約 | 次の巡回 | 通知 |
   | --- | --- | --- | --- | --- |
   | `BrokerUnavailableException` | 接続確立の失敗＝**確実に未発注** | **解放する** | **撃ち直してよい**（同じ DecisionId で再予約できる） | 従来どおり `Remediation=None`（Critical） |
   | `BrokerDispatchIndeterminateException` | 送信済み・**届いたか不明** | **解放も確定もしない**（`Reserved` のまま） | 🔴 **撃ち直さない**（予約が残っている限り再送しない） | **新設 `Remediation=CloseDispatchIndeterminate`（Critical）**。`CloseDecisionId` / `CloseIntent` を運ぶ |
   | それ以外（`OperationCanceledException` を除く） | **未発注と言い切れない**（送信後の保存失敗を含む） | 同上 | 同上 | 同上 |

   「確実に未発注」と言えるのは `BrokerUnavailableException` **だけ**であり（IADR-0211 決定 1 がその契約を
   型に持たせている）、**それ以外は全部「送ったかもしれない」側へ倒す**。
4. **巡回の入口で、この試行の手仕舞いレグの痕跡を先に見る**（逆指値の再発注より**前**）:
   - 発注結果の記録（`executed_orders`）が既にある → **送らずに**その記録で完了させる
     （送信後に行の更新だけが失われた窓・突合が `Placed` と解決した後）。`PositionClosed` を発行する。
   - 予約だけがある（記録なし）→ **逆指値の再発注も成行も行わず据え置く**（`Outcome.Unknown`）。
     🔴 **逆指値の再発注より前に見る理由**: 成行手仕舞いが生きているかもしれない建玉へ新しい逆指値を張ると、
     手仕舞いが約定した後に**建玉なき逆指値**が残り、発火すれば反対建玉になる。
5. **無音にしない。** 不明になった巡回で Critical の通知を 1 回出す（上表）。以後の据え置きは巡回のたびに
   Warning ログ（`DecisionId` つき）と件数サマリ（`Unknown`）に現れる。**30 秒ごとに Critical を重ねない**
   （IADR-0211 決定 5 が通知の重みを決めたのと同じ判断。本当に止まる事象の通知を埋もれさせない）。
   ［2026-09-19 追記 / #848・4 巡目監査］**「1 回」は改めた。** 据え置きが続くあいだ 1 時間ごとに、再起動後は最初の巡回で
   出し直す（30 秒ごとに重ねない判断は維持）。末尾の B5 の節「非ブロッキングの受け止め」1〜3。

### なぜ `ProtectiveStopRemediation` へ値を足すのか（`OrderStatus` へは足さなかったのに）

B3 で `OrderStatus` へ `Unknown` を足さなかったのは、`OrderStatus` が**証券会社に存在する注文の状態**の
集合であり、存在するか分からない注文はその要素ではないからだった。**`ProtectiveStopRemediation` は
「保護喪失に対してシステムが何をしたか」の集合であり、「成行手仕舞いを送ったが結果を確認できていない」は
その正当な要素である。** 既存の `None`（対処も失敗）で代用すると 2 つの実害が出る。

- **台帳が押さえない。** `None` は `CloseDecisionId` / `CloseIntent` を運ばない約束であり、リスク管理は承認行を
  足さない。生きているかもしれない成行手仕舞いが**処理中の決済として数えられず**、利用者の手仕舞い要求
  （UC-06）が通って**同じ株数に 2 本の決済が並ぶ**。是正前（偽 ID の `Rejected`＋`PositionClosed`）は
  承認行が足されて 30 分の窓で押さえられていたので、**B3 はここでも安全側を 1 つ外していた**。
- **通知が人を誤誘導する。** `None` の文面は「建玉の解消にも失敗しました。直ちに確認してください」であり、
  読んだ利用者は**手で成行を重ねる**。不明のときに伝えるべきは「**送った。届いたか分からない。
  重ねる前に証券会社の画面で注文と建玉を確かめよ**」である。

面は小さい —— 値を読むのは通知（`NotificationFormatter`）と監査要約（`AuditEntryFactory`）の 2 か所だけで、
どちらも既定アームが Critical 側へ倒れる。リスク管理の台帳ハンドラは `Remediation` を読まず
**`CloseDecisionId` / `CloseIntent` の有無**だけで承認行を足すため、コードは変わらない（コメントだけ直す）。
列挙は**末尾へ足す**（既存値の序数を動かさない）。

### 全呼び出し元の走査（B4 と同じ穴が他に無いか）

走査（2026-09-19・`git rev-parse --is-shallow-repository` ＝ `false`）:

- 軸 1: `grep -rn "PlaceOrderAsync\|PlaceStopOrderAsync\|PlaceMarketOrderAsync\|PlaceAlternativeStopOrderAsync" backend --include=*.cs`
  からテストと定義（インターフェース・アダプタ実装）を除いた**本番の呼び出し**＝ 7 箇所（5 経路）
- 軸 2: 各呼び出しを囲む `catch` を**コードで読む**（`grep -n "catch (" <file>`）
- 軸 3（**誤りの側の語**）: `grep -rn "分岐は是正前後で同一\|挙動は 1 バイトも\|リコンサイルが守る\|リコンサイルの解決\|リコンサイルに\|突合が解決\|突合の解決" backend docs .ai-context`

`BrokerDispatchIndeterminateException` を投げ得るのは `MoomooBrokerAdapter` の 4 メソッドで、
いずれも `PlaceWithRejectionDetailAsync` を通る。行番号は是正前（head `f873a6d3`）のもの。

| # | メソッド | 呼び出し元 | 例外の扱い（是正前） | 撃ち直すか | 判定・本追記での扱い |
| --- | --- | --- | --- | --- | --- |
| 1 | `PlaceOrderAsync`（2 形） | `OrderExecutionAppService.ExecuteAsync` :115 / :116 | 個別に捕捉し再送出。予約は `Reserved` のまま | **しない**（再配送は `TryReserve` が失敗し `OrderDispatchReservationConflictException`） | **安全**（B3 で是正済み）。分岐は変更なし（コメントとログ文面だけ条件つきへ直す） |
| 2 | `PlaceAlternativeStopOrderAsync` / `PlaceStopOrderAsync` | `OrderExecutionAppService.PlaceProtectiveStopAsync` :268 / :279 | 一括 catch → 「未受理」分岐（エントリーの取消／成行手仕舞い） | **しない**（単発。再配送は相 1 が既存結果を返す） | 未発注と**仮定している**が、分岐は B3 の前後で**同一**（前は偽 ID の `Rejected` が返り同じ分岐へ落ちた）。逆指値が生きていれば**孤立した逆指値**が残る＝ #853 の 1。**変更なし**（IADR-0210 の fail-closed との優先順位の裁定が要る） |
| 3 | `PlaceMarketOrderAsync` | `OrderExecutionAppService.CloseUnprotectedPositionAsync` :379 | 一括 catch → `Remediation=None` | **しない**（単発） | 🔴 **B3 で挙動が変わっていた**（前: 偽 ID の記録＋`PositionClosed`＝台帳が 30 分押さえる／後: `None`＝**台帳が押さえない**・通知は「解消に失敗」）。撃ち直しはしないが、**利用者の手仕舞い要求が通って二重決済になり得る**。**変更**: 3 相へ載せ、不明は `CloseDispatchIndeterminate`（`CloseIntent` つき）で台帳に押さえさせる |
| 4 | `PlaceStopOrderAsync` | `ProtectiveStopGuard.ReplaceOrCloseAsync` :134 | 一括 catch → 成行手仕舞いへ落ちる | 成行が「確実に未発注」で終わったときだけ、次の巡回で**同じ `StopDecisionId` の逆指値を再送し得る** | 分岐は B3 の前後で**同一**（前は偽 ID の `Rejected` が返り同じ分岐）。逆指値が生きていた場合の孤立・重複は #853 の 1。**逆指値レグは変更なし**。ただし本追記の 4（入口で手仕舞いレグの予約を見る）により、**成行が不明のあいだは逆指値も重ねない** |
| 5 | `PlaceMarketOrderAsync` | `ProtectiveStopGuard.ReplaceOrCloseAsync` :171 | 一括 catch → `None`・記録は `Active` のまま | 🔴 **する**（30 秒ごとに全数量の成行が 1 本ずつ増える） | **B4 本体。変更**（上の是正 1〜5） |

対象外:

- **S1 の `SoftwareStopExecutor`**（ソフトウェア逆指値の成行決済）は**本ブランチに無い**（PR #830 側）。
  あちらは最初から予約＋決定的 DecisionId で書かれており、本追記はその作法に**倣った側**である。
  #830 と本 PR のどちらが後にマージされても、`BrokerDispatchIndeterminateException` は S1 の
  包括 catch（届いたか不明＝予約を残す）へ落ちるため向きは合う。
- `PaperBrokerAdapter`: 送信が無く、本例外を投げない。
- `OrderAmendmentDispatcher`（訂正・取消）: 発注（`Place*`）を呼ばない。

### 追随する記述（規則 9・10。誤りの側の語で引いた結果）

| 箇所 | 扱い |
| --- | --- |
| 本仕様書の B3 の表（`ProtectiveStopGuard.cs` / `PlaceProtectiveStopAsync` の「変更なし」） | **本追記で訂正**（上の引用ブロック。B3 の節の本文は書き換えない＝当時の判断の記録として残す） |
| PR #851 本文の同じ表 | **訂正**する |
| #853 の本文「#851 は例外へ変えただけで挙動は 1 バイトも変わっていない」 | 成行手仕舞いについては**事実と異なる**。#853 の射程（逆指値レグ）については正しい。報告に残す |
| 「リコンサイルが解決する」と断定している箇所（`BrokerDispatchIndeterminateException` の契約コメント・`BrokerUnavailableException`・`OrderExecutionAppService` / `MoomooBrokerAdapter` のコメントとログ文面・runbook・IADR-0117 改定 6・IADR-0211 追記・本仕様書 B3 の節） | **条件つきへ直す**（下） |

### 非ブロッキングの受け止め

- 🔴 **「リコンサイルが解決する」は、いまの配備では成立しない。** `Reconciliation:Enabled` の既定は `false`、
  `Reconciliation:UseBrokerProbe` も `false`（`ReconciliationOptions.cs` / `Program.cs`）であり、
  `deploy/` に上書きは無い（`grep -rni reconcil deploy` ＝ 0 件）。**滞留 `Reserved` は自動では解決しない。**
  上の B3 の節が「突合が実状態を解決する」と書いたのは**機構が存在する**という意味でしかなく、
  **稼働しているとは限らない**。コード・runbook・IADR の文面を「**有効なら**突合が解決する／
  既定（無効）では**人が解決する**」へ直し、runbook に人手の手順を足す。
  **有効化そのものは別 issue（#856）**（実照会プローブは SIMULATE の注文履歴照会に依存し、`NotPlaced` の誤判定は
  二重発注に直結するため、有効化は実機での検証を伴う）。
- `_error` キューに最後に残る例外は `OrderDispatchReservationConflictException` であり**真因を指さない**
  （初回が `BrokerDispatchIndeterminateException`、再配送が予約の衝突で落ちるため）。
  runbook に「**真因は初回の Error ログ**」と明記する。
- `MoomooBrokerAdapter.MapState` の既定アーム `_ => Accepted` が **`Unknown` 専用ではない**
  （将来 `MoomooOrderState` に足された値もここへ落ちる）ことをコメントで明示する。

- **走査で引き当てた射程外の穴（#857）**: 成行手仕舞いが**確認できた拒否**（`Rejected` が**返る**）で終わっても、
  ガードと発注執行は状態を見ずに `PositionClosed` を主張し、ガードは記録を `Completed` にする
  （逆指値なしの建玉が巡回対象から外れる）。**本 PR の前後で同一**であり、直すには試行番号の進め方と
  撃ち直しの上限を決める必要があるため別 issue とした。本追記は**不明**だけを扱う。

### 追加する受け入れ基準

10. **保護逆指値ガードの成行手仕舞いは、届いたか不明のとき撃ち直さない。** 2 巡回・3 巡回まわしても
    成行の送信回数は **1 回のまま**であり、逆指値の再発注も重ねない。Critical の通知が 1 回出る
    （［2026-09-19 追記 / #848］3 巡回＝1 時間未満のあいだの話である。据え置きが続いたときは受け入れ基準 15・16）。
11. **確実に未発注（接続確立の失敗）のときは、従来どおり次の巡回で撃ち直せる**（変えない側）。
12. **不明な成行手仕舞いは、取引台帳が処理中の決済として押さえる**（`CloseIntent` を運ぶ）。

### 追加するテスト（`T-10-409`）

| ID | 固定すること |
| --- | --- |
| **T-10-409** | 🔴 **成行手仕舞いの「届いたか不明」を未発注と仮定して撃ち直さない。** ガード: 3 巡回まわして成行の送信が 1 回のまま／逆指値の再発注も 1 回のまま／予約は `Reserved` のまま・記録なし／通知は不明の 1 回だけで `CloseIntent` を運ぶ／分類できない例外も同じ側へ倒す／**実アダプタを通した結線**（送信後の応答異常）／突合が `Placed` と解決したら送らずに完了／**変えない側**: 確実に未発注は予約を解放して次の巡回で撃ち直す・成功は予約を確定する。発注執行（エントリー直後の成行手仕舞い）: 不明は `CloseDispatchIndeterminate`＋予約据え置き・確実に未発注は `None`＋解放。下流: 台帳は不明の手仕舞いを押さえる・通知と監査要約は「重ねて発注しない・証券会社の画面で確認」を伝える |

### 既存テストの刺激を変えたもの（弱めていないことの説明）

- `ProtectiveStopGuardTests.手仕舞いも失敗したらNoneで…` と
  `OrderExecutionServiceProtectiveStopTests.手仕舞いも失敗したらNoneを返す`: 刺激を
  `InvalidOperationException`（分類できない例外）から **`BrokerUnavailableException`（確実に未発注）**へ変えた。
  両テストが固定したいのは「**次の巡回で再試行できる失敗は `None` で人手対応を求める**」であり、
  それが成り立つのは確実に未発注のときだけである。分類できない例外は T-10-409 が**逆向き**（撃ち直さない）で固定する。
- `不変条件_建玉が残るなら有効な逆指値があるか人手対応が発火している`: 人手対応の発火に
  `CloseDispatchIndeterminate` を加えた（Critical であることは通知のテストが固定する）。

### 対照実験（実走した実測・2026-09-19）

`ProtectiveStopGuard.cs` と `OrderExecutionAppService.cs` のロジックを是正前（head `f873a6d3`）へ戻し
（`git diff --stat` で `OrderExecutionAppService.cs` は差分 0・`ProtectiveStopGuard.cs` は新テストをコンパイルさせるための
コンストラクタ引数 5 行だけ、を確認）、`dotnet test backend/Services/OrderExecutionService/Tests` を実走した。

```
失敗 …ProtectiveStopGuardIndeterminateCloseTests.成行手仕舞いが届いたか不明なら_3巡回まわしても成行の送信は1回のまま_否定形(close: Indeterminate)
   Expected h.Broker.MarketCloseCount to be 1 because 届いたか不明の成行を、未発注と仮定して撃ち直してはならない（二重決済でショート化）, but found 3.
失敗 …同上(close: Unclassified)
   Expected h.Broker.MarketCloseCount to be 1 because …, but found 3.
失敗 …実アダプタ経由_成行の送信後タイムアウトを_巡回ごとに撃ち直さない_否定形
   Expected client.MarketSendCount to be 1 because OpenD へ送った成行は 1 本だけ（是正前は巡回ごとに 1 本ずつ増えた）, but found 3.
失敗 …届いたか不明は無音にしない_Criticalの通知を不明になった巡回で1回だけ出し_手仕舞いレグを運ぶ
   Expected lost.Remediation[0] to be ProtectiveStopRemediation.CloseDispatchIndeterminate {value: 3} …, but found ProtectiveStopRemediation.None {value: 2}.
失敗 …突合が発注済みと解決したら_送らずに記録で完了させる       Expected reconciled.Terminalized to be 1, but found 0
失敗 …突合が未発注と解決したら_予約が解放され次の巡回で撃ち直せる   Expected (…).Released to be 1, but found 0
失敗 …成行手仕舞いが成功したら予約を確定する
失敗 …OrderExecutionServiceProtectiveStopTests.成行手仕舞いが届いたか不明なら_未発注と主張せず手仕舞いレグを運び予約を据え置く_否定形(close: Indeterminate)
   Expected lost.Remediation to be …CloseDispatchIndeterminate {value: 3}, but found …None {value: 2}.
失敗 …同上(close: Throw)
失敗 …OrderExecutionServiceProtectiveStopTests.成行手仕舞いが成功したら手仕舞いレグの予約を確定する
失敗!   -失敗:    10、合格:   496、スキップ:     0、合計:   506
```

**10 件が赤**（T-10-409 として発注執行に足した 11 ケース中）。残り 1 件
（`確実に未発注_接続確立の失敗_なら予約を解放し_次の巡回で撃ち直す`）は**是正前も緑**——変えない側の固定だからである。
是正後は **506 件すべて緑**。

### 残余リスク（本追記で新たに残るもの）

- **据え置かれた建玉は、予約が解決されるまで逆指値なしのまま残る。** 撃ち直さないことの代償であり、
  「二重決済でショート化しない」を優先した結果である。Critical の通知・巡回ごとの Warning ログ・
  `order_dispatch_reservations` の滞留行で見える。自動リコンサイルが無効のあいだは人が解決する（runbook。有効化は #856）。
- **送信中にプロセスが止まった場合（`OperationCanceledException`）は通知が出ない。** 予約は `Reserved` のまま残るので
  再起動後も撃ち直しはしない（安全側）が、Critical は発行されず、巡回ごとの Warning ログでしか気付けない。
  S1（#820）の決済も同じ形である。
  ［2026-09-19 追記 / #848・4 巡目監査］🔴 **この項は被害を小さく書いていた。** 出ないのは Critical だけではなく、
  **`CloseIntent` も一度も発行されないので取引台帳が一切押さえない**（利用者の手仕舞いが 30 分待たずに通る）。
  **常駐ガードについては塞いだ**（再起動後の最初の巡回が入口で発行する。末尾の B5 の節）。
  発注執行の単発の成行手仕舞い（`CloseUnprotectedPositionAsync`）には巡回が無く、同じ穴が**残る**。
- **不明の成行を台帳が押さえるのは 30 分の窓のあいだだけ**（IADR-0117 決定 3 の窓）。窓の満了後は利用者の手仕舞い要求が通る。
  窓は「終端が届かない承認による恒久ロックを防ぐ」ための意図した受け皿であり、本追記では変えない。
- 逆指値レグの不明（#853）・成行への確認できた拒否（#857）は本追記の射程外（どちらも本 PR の前後で同一）。

---

## ［2026-09-19 追記 / #848］監査ブロッキング B5 の是正（4 巡目・PR #851 head `ff36cd9c` に対する指摘）

フェーズ末監査の 4 巡目が**ブロッキング 1 件（B5）**を出した。B1〜B4 の是正は確認済み。
B5 は **B3 の節が「変えない側」として残した分岐そのもの**であり、B2（OpenD の注文状態 `4`＝TIMEOUT を不明へ分けた）と
**同じ基準で扱うべきだった穴**である。

### B5: 発注応答の `retType` の「不明」系の値が `Rejected` に畳まれている

**指摘（実測）**: `MMApiMoomooTradeClient.EnsureSucceeded` は `retType != 0` を**すべて**
`MoomooTradeRequestException` にし、`MoomooBrokerAdapter.PlaceWithRejectionDetailAsync` がそれを
`Terminal(Rejected)` として返す。監査が SDK の列挙を反射で実測した結果:

```
Moomoo.OpenApi.Pb.Common+RetType: Succeed=0 Invalid=-500 Unknown=-400 DisConnect=-200 TimeOut=-100 Failed=-1
```

監査の実測（実アダプタ＋`OrderExecutionAppService` に利用者の Close 承認を流した）:

```
retType=-100/-400/-200/-1: OrderExecuted.Status=Rejected orderId=<偽 32hex> reservation=Completed
```

> 🔴 **上の B3 の節（「変えない側」の箇条と受け入れ基準 9）は `retType != 0`＝「確認できた非受理」と断定していた。事実と異なる。**
> `retType` は「証券会社が答えた拒否」だけを運ぶ値ではない。**`-1` 以外は『返事が読めなかった』を SDK が応答の形に包んだもの**である（下の実測）。
> 誤りの形は B2・B3・B4 と同じで、**「変えない側」と書く前に、その値域を実測しなかった**ことである。

**帰結は B3 と同一**: Close なら `OrderExecuted(Rejected)` が `MarkTerminal` に届いて在庫が解放され、再要求が通る
（注文が実際には届いていれば**二重決済でショート化**）。Open なら「終端失敗＝建玉は生じない」と読まれて保護レグを張らない
（届いていれば**無保護の建玉**）。

### `retType` の値はどこで作られるのか（SDK の逆コンパイルで実測・2026-09-19）

`moomoo-api 10.8.6808`（`lib/netcoreapp2.1/MMAPI4Net.dll`）を `ilspycmd` で逆コンパイルして読んだ。

| 値 | 列挙名 | 作る場所（実測） | 送信は済んでいるか | 分類 |
| --- | --- | --- | --- | --- |
| `0` | `Succeed` | OpenD の応答 | 済 | 成功 |
| `-1` | `Failed` | **OpenD の応答**（`retMsg` に理由文が入る） | 済 | **確認できた非受理** → `Rejected`（従来どおり） |
| `-100` | `TimeOut` | 🔴 **SDK がクライアント側で合成する**。`MMAPI_Conn` が送信済み要求を `sentTime` から **12,000 ms** で打ち切り、`HandleReplyPacket(ReqReplyType.Timeout, …)` → `MMAPI_Trd.OnReply` が `TrdPlaceOrder.Response.CreateBuilder().SetRetType((int)replyType)` で**応答オブジェクトを自前で組み立てて** `OnReply_PlaceOrder` へ渡す | **済**（`sentProtoMap` に載るのは送信後） | **届いたか不明** |
| `-200` | `DisConnect` | SDK の `ReqReplyType` に定義がある（応答待ち中の切断）。**本バージョンの `MMAPI_Conn` が発注応答へ合成している箇所は見つからなかった**が、`MMAPI_Trd.OnReply` は `SvrReply` 以外の値をそのまま `retType` へ写す。OpenD 自身が返す可能性も否定できない | 済（応答として届く以上、要求は送信後） | **届いたか不明** |
| `-400` | `Unknown` | 同上（「結果未知」） | 済 | **届いたか不明** |
| `-500` | `Invalid` | 🔴 **SDK がクライアント側で合成する**。①応答本文の復号に失敗（`MMAPI_Conn` の受信ループ `catch → reqReplyType = ReqReplyType.Invalid`）、②`TrdPlaceOrder.Response.ParseFrom(data)` が `InvalidProtocolBufferException`（`SetRetType(-500)`） | **済**（**サーバーの返事は届いているが読めなかった**） | **届いたか不明** |
| その他 | （未定義） | — | 不明 | **届いたか不明**（B2 の「知らないコードは Unknown へ」と同じ規律） |

🔴 **`-100` は周辺事象ではなく、既定構成での「返信待ちタイムアウト」の本線である。**
本実装の返信待ち（`Broker:Moomoo:OpenD:ReplyTimeoutSeconds`）の既定は **15 秒**、SDK の打ち切りは **12 秒**である。
したがって既定構成では、B3 が「代表例」として塞いだ `SendAsync` の `TimeoutException`（15 秒）より**先に**、
SDK が `retType=-100` の応答を返す。**B3 の是正は、既定構成の返信待ちタイムアウトを塞げていなかった**
（塞げていたのは、返信待ちを 12 秒未満に構成した場合と、SDK の外で例外が起きた場合だけである）。

#### `Invalid(-500)` をどちらへ入れたか —— **「届いたか不明」の側**

依頼は「送信前のパラメータ検証で弾かれた＝確実に未発注と言えるか」を確かめて決めよ、であった。**言えない。**
列挙名（と proto の注釈「包内容非法」）は「要求が不正で弾かれた」と読めるが、**SDK の実装はそう使っていない**。
`-500` が作られるのは上表のとおり**応答の復号・パースに失敗したとき**であり、どちらも**要求は送信済みで、
サーバーの返事が届いた後**である。注文が受理された応答を読み損ねた場合もここへ落ちる。
仮に OpenD 自身が要求の不正を `-500` で返すことがあるとしても、受け手は SDK 合成の `-500` と区別できない。
**区別できない以上、安全側（不明）へ倒す。**

### 是正（決定 2 の補い・**発注応答の写像**で直す。B2 は注文状態の写像、B3 は例外の写像だった）

- **確認できた非受理は `retType == -1`（`Failed`）だけ**とする。値と述語は SDK 非依存の
  `MoomooRetType`（`IMoomooTradeClient.cs`）に置き、`MoomooTradeRequestException.IsConfirmedFailure` で読む。
  値が SDK の列挙と一致することはテストで固定する（SDK 更新で値がずれたら落ちる）。
- `MoomooBrokerAdapter.PlaceWithRejectionDetailAsync` の
  `catch (MoomooTradeRequestException ex)` に **`when (ex.IsConfirmedFailure)`** を付ける。
  **それ以外の `retType` は次の包括 catch（B3 で作った「届いたか不明」の口）へそのまま落ち**、
  `BrokerDispatchIndeterminateException` で伝播する。呼び出し側（`ExecuteAsync`／ガード／
  `CloseUnprotectedPositionAsync`／保護レグ）は B3・B4 で既に正しく受けるので**変更しない**。
  例外メッセージは `retType` / `retMsg` を含む（S3 の試行の記録に理由が残る）。
- **`EnsureSucceeded` 自体は変えない。** 照会・取消も同じ関数を通るが、どちらも失敗を「不明／まだ生きている」側へ
  読む（下の走査）。分類が要るのは**発注だけ**であり、発注の分類点はアダプタ 1 か所にある。

**回帰させない 2 件（稼働環境で実測済みの確認できた拒否。どちらも `retType=-1`）**:

| 実測 | 記録 |
| --- | --- |
| `retType=-1 The precision of Price in Place Order does not meet the specification.` | #844 |
| `moomoo PlaceOrder が失敗しました（retType=-1）: Paper trading does not support Stop order.` | #809 の本文（2026-09-16 14:43Z のログ） |

### 走査（B5 と同じ穴が他に無いか。規則 9・10）

走査（2026-09-19・`git rev-parse --is-shallow-repository` ＝ `false`）:

- 軸 1（**誤りの側の語**）: `grep -rn "retType != 0\|RetType != 0" backend docs .ai-context`
- 軸 2: `grep -rn "確認できた非受理\|確認できた\*\*非受理" backend docs .ai-context`
- 軸 3: `grep -rn "証券会社が受理しなかった\|OpenD が非成功\|ブローカー応答の拒否" backend docs .ai-context`
- 軸 4: `grep -rn "MoomooTradeRequestException" backend --include=*.cs`（型を捕まえ得る全 catch・全生成箇所）
- 軸 5: `grep -n "EnsureSucceeded" MMApiMoomooTradeClient.cs`（同じ関数を通る全操作）

| 箇所 | 扱い |
| --- | --- |
| `Infrastructure/ExternalServices/IMoomooTradeClient.cs` | **変更**（`MoomooRetType` を追加・例外に `IsConfirmedFailure`・「非成功＝拒否」と読める注釈を是正） |
| `Infrastructure/ExternalServices/MoomooBrokerAdapter.cs` | **変更**（`when (ex.IsConfirmedFailure)`・注釈・ログ文面） |
| `Infrastructure/ExternalServices/MMApiMoomooTradeClient.cs` | **変更**（`EnsureSucceeded` の注釈だけ。「非成功＝拒否ではない」と SDK の 12 秒打ち切りを書く。ロジックは不変） |
| `Shared.Contracts/Ports/BrokerDispatchIndeterminateException.cs` | **変更**（契約の注釈「OpenD が retType != 0 を返した」は対象外、が誤りになった） |
| `Tests/.../MoomooBrokerAdapterTests.cs` `証券会社が非受理を返したときは…` | **変更**（刺激が `retType=1`＝**SDK に存在しない値**だった。実測値 `-1` へ直す。**弱めていない**——固定したいのは「確認できた非受理は `Rejected`」であり、それが成り立つ値は `-1` だけである） |
| `Tests/.../MoomooBrokerAdapterAlternativeStopTests.cs` `代替注文種別の拒否は…` | **変更**（同上。`1` → `-1`） |
| `Tests/.../ProtectiveStopGuardIndeterminateCloseTests.cs` の `-1` | 変更なし（既に実測値） |
| `GetOrderList` / `GetHistoryOrderList` / `GetPositionList` / `GetAccList`（照会） | 変更なし —— 例外は `GetOrderAsync`＝`null`（照会不能）・`GetPositionsAsync`＝`null`（不明）・プローブ＝`Indeterminate` へ落ちる。**どれも「不明」の側**であり、`-100` でも `-1` でも向きは同じ |
| `CancelOrder`（取消） | 変更なし —— 例外は呼び出し側で「取消できなかった＝**注文はまだ生きている**」と読まれる（ガードは次の巡回で再試行・建玉解消は照会へ進む・訂正 API はエラーを返し `OrderCancelled` を発行しない）。**在庫を解放する向きへは倒れない** |
| `BacktestService` の `retType != 0`（ヒストリカル K 線） | 対象外 —— 発注ではなく、失敗は「その銘柄を欠測」へ落ちる |
| `.ai-context/adr/IADR-0117` 改定 6 の「変えない側」・`IADR-0211` の「現在 2 事象」・`IADR-0210` の追記・各索引行 | **変更**（日付つき追記で訂正。IADR-0117 は改定 8） |
| `docs/tests/FR-10_risk-controls-tests.md` の T-10-303 / T-10-363 / T-10-408 / T-10-409 | **変更**（「非成功は終端拒否」を「**確認できた**非成功は」へ。「Critical は 1 回だけ」を是正。T-10-450 / T-10-451 を追加） |
| `docs/operations/broker-execution-paths-runbook.md` | **変更**（ログ文面の表・非ブロッキング 1・5） |
| PR #851 本文の「B3 で変えていない側」 | **訂正**する |

### 非ブロッキングの受け止め（4 巡目。無音の失敗に当たるものを本 PR で塞ぐ）

1. **Critical が 1 回きり**（B4 の是正 5 が「30 秒ごとに重ねない」を「1 回だけ」にしていた）。
   通知を見逃すと逆指値なしの建玉が無期限に残る。`None` は巡回ごとに Critical を出すので扱いが**非対称**だった。
   → **据え置きが続くあいだ、1 時間ごとに `CloseDispatchIndeterminate` を再発行する**（間隔は定数。構成キーは足さない）。
   台帳の `AppendApproval` は `DecisionId` で冪等なので、再発行しても二重に押さえない（**窓も延びない**——最初の承認時刻のまま）。
   通知の文面に「予約が解決されるまで約 1 時間ごとに再通知する・同じ `CloseDecisionId` は同じ 1 本の成行」を足す
   （再通知を「もう 1 本送った」と読ませない）。
2. **送信中にプロセスが止まった場合**（`OperationCanceledException`）。監査の実測では再起動後のイベントが 0 件で、
   Critical が出ないだけでなく **`CloseIntent` も一度も発行されず、台帳が一切押さえない**。
   → **「予約だけある」分岐（入口の (b)）へ入った巡回で、このプロセスがまだ通知していなければイベントを発行する。**
   「通知したか」は**プロセス内の記憶**（`HeldCloseNotificationTracker`・singleton）で持つ。再起動で消えるので、
   **再起動後の最初の巡回が必ず再発行する**（永続化しないことが、そのまま「再起動後に塞ぐ」仕組みになる）。
3. **発行の欠落**（巡回の結果を受け取ってから `PublishAsync` するまでのあいだに落ちる）。
   → **2 と同じ仕組みで塞がる。** プロセスが落ちれば記憶が消え、再起動後の最初の巡回が入口で再発行する。
   プロセスが落ちずに `PublishAsync` だけが失敗した場合は、`ProtectiveStopGuardService` が**未発行分の記憶を消してから**
   例外を投げ直す（次の巡回＝30 秒後に再発行される）。配送は at-least-once になるが、受け側は台帳＝冪等・
   通知と監査＝重複しても害が無いので成立する。
4. **#853 の本文は「孤立」しか書いていない**が、実測では**届いたか不明の逆指値を巡回ごとに送り直している**
   （3 巡回で `stopSends` 1→2→3。develop でも同じ挙動）。→ **#853 へコメントで追記**する。**本 PR では直さない**
   （B5 の是正で `retType=-100` の逆指値も「不明」になるが、ガードの逆指値の catch は B3 の前後と同じ分岐へ落ちるので
   挙動は変わらない。成行が不明のあいだは入口の (b) が逆指値も止める）。
5. **runbook の「建玉突合が乖離として報告する」**は、稼働 PoC で突合が働いていることを確かめていない記述だった。
   → 条件つきへ直す（コードの既定は有効だが、実環境で乖離の報告が出ることは未確認。**突合に頼らず証券会社の画面で確かめる**）。
   同じ断定は本仕様書の B3 の残余リスク（「建玉突合（IADR-0118）が乖離として報告する経路は残る」）と
   IADR-0117 の索引行にもある（`grep -rn "乖離として報告" docs .ai-context` で引いた）。どちらも**機構が存在する**という
   意味でしかなく、**稼働 PoC で報告が出ることは確かめていない**（B4 で「突合が解決する」を条件つきへ直したのと同じ型）。
   索引行には注記を足した。B3 の節の本文は当時の記録として残し、ここで訂正する。

**本 PR で塞がないもの（残余リスクへ）**: 発注執行の `CloseUnprotectedPositionAsync`（エントリー直後の単発の成行手仕舞い）で
送信中にプロセスが止まった場合。こちらは**巡回が無い**（逆指値の記録が作られていないのでガードの対象にならない）ため、
入口で再発行する仕組みが効かない。予約は残るので撃ち直しはしないが、Critical も台帳の押さえも出ない。

### 追加する受け入れ基準

13. **発注応答の `retType` が `-1` 以外の非成功（`-100` / `-200` / `-400` / `-500` / 未定義の値）のとき、
    「拒否された」とも「建玉は生じていない」とも仮定しない。** 終端の記録（`ExecutionRecord` / `OrderExecuted(Rejected)`）が
    1 件も生じず、予約は `Reserved` のまま据え置かれる（受け入れ基準 8・9 と同じ結果）。
14. **`retType == -1`（確認できた非受理）では従来どおり `Rejected` の終端記録が作られ、在庫解放の引き金になる**（変えない側）。
15. **成行手仕舞いの据え置きが続くあいだ、Critical は 1 時間ごとに再発行される**（1 時間未満では重ねない）。
16. **予約だけが残っている成行手仕舞い（送信中の停止・発行の欠落）は、再起動後の最初の巡回で
    `CloseDispatchIndeterminate`（`CloseIntent` つき）が発行される。** 成行も逆指値も送らない。

### 追加するテスト（`T-10-450` から採番。409 以下と 410〜449 は使用中）

| ID | 固定すること |
| --- | --- |
| **T-10-450** | 🔴 **発注応答の「返事が読めなかった」系の `retType` を「確認できた拒否」に畳まない**（B5）。写像（`-100` / `-200` / `-400` / `-500` / 未定義の値は伝播・`-1` は `Rejected`・列挙値が SDK と一致）／**実アダプタを通した結線**: 手仕舞いで**在庫解放の引き金（`OrderExecuted(Rejected)`）が作られない**・エントリーで**建玉なしと仮定しない**（保護レグの分岐へ進まず例外で終わる）・ガードの成行を巡回ごとに撃ち直さない／**変えない側**: `-1`（実測した 2 つの理由文）は従来どおり `OrderExecuted(Rejected)` が作られ予約が確定する。台帳側で `Rejected` が在庫を解放することは T-10-401 が固定済み |
| **T-10-451** | 🔴 **成行手仕舞いの据え置きを無音にしない**。1 時間未満では Critical を重ねない／1 時間たてば再発行する（`CloseIntent` つき・成行も逆指値も送らない）／**再起動後（予約だけが残り、通知の記憶が無い）の最初の巡回で発行する**／発行に失敗したら次の巡回で再発行する／解決したら止まる |

### 既存テストの刺激を変えたもの（弱めていないことの説明）

- `MoomooBrokerAdapterTests.証券会社が非受理を返したときは従来どおり終端_Rejected` と
  `MoomooBrokerAdapterAlternativeStopTests.代替注文種別の拒否はretTypeとretMsgを戻り値へ載せる`: 刺激の `retType` を
  **`1` から `-1` へ**変えた。`1` は **SDK の列挙に存在しない値**であり、両テストが固定したい「確認できた非受理は `Rejected`・
  理由を持ち帰る」が成り立つのは `-1` だけである（未定義の値は T-10-450 が**逆向き**＝不明へ伝播、で固定する）。
  B3 の「変えない側」が緑で固定されていたのに B5 が成立していたのは、**刺激が実在しない値だった**からである。
- `ProtectiveStopGuardIndeterminateCloseTests.届いたか不明は無音にしない_Criticalの通知を不明になった巡回で1回だけ出し…`:
  コードは変えていない（時計が進まないテストなので 1 時間未満＝重ねない、が成り立つ）。前提を注釈に書き足した。

### 対照実験（実走した実測・2026-09-19）

`MoomooBrokerAdapter.cs` の `catch (MoomooTradeRequestException ex) when (ex.IsConfirmedFailure)` から
**`when` 句だけを外して**是正前のロジックへ戻し（`git diff` でその行が HEAD `ff36cd9c` と一致することを確認。
新テストをコンパイルさせるため `MoomooRetType` の定義は残した）、`dotnet test backend/Services/OrderExecutionService/Tests` を実走した。

```
失敗 …MoomooPlaceOrderRetTypeClassificationTests.実アダプタ経由_手仕舞いの応答が不明系の_retType_なら_在庫解放の引き金を作らない_否定形(retType: -100)
   Expected executed to be <null> because OrderExecuted(Rejected) は取引台帳の在庫解放の引き金である。…, but found AiStockTrading.Shared.Contracts.Events.OrderExecuted
Expected store.GetAll() to be empty because 偽の注文 ID を持つ終端 Rejected の記録を作らない, but found at least one item
Expected reservation?.State to be OrderDispatchState.Reserved {value: 0} because 是正前は Completed（＝終端として確定）になっていた, but found OrderDispatchState.Completed {value: 1}.
Expected reservation?.BrokerOrderId to be <null> because 注文 ID を捏造しない, but found "2f9a947e9bd84b51a0fbac394daacffe".
Expected thrown to be …BrokerDispatchIndeterminateException because 拒否にも見送りにも畳まず、不明のまま伝播する, but found <null>.
失敗 …同上(retType: -200) / (retType: -400) / (retType: -500) / (retType: -999)
失敗 …実アダプタ経由_エントリーの応答が不明系の_retType_なら_建玉なしと仮定しない_否定形(retType: -100)
   Expected executed to be <null> because 『終端失敗（Rejected）だから建玉は生じない＝保護レグは不要』と読んで正常終了すると、…, but found …OrderExecuted
   Expected reservations.Find(approved.DecisionId)?.State to be OrderDispatchState.Reserved {value: 0} …, but found OrderDispatchState.Completed {value: 1}.
失敗 …同上(retType: -500)
失敗 …実アダプタ経由_ガードの成行の応答が_TimeOut_なら_完了を主張せず撃ち直しもしない_否定形
   Expected store.FindByDecisionId(closeDecisionId) to be <null> because 是正前は偽 ID の Rejected が記録され、ガードはそれを『手仕舞い済み』として保護を完了していた, but found …ExecutionRecord
失敗 …返事を読めなかった系の_retType_は_全レグで_Rejected_へ畳まず伝播する_否定形(retType: -100) / (-200) / (-400) / (-500) / (1) / (-999)
   Expected a <…BrokerDispatchIndeterminateException> to be thrown because retType=-100 は『送ったが返事を読めなかった』であり、…, but no exception was thrown.
失敗!   -失敗:    14、合格:   525、スキップ:     0、合計:   539
```

**14 件が赤**（T-10-450 の 26 ケース中）。監査の実測（`OrderExecuted.Status=Rejected orderId=<偽 32hex> reservation=Completed`）が
そのまま再現している。残り 12 件（値と述語の 9・**`-1` は従来どおり `Rejected`** の 2・S3 の理由の持ち帰り 1）は**是正前も緑**
——変えない側の固定だからである。

非ブロッキング 1〜3 も同じ手順で確かめた。

```
（入口の RenotifyHeldCloseIfDue の呼び出しを外す）
失敗 …ProtectiveStopGuardHeldCloseRenotifyTests.据え置きが1時間続いたら_Criticalを再発行する_それ未満では重ねない
失敗 …送信中にプロセスが止まっても_再起動後の最初の巡回で_CloseIntentつきのCriticalを発行する
失敗 …発行の前にプロセスが落ちても_再起動後の最初の巡回で発行し直す
失敗 …発行に失敗したら通知済みと覚えず_次の巡回で発行し直す
   Expected var lost = result.Events.OfType<ProtectiveStopCoverageLost>() to contain a single item, but the collection is empty.（4 件とも）
失敗!   -失敗:     4、合格:   535、スキップ:     0、合計:   539

（常駐の tracker?.Forget を外す）
失敗 …発行に失敗したら通知済みと覚えず_次の巡回で発行し直す
失敗 …発行の失敗で忘れるのは_未発行の据え置き通知だけ
   Expected tracker.IsDue(failedId, Start.AddSeconds(30)) to be True because 発行できなかった通知は次の巡回で発行し直す, but found False.
失敗!   -失敗:     2、合格:   537、スキップ:     0、合計:   539
```

「再起動後のイベント 0 件」（監査の実測）が再現している。是正後は **539 件すべて緑**。

### 残余リスク（本追記で新たに残るもの）

- **`retType == -1` の `retMsg` の中身は検査しない。** OpenD が自身の上流（証券会社のサーバー）のタイムアウトを
  `-1` で返す可能性は、SDK からは否定できない。OpenD はタイムアウト・結果未知に専用の値（`-100` / `-400`）を持つので、
  それを契約として読む（`retMsg` の文言照合は OpenD の版と表示言語で変わるため採らない）。
  稼働環境で `-1` かつタイムアウトを示す `retMsg` を実測したら、その時点で分類を見直す。
- **不明が増える。** これまで偽の `Rejected` で静かに終わっていた返信待ちタイムアウトが、予約の滞留（`Reserved`）と
  Error ログ・`_error` キューとして見えるようになる。自動の突合は既定で無効であり、人が解決する（runbook。有効化は #856）。
  利用者の手仕舞いがこの形で滞留すると、台帳は 30 分の窓の満了まで押さえ続ける（**意図した安全側**。
  確認できた拒否 `-1` は従来どおり即座に解放される）。
- **発注執行の単発の成行手仕舞い（`CloseUnprotectedPositionAsync`）で送信中にプロセスが止まった場合**は塞いでいない
  （巡回が無く、入口で出し直す仕組みが効かない）。予約は残るので撃ち直しはしないが、Critical も台帳の押さえも出ない。
- **再通知の記憶はプロセス内にしか無い。** 再起動のたびに、据え置き中の手仕舞い 1 件につき Critical が 1 回出る
  （再起動を繰り返すと通知も繰り返す）。「通知が出ない」より「重複する」側を選んだ結果である。
- **不明の成行を台帳が押さえるのは 30 分の窓のあいだだけ**であることは変わらない。再発行しても `AppendApproval` は
  冪等で、承認時刻は最初のままである（再通知が窓を延ばすことはない）。窓の満了後は利用者の手仕舞い要求が通るので、
  再通知の文面が「重ねる前に証券会社の画面で確認」を求め続けることが最後の守りになる。
- 逆指値レグの不明を巡回ごとに送り直す件（#853 へ追記）・成行への確認できた拒否（#857）は本追記の射程外。
