---
title: IADR-0362 滞留 Reserved の自動突合は配備で有効化し、解放（NotPlaced）だけを別の門で閉じたままにする
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-10, FR-11, UC-06, ADR-0002, ADR-0003, ADR-0016, IADR-0057, IADR-0058, IADR-0074, IADR-0092, IADR-0111, IADR-0113, IADR-0114, IADR-0117, IADR-0210, IADR-0211]
author: endazon (with Claude Code)
created: 2026-09-19
updated: 2026-09-19
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-05 発注執行)
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md (UC-06 利用者による建玉の手仕舞い)
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md (OpenD 常駐・SPOF)
---

# IADR-0362: 滞留 Reserved の自動突合は配備で有効化し、解放（NotPlaced）だけを別の門で閉じたままにする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-19
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-05（発注執行。「拒否」＝証券会社が受理しなかった状態）、FR-10 / FR-11、UC-06、ADR-0002
- 対象 Issue: [#856](https://github.com/endazon/ai-stock-trading/issues/856)
  （[#851](https://github.com/endazon/ai-stock-trading/issues/851) 3 巡目監査の非ブロッキング切り出し）
- 関連する実装仕様書: [20260919_856_reservation-reconciler-enablement](../specs/20260919_856_reservation-reconciler-enablement.md)
- 関連 IADR: [IADR-0057](IADR-0057_order-dispatch-idempotency.md)（発注 3 相・予約）、
  [IADR-0074](IADR-0074_reservation-reconciliation.md)（自動リコンサイル本体・fail-safe 既定 no-op）、
  [IADR-0092](IADR-0092_reservation-broker-probe-moomoo.md)（実照会プローブ・remark 突合）、
  [IADR-0117](IADR-0117_owner-position-close-path.md)（改定 6・7＝「届いたか不明」の据え置き）、
  [IADR-0211](IADR-0211_opend-unavailable-forgo-without-queueing.md)（「確実に未発注」だけが解放してよい）、
  [IADR-0114](IADR-0114_route-b-parity-observed-drawdown-and-official-sources.md)（決定 5-3＝本 ADR が覆す「入れない判断」）

## 背景・課題

#851（#848 の是正）は、送信後に結果を確認できなかった発注を `BrokerDispatchIndeterminateException` で伝播させ、
予約を `Reserved` のまま据え置く形にした。その文面は一貫して
**「滞留した `Reserved` は client order id による突合（IADR-0074 / IADR-0092）が解決する」**を前提にしていた。

**その突合は配備で動いていなかった。** 実物で確かめた原因は 3 点の複合である（推測ではない）。

1. `ReconciliationOptions.Enabled` の既定が `false`（初期化子なし）。無効時は常駐が 1 行ログして `return` する。
2. `ReconciliationOptions.UseBrokerProbe` の既定も `false`。実照会プローブ（`MoomooReservationBrokerProbe`）は
   `Broker:Provider=moomoo` との AND でしか配線されず、それ以外は常に `Indeterminate` を返す no-op プローブになる。
3. `deploy/` に上書きが無い（`grep -rni reconcil deploy/` ＝ 0 件。2026-09-19 実測）。
   `values.yaml` の `services.order-execution` は `image` / `db` の 2 キーだけで `extraEnv` を持たず、
   経路B の `values-local.yaml` は `order-execution` をそもそも含まない。

設定キーの綴り違いでも、条件分岐による実質無効でもない。**「既定が false」＋「配備で設定していない」だけである。**

この突合は、**「送ったが結果が不明」な注文を後から確定させる唯一の自動経路**である。動いていないと在庫の押さえが
残ったまま人が証券会社の画面で直すしかない（2026-09-18 に実際にそうなった。#847 / #848 / #849 の連鎖）。

一方、#851 が有効化しなかった理由も正しい。**`NotPlaced`（未発注）判定による解放は「再発注の許可」であり、
誤判定は二重発注に直結する。** `NotPlaced` の根拠は「発注時に伝播した remark（＝DecisionId）で全市場の
現在＋履歴を成功裏に列挙して一致ゼロ」であって、証券会社が「無い」と答えた事実ではない。
**moomoo SIMULATE が remark を往復させるかは実機未検証**であり、往復していなければ**発注済みの注文も
「一致ゼロ」に見える**——全件が `NotPlaced`＝全件解放＝全件二重発注になる。

## 検討した選択肢

1. **何も変えない（人手のまま）** — #851 が前提にした「突合が解決する」が恒久的に成立しない。却下。
2. **`Enabled` / `UseBrokerProbe` を一気に有効化し、解放もそのまま開ける** — #856 が
   「実機の記録で示せないなら有効化しない」と名指しで禁じている。remark 往復が未検証のまま開けるのは
   IADR-0211 決定 1 の規律（確実に未発注だけが解放してよい）を突合の側で破ることでもある。却下。
3. **`Enabled` だけ有効化し、プローブは no-op のまま** — 動くのは phase-4 自己修復（記録があるのに予約が
   `Reserved` のまま）だけである。#848 の滞留は記録が無い側なので**解消しない**。却下。
4. **「有効化」と「解放の解禁」を別のスイッチにする**（採用）。

## 決定

### 決定 1: 解放の門 `Reconciliation:ReleaseOnNotPlaced` を新設し、既定を閉じる

`OrderReservationReconciler` は、照会が `NotPlaced` を返しても**門が閉じているあいだは予約を解放しない**。
据え置いたうえで `DecisionId` を結果の `HeldNotPlaced` に載せ、常駐が警告でログする（**無音にしない**）。

- 構成が DI に無いときは**閉じた側へ倒す**（未登録＝解放してよい、にしない）。
- 🔴 **門は `NotPlaced` にしか効かない。** `Indeterminate` は門を開けても据え置く。
  「門を開ける」が「不明も解放してよい」へ広がらないことを否定形テスト（T-10-602）で固定する。
- 門を開けてよいのは、実機で `NotPlaced` の偽陽性が無いことを**記録つきで示した後**だけである（#856 に残る）。

これは IADR-0211 決定 1 の規律を突合の側へ延長したものである——**「未発注」の根拠が未検証の前提に依るあいだは、
それを「確実に未発注」と呼ばない。**

### 決定 2: 有効化はアプリの既定ではなく配備（Helm values）で行う

`deploy/helm/ai-stock-trading/values.yaml` の `services.order-execution.extraEnv` に 5 キーを置く。

| キー | 値 | 意図 |
| --- | --- | --- |
| `Reconciliation__Enabled` | `"true"` | 巡回を立ち上げる |
| `Reconciliation__UseBrokerProbe` | `"true"` | moomoo 選択時だけ実照会が配線される。照会は**読み取りのみ** |
| `Reconciliation__ReleaseOnNotPlaced` | `"false"` | 🔴 決定 1 の門。既定と同値だが**明示値で可視化する** |
| `Reconciliation__StallThresholdHours` | `"2"` | 24 時間は保護レグの据え置きを解くには遅すぎる |
| `Reconciliation__IntervalHours` | `"1"` | 下限。検知遅れは最悪 2 + 1 = 3 時間 |
| `Reconciliation__BatchSize` | `"50"` | アプリ既定 200 から**下げる**（#882 監査 N4）。1 予約あたり最大 4 往復を、発注アダプタと**単一インスタンスを共有する** OpenD 接続へ流すため、初回デプロイで滞留が溜まっていると最悪 800 往復が 1 バーストで同じ接続に乗り、発注そのものを詰まらせ得る（返信待ち既定 15 秒）。溢れた分は次の巡回が拾う |

**アプリの既定（3 つとも `false`）は変えない。** リポジトリは一貫して「fail-safe 既定＋配備で明示的に有効化」を
採っており（`MaintenanceMarginEvaluation` が同型）、既定を反転させると docker-compose・単体開発環境の挙動まで変わる。
有効化の設定点を配備の 1 箇所に集める。

本番描画（ArgoCD が読む `values.yaml`）にも入る。paper 構成では no-op プローブのままなので、
走るのは phase-4 自己修復だけであり**無害**である。`values-local.yaml` は `order-execution` を持たないため、
マップのマージで経路B にもそのまま効く（リスト置換は同じキーを定義したときだけ起きる）。

`.github/workflows/helm.yml` に描画アサーションを 1 ステップ足し、既定描画 / `broker.tier=moomoo-sim` /
`values-local` の 3 描画で 5 キーが載ること、**`ReleaseOnNotPlaced` が `"false"` であること**を固定する
（門が黙って開くのを CI で止める）。

### 決定 3: 突合で `Placed` と確定した終端化は「黙って通り過ぎさせない」。保護レグを張るかは #853 が持つ

`OrderReservationReconciler` は保護逆指値を発注しない。有効化するとこの経路が実際に踏まれるため、
**無保護の建玉が台帳へ載り得る**（#853 の 2 番）。本 ADR は挙動を変えず、**見えるようにするだけ**にする。

- 結果に `ProbeTerminalized`（DecisionId・注文 ID・銘柄・数量・状態）を載せ、常駐が 1 件ずつ **Critical** でログする
  （本文に「この経路は保護逆指値を張りません」を明記する）。
- 🔴 **記録は発行（`PublishAsync`）より先に出す**（#882 監査 N1）。発行は外部のメッセージ基盤に触れるため落ち得る。
  記録を発行の後ろに置くと、例外で巡回ごと抜けて記録が出ない —— **しかも予約は既に `MarkCompleted` を commit 済みで
  次回巡回の `FindStalledReserved` に載らないため、その Critical は二度と出ない。**
  「黙って通り過ぎさせない」は発行の成否に依ってはならない。T-10-609 が破棄済みバスで固定する。
- **据え置き（`HeldNotPlaced`）も巡回サマリの件数に出す**（#882 監査 N3）。出さないと、全件が門で据え置かれた巡回が
  「滞留 5 件を走査（終端化 0 / 解放 0 / 不確定 0 / 失敗 0）」になり、運用者には内訳の合わない行に見える。
- 運用手順（`docs/operations/broker-execution-paths-runbook.md` / `operations.md`）に確認手順を書く。
- **phase-4 自己修復は載せない。** ブローカへ照会していない＝突合ではなく、記録も保護レグの有無も通常フローが決めている。

🔴 **Critical ログには通知の配線が無い**（#882 監査 N2）。本 ADR が足した `LogCritical` は**リポジトリ唯一**であり、
`deploy/` にアラート配線は存在しない。他の重大事象（`ProtectiveStopCoverageLost` 等）がイベント → 通知 → Discord で
人に届くのに対し、この経路はログ止まりである。**承知のうえでログに留める** —— 通知は「手で何をすべきか」を決めるため、
エントリーと手仕舞いレグを取り違えたまま出すほうが有害だからである（上記）。配線は #853 の裁定と併せて行う。

**保護レグを張るか否かの裁定は #853 に残す。** IADR-0210 の fail-closed（逆指値なしの建玉を持たない）と
「状態が不明なとき建玉を落としてよいか」の優先順位を決める必要があり、単純な実装変更では済まない。

🔴 **新しい契約イベントは作らない。特に `ProtectiveStopCoverageLost`（`Remediation=None`）の再利用は採らない。**
**予約行だけからはエントリーと手仕舞いレグを区別できない** —— 予約は `PositionEffect` を持たず、
プローブが返す `BrokerOrder` の `PositionEffect` は `MoomooReservationBrokerProbe` が `Open` に固定している。
手仕舞いレグに対して「逆指値なしの建玉が残っている可能性があり人手対応を要する」と通知すると、
読んだ人が手で成行を重ねる＝**二重決済でショート化**を誘発する。**通知は「手で何をすべきか」を決める**ので、
区別がつかないまま発行してはならない。

［#882 監査 N2 の指摘を容れて訂正］**「原理的に区別できない」とは言えない。**
`ProtectiveStopIds.StopDecisionId` / `CloseDecisionId` はエントリー ID からの**決定的導出**であり、
発注執行が持つ保護逆指値ストアの active な行のエントリー ID から候補を総当たりで導いて突き合わせれば判別できる
（`protective_stop_orders` に行が残っている範囲で、という限定はつく）。**正しい表現は「予約行だけからは区別できない」**である。
それでも本 PR では実装しない —— 総当たり突合は不完全な判別であり、外れたときに倒れる先が
**二重決済を誘発する側**になる。外れ方は 2 通りある: **行が消えている**場合に加えて、
`IProtectiveStopOrderStore` に**逆引きの入口が無い**ため（口は `Save` / `Find(Guid)` / `FindActive(int)` の 3 つで、
候補を導けるのは active な行の列挙だけである）、しかも**EntryDecisionId につき高々 1 行（最新の試行のみ）**しか
持たないため、**試行番号が進んだ**場合も候補から外れる。
確実に区別するには予約表へ `PositionEffect` を持たせる（Migration）のが筋であり、それも #853 の裁定の一部である。
fail-safe 側（通知を出さずログに留める）で待つ。

## 理由

- 「有効化」と「解放の解禁」を分けると、**安全側を 1 バイトも崩さずに滞留の解消を先に成立させられる**。
  `Placed` は在庫を戻さず注文も増やさない（remark が GUID なので偽陽性も実用上起きない）。危険なのは解放だけである。
- 閾値を 2 時間へ下げたのは、保護逆指値ガードの据え置き（＝無保護の建玉が残っている状態）を 24 時間放置できないため。
  再配送窓（共通再試行 2s/10s/30s ＝約 42 秒）と `_error` 滞留の外側であり、下限クランプ（1 時間）より上にある。
- 明示値で `false` を書くのは、**「書いていない」と「閉じると決めた」を区別する**ためである
  （`MaintenanceMarginEvaluation__Enabled` を明示している理由と同じ）。

## 影響・追随

- 既定の反転は無い。`docker-compose` / 単体テスト / 開発環境の挙動は不変。
- `OrderReservationReconciler` のコンストラクタに `IOptions<ReconciliationOptions>?`（既定 `null`）が増える。
  `ReservationReconciliationResult` に 2 つの明細（`ProbeTerminalized` / `HeldNotPlaced`）が増える。
- 「リコンサイルは既定で無効／いまの配備では人が解決する」と書いていた記述を是正した（誤りの側の語で全走査した）——
  `BrokerDispatchIndeterminateException` / `BrokerUnavailableException` の契約コメント、
  `OrderExecutionAppService` / `ProtectiveStopGuard` / `MoomooBrokerAdapter` / `Program.cs` /
  `OrderReservationReconciliationService` / `ReconciliationOptions`、運用手順 3 本、
  IADR-0117（改定 6・7）・IADR-0211・IADR-0114（決定 5-3 の「入れない判断」を覆した）。
- `IADR-0074` 決定 4 と `IADR-0092` 決定 4（どちらも「既定は無効／既定は no-op」）は**アプリ既定の記述であり真のまま**。
  配備で有効化した事実は本 ADR が持つ。

## 残余リスク

- 🔴 **`NotPlaced` の実機検証は行っていない**（本 PR では実クラスタにも実ブローカーにも触れていない）。
  門が閉じているあいだ、未発注で滞留した予約は**人が解決する**。滞留の自動解消は `Placed` 側だけである。
- 🔴 **突合で確定したエントリーは保護レグを持たない**（決定 3）。Critical のログと運用手順でしか見えない。
  Discord へ出るのは `OrderExecuted`（「約定した」）だけで、「保護が無い」は出ない。#853 の裁定を待つ。
- `MoomooReservationBrokerProbe` が `PositionEffect` / `ProductType` / `Mode` を既定値で近似するため、
  突合で作られる `ExecutionRecord` はそれらの列が実態とずれる（手仕舞いレグでも `Open` と記録される）。
  IADR-0092 が記録した既知の近似であり、本 ADR では変えない（#853 の射程）。
  🔴 **ただし時系列に注意する**（#882 監査）: 近似そのものは `16040fc17`（2026-07-19）からの既存だが、
  **決済レグに予約が付いたのは `c53876d49`（2026-09-19・#851）**である。したがって
  「決済レグが `Open` として `executed_orders` に載る」という事象は、**本 ADR の有効化で初めて実際に書かれる**。
  下流はこの列を読まないため実害は無いが、「前からある近似」と片付けてよい話ではない。
- 🔴 **構造的な脆さ（今日は到達不能・#882 監査）**: `OrderReservationReconciler` の
  `catch (Exception ex) when (ex is not OperationCanceledException)` は、プローブが**呼び出し元のトークンと無関係の**
  `OperationCanceledException` を投げた場合もバッチごと中断させる。常駐の `ExecuteAsync` は
  `catch (OperationCanceledException) { break; }` なので、**巡回ループごと恒久停止する**（再起動まで戻らない）。
  現行の `MoomooReservationBrokerProbe` は自前のタイムアウトを `TimeoutException` で投げるため到達しないが、
  **プローブを差し替えるときはここを踏む**。差し替える PR はこの分岐を先に直すこと。
  🔴 **恒久停止だけが問題ではない**（#882 監査 N1'）。`ThrowIfCancellationRequested` は `ReportFindings` より
  **手前**にあるため、**巡回の途中で中断されると、その時点で既に確定（`MarkCompleted` を commit）した予約の
  所見が 1 行も出ない。** 確定した予約は次の巡回の `FindStalledReserved` に載らないので、
  **その Critical は永久に失われる**（監査のプローブ実測: 1 件目 `Completed` ／ ログ 0 行 ／ 次巡回の走査対象は 2 件目のみ）。
  こちらの到達性は発行の失敗より高い —— **通常のローリングデプロイや Pod 再起動が巡回中に重なるだけ**で起きる
  （1 巡回は 50 件 × 最大 4 往復 × 最悪 15 秒＝数分に及び得る）。
- 🔴 **発行された `OrderExecuted` 自体も失われ得る**（#882 監査 N2'。上と同じ幾何）。Wolverine の durable outbox は
  **配線されていない**（`git grep -n "Durability\|UseDurableOutbox\|PersistMessagesWith" -- backend` ＝ 0 件）。
  🔴 **`git grep` で数えること**——ビルド済みツリーで素の `grep -rn` を使うと `bin/**/Wolverine*.dll` の
  バイナリ一致で 52 件返り、結論が逆に読める（#882 監査 N-4 の実測）。
  `PublishAsync` が落ちた時点で予約は既に `Completed` であり、**監査・リスク管理・通知は突合が確定させた約定を
  二度と受け取らない**（台帳に約定が載らない）。順序自体は本 ADR 以前からのものだが、
  **突合が配備で有効になったことで初めて本番経路になる**（`PositionEffect.Open` の項と同じ論法である）。
- 🔴 上の 2 つ（**「commit 済み → 再走査されない」幾何**）は [#890](https://github.com/endazon/ai-stock-trading/issues/890)
  へ切り出した。考え得る方向（所見を 1 件ずつ commit の直後に出す／durable outbox を配線する／`MarkCompleted` を
  publish 成功まで遅らせる）を列挙したうえで、**本 ADR ではどれも採らない**。失うのはログ 1 行と 1 通のイベントであって
  資金ではなく、**安全側（撃ち直さない・在庫の押さえを解かない）はこの幾何に依らず成立する**からである
  （予約が `Completed` で残る限り再配送は二重発注しない）。`MarkCompleted` を遅らせる案は、IADR-0057 の相
  （**予約 → 発注 → 確定**）を**反転させはしない**が、**確定までの「発注済みか不明」な窓を広げる**——
  その窓で落ちれば同じ照会をやり直すことになる。IADR-0057 の中核（予約はブローカ発注の**前**に独立して
  コミットする）に触る判断であり、本 ADR の射程では決められない
  〔#882 監査 N-3: ここは当初「相順（保存 → 確定）と逆向き」と書いていたが、**IADR-0057 の相は
  「予約 → 発注 → 確定」であり反転は起きない**。言い過ぎだったので直した〕。
- 検知遅れは最悪 3 時間（滞留 2 時間 ＋ 巡回 1 時間）。巡回間隔の下限が 1 時間にクランプされているため、
  これ以上は縮まらない。短縮するには `ReconciliationOptions.Interval` のクランプ自体を改める必要がある。
