---
title: 終端になった決済承認を「処理中の決済」から除き、取り消した手仕舞いが建玉をロックし続けないようにする
type: spec
status: accepted
related_ids: [FR-05, FR-10, FR-11, FR-19, UC-06, ADR-0003, ADR-0013, IADR-0018, IADR-0067, IADR-0113, IADR-0117, IADR-0129, IADR-0159, IADR-0346]
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
