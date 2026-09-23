---
title: 突合は確定した 1 件ごとに、その場で記録して発行する（巡回の中断で確定済みの所見と OrderExecuted を失わない）（#890）
type: spec
status: accepted
related_ids: [FR-05, FR-10, FR-20, UC-06, ADR-0002, ADR-0013, IADR-0057, IADR-0074, IADR-0092, IADR-0129, IADR-0149, IADR-0362, IADR-0371]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-23
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-05 発注執行 / FR-10 リスク管理)
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md (UC-06 利用者による建玉の手仕舞い)
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md (OpenD 常駐・SPOF)
---

# 仕様書: 突合は確定した 1 件ごとに、その場で記録して発行する（#890）

## 起点

- **#890**（`tech-debt`）。PR #882（#856 の実装）の差分監査が引き当てた非ブロッキングの切り出し。
- 起点 ID: **FR-05**（発注執行）。関連 **FR-10**（無保護の建玉の可視化）・**FR-20**（発行する `OrderExecuted` の発注先）。
- 関連 IADR: [IADR-0362](../adr/IADR-0362_reservation-reconciliation-enabled-with-release-gate.md)（突合の有効化と解放の門）、
  [IADR-0074](../adr/IADR-0074_reservation-reconciliation.md)（突合本体）、
  [IADR-0057](../adr/IADR-0057_order-dispatch-idempotency.md)（発注 3 相・予約）、
  [IADR-0092](../adr/IADR-0092_reservation-broker-probe-moomoo.md)（実照会プローブ）。
- 本作業で起草する実装 ADR: **IADR-0371**。

## 診断（コードの実測）

### 幾何: 「commit 済み → 再走査されない」

`OrderReservationReconciler.ReconcileAsync`
（`backend/Services/OrderExecutionService/Features/OrderExecution/ReconcileOrderReservations/OrderReservationReconciler.cs`）は
滞留 `Reserved` を 1 件ずつ処理し、**確定した時点で `MarkCompleted` を commit する**（同 72 / 94 / 103 行）。
確定した予約は次の巡回の `FindStalledReserved`（`State == Reserved` のみを返す。
`InMemoryOrderReservationStore.cs` 53 行 / EF 実装も同じ述語）に**載らない**。

したがって **その予約について「この先に出るはずだったもの」は、出なければ永久に失われる。**
「次の巡回で拾い直す」は成立しない —— 拾い直す対象がもう無い。

### 是正前の出口はすべて巡回の末尾にあった

`OrderReservationReconciliationService.ReconcileOnceAsync`（是正前 86〜102 行）:

```
var result = await reconciler.ReconcileAsync(cutoff, batchSize, cancellationToken);   // ループ全体
ReportFindings(result);                                                                // 巡回の末尾
foreach (var executed in result.Executed)                                              // 巡回の末尾
    await new MessageBus(runtime).PublishAsync(executed);
```

`ReconcileAsync` のループ先頭には `cancellationToken.ThrowIfCancellationRequested()` がある（同 59 行）。
**2 件目の処理に入る前に停止要求が来ると、1 件目は `Completed` を commit 済みなのに、
その所見（Critical）も `OrderExecuted` も 1 つも出ないまま `ReconcileOnceAsync` ごと抜ける。**

issue のプローブ実測（#882 監査 PROBE5）と一致する:

```
PROBE5: exception = OperationCanceledException
PROBE5: log entries = 0
PROBE5: first  reservation state = Completed
PROBE5: second reservation state = Reserved
PROBE5: next cycle would rescan 1 reservation(s): SECOND
```

失われる Critical は「**突合で発注済みと確定した注文に保護逆指値が張られていない**」であり、
#853 の裁定が下りるまでの唯一の可視化手段である（通知の配線は無く、ログだけ。IADR-0362 決定 3）。

### 到達性

発行（`PublishAsync`）の失敗より高い。**通常のローリングデプロイや Pod 再起動が巡回に重なるだけ**で起きる。
配備値（IADR-0362 決定 2）は 1 巡回 50 件、1 予約あたり最大 4 往復、OpenD の返信待ち既定 15 秒なので、
1 巡回は最悪で分の単位に及ぶ。`BackgroundService` の `stoppingToken` はホスト停止で必ず落ちる。

### durable outbox は無い（本作業の射程外）

`git grep -n "Durability\|UseDurableOutbox\|PersistMessagesWith" -- backend` ＝ **0 件**（2026-09-23 実測）。
🔴 **`git grep` で数えること。** ビルド済みツリーで素の `grep -rn` を使うと `bin/**/Wolverine*.dll` の
バイナリ一致で数十件返り、結論が逆に読める（#882 の 3 巡目監査 N-4 の実測）。

## 対象範囲

- 対象（製品コード 3 ファイル）:
  - `backend/Services/OrderExecutionService/Features/OrderExecution/ReconcileOrderReservations/OrderReservationReconciler.cs`
  - `backend/Services/OrderExecutionService/Features/OrderExecution/ReconcileOrderReservations/IReservationReconciliationSink.cs`（新設）
  - `backend/Services/OrderExecutionService/Hosted/OrderReservationReconciliationService.cs`
- 対象（テスト 2 ファイル）:
  - `backend/Services/OrderExecutionService/Tests/Features/OrderExecution/ReconcileOrderReservations/OrderReservationReconcilerTests.cs`
  - `backend/Services/OrderExecutionService/Tests/Hosted/OrderReservationReconciliationServiceTests.cs`
- 対象（文書）: 本仕様書・`.ai-context/adr/IADR-0371_*.md`（新設）・`.ai-context/adr/README.md` の索引行・
  `IADR-0362` への日付つき追記・`docs/tests/FR-10_risk-controls-tests.md` の節追加。
- **対象外**:
  - 🔴 `ReconciliationOptions.ReleaseOnNotPlaced` の既定・配備値（**`false` のまま**。#856 / IADR-0362 決定 1）。
  - 🔴 Wolverine の durable outbox 配線（issue の方向 (b)）。送信ストアの DB スキーマ・運用が増え、
    サービス横断の判断になる。**本作業では採らず、残余リスクとして IADR-0371 に残す。**
  - 🔴 `MarkCompleted` を発行成功まで遅らせること（issue の方向 (c)）。IADR-0057 決定 1 の射程に触る。
  - `catch (Exception ex) when (ex is not OperationCanceledException)` が呼び出し元と無関係な
    `OperationCanceledException` で巡回ごと恒久停止する脆さ（IADR-0362 残余リスク。今日は到達不能・別 issue）。
  - 保護逆指値を張るか否か（#853）。Critical の通知配線（#853 と併せて行う）。

## 母集合（走査と除外理由）

「巡回の末尾で出口を回している箇所」と「確定済みの所見・発行を扱う記述」を**誤りの側の文字列**で走査した
（`.claude/rules/traceability.repo.md` 規則 9）。

| 走査 | 件数 | 扱い |
| --- | --- | --- |
| `git grep -n "ReconcileAsync(" -- backend` | 23（定義 1・呼び出し 22＝製品 1・テスト 21） | 製品 1（常駐）を是正。テスト 21 は `(cutoff, batchSize)` の 2 引数呼びなので、`sink` を**省略可能引数**にすればそのまま通る |
| `git grep -n "result.Executed\|ProbeTerminalized" -- backend` | 製品 2 箇所（常駐の `foreach` 2 本）＋テスト | 常駐の 2 本を撤去（1 件ごとの出口へ移す）。結果型の 2 つの明細は**残す**（巡回サマリと既存テストの表明が使う） |
| `git grep -n "記録は発行より先" -- backend .ai-context docs` | 3（常駐コメント・IADR-0362・#856 仕様書） | 順序の主張は**変わらない**（1 件の中でも記録 → 発行）。凍結記録は書き換えず、IADR-0362 へ日付つき追記のみ |
| `git grep -n "T-10-6[45][0-9]"` | 0 | 予約ブロック T-10-646..657 は未使用。本作業で 646〜652 を使う |
| `git grep -n "Durability\|UseDurableOutbox\|PersistMessagesWith" -- backend` | 0 | durable outbox は未配線（射程外の確認） |

導出値（`ReleaseOnNotPlaced` の既定・配備値）は走査ではなく**読み直して**確認した:
`ReconciliationOptions.cs` に初期化子なし（＝`false`）、
`deploy/helm/ai-stock-trading/values.yaml` の `Reconciliation__ReleaseOnNotPlaced` は `"false"`。**本作業で触らない。**

## 方向の選択

issue が並べた 3 方向のうち **(a)「所見を 1 件ずつ、その予約の commit 直後に出す」を採る。**
ただし issue の (a) は「所見（ログ）」だけを指していた。**本作業は発行（`OrderExecuted`）も同じ位置へ移す** ——
失われる幾何は所見と発行で同一（どちらも「commit 済みの予約についてこの先に出るはずだったもの」）であり、
片方だけ移すと同じ穴が残る。

- (b) durable outbox: 送信ストアの運用が増え、サービス横断。**採らない**（残余リスクへ）。
- (c) `MarkCompleted` の遅延: IADR-0057 決定 1 の射程。**採らない**。

issue が (a) の代価に挙げた「巡回サマリと二重に出る」は、**巡回末尾の `foreach` を撤去する**ことで回避する
（サマリは件数の 1 行だけを残す）。

## 設計

### 1 件ごとの出口（`IReservationReconciliationSink`）

Application 層（`Features/`）はメッセージ基盤に非依存という既存レイヤリングを維持するため、
出口をポートで切る。実装は Worker 層（`Hosted/`）が持つ。

```csharp
public interface IReservationReconciliationSink
{
    // 🔴 CancellationToken を取らない。確定（commit）済みの 1 件の出口は中断させない。
    Task EmitAsync(ReservationTerminalizationEmission emission);
}

public sealed record ReservationTerminalizationEmission(
    OrderExecuted Executed,
    ReservationReconciliationFinding? ProbeFinding);   // null = phase-4 自己修復（突合ではない）
```

`ReconcileAsync` の署名へ省略可能引数として足す（既存テストの呼び出しはそのまま通る）:

```csharp
ReconcileAsync(DateTimeOffset stallCutoff, int batchSize,
               IReservationReconciliationSink? sink = null, CancellationToken cancellationToken = default)
```

### ループの形

```
foreach (reservation in stalled)
{
    cancellationToken.ThrowIfCancellationRequested();     // ← ここまでは是正前と同じ

    ReservationTerminalizationEmission? emission = null;
    try   { …確定処理…; emission = …; }                   // 1 件の失敗はバッチを止めない（既存どおり failed++）
    catch (Exception ex) when (ex is not OperationCanceledException) { failed++; }

    if (emission is not null)
        await sink.EmitAsync(emission);                   // 🔴 try の**外**。commit 済みの出口である
}
```

🔴 **`EmitAsync` を per-item の `try` の外に置くのは意図的である。** 中に入れると発行の失敗が `failed++` に
吸い込まれ、「発行の失敗は握り潰さない」（T-10-609）が壊れる。しかも予約は既に `Completed` なので
`failed` に数えるのは事実として誤りである（据え置かれていない）。

### 出口の中身（Worker 層）

`OrderReservationReconciliationService` が `IReservationReconciliationSink` を実装し、`this` を渡す。

1. `ProbeFinding` があれば **Critical** をログする（保護レグ不在。IADR-0362 決定 3 の文面を変えない）。
2. そのあと `new MessageBus(runtime).PublishAsync(executed)` で発行する。

🔴 **1 件の中でも「記録が先・発行が後」は変わらない**（IADR-0362 決定 3 / #882 監査 N1）。
発行の失敗は**握り潰さず伝播させる**（巡回は中断し、常駐が拾って次巡回で再試行する）。
残りの滞留は `Reserved` のままなので次巡回で拾い直せる。

### 巡回の末尾に残すもの

- 件数サマリ 1 行（`Scanned` / `Terminalized` / `Released` / `Held` / `Indeterminate` / `Failed`）。
- `Failed > 0` の警告。
- `HeldNotPlaced` の 1 件ずつの警告。**据え置いた予約は `Reserved` のままで次巡回に載る**ため、
  巡回が中断してもこれらは失われない（＝末尾のままでよい）。

### 安全側は 1 バイトも動かさない

- `ReleaseOnNotPlaced` の既定・配備値・分岐は**未変更**。`NotPlaced` の解放条件は同じ式のままである。
- `Indeterminate` は据え置きのまま（門に依らない）。
- 出口へ渡すのは **`MarkCompleted` を commit した予約だけ**である。`HeldNotPlaced` / `Indeterminate` /
  `failed` はいずれも出口へ渡さない。**「確実に未発注」と「送ったが不明」の区別は本作業で一切触れていない。**
- 発行の再送は増やさない（1 予約につき出口は高々 1 回）。二重発注の経路は作られない。

## テスト（T-10-646 〜 T-10-652）

予約ブロック T-10-646..657 のうち **646〜652 を使う**（653〜657 は未使用のまま残す）。
時計は注入（`FakeClock`）、実時間の `sleep` は使わない。中断は `CancellationTokenSource` を
**プローブのコールバックから `Cancel()` する**ことで決定的に起こす。

| ID | 位置 | 形 | 固定すること |
| --- | --- | --- | --- |
| T-10-646 | Reconciler | **否定形**（本 issue の幾何） | 2 件目の手前で中断しても、1 件目の所見と `OrderExecuted` は出口へ渡っている |
| T-10-647 | Reconciler | **否定形** | 中断した巡回のあと次の巡回を回しても、1 件目は**二度と**出口へ渡らない（2 件目だけ） |
| T-10-648 | Reconciler | **否定形・安全側** | 出口へ渡るのは確定した予約だけ。門が閉じた `NotPlaced` と `Indeterminate` は 1 件も渡らず、予約は `Reserved` のまま |
| T-10-649 | Reconciler | 境界 | phase-4 自己修復も出口へ渡すが `ProbeFinding` は `null`（突合ではない。IADR-0362 決定 3） |
| T-10-650 | 常駐 | **否定形**（PROBE5 の形） | 巡回が中断されても、確定済み 1 件の Critical が**ちょうど 1 行**出る |
| T-10-651 | 常駐 | **否定形** | 巡回が中断されても、確定済み 1 件の `OrderExecuted` は発行済みである（2 件目は出ない） |
| T-10-652 | 常駐 | **否定形** | 中断のあと次の巡回を回しても、同じ `DecisionId` の `OrderExecuted` は 1 通のままである |
| T-10-653 | 完走する巡回では確定ごとの Critical がちょうど 1 行ずつ（末尾の明細ループを戻すと赤。PR #914 監査 M3） | `OrderReservationReconciliationServiceTests` |

既存の緑を保つこと（受け入れ基準）:
T-10-402 / T-10-403 / T-10-406 / T-10-407 / T-10-408 / T-10-409（二重発注・二重決済の否定形）、
T-10-600 〜 T-10-609（解放の門と記録順序。とくに **T-10-609**「発行が落ちても Critical は出る」）。

## 受け入れ基準

1. 2 件の滞留のうち 1 件目の確定後に中断させると、**1 件目の Critical と `OrderExecuted` が出ている**（T-10-646 / 650 / 651）。
2. 中断した巡回の後続の巡回で、1 件目が**二重に出ない**（T-10-647 / 652）。
3. 確定していない予約（門が閉じた `NotPlaced` / `Indeterminate` / 例外）は出口へ渡らず `Reserved` のまま（T-10-648）。
4. 🔴 `Reconciliation__ReleaseOnNotPlaced` の既定・配備値が `false` のまま（差分に現れない）。
5. `dotnet build` / `dotnet test`（OrderExecutionService）が緑。`dotnet format --verify-no-changes` が無差分。
6. `node scripts/check-commit-messages.js` / `check-trace-blocks.js` / `check-adr-index-sync.js` /
   `check-adr-index-addendum-loss.js` / `check-doc-links.js` / `gen-knowledge-graph.js --check` /
   `check-plan-id-qualification.js` / `scripts.test.js` が緑。

## 残余リスク

- 🔴 **プロセスが落ちる瞬間の 1 件は依然として失われ得る。** 確定（commit）と出口のあいだには常に窓がある。
  窓は「巡回全体（最悪で分の単位）」から「1 件ぶんの記録＋発行」へ縮んだが、ゼロにはならない。
  ゼロにするには durable outbox（方向 (b)）が要る —— 本作業では採らない（IADR-0371 の残余リスク）。
- 🔴 **発行が落ちるとその巡回はそこで止まる**（是正前と同じ）。止まった時点で確定済みだった予約の
  Critical は出ている（T-10-609 / T-10-650）が、**落ちた 1 件の `OrderExecuted` は失われる**。
- 出口が 1 件ごとになるため、**発行の失敗で止まる位置が前倒しになる**。是正前は「全件確定 → 発行で失敗」、
  是正後は「1 件目の発行で失敗 → 2 件目以降は確定もされない」。残りは `Reserved` のままで次巡回が拾うため、
  **安全側は同じ**（据え置きは二重発注を生まない）。滞留の解消は遅れ得る。
- 通知の配線は依然として無い（Critical はログ止まり。IADR-0362 決定 3 / #853）。
