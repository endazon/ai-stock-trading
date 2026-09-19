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
- 運用手順（`docs/operations/broker-execution-paths-runbook.md` / `operations.md`）に確認手順を書く。
- **phase-4 自己修復は載せない。** ブローカへ照会していない＝突合ではなく、記録も保護レグの有無も通常フローが決めている。

**保護レグを張るか否かの裁定は #853 に残す。** IADR-0210 の fail-closed（逆指値なしの建玉を持たない）と
「状態が不明なとき建玉を落としてよいか」の優先順位を決める必要があり、単純な実装変更では済まない。

🔴 **新しい契約イベントは作らない。特に `ProtectiveStopCoverageLost`（`Remediation=None`）の再利用は採らない。**
予約は `PositionEffect` を持たず、プローブが返す `BrokerOrder` の `PositionEffect` は
`MoomooReservationBrokerProbe` が `Open` に固定しているため、**エントリーと手仕舞いレグを区別できない**。
手仕舞いレグに対して「逆指値なしの建玉が残っている可能性があり人手対応を要する」と通知すると、
読んだ人が手で成行を重ねる＝**二重決済でショート化**を誘発する。**通知は「手で何をすべきか」を決める**ので、
区別できないまま発行してはならない。区別するには予約表へ `PositionEffect` を持たせる必要があり（Migration）、
それも #853 の裁定の一部である。

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
- 検知遅れは最悪 3 時間（滞留 2 時間 ＋ 巡回 1 時間）。巡回間隔の下限が 1 時間にクランプされているため、
  これ以上は縮まらない。短縮するには `ReconciliationOptions.Interval` のクランプ自体を改める必要がある。
