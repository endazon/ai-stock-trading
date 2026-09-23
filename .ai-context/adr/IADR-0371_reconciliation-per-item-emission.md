---
title: IADR-0371 突合の記録と発行は「確定した 1 件」ごとに、その場で行う（巡回の末尾に出口を置かない）
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-10, FR-20, UC-06, ADR-0002, ADR-0013, IADR-0057, IADR-0074, IADR-0092, IADR-0129, IADR-0149, IADR-0362]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-23
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-05 発注執行 / FR-10 リスク管理)
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md (UC-06 利用者による建玉の手仕舞い)
---

# IADR-0371: 突合の記録と発行は「確定した 1 件」ごとに、その場で行う（巡回の末尾に出口を置かない）

- 状態: Accepted
- 日付: 2026-09-23
- 決定者: claude (Claude Code) / [#890](https://github.com/endazon/ai-stock-trading/issues/890)

## 起点・関連

- 関連する計画書 ID: FR-05（発注執行）・FR-10（無保護の建玉の可視化）・FR-20（発行する `OrderExecuted` の発注先）・UC-06
- 起票: [#890](https://github.com/endazon/ai-stock-trading/issues/890)
  （[#882](https://github.com/endazon/ai-stock-trading/pull/882)＝[#856](https://github.com/endazon/ai-stock-trading/issues/856) の実装の差分監査 N-1' が引き当てた非ブロッキングの切り出し）
- 関連する実装 ADR: [IADR-0362](IADR-0362_reservation-reconciliation-enabled-with-release-gate.md)（突合の有効化と解放の門。
  決定 3「記録は発行より先」を**覆さず、その適用単位を巡回から 1 件へ狭める**）、
  [IADR-0074](IADR-0074_reservation-reconciliation.md)（突合本体）、
  [IADR-0057](IADR-0057_order-dispatch-idempotency.md)（発注 3 相・予約。**本 ADR は触らない**）、
  [IADR-0092](IADR-0092_reservation-broker-probe-moomoo.md)（実照会プローブ）
- 関連する実装仕様書: `.ai-context/specs/20260923_890_reconciliation-per-item-emission.md`

## コンテキストと課題

### 「commit 済み → 再走査されない」幾何

`OrderReservationReconciler.ReconcileAsync` は滞留 `Reserved` を 1 件ずつ処理し、**確定した時点で
`MarkCompleted` を commit する**。確定した予約は次の巡回の `FindStalledReserved`（`State = Reserved`
のみを返す）に**載らない**。

したがって **その予約について「この先に出るはずだったもの」は、出なければ永久に失われる。**
「次の巡回で拾い直す」は成立しない —— 拾い直す対象がもう無い。

是正前、出口はすべて巡回の**末尾**にあった（`OrderReservationReconciliationService.ReconcileOnceAsync`）:

```
var result = await reconciler.ReconcileAsync(cutoff, batchSize, cancellationToken);  // ループ全体
ReportFindings(result);                                                               // 末尾
foreach (var executed in result.Executed) await …PublishAsync(executed);              // 末尾
```

`ReconcileAsync` のループ先頭には `cancellationToken.ThrowIfCancellationRequested()` があり、
**`ReportFindings` より手前**で投げる。**2 件目の処理に入る前に停止要求が来ると、1 件目は
`Completed` を commit 済みなのに、その所見（Critical）も `OrderExecuted` も 1 つも出ない。**

#882 の監査プローブ実測（PROBE5）:

```
PROBE5: exception = OperationCanceledException
PROBE5: log entries = 0
PROBE5: first  reservation state = Completed
PROBE5: second reservation state = Reserved
PROBE5: next cycle would rescan 1 reservation(s): SECOND
```

### 何が失われるか

1. **保護レグ不在の Critical** —— 「突合で発注済みと確定した注文に保護逆指値が張られていない」。
   #853 の裁定が下りるまでの**唯一の可視化手段**である（通知の配線は無く、ログだけ。IADR-0362 決定 3 / 同 N2）。
2. **`OrderExecuted` そのもの** —— 出なければ**監査・リスク管理・通知は突合が確定させた約定を二度と
   受け取らない**（台帳に約定が載らない）。durable outbox は配線されていない
   （`git grep -n "Durability\|UseDurableOutbox\|PersistMessagesWith" -- backend` ＝ 0 件。2026-09-23 実測。
   🔴 **`git grep` で数えること**。ビルド済みツリーで素の `grep -rn` を使うと `bin/**/Wolverine*.dll` の
   バイナリ一致で数十件返り、結論が逆に読める）。

### 到達性は発行の失敗より高い

**通常のローリングデプロイや Pod 再起動が巡回に重なるだけ**で起きる。配備値（IADR-0362 決定 2）は
1 巡回 50 件・1 予約あたり最大 4 往復・OpenD の返信待ち既定 15 秒なので、1 巡回は最悪で分の単位に及ぶ。
`BackgroundService` の `stoppingToken` はホスト停止で必ず落ちる。

## 検討した選択肢

#890 が並べた 3 方向である。

1. **(a) 所見を 1 件ずつ、その予約の commit 直後に出す** — 中断に効く。代価は「巡回サマリと二重に出る」。
2. **(b) Wolverine の durable outbox を配線する** — 発行の喪失に効くが、送信ストアの DB スキーマ・運用が増え、
   サービス横断の判断になる。
3. **(c) `MarkCompleted` を発行成功まで遅らせる** — 両方に効くが、**突合と確定のあいだの窓を広げる**。
   IADR-0057 の相（予約 → 発注 → 確定）を反転はさせないが、その射程（IADR-0057 決定 1）に触るため単独で決められない。

## 決定

### 決定 1: (a) を採る。ただし所見だけでなく**発行も**同じ位置へ移す

出口（記録＋発行）は、その予約の `MarkCompleted` を commit した**直後**に、1 件ずつ実行する。

🔴 **#890 の (a) は「所見（ログ）」だけを指していたが、失われる幾何は所見と発行で同一である**
（どちらも「commit 済みの予約についてこの先に出るはずだったもの」）。片方だけ移すと同じ穴が残る。

- (b) は**採らない**（残余リスクへ。サービス横断の判断であり、本 issue の射程を超える）。
- (c) は**採らない**（IADR-0057 決定 1 の射程）。

### 決定 2: 出口はポート `IReservationReconciliationSink` で切る

Application 層（`Features/`）がメッセージ基盤に非依存という既存レイヤリングを維持する。
実装は Worker 層（`Hosted/OrderReservationReconciliationService` が自身で実装し、`this` を渡す）。

```csharp
public interface IReservationReconciliationSink
{
    Task EmitAsync(ReservationTerminalizationEmission emission);   // 🔴 CancellationToken を取らない
}

public sealed record ReservationTerminalizationEmission(
    OrderExecuted Executed,
    ReservationReconciliationFinding? ProbeFinding);               // null = phase-4 自己修復
```

🔴 **`CancellationToken` を取らないのは意図的である。commit 済みの 1 件の出口は中断させない。**
中断してよいのは「まだ確定していない予約の処理」だけであり、それは呼び出し側のループ先頭が判定する。

`ReconcileAsync` には**省略可能引数**として足す（既存の 21 箇所のテスト呼び出しは 2 引数のままで通る）。

### 決定 3: 出口は per-item の `try/catch` の**外**に置く

```
try   { …確定処理…; emission = …; }
catch (Exception ex) when (ex is not OperationCanceledException) { failed++; }

if (emission is not null && sink is not null)
    await sink.EmitAsync(emission);        // 🔴 try の外
```

中に入れると発行の失敗が `failed++` に吸い込まれ、**「発行の失敗は握り潰さない」（T-10-609）が壊れる**。
しかも予約は既に `Completed` であり、`failed`（＝据え置き・次回巡回で再試行）に数えるのは事実として誤りである。

発行が落ちたときの挙動は是正前と同じ —— 巡回はそこで止まり、常駐（`ExecuteAsync`）が拾って次回巡回で再試行する。
残りの滞留は `Reserved` のままなので拾い直せる。

### 決定 4: 1 件の中でも「記録が先・発行が後」は変えない

IADR-0362 決定 3 / #882 監査 N1 の順序はそのままである。**本 ADR が変えるのは適用単位（巡回 → 1 件）だけ**であり、
順序そのものは覆していない。

### 決定 5: 巡回の末尾に残すのは「巡回が回りきって初めて言えること」だけ

件数サマリ 1 行・`Failed` の警告・`HeldNotPlaced` の 1 件ずつの警告を残す。
**`ProbeTerminalized` と `Executed` の `foreach` は撤去する**（残すと `EmitAsync` と二重に出る＝#890 が (a) の代価に
挙げた点）。

🔴 **`HeldNotPlaced` を末尾のままにしてよい理由**: 据え置いた予約は **`Reserved` のまま**であり次回巡回の
`FindStalledReserved` に載る。**永久に失われるのは「確定済みで再走査されない」ものだけ**である。

## 理由

- 幾何そのものに対応している。失われるのは「確定済みで再走査されない予約についての出力」なので、
  **出力を確定と同じ位置へ寄せる**のが最小で直接的な是正である。
- 安全側を 1 バイトも動かさない（次節）。
- (b)・(c) と違い、DB スキーマにも IADR-0057 の相順にも触れない。

## 安全側の不変（🔴 本 ADR が壊していないこと）

- `Reconciliation:ReleaseOnNotPlaced` の**既定は `false` のまま**（`ReconciliationOptions` に初期化子なし）。
  配備値（`deploy/helm/ai-stock-trading/values.yaml`）も `"false"` のまま。**差分に現れない。**
- `NotPlaced` の解放条件は同じ式のままであり、**「確実に未発注」と「送ったが不明」を近づける変更はしていない**
  （IADR-0362 決定 1 / IADR-0211 決定 1）。`Indeterminate` は門の開閉に依らず据え置く。
- 出口へ渡すのは **`MarkCompleted` を commit した予約だけ**である。`HeldNotPlaced` / `Indeterminate` /
  例外で失敗した予約は 1 件も渡らない（T-10-648 が Theory で両方の門の状態を固定する）。
- 1 予約につき出口は高々 1 回。**二重発注・二重発行の経路は作られない**（T-10-647 / T-10-652）。

## 影響・追随

- `OrderReservationReconciler.ReconcileAsync` の署名に `IReservationReconciliationSink? sink = null` が増える
  （第 3 引数。`CancellationToken` は第 4 引数へ）。**位置引数で `cancellationToken` を渡していた呼び出しは
  常駐 1 箇所だけ**であり、そこは本 PR で書き換えた。
- 新設: `IReservationReconciliationSink` / `ReservationTerminalizationEmission`
  （`Features/OrderExecution/ReconcileOrderReservations/IReservationReconciliationSink.cs`）。
- `OrderReservationReconciliationService` が `IReservationReconciliationSink` を実装する。
  `ReportFindings` は `ReportRoundSummary`（巡回の要約のみ）へ改名し、明細の 2 つの `foreach` のうち
  `ProbeTerminalized` 側は `EmitAsync` から呼ぶ `ReportProbeTerminalized` へ移した。
- 🔴 **T-10-609 の 2 つ目の表明を反転させた**（既存テストの意図的な変更）。発行が落ちる巡回では
  **巡回サマリが出なくなる**（是正前は発行より先に出ていた）。サマリは「巡回が回りきって初めて言えること」であり、
  回りきっていない巡回の件数は部分値にすぎない。**永久に失われる側（確定済み 1 件の Critical）は出ている**という
  T-10-609 の中心的主張は変わらない。テストは `NotContain` で新しい事実を固定し、末尾の `foreach` が
  黙って戻るのも同時に塞ぐ。
- DI 登録の変更は無い（`Program.cs` 不変。常駐が自身を sink として渡すため）。
- 配備（Helm values）の変更は無い。

## テスト（T-10-646 〜 T-10-652）

| ID | 位置 | 固定すること |
| --- | --- | --- |
| T-10-646 | Reconciler（否定形） | 2 件目の手前で中断しても、1 件目の所見と `OrderExecuted` は出口へ渡っている |
| T-10-647 | Reconciler（否定形） | 中断の後に次の巡回を回しても、1 件目は二度と出口へ渡らない |
| T-10-648 | Reconciler（否定形・Theory） | 確定していない予約（門が閉じた `NotPlaced` / `Indeterminate` / 例外）は 1 件も出口へ渡らない |
| T-10-649 | Reconciler | phase-4 自己修復も出口へ渡すが `ProbeFinding` は `null`（突合ではない） |
| T-10-650 | 常駐（否定形） | 巡回が中断されても、確定済み 1 件の Critical がちょうど 1 行出る |
| T-10-651 | 常駐（否定形） | 巡回が中断されても、確定済み 1 件の `OrderExecuted` は発行済みである |
| T-10-652 | 常駐（否定形） | 中断の後に次の巡回を回しても、同じ `DecisionId` の `OrderExecuted` は 1 通のままである |

**是正前に落ちること（変異注入の実測。2026-09-23）**: `EmitAsync` を巡回の末尾へ戻す（＝是正前の幾何）と
**5 件が赤**になる —— T-10-646 / T-10-647 / T-10-650 / T-10-651 / T-10-652。
T-10-648 / T-10-649 は緑のまま（中断を含まない安全側・境界のケースであり、この変異では動かない）。

```
失敗!   -失敗:     5、合格:   697、スキップ:     0、合計:   702
是正後: 成功!   -失敗:     0、合格:   702、スキップ:     0、合計:   702
```

時計は注入（`FakeClock`）、実時間の `sleep` は使わない。中断はプローブのコールバックから
`CancellationTokenSource.Cancel()` を呼んで決定的に起こす。

## 残余リスク

- 🔴 **プロセスが落ちる瞬間の 1 件は依然として失われ得る。** 確定（commit）と出口のあいだには常に窓がある。
  窓は「巡回全体（最悪で分の単位）」から「1 件ぶんの記録＋発行」へ縮んだが、**ゼロにはならない。**
  ゼロにするには durable outbox（方向 (b)）が要る —— 本 ADR では採らない。
- 🔴 **発行が落ちた 1 件の `OrderExecuted` は失われる**（是正前と同じ）。予約は既に `Completed` であり
  次回巡回では拾えない。落ちた事実は常駐の `LogError` に残る。
- **発行の失敗で止まる位置が前倒しになる。** 是正前は「全件確定 → 発行で失敗」、是正後は
  「1 件目の発行で失敗 → 2 件目以降は確定もされない」。残りは `Reserved` のままで次巡回が拾うため
  **安全側は同じ**（据え置きは二重発注を生まない）が、滞留の解消は遅れ得る。
- **発行が落ちる巡回では巡回サマリが出ない**（上の「影響・追随」）。
- 通知の配線は依然として無い（Critical はログ止まり。IADR-0362 決定 3 / #853）。
- IADR-0362 の残余リスクのうち、**プローブが呼び出し元と無関係な `OperationCanceledException` を投げると
  巡回ループごと恒久停止する**脆さは**本 ADR でも直していない**（今日は到達不能。プローブを差し替える PR が先に直す）。
