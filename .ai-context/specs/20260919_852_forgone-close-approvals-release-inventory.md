---
title: 見送られた決済承認を「処理中の決済」から外し、確実に未発注と判っている理由だけを在庫解放の引き金にする
type: spec
status: accepted
related_ids: [FR-05, FR-10, FR-11, UC-06, ADR-0002, ADR-0003, ADR-0013, ADR-0024, IADR-0018, IADR-0057, IADR-0067, IADR-0113, IADR-0117, IADR-0129, IADR-0210, IADR-0211, IADR-0342, IADR-0346, IADR-0347, IADR-0356]
author: endazon (with Claude Code)
created: 2026-09-19
updated: 2026-09-19
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-05 発注執行 / FR-10「kill switch・日次損失ロックアウト・一時停止はいずれも手仕舞い〔Close〕と損切りは止めない」)
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md (UC-06 利用者による建玉の手仕舞い)
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md (OpenD 常駐・SPOF・INDEX 決定 33「再起動中は発注不可」)
  - planning:projects/ai-stock-trading/07_adr/ADR-0024_opend-unattended-restart-conditional.md
---

# 仕様書: 見送られた決済承認を「処理中の決済」から外す（#852）

## 起点

- #852（bug）。#848（終端になった決済承認を処理中から外す）の 2 巡目監査で非ブロッキングとして指摘され、
  別 issue に切られたもの。
- 症状: 発注執行が**見送り**（`OrderDispatchForgone`）にした決済承認は、取引台帳（`approved_orders`）では
  **処理中のまま**残る。手仕舞いの再要求は `DefaultInFlightWindow = 30 分` が満了するまで
  在庫超過（`ExceedsAvailable` → 422）で拒否され続ける。
- 見送りは OpenD の再起動中（ADR-0002 の SPOF・ADR-0024）に出るため、**手仕舞いが必要なときに
  まとまって発生し得る**。
- #848 が直したのは「**終端**になった承認を処理中から外す」ことであり、見送りは終端ではない
  （そもそもブローカーに注文が存在せず、注文状態を持たない＝IADR-0211）ため射程外だった。

## 原因（実測した母集合）

`OrderDispatchForgone` の購読（本リポジトリの追跡下・テストを除く）:

```
$ grep -rln "OrderDispatchForgone" backend --include=*.cs | grep -v /Tests/
backend/Services/AuditService/Domain/AuditEntryFactory.cs                       （監査）
backend/Services/AuditService/Infrastructure/Steps/AuditEventHandlers.cs        （監査）
backend/Services/NotificationService/Features/Notifications/NotificationFormatter.cs   （通知）
backend/Services/NotificationService/Infrastructure/Steps/NotificationHandlers.cs      （通知）
backend/Services/OrderExecutionService/...（発行側）
backend/Services/RiskManagementService/Infrastructure/Steps/OrderActivityProjectionHandlers.cs
                                                          （注文アクティビティ射影のみ）
backend/Shared/...（契約・メトリクス）
```

リスク管理側の購読は `OrderDispatchForgoneActivityHandler` の 1 本だけで、
`IPortfolioLedgerStore.MarkTerminal` を呼ぶ `*LedgerHandler` が存在しない。
`order_activity`（IADR-0067）は終端になるが、**`approved_orders` は一切動かない**
（IADR-0117 改定 5 のとおり、台帳が数える承認の終端は台帳が持つ）。

## 射程

1. `OrderDispatchForgone` を**取引台帳側でも購読**し、当該 `DecisionId` の承認を処理中から外す。
   **新しいキューは増えない**（リスク管理は同イベントを既に購読しており、Wolverine では
   1 サービス内 1 イベント型 = 1 キューである。ADR-0013 / IADR-0129 決定 10）。
2. 🔴 **fail-safe の向きを守る。** 外してよいのは見送りの理由が「**確実に未発注**」を意味するときだけである。
   **理由を見ずに一律で外さない。** 既定は「解放しない」側に置き、確実に未発注と判っている理由だけを
   **明示的に列挙する**（allowlist。列挙漏れが安全側へ倒れる形）。
3. `TerminalStatus` に何を入れるかを決める（見送りは注文状態を持たない）。

**射程外**: 見送りの再発注（IADR-0211 決定 3「再発注は次の取引判断からのみ」を変えない）。
`order_activity` 側の射影（既に正しく終端になっている）。窓（30 分）の設計（IADR-0117 のまま残す）。

## 決定（詳細と根拠は IADR-0356）

### 決定 A: 台帳の口は `MarkForgone` を新設する（`MarkTerminal` を流用しない）

`IPortfolioLedgerStore.MarkForgone(Guid decisionId, DateTimeOffset forgoneAt)` を足す。
`MarkTerminal(decisionId, OrderStatus, terminalAt)` を流用しない理由は、流用すると**見送りに注文状態を
与えることになる**からである。IADR-0211 は「発注されていないものは注文状態を持たない」を決定の中核に
置いており（`OrderStatus` へ `Unplaced` を足す案を明示的に却下している）、`Cancelled` や `Rejected` を
仮に入れると FR-05 の「拒否」の別集計が接続障害で汚染される——**まさに IADR-0211 が塞いだ穴**である。

`approved_orders` の列は既存のままで足りる（**migration 不要**）:

- `TerminalAt` = 見送り時刻。`GetInFlightCloseQuantity` の判定に使うのは**本列だけ**であり
  （IADR-0117 改定 1）、「未約定残が二度と約定しない」という本列の意味は見送りでも成立する
  （注文自体が存在しないので、未約定残は永久に約定しない）。
- `TerminalStatus` = **`null` のまま**（診断用の列であり、判定には使わない）。
  `TerminalAt is not null && TerminalStatus is null` が「見送り」の表現になる。

意味論は `MarkTerminal` と揃える: 相関する承認が無ければ**何もしない**（後着の承認は処理中として数える＝安全側）。
既に `TerminalAt` が立っていれば**何もしない**（単調・冪等）。

### 決定 B: 理由の allowlist は取引台帳側の純関数が持つ

`RiskManagementService.Domain.OrderDispatchForgoneLifecycle.ConfirmsNoOrderPlaced(reason)` を新設し、
**確実に未発注と実測できた理由だけ**を `true` にする。`switch` の既定（`_`）は **`false`**＝解放しない。

`OrderStatusLifecycle` と同じ作法である——サービス間で共有する契約は列挙そのものであり、
**その解釈は各サービスが自分で持つ**（発注執行側の判定を境界を越えて参照しない）。

列挙の根拠（`OrderExecutionAppService.ExecuteAsync` を読んで実測した。いずれも
`reservations.TryReserve` より**前**・ブローカーへの送信より**前**に `return Forgone(...)` する）:

| 理由 | 実測した位置 | 解放してよいか |
| --- | --- | --- |
| `BrokerUnavailable` | `catch (BrokerUnavailableException)`。接続確立の失敗＝確実に未発注（IADR-0211 決定 1。予約も解放している） | ○ |
| `StopLossPriceMissing` | Open の発注前判定（`intent.StopLossPrice` が無い） | ○ |
| `StopOrderUnsupported` | Open の発注前判定（`broker is not IProtectiveOrderBroker` ／ S3 の能力なし） | ○ |
| `StopLossMethodNotPermitted` | Open の発注前判定（`disposition == Refused`。予約の取得より前） | ○ |
| 将来足される値 | 不明 | ✗（既定） |

🔴 **issue 本文は 3 つを例示しているが、`StopLossMethodNotPermitted`（#819 で後から足された 4 つ目）も
同じく発注前確定であることをコードで確かめて列挙に入れた。**「入れない側」も主張であり、
実測せずに落とすと（#848 の B5 と同型で）根拠のない列挙になるためである。
なお 4 つ目は `PositionEffect.Open` でしか出ないため、`GetInFlightCloseQuantity`（Close だけを数える）の
挙動は列挙に入れても**1 バイトも変わらない**。入れるのは台帳の記録を事実に合わせるためである。

### 決定 C: 届け方は既存のイベント・既存のキュー

`OrderDispatchForgoneLedgerHandler`（新設）が `OrderDispatchForgone` を受け、
`ConfirmsNoOrderPlaced(reason)` が `true` のときだけ `MarkForgone` を呼ぶ。
Wolverine は同一サービス内の同一イベント型のハンドラを 1 本のチェーンにまとめるため、
`OrderDispatchForgoneActivityHandler` と同じチェーンで実行される（**新しいキューは増えない**）。
再試行で両方が再実行されるが、双方の書き込みは冪等である
（`MarkForgone` は単調、`RecordForgone` はストア側で冪等）。

## 是正・追随の母集合（規則 9・10。着手前に自分で引いた）

`IPortfolioLedgerStore` を実装する型を**インタフェース名の文字列**で全走査した:

```
$ grep -rn ": IPortfolioLedgerStore" backend --include=*.cs
backend/Services/RiskManagementService/Infrastructure/Persistence/EfPortfolioLedgerStore.cs:9
backend/Services/RiskManagementService/Infrastructure/Persistence/InMemoryPortfolioLedgerStore.cs:10
backend/Services/RiskManagementService/Tests/Features/RiskManagement/GetOpenPositions/OpenPositionsServiceTests.cs:16       （FakeLedger）
backend/Services/RiskManagementService/Tests/Features/RiskManagement/GetShortSellingStatus/ShortSellingStatusServiceTests.cs:24 （FakeLedger）
backend/Services/RiskManagementService/Tests/Infrastructure/Steps/PortfolioLedgerConsumersTests.cs:357                      （InFlightProbeLedger）
```

**5 件すべてに `MarkForgone` を追随させる**（インタフェースへメソッドを足すと、追随漏れは `CS0535` で
ビルドが落ちる——過去に実績がある）。除外はゼロ。`MaintenanceMarginReductionServiceTests` 等の
`IPortfolioLedgerStore? ledger = null` は**実装ではなく引数**なので母集合外である。

「見送り」で追随すべき記述の走査（規則 10。是正で新たに誤りになる自分の記述を引き直した）:

- `IPortfolioLedgerStore` の `MarkTerminal` の XML コメント／`PersistenceRows.ApprovedOrderRow.TerminalAt` /
  `TerminalStatus` の XML コメント: いずれも「終端＝取消・失効・拒否」とだけ書いており、見送りを足すと
  不足になる → **追記する**。
- `Domain/OrderStatusLifecycle`: 述語は `OrderStatus` の解釈であり見送りを扱わない → **変えない**
  （見送りは別の列挙であり、別の純関数を足す。「足すなら述語を足す」の作法どおり）。
- `docs/tests/FR-10_risk-controls-tests.md`: 在庫解放の表に行を足す（T-10-410）。

## テスト（3 点セット。境界値・肯定形・否定形）

| ID | 内容 |
| --- | --- |
| T-10-410（肯定形・主目的） | 見送られた手仕舞いが 30 分の窓を待たずに在庫へ戻り、再要求が通る（`PositionCloseService` を通した再現） |
| T-10-410（否定形・最重要） | 🔴 allowlist に無い理由（将来足される値を `(OrderDispatchForgoneReason)9999` で模す）では**在庫を解放しない** |
| T-10-410（境界値） | 現行 4 値すべてについて `ConfirmsNoOrderPlaced` の真偽を固定し、**列挙の要素数**も固定する（値が増えたら落ちて分類を見直させる） |
| T-10-410（否定形） | 相関する承認が無い見送りは**書かない**（後着の承認は処理中として数える） |
| T-10-410（戻り幅） | 戻るのは**未約定残だけ**で約定済みぶんを二重に引かない。🔴 **窓内に「戻らない側」の手仕舞いを残して差分を測る**——見送った承認だけでは 0 になり、未約定残が戻ったのか承認数量が戻ったのか区別できない |
| T-10-410（冪等・単調） | 見送りの再送で時刻が動かない／先に終端が立っていれば見送りで上書きしない。🔴 **`TerminalAt` を直接アサートする**——数量はどちらに転んでも 0 で、単調性を測れない。後着は `Cancelled` を使う（`Accepted` / `PartiallyFilled` は `MarkTerminal` の最初の門で return し、`TerminalAt` のガードを通らない） |
| T-10-410（結線） | ハンドラ経由（Wolverine のテストハーネス）で在庫が戻る／allowlist 外では戻らない。両実装（Ef / InMemory）で同一の意味論 |

**既存の T-10-402 / T-10-403 / T-10-406 / T-10-407 / T-10-408 が緑のままであること**を回帰の条件とする。

## 受け入れ基準

1. 見送られた手仕舞いの数量が、30 分の窓を待たずに在庫へ戻る。
2. 🔴 理由が「確実に未発注」でない見送りが将来増えても、自動では在庫を解放しない（既定は解放しない側）。
3. 二重決済でショート化しないことを固定する既存テストが緑のまま。
4. `EfPortfolioLedgerStore` と `InMemoryPortfolioLedgerStore` が同一の意味論を持つ（同名テストを両側に置く）。

## 残余リスク

- 見送りが承認より**先に**台帳へ届いた場合は記録できず、承認は 30 分の窓が満了するまで処理中のままになる
  （`MarkTerminal` と同じ既知の性質。安全側へ倒れる）。実運用では `OrderApproved` はリスク管理自身が発行し、
  見送りは発注執行がそれを消費した後に出るため、この順序は起きにくい。
- 監査・通知の集計は変えていない。見送りは従来どおり `OrderRejected` / `OrderExecuted(Rejected)` と別集計である。

［2026-09-19 追記 / #852・PR #872 のフェーズ末監査 N1］
- 🔴 **本仕様が新たに作る露出**: 見送りで在庫を解放した後に**同じ `OrderApproved` が重複配送**されると、
  予約は削除済み・`FindByDecisionId` も空のため**再予約が通り**、OpenD が復帰していれば本物の決済注文が出る。
  ところが `TerminalAt` を戻す経路が無いため、**生きている決済が処理中に数えられず 2 本目の手仕舞いが
  通り得る**。是正前は台帳が 30 分の窓で押さえ続けていたため露出していなかった。
  前提条件（重複配送＋OpenD 復帰）が要るためブロッキングとせず、**是正の方向は
  [#876](https://github.com/endazon/ai-stock-trading/issues/876) で裁定する**（詳細は IADR-0356 の残余リスク 3）。

［2026-09-19 追記 / #852・PR #872 の差分監査 B1・B2］
- 🔴 **`EfPortfolioLedgerStore.MarkForgone` は `MarkTerminal` と同型の TOCTOU を持つ**
  （`Find` → `TerminalAt is not null` 検査 → 代入 → `SaveChanges`。`ApprovedOrderRow` に並行トークンが無い）。
  実測 200 試行で `TORN=33`＝**決定 A が守ると宣言した「`TerminalAt` は見送りの時刻・`TerminalStatus` は null」
  の破れ**。在庫の押さえは冪等なので壊れず、壊れるのは診断だけ。IADR-0356 の残余リスク 5 に記録した。
- 🔴 **`MarkForgone` にも `DbUpdateConcurrencyException` の catch を先回りで入れた**
  （[#881](https://github.com/endazon/ai-stock-trading/issues/881) が `TerminalAt` を並行トークンにするため）。
  **マージ順に依存しない形**にするためであり、トークンが無い現状では決して投げないので無害である。
  catch が無いまま #881 が先に入ると 200 試行中 43 件で送出し、ハンドラを貫通して error キューへ落ちる
  ＝見送りが記録されず **#852 の実害が間欠的に再発する**。
- **rebase 時の義務**: #881 の `RiskManagementDbContext` のコメント「本列を書く唯一の操作（`MarkTerminal`）」を
  **`MarkTerminal` / `MarkForgone` の 2 つへ是正する**（本 PR がマージされた時点で偽になる記述である）。
- **#873 との衝突**: `OrderDispatchForgoneReason` が 4 → 6 になるため、**後からマージする側**が
  要素数テストを 6 へ更新し、**2 値とも allowlist へ `true` で足す**
  （`BrokerPositionsIndeterminate` の「不明」は***建玉照会*の不明**であり、***発注*の不明**ではない）。
