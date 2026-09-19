---
title: 滞留 Reserved の自動リコンサイルを稼働 PoC で有効化し、解放（NotPlaced）だけを実機検証まで閉じたままにする
type: spec
status: accepted
related_ids: [FR-05, FR-10, FR-11, UC-06, ADR-0002, ADR-0003, ADR-0016, ADR-0040, IADR-0057, IADR-0058, IADR-0059, IADR-0074, IADR-0092, IADR-0111, IADR-0113, IADR-0114, IADR-0117, IADR-0210, IADR-0211, IADR-0362]
author: endazon (with Claude Code)
created: 2026-09-19
updated: 2026-09-19
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-05 発注執行・「拒否」は証券会社が受理しなかった状態)
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md (UC-06 利用者による建玉の手仕舞い)
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md (OpenD 常駐・SPOF)
---

# 仕様書: 滞留 Reserved の自動リコンサイルを稼働 PoC で有効化する（#856）

## 起点

- #856（enhancement）。#851（#848 の是正）3 巡目監査の**非ブロッキング**切り出し。
- #851 は「滞留した `Reserved` は client order id による突合が解決する」を前提に文面を書いたが、
  **その突合は配備で動いていない**。#851 は文面を条件つきに直しただけで、有効化そのものは行っていない。
- 同じ場所で 2026-09-18 に人が詰んだ（#847 / #848 / #849 の連鎖）。

## 1. なぜ無効なのか（実物で特定した証跡）

推測ではなくコードと `deploy/` の実物を読んだ。**原因は 2 つの既定値 ＋ 配備側の上書き不在の 3 点**であり、
「綴り違い」でも「条件分岐で実質無効」でもない。

### (a) アプリの既定が `false`（2 つとも）

`backend/Services/OrderExecutionService/Features/OrderExecution/ReconcileOrderReservations/ReconciliationOptions.cs`

```csharp
public bool Enabled { get; set; }         // 既定 false（初期化子なし）
public bool UseBrokerProbe { get; set; }  // 既定 false（初期化子なし）
```

`Enabled=false` のとき `OrderReservationReconciliationService.ExecuteAsync` は
「発注予約の自動リコンサイルは無効です（Reconciliation:Enabled=false）」を 1 行ログして **`return` する**
（巡回そのものが立ち上がらない）。

### (b) 実照会プローブは `UseBrokerProbe=true` × `Broker:Provider=moomoo` の AND でしか配線されない

`backend/Services/OrderExecutionService/Program.cs`（115〜132 行）

```csharp
if (brokerSelection.IsMoomoo
    && builder.Configuration.GetSection(ReconciliationOptions.SectionName).Get<ReconciliationOptions>()?.UseBrokerProbe == true)
{ /* MoomooReservationBrokerProbe */ }
else
{ builder.Services.AddSingleton<IReservationBrokerProbe, IndeterminateReservationBrokerProbe>(); }
```

`IndeterminateReservationBrokerProbe` は**常に `Indeterminate`** を返す。すなわち `Enabled=true` にしても
プローブが no-op のままなら `Placed` / `NotPlaced` は 1 度も発火せず、動くのは
**phase-4 自己修復（`executed_orders` に記録があるのに予約が `Reserved` のまま）だけ**である。

### (c) `deploy/` に上書きが無い（2026-09-19 実測）

```
$ grep -rni reconcil deploy/ | wc -l
0
```

`deploy/helm/ai-stock-trading/values.yaml` の `services.order-execution` は `image` / `db` の 2 キーだけで
`extraEnv` を持たない。`values-local.yaml`（経路B プロファイル）は `order-execution` を**そもそも含まない**。
したがって稼働 PoC の Pod には `Reconciliation__*` が 1 つも注入されておらず、(a) の既定がそのまま効く。

**結論**: 「既定値が false」＋「Helm values で未設定」の複合。設定キーの綴り違いでも条件分岐の事故でもない。

## 2. 有効にしたときに走る処理が、現在の develop の台帳の意味論と噛み合うか

### 2-1. `Placed → 記録と OrderExecuted の発行`

- `OrderReservationReconciler` は `executedOrders.Save(...)` → `reservations.MarkCompleted(...)` →
  `OrderExecuted` を Worker 層が `Publish` する。台帳（リスク管理）は `OrderExecuted` を
  `DecisionId` で承認行に相関して約定を載せる。承認行は
  - エントリー: 承認時に `AppendApproval` 済み
  - 保護レグの成行手仕舞い: `CloseDispatchIndeterminate` が `CloseIntent` を運んで承認行を作る（#851）
  のいずれかで既に存在する。**噛み合う**（#851 が想定した解決経路そのもの）。
- 保護逆指値ガードは巡回の入口で手仕舞いレグの痕跡を見るため、突合が記録を作れば
  **次の巡回は「送らずに完了」**になる（#851 で実装済み）。噛み合う。

### 2-2. 🔴 `Placed` と確定したエントリーに保護レグが張られない（#853 の 2 番）

`OrderReservationReconciler` は保護逆指値を発注しない。有効化するとこの経路が実際に踏まれるため、
**無保護の建玉が台帳へ載り得る**。

- **本 PR では保護レグを張らない。** 張るか否かは IADR-0210 の fail-closed（「逆指値なしの建玉を持たない」）と
  「状態が不明なとき建玉を落としてよいか」の優先順位の裁定を要し、**#853 がその裁定を持つ**。
- 本 PR が負うのは「**黙って通り過ぎないこと**」である。突合が `Placed` で終端化した予約を
  `ReservationReconciliationResult.ProbeTerminalized` に載せ、常駐が **1 件ずつ Critical でログする**
  （DecisionId・注文 ID・銘柄・数量・状態、および「この経路は保護レグを張らない」の明示）。
  運用手順（runbook）にも「突合が解決した注文は保護レグを持たない。エントリーなら保護の有無を確認せよ」を書く。
- 🔴 **記録は発行（`PublishAsync`）より先に出す**（#882 監査 N1）。発行は外部のメッセージ基盤に触れるため落ち得る。
  記録を後ろに置くと例外で巡回ごと抜けて記録が出ず、**予約は既に `MarkCompleted` を commit 済みで次回巡回の
  `FindStalledReserved` に載らないため、その Critical は二度と出ない**。T-10-609 が破棄済みバスで固定する。
- 据え置き（`HeldNotPlaced`）も巡回サマリの件数に出す（#882 監査 N3。出さないと内訳の合わない行に見える）。
- ⚠️ **Critical ログには通知の配線が無い**（#882 監査 N2）。本 PR が足した `LogCritical` はリポジトリ唯一であり、
  `deploy/` にアラート配線は無い。承知のうえでログに留める（通知は「手で何をすべきか」を決めるため、
  エントリーと手仕舞いレグを取り違えたまま出すほうが有害である）。配線は #853 の裁定と併せて行う。
- **通知（Discord）の面では、突合の終端化は既に `OrderExecuted` として通知される**（`OrderExecutedNotificationHandler`）。
  本 PR で足すのは「保護が無い」という追加の事実であり、新しい契約イベントは**作らない**
  （`ProtectiveStopCoverageLost` の再利用は採らない。理由は下記「採らなかった案」）。

### 2-3. 🔴 `Release` が「確実に未発注」に限定されているか

`Release` は在庫（予約）を戻す＝**再発注を許可する**操作であり、誤判定は二重発注に直結する。
判定根拠の連鎖を端まで読んだ。

| 層 | 契約 | 実物 |
| --- | --- | --- |
| `IReservationBrokerProbe` | 判定不能・照会不達は必ず `Indeterminate`。`NotPlaced` は「確実に未発注」を確定できた窓だけ | 契約コメントで宣言 |
| `MoomooReservationBrokerProbe` | client が `null` を返したときだけ `NotPlaced`。例外は `Indeterminate` | 実装どおり |
| `MMApiMoomooTradeClient.FindOrderByClientIdAsync` | 全対応市場の現在＋履歴を**成功裏に**列挙して一致ゼロのとき `null`。途中の失敗は例外 | 実装どおり（`remark` 空なら例外） |
| 発注側 | `MoomooBrokerAdapter` は `IClientOrderIdBroker` を実装し、予約を伴う全経路（エントリー・逆指値・成行手仕舞い・代替種別）で `remark = DecisionId("N")` を伝播する | 実装どおり |

**構造としては筋が通っている。しかし「ブローカーの事実」ではなく「remark が往復する」という未検証の前提に依存する。**

- moomoo SIMULATE が `remark` を保存し、`TrdGetOrderList` / `TrdGetHistoryOrderList` の応答に返すかは**実機未検証**
  （#856 本文が明記）。返さなければ**発注済みの注文が 1 件も一致せず、全件が `NotPlaced`＝全件解放**になる。
- 履歴窓は `reservedAt - 2 日` 〜 `now + 1 日`。ブローカー側の履歴保持・照会レンジ上限は未検証。
- すなわち `NotPlaced` は現時点で「**確実に未発注**」を名乗れない。

**決定: 解放（`NotPlaced → Release`）を既定で閉じる。** `ReconciliationOptions.ReleaseOnNotPlaced`（既定 `false`）を
新設し、閉じているあいだは `NotPlaced` でも**解放しない**（据え置き＋件数計上＋警告ログ）。
開けてよいのは #856 の受け入れ基準が要求する実機記録（`NotPlaced` の偽陽性が無いこと）を示した後である。
これで「有効化」と「解放の解禁」を**別々のスイッチ**にできる——#851 が言う安全側（撃ち直さない・押さえを解かない）を
1 バイトも崩さずに、滞留の解消（`Placed` 側）だけを先に成立させられる。

## 3. 稼働 PoC の設定をどう変えるか

**アプリの既定は変えない**（`Enabled=false` / `UseBrokerProbe=false` / `ReleaseOnNotPlaced=false` のまま）。
リポジトリの fail-safe 既定（ブローカ paper・外部連携空＝no-op）の規律をそのまま保つ。
変えるのは `deploy/helm/ai-stock-trading/values.yaml` の `services.order-execution.extraEnv` だけである。

| キー | 値 | 理由 |
| --- | --- | --- |
| `Reconciliation__Enabled` | `"true"` | 巡回を立ち上げる。paper 構成では no-op プローブのため phase-4 自己修復のみ（無害） |
| `Reconciliation__UseBrokerProbe` | `"true"` | moomoo 選択時だけ実照会が配線される。照会は**読み取りのみ**で発注を 1 本も増やさない |
| `Reconciliation__ReleaseOnNotPlaced` | `"false"` | 🔴 解放の門。実機検証まで閉じる。**明示値で可視化する**（既定と同値でも書く） |
| `Reconciliation__StallThresholdHours` | `"2"` | 24 時間は保護レグの据え置き（無保護の建玉が残る）を解くには遅すぎる。再配送窓（約 42 秒）と `_error` 滞留の外側であり、下限クランプ（1 時間）より上 |
| `Reconciliation__IntervalHours` | `"1"` | 6 時間 → 下限の 1 時間。検知遅れの最悪値は 2 + 1 = 3 時間 |
| `Reconciliation__BatchSize` | `"50"` | #882 監査 N4。アプリ既定 200 から下げる。1 予約あたり最大 4 往復を**発注アダプタと単一インスタンスを共有する** OpenD 接続へ流すため、初回デプロイで滞留が溜まっていると最悪 800 往復が 1 バーストで同じ接続に乗る（返信待ち既定 15 秒）。溢れた分は次の巡回が拾う |

⚠️ `StallThresholdHours` を 23 時間より上げてはならない（#882 監査 N6）。約定追跡の追跡上限（`MaxTrackingHours`＝24 時間）を
超えると、突合が `Placed` で記録を作った時点で既に追跡窓の外にあり、非終端のまま取り残される。

`values-local.yaml` は `order-execution` を持たないため、マップのマージで上記がそのまま経路B にも効く
（Helm がリストを置換するのは同じキーを定義したときだけ。`helm.yml` の「values-local drops no env from prod default」検査も通る）。

`helm.yml` に描画アサーションを 1 ステップ足し、既定描画・`broker.tier=moomoo-sim` 描画・`values-local` 描画の
3 つで上記 6 キーが order-execution の Deployment に載ること、`ReleaseOnNotPlaced` が `"false"` であることを固定する。

## 採らなかった案

1. **アプリの既定を `Enabled=true` にする** —— docker-compose・単体開発環境まで挙動が変わる。
   本リポジトリは「fail-safe 既定＋配備で明示的に有効化」を一貫して採っており（`MaintenanceMarginEvaluation` が同型）、
   そちらへ揃える。既定を変える積極的な理由が無い。
2. **`UseBrokerProbe` も含めて一気に解放まで開ける** —— #856 が「示せないなら有効化しない」と名指しで禁じている。
3. **`ProtectiveStopCoverageLost`（`Remediation=None`）を突合の終端化で発行する** ——
   **予約行だけからはエントリーと手仕舞いレグを区別できない**（予約は `PositionEffect` を持たず、
   プローブが返す `BrokerOrder` の `PositionEffect` は `MoomooReservationBrokerProbe` が `Open` に固定している）。
   手仕舞いレグに対して「逆指値なしの建玉が残っている可能性があり人手対応を要する」と通知すると、
   読んだ人が手で成行を重ねる＝**二重決済でショート化**を誘発する。採らない。
   ［#882 監査 N2 の訂正］**「原理的に区別できない」とは言えない** —— `ProtectiveStopIds` の決定的導出と
   保護逆指値ストアの active 行を総当たりで突き合わせれば判別の余地はある。ただし「行が残っているか」に依存する
   不完全な判別であり、外れたときに倒れる先が二重決済を誘発する側なので採らない。
4. **予約表へ `PositionEffect` を持たせる（Migration）** —— 3 を成立させる正攻法だが、
   `TryReserve` の署名変更＋Migration であり #830 / #873 / #881 と衝突面が広い。#853 の裁定に委ねる。

## 影響範囲（母集合）

**走査した語**（誤りの側の文字列で全追跡ファイルを走査した。規則 9）:
`Reconciliation:Enabled` / `Reconciliation__Enabled` / `UseBrokerProbe` / `リコンサイル` × `既定|無効|有効` /
`reconcil`（`deploy/`）。

### 直す（記述が実態と食い違うようになる）

| ファイル | 何が誤りになるか |
| --- | --- |
| `backend/Shared/AiStockTrading.Shared.Contracts/Ports/BrokerDispatchIndeterminateException.cs` | 「リコンサイルは既定で無効（…**`deploy/` にも上書きは無い**）」——上書きを入れるので偽になる |
| `backend/Shared/AiStockTrading.Shared.Contracts/Ports/BrokerUnavailableException.cs` | 同型のコメント参照（「リコンサイルは既定で無効」） |
| `backend/Services/OrderExecutionService/Features/OrderExecution/DispatchApprovedOrder/OrderExecutionAppService.cs` | 「🔴 **既定は無効**（…）であり、その場合は人が…（有効化は #856）」 |
| `backend/Services/OrderExecutionService/Features/OrderExecution/GuardProtectiveStops/ProtectiveStopGuard.cs` | 「既定は無効であり、その場合は人が…」 |
| `backend/Services/OrderExecutionService/Infrastructure/ExternalServices/MoomooBrokerAdapter.cs` | 「既定は無効で、その場合は人が解決する（#856）」 |
| `backend/Services/OrderExecutionService/Program.cs` | 配線コメント（既定無効の説明） |
| `backend/Services/OrderExecutionService/Hosted/OrderReservationReconciliationService.cs` | 「既定は無効（IADR-0074 決定4）」＋無効時ログ |
| `backend/Services/OrderExecutionService/Features/OrderExecution/ReconcileOrderReservations/ReconciliationOptions.cs` | 「**既定は無効**」の前提説明（配備では有効） |
| `docs/operations/broker-execution-paths-runbook.md` | 「🔴 滞留した予約は、**既定では自動で解決しない**」 |
| `docs/operations/operations.md` | 「有効化手順」「無効時はログに…」「未配線（既定 no-op）なら手動で」 |
| `docs/operations/live-trading-cutover-runbook.md` | 移行チェックリスト行（「既定 no-op では自動解消しない」） |
| `.ai-context/adr/IADR-0117_owner-position-close-path.md` | 改定 6/7 の「🔴 突合は既定で無効（有効化は #856）＝滞留 Reserved は人が解決する」→ 日付つき追記で更新 |
| `.ai-context/adr/IADR-0211_opend-unavailable-forgo-without-queueing.md` | 「既定は無効（…）で、**いまの配備では人が解決する**（有効化は #856）」→ 日付つき追記 |
| `.ai-context/adr/IADR-0114_route-b-parity-observed-drawdown-and-official-sources.md` | 決定 3「リコンサイルは paper で自己修復のみ＋巡回下限 1 時間」＝**入れない判断**が覆る → 日付つき追記 |
| `.ai-context/adr/README.md` | 上記 3 本＋新設 IADR-0362 の索引行（`check-adr-index-sync.js`） |

### 直さない（と、その理由）

| ファイル | 除外理由 |
| --- | --- |
| `.ai-context/adr/IADR-0074_*.md` 決定 4 / `IADR-0092_*.md` 決定 4 | **アプリの既定は `false` のまま**なので記述は真。配備で有効化した事実は IADR-0362 が持つ |
| `.ai-context/adr/IADR-0083_*.md` / `IADR-0113_*.md` / `RiskManagementService/Hosted/*Options.cs` | 「#141 リコンサイルと同型（既定無効）」の**引き合い**。アプリ既定は不変なので真のまま |
| `.ai-context/specs/2026*.md`（`20260718_141` / `20260719_141` / `20260729_279` / `20260919_848`） | 確定済みの凍結記録。本文プロズを後から書き換えない（`.ai-context/specs/` は日付つき追記が可だが、当時の記述は当時の事実として正しく、追記の必要が無い） |
| `backend/.../IndeterminateReservationBrokerProbe.cs` | no-op プローブそのものの説明。実物は不変 |
| `backend/.../Tests/**` の「既定無効」コメント | テストが固定しているのはアプリ既定であり不変 |
| `src/ai-stock-trading` 以下・`CHANGELOG.md` | 自動生成・submodule |

導出値（`StallThresholdHours` から来る検知遅れ「最悪 3 時間」）は走査ではなく**計算し直した**（2 + 1）。

## テスト（T-10-600 以降。develop 時点の最大は 482、割当済みは 483〜505 / 508〜513 / 520〜 / 540〜 / 560〜 / 580〜）

| ID | 内容 | 是正前 |
| --- | --- | --- |
| T-10-600 | 🔴 **否定形・最重要**: `NotPlaced` でも解放の門が閉じているあいだは**解放しない**（予約は `Reserved` のまま・記録も作らない） | 落ちる（解放される） |
| T-10-601 | 門を開けたときだけ `NotPlaced` が解放する（門の実効） | 落ちる（門が無い） |
| T-10-602 | 🔴 **否定形**: `Indeterminate` は門の開閉に依らず解放しない | 通る（回帰の固定） |
| T-10-603 | 門が閉じた `NotPlaced` は**無音にならない**（結果に `HeldNotPlaced` として載る） | 落ちる |
| T-10-604 | 🔴 突合で `Placed` と確定した終端化は結果に `ProbeTerminalized` として載る（保護レグ不在が黙って通り過ぎない） | 落ちる |
| T-10-605 | phase-4 自己修復（記録あり）は `ProbeTerminalized` に載せない（ブローカ照会していない＝突合ではない） | 落ちる |
| T-10-606 | 常駐（`OrderReservationReconciliationService`）経由でも門が閉じた `NotPlaced` を解放しない | 落ちる |
| T-10-607 | `ReconciliationOptions` の既定 3 つが `false`（配備で明示的に開ける規律の固定） | 通る（回帰の固定） |
| T-10-608 | Helm: 既定描画 / `broker.tier=moomoo-sim` / `values-local` の 3 つで 6 キーが order-execution に載り、`ReleaseOnNotPlaced` が `"false"` | 落ちる（`helm.yml` のアサーション） |
| 🔴 T-10-609 | **否定形・#882 監査 N1**: 発行（`PublishAsync`）が落ちても、保護レグ不在の Critical は**既に出ている**（記録は発行より先） | 落ちる（記録を発行の後ろへ戻すと 1 件も出ないことを破棄済みバスで実証） |

**変えてはいけない側**: T-10-402 / T-10-403 / T-10-406 / T-10-407 / T-10-408 / T-10-409 は緑のまま
（二重決済でショート化しないことの固定）。既存の
`OrderReservationReconcilerTests.未発注が確定した滞留予約は解放される` は**意味が変わる**ため、
門を明示的に開ける構成へ書き換える（削除しない）。

## 受け入れ基準

1. 稼働 PoC の構成（`values.yaml` ＋ `values-local.yaml`）でリコンサイラが動く。`helm template` の実出力で示す。
2. 滞留した `Reserved` が、人の手を借りずに解決する経路が成立する（phase-4 自己修復＋突合の `Placed`）。
3. 🔴 「確実に未発注」でないものを解放しない。否定形テストで固定する。
4. 🔴 T-10-402 / T-10-403 / T-10-406 / T-10-407 / T-10-408 が緑のまま。
5. 突合で `Placed` と確定した建玉が保護を持たないことが、記録（Critical ログ＋運用手順）で見え、
   かつ「保護レグを張るか」の裁定が #853 へ明示的に分離されている。

## 射程外

- `NotPlaced` の実機検証そのもの（SIMULATE で remark が往復するか）。**#856 に残す**——門を開ける PR が持つ。
- 突合で確定したエントリーへ保護レグを張ること（#853）。
- `MoomooReservationBrokerProbe` が `PositionEffect` / `ProductType` を既定値で近似している件（#853 で扱う）。
