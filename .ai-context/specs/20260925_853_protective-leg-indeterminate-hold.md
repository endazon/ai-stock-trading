---
title: 保護レグ（逆指値）の送信結果が不明なら据え置き・予約で重複を止め、突合で発注済みと確定したエントリーに保護レグを張る（#853）
type: spec
status: accepted
related_ids: [FR-05, FR-10, FR-11, FR-12, UC-02, UC-06, ADR-0040, IADR-0057, IADR-0074, IADR-0092, IADR-0117, IADR-0210, IADR-0342, IADR-0344, IADR-0347, IADR-0362, IADR-0371, IADR-0428]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10「逆指値が未受理・失効した場合は建玉を持たない」)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1)
---

# 仕様書: 保護レグの「届いたか不明」を据え置き、突合で確定したエントリーに保護レグを張る（#853）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-10（逆指値なしの建玉を持たない・未受理なら建玉を持たない）、FR-05（発注）、FR-11（監査・通知）、FR-12（損切りの手法）
- ユースケース（UC）: UC-02（損切り）、UC-06（手仕舞い）
- 画面（SC）: なし
- 関連 ADR: ADR-0040 決定 1（S0〜S3 の手法）。計画 ADR の決定は変えない
- 関連 IADR: IADR-0210（本件の記録先・日付つき追記）、IADR-0117 改定 6〜9（届いたか不明の据え置き）、IADR-0057（3 相）、
  IADR-0074 / IADR-0092 / IADR-0362 / IADR-0371（突合）、IADR-0342 / IADR-0344 / IADR-0347（S2 / S1 / S3）。新規 IADR-0428
- 裁定: #853 のオーナー裁定（2026-09-25）「据え置き・予約・保護レグを張る」

## 目的・背景

#853 本文と 2026-09-19 の追記のとおり、保護レグ（逆指値）の送信結果が不明（`BrokerDispatchIndeterminateException`）のとき、

1. `OrderExecutionAppService.PlaceProtectiveStopAsync` と `ProtectiveStopGuard.ReplaceOrCloseAsync` は例外の種類を見ずに
   「未受理」と同じ分岐（エントリーの取消・成行手仕舞い）へ落ちる。逆指値が実は生きていれば、建玉だけ消えて逆指値が孤立する。
2. ガードでは、成行も確実に未発注で終わると記録が Active のまま残り、**次の巡回が同じ `StopDecisionId` の逆指値をもう一度送る**
   （3 巡回で `stopSends` 1→2→3）。
3. `OrderReservationReconciler` は突合で `Placed` と確定したエントリーに保護レグを張らない。

## 裁定（2026-09-25・オーナー）

1. 保護レグの送信結果が**不明**なら、成行手仕舞いへ倒さず、照会で確定するまで据え置く。逆指値にも予約を持たせ、巡回ごとの送り直しを止める。
   確実に未発注（`BrokerUnavailableException`・確認できた拒否）は従来どおり。
2. 突合で `Placed` と確定したエントリー（`PositionEffect.Open`）には、承認時の手法で保護レグを張る（S0 逆指値・S1 ソフトウェア逆指値の記録・S2 なし・S3 代替注文種別）。
3. IADR-0210 に追記する。T-10-402 / 403 / 406 / 407 / 408 は緑のまま。

## 対象範囲

- 含む:
  - 逆指値レグの 3 相化（予約 → 発注 → 確定）。エントリー同時（発注執行）とガードの再発注の両方
  - 不明のときの据え置き（保護記録を「送信結果待ち」の形で残す）と、ガードによる解決（記録が現れたら採用・予約が消えたら未発注として扱う）
  - 据え置きの可視化（`ProtectiveStopRemediation.StopDispatchIndeterminate`・Critical・1 時間ごとの再通知・台帳への結線）
  - 承認時の保護の文脈の事前記録（S0 / S3 の新規建てを送る前に、巡回対象外の状態 `AwaitingEntry` で保護記録を残す）
  - 突合の出口に「確定したエントリーへ保護レグを張る」口（`IReconciledEntryProtection`）を足し、本番の組み立てで結線する
- 含まない:
  - ガードからの直接照会（remark による即時照会）。解決は既存の突合（配備: 滞留 2 時間・間隔 1 時間）に委ねる（残余リスクに記録）
  - 解放の門（`ReleaseOnNotPlaced`）の開閉。未発注の判定で予約は解放しない（据え置きが続く＝人が確認する）
  - 予約表へ `PositionEffect` を持たせる Migration（エントリーの判別は保護記録の有無で行う。下記）
  - S1 の実行挙動（今夜の AAPL は S1。S1 の行・決済経路には手を入れない。突合で確定した S1 のエントリーに配置の通知を足すだけ）

## 設計

### 1. 逆指値レグの 3 相（エントリー同時・ガード共通）

| 送信の結果 | 予約（`StopDecisionId`） | 以降 |
| --- | --- | --- |
| 受理（Accepted / PartiallyFilled / Filled） | 記録を保存してから `MarkCompleted` | 従来どおり保護記録 Active（注文 ID あり） |
| 確認できた拒否（終端が返った） | **解放**（`Release`。確実に未発注） | 従来どおり（エントリー同時＝取消／成行、ガード＝成行） |
| `BrokerUnavailableException` | **解放** | 従来どおり |
| `BrokerDispatchIndeterminateException`・分類できない例外 | **Reserved のまま** | **据え置き**（成行も取消もしない）。保護記録を「送信結果待ち」へ |
| 予約が取れない（既に Reserved） | 触らない | 据え置き（送らない） |

「送信結果待ち」の保護記録 = `State=Active`・`Mechanism=S0`・`StopOrderId=""`（空）・`StopDecisionId`＝据え置いたレグ・`Attempt`＝その試行。

分類できない例外を不明側へ倒すのは IADR-0117 改定 7（成行手仕舞い）と同じ規律である（未発注と言い切れない）。

### 2. ガードの「送信結果待ち」の評価（S0 の行で `StopOrderId` が空）

1. 発注結果の記録（`StopDecisionId`）がある → 突合が `Placed` と確定した（または行の更新だけ失われた）。注文 ID を行へ採用する。
   生きていれば `ProtectiveStopPlaced` を出して `Replaced`、生きていなければ採用した行で通常の評価（失効→再発注、約定→完了）。
2. 記録なし・予約が Reserved（または Completed） → **据え置き**（`Unknown`）。建玉が消えていても完了させない（生きていれば孤立するため）。
   このプロセスが未通知、または前回から 1 時間で `StopDispatchIndeterminate` を再発行する（`HeldCloseNotificationTracker` を共用）。
3. 記録も予約も無い（突合の門を開けて解放された・人が解放した） → 確実に未発注。通常の失効と同じく、建玉残に応じて完了または再発注（次の試行）。

### 3. 可視化と台帳

- `ProtectiveStopRemediation.StopDispatchIndeterminate`（末尾）。`CloseDecisionId`＝据え置いた**逆指値レグ**の DecisionId、
  `CloseIntent`＝そのレグの決済意図。通知は Critical で「送信した・届いたか不明・成行も取消もしない・重ねる前に証券会社の画面で確認」。
- 取引台帳は `CloseIntent` を承認行にする（由来は `ProtectiveStopS0`＝逆指値が武装されたときと同じ）。
  生きていれば約定が相関でき（承認が約定より数時間先行する）、`ProtectiveStopPlaced` の後着は冪等で何もしない。
- エントリー同時の経路（発注執行）は「通知した」をガードの記憶へ**書かない**。この経路の発行（`OrderApprovedHandler`）には
  発行失敗の補償が無く、書いた後に発行が落ちるとガードの再通知が 1 時間黙る。代わりにガードの最初の巡回（既定 30 秒後）が
  同じ通知を 1 回重ねる（重複は安全。黙るより重なる側へ倒す）。突合の出口は常駐ガードと同じ補償つきの発行を使う。

### 4. 承認時の文脈の事前記録（`ProtectiveStopState.AwaitingEntry`）

- S0（未知の手法の S0 扱いを含む）・S3 の新規建ては、エントリーの予約を**取った後・送る前**に、保護記録を `AwaitingEntry` で残す
  （既に行があれば触らない）。`AwaitingEntry` は `FindActive` に載らない（ガード・約定追跡・純額の計算の対象外）。
  書けなかったら Error を残して発注は続ける（事前記録の失敗で平常の発注を止めない）。
- 受理→保護記録 Active で上書き／据え置き→送信結果待ちで上書き／見送り（接続不能）・エントリー終端失敗・保護の失敗（取消・成行）→ `Completed`。
- S1 は従来どおり（エントリー前に Active で武装済み）。S2 は記録を作らない（従来どおり）。
- 列挙の値を足すだけで Migration は不要（整数列）。

### 5. 突合で確定したエントリーに保護レグを張る（`IReconciledEntryProtection`）

- 実装は `OrderExecutionAppService.ProtectAsync`（平常の経路と同じ `PlaceProtectiveStopAsync` を通す）。
- 突合は確定した 1 件の `OrderExecuted` を出口へ渡した**後**に呼ぶ（IADR-0371: 確定済みの 1 件の出口を失わない）。
  結果のイベントは出口の新しい口 `EmitProtectionAsync` で記録・発行する。取消トークンは渡さない（確定後の後始末）。
- 呼ぶのは突合（`Placed`・競合なし）と phase-4 自己修復。競合（通常フローが確定中）では呼ばない。
- 判別は保護記録の有無で行う（予約・プローブの `PositionEffect` は当てにならない。IADR-0362 決定 3）:

| 保護記録 | 動作 |
| --- | --- |
| `AwaitingEntry`（S0 / S3） | エントリーが生きている→承認時の手法で `PlaceProtectiveStopAsync`（受理／据え置き／取消・成行）。終端で約定あり→約定数量で張る。終端で約定なし→記録を完了 |
| Active の S1 | 生きている・約定あり→`SoftwareStopArmed` を出す（行はそのまま）。なし→何もしない（ガードが完了させる） |
| それ以外（Active の S0・完了） | 既に扱われた（何もしない） |
| 無い | 張れない。突合で確定したものは Critical で知らせる（S2 の免除・文脈を記録する前に止まった・手仕舞いレグ等） |

## 母集合の引き直し（規則 1〜6・9・10）

- 誤りの側（「逆指値の例外を未受理と同じ分岐へ落とす」）から: `git grep -n "PlaceStopOrderAsync\|PlaceAlternativeStopOrderAsync\|StopDecisionId("`
  （本番コード）→ 呼び出しは `OrderExecutionAppService.PlaceProtectiveStopAsync`・`ProtectiveStopGuard.ReplaceOrCloseAsync` の 2 箇所だけ。
  実装は `MoomooBrokerAdapter`（送信後の失敗は不明・接続確立の失敗は未発注だけを投げる）と `PaperBrokerAdapter`（例外なし）。**除外なし**。
- 誤りの側（「突合は保護レグを張らない」と書いた記述）から: `git grep -n "#853\|issues/853"`・`git grep -n "張らない\|張りません\|張られていない"`（拡張子で絞らない）。
  追随する: `OrderReservationReconciler.cs`・`OrderReservationReconciliationService.cs` のコメントと Critical の文面、
  `deploy/helm/ai-stock-trading/values.yaml` の注記、`docs/operations/broker-execution-paths-runbook.md`・`docs/operations/operations.md`、
  `OrderExecutionAppService.cs` :661・`ProtectiveStopGuard.cs` :360 の「#853 で扱う」注記。
  除外: `.ai-context/specs/`（確定済みの凍結記録）、IADR-0117 / IADR-0362 / IADR-0369 / IADR-0370 / IADR-0371 / IADR-0395 の本文
  （凍結記録。当時の射程を正しく書いている。決定を変えるのは IADR-0210 の追記と IADR-0428 に集める）。
- `ProtectiveStopRemediation` の値を分岐する箇所（`git grep -n "ProtectiveStopRemediation\."` 本番）: `NotificationFormatter`・`AuditEntryFactory`・
  `ProtectiveStopGuardService.PublishAllAsync`（発行失敗時の記憶の補償）・`ProtectiveStopCoverageLostLedgerHandler`（レグの有無で判定）。4 箇所とも追随。
  文書: `docs/functional/FR-10_risk-controls.md`（場面の表）・`docs/data/audit-events.md`（`Remediation` の注記）。
- `ProtectiveStopState` の新しい値を読み得る箇所（`git grep -n "ProtectiveStopState\.\|\.Find(" ` 本番）: 状態で絞る読み出しはすべて `Active` か `Completed` を明示しており、
  `Find(EntryDecisionId)` の呼び出し（発注執行の S1 分岐・ガード・純額の再読込・楽観並行の更新）は S1 か Active 行に対してだけ使われる。
  `ProtectiveStopGuard.ForgetTrackersOfFinishedRecords` は Active 以外を「終わった」と読むが、`AwaitingEntry` は記憶に載らない。追随不要。
- 空の `StopOrderId` を持つ S0 の行を読み得る箇所: 約定追跡（空を除外済み）・ガードの `RenewStopLegTracking`（空を除外済み）・
  乖離の取り込み（取消が失敗し「取消を確認できない」へ倒れる＝主張を減らさない安全側）。追随不要（残余リスクに記録）。

## 受け入れ基準

- [x] 逆指値の送信結果が不明なら、エントリー同時でもガードでも取消・成行を送らず据え置く（否定形）
- [x] ガードで不明が続いても逆指値を送り直さない（3 巡回で送信 1→1→1）
- [x] 確実に未発注（接続不能・確認できた拒否）は従来どおり（予約は解放する）
- [x] 据え置きは Critical で知らせ、1 時間ごとに再通知し、台帳は処理中の決済として押さえる
- [x] 突合で `Placed` と確定したエントリーに承認時の手法で保護レグを張る（S0 / S3 / S1 / 記録なし）
- [x] 本番の Program.cs で、突合が発注執行の保護の口を使う
- [x] T-10-402 / 403 / 406 / 407 / 408 が緑のまま

## テストへの写像

| テスト ID | 内容 |
| --- | --- |
| T-10-1060 | エントリー同時: 逆指値が不明（届いたか不明・分類できない例外）→ 取消も成行もしない・予約 Reserved・送信結果待ちの記録・`StopDispatchIndeterminate` |
| T-10-1061 | エントリー同時: 確実に未発注（接続不能・確認できた拒否）→ 従来どおり・予約は解放 |
| T-10-1062 | エントリー同時: 受理 → 予約は注文 ID つきで確定（3 相） |
| T-10-1063 | ガード: #853 追記の再現（逆指値が不明・成行は未発注）で 3 巡回の送信が 1→1→1・成行 0 |
| T-10-1064 | ガード: 確実に未発注の逆指値は従来どおり成行へ・予約は解放 |
| T-10-1065 | ガード: 送信結果待ちに記録が現れたら注文 ID を採用する（生きている→Placed／失効→次の試行） |
| T-10-1066 | ガード: 予約が消えた（解放）送信結果待ちは未発注として再発注する |
| T-10-1067 | ガード: 据え置き中に建玉が消えても記録を完了させない（否定形） |
| T-10-1068 | ガード: 予約が既にある（送信中に停止した）→ 送らずに送信結果待ちへ・通知 |
| T-10-1069 | 事前記録: S0 / S3 は送る前に `AwaitingEntry`・巡回対象外・結果に応じて上書き／完了。S1 / S2 は不変 |
| T-10-1070 | 突合の保護: `AwaitingEntry` の S0 / S3 に承認時の手法で張る（受理・据え置き・拒否） |
| T-10-1071 | 突合の保護: S1 は配置の通知だけ・記録なしは張らない・終端で約定なしは不要・終端で約定ありは約定数量 |
| T-10-1072 | 突合: 保護の口の失敗でもエントリーの発行を失わない・競合では呼ばない・自己修復では呼ぶ |
| T-10-1073 | 本番の組み立て: Program.cs の突合が発注執行の保護の口で逆指値を張る |
| T-10-1074 | 通知: `StopDispatchIndeterminate` は Critical・「失敗」と言わず重ねる前の確認を求める |
| T-10-1075 | 監査: `StopDispatchIndeterminate` の要約 |
| T-10-1076 | 台帳: `StopDispatchIndeterminate` のレグを S0 の由来で承認行にする |
| T-10-1077 | ガード常駐: 発行に失敗した `StopDispatchIndeterminate` の通知の記憶を捨てる |
| T-10-1078 | 突合の常駐: 保護の結果を種類ごとに記録し、イベントを発行する |

既存テストの追随（裁定で意味が変わったもの。理由はテストのコメントに残した）:

- `逆指値未受理でエントリー未約定なら取り消す`・`S3の拒否は未約定ならエントリーを取り消す`・T-10-751: 「保護記録が無い」→「承認時の文脈は Completed・Active は 0 件」（巡回の対象に入らない、は不変）
- `逆指値の発注例外も未受理と同じ分岐に入る`: 分類できない例外（Throw）の行を外した（T-10-1060 が据え置きを固定する）
- 不変条件（プロパティ）: 人手対応の集合に `StopDispatchIndeterminate` を足した
- ガードの `ThrowOnStopPlace`: 接続確立の失敗（確実に未発注）を投げる形へ（従来の期待＝成行へ進む、を保つ）
- T-10-609 / 650 / 653: Critical の部分文字列を「この時点では保護レグがありません」へ（発行より先・1 件 1 行、は不変）

## 残余リスク

- 据え置きの解決は突合（配備: 滞留 2 時間・間隔 1 時間）頼みであり、**最大約 3 時間、保護の有無が分からない建玉が残る**。
  通知が「証券会社の画面で確認」を求める。ガードからの直接照会（remark）は採らなかった（照会の重複実装・OpenD の照会頻度制限）。
- 解放の門が閉じている配備では、突合が「未発注」と答えても据え置きが続く（人が証券会社の画面で確かめ、手で逆指値を置く・予約を解放する）。
- エントリーの予約を取った直後・事前記録の前にプロセスが止まると文脈が残らない（突合は「記録なし」の Critical を出す）。
- 逆指値の送信中にプロセスが止まると、エントリー同時の経路では保護記録が無く予約だけが残る（従来の穴と同じ幅。突合は「記録なし」の Critical を出す）。
- 送信結果待ちの行に乖離の取り込みが当たると、取消が失敗し主張を減らさない（安全側）。
