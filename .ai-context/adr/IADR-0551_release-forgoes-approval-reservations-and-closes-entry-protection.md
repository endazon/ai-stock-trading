---
title: IADR-0551 突合が「未発注」と確定して解放の門が開いていたとき、承認の経路の予約は削除せず見送り（Forgone）として閉じ、続けて送る前に残した保護の記録（S1 の未確定・未到達の行／S0・S3 の AwaitingEntry）を完了にする。保護の機構のレグは従来どおり削除する
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-10, NFR-09, ADR-0040, ADR-0045, IADR-0057, IADR-0074, IADR-0211, IADR-0344, IADR-0362, IADR-0398, IADR-0428, IADR-0444, IADR-0481, IADR-0488, IADR-0515, IADR-0538]
author: claude (Claude Code)
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-05 発注・FR-10 逆指値なしの建玉を持たない・NFR-09 未確定データの保持)
  - planning:projects/ai-stock-trading/07_adr/ADR-0045_reservation-release-criterion-per-trading-env.md (解放の基準)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (S0〜S3)
related_specs:
  - ../specs/20261017_1343_release-closes-entry-protection.md
---

# IADR-0551: 解放の門で閉じる新規建ては見送りとして終端にし、保護の記録も閉じる（#1343）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-17
- 決定者: claude が起案（起票 [#1343](https://github.com/endazon/ai-stock-trading/issues/1343)。レビューは PR で受ける）

## 起点・関連

- 事象: 門による解放（`OrderReservationReconciler`）は予約行を消すだけで、送る前に残した保護の記録を閉じなかった。
  S1 の行は発注の記録が無いまま「これから約定し得る」として永久に据え置かれ（`ProtectiveStopGuard` の未確定の分岐）、
  同じ銘柄・同じ方向の別の手法の新規建てを見送り続ける（`StopLossMethodCoexistenceGate`。AwaitingEntry も同じ）。
  消した予約の承認は `_error` の再投入でしか送られず、再投入は承認の古さを確かめない（止めるのは Forgone だけ。IADR-0398）。
  実例は 2026-10-05 の BeforeSend の注入で残った AAPL（#856）。門は閉じているので実害はまだ無いが、**SIMULATE の門を開けると日常的に起きる**。
- 関連 IADR:
  - [IADR-0398](IADR-0398_forgone-decision-never-redispatched.md)（見送った DecisionId は再配送されても発注しない）。本 IADR はその終端を突合の解放にも使う。
  - [IADR-0362](IADR-0362_reservation-reconciliation-enabled-with-release-gate.md) / [IADR-0444](IADR-0444_release-gate-per-trading-environment.md)（解放の門）。門の値・選び方は変えない。
  - [IADR-0344](IADR-0344_s1-software-stop-loss.md)（S1 は送る前に武装）・[IADR-0428](IADR-0428_protective-leg-indeterminate-hold-and-reconciled-entry-protection.md) 決定 3（S0 / S3 の AwaitingEntry）・
    [IADR-0481](IADR-0481_same-symbol-method-coexistence-fill-tracking-abandonment-provider-alignment.md)（併存の見送り）。
  - [IADR-0538](IADR-0538_held-not-placed-warning-throttle-and-unrecorded-s1.md)（幽霊の S1 の識別。「解放時に保護の記録が閉じない」は範囲外として本件へ送られた）。
- 計画: NFR-09・ADR-0045 は解放してよい**条件**を定め、解放が再発注を許可しなければならないとは定めていない。本 IADR は門の開閉・基準を変えない。

## コンテキスト

1. 予約表には 2 種類の書き手がいる。**承認の経路**（`OrderApproved` の相 2。承認の出どころ `ApprovalOrigin` を書く）と、
   **保護の機構**（ガードの逆指値・成行手仕舞い、S1 の決済。出どころは null）である。
2. 機構のレグは、解放（削除）された後に機構自身の次の巡回が同じ DecisionId で撃ち直す（`ProtectiveStopGuardIndeterminateCloseTests`）。
   撃ち直さないと建玉が無防備のまま残る。
3. 承認の経路の予約は、削除しても誰も撃ち直さない。人が `_error` から再投入したときだけ送られ、そのとき承認の古さを確かめない
   （何日も前の指値がそのまま出る）。送る前に残した保護の記録は、送る前の見送りの経路だけが閉じていた。
4. 出どころの列（#1253）を足す前の予約は null を持つ。AAPL の予約もこれだが、S1 の行（`EntryDecisionId` が予約の DecisionId）を持つ。

## 決定

### 決定 1: 承認の経路の予約は、削除せず見送り（Forgone）として閉じる。機構のレグは削除のまま

- 照会が `NotPlaced` で、その予約の取引環境の門が開いているとき（`ReleaseGatePolicy` は変えない）、予約が**承認の経路**なら
  `MarkReservationForgone`（Reserved → Forgone。`CompletedAt` に時刻）で閉じる。
- 承認の経路の判別: `ApprovalOrigin` が非 null、**または** `protective_stop_orders` に `EntryDecisionId` がその DecisionId の行がある
  （発注執行の `IReconciledEntryProtection.HasEntryProtectionRecord`）。機構のレグの DecisionId は `StopDecisionId` / `CloseDecisionId` であり、
  `EntryDecisionId` とは一致しない。判別が例外で落ちたら、その 1 件は失敗として据え置く（Reserved のまま）。
- それ以外は従来どおり `Release`（削除）。
- 件数は従来の「解放」（計器 `released`）に数える。並行に確定していて Forgone にできなかった行は数えない。
- 突合は見送り（`OrderDispatchForgone`）を**発行しない**（是正前の削除も何も発行しなかった）。リスク管理の承認の記録は解かれない。

### 決定 2: 予約を見送りにした後に、送る前に残した保護の記録を閉じる

- 閉じる口は発注執行（`IReconciledEntryProtection.CloseUnplacedEntryProtection`）。送る前の見送りが閉じるのと同じ 2 つを DecisionId だけで行う。
- 閉じてよいのは「建玉が生じていないと言える行」だけ:
  - 発注の記録（`executed_orders`）が無い。
  - S1: `Active`・`RemainingProtected` が null（未確定）・`TriggeredAt` が null（未到達）・`Attempt` が 0（決済を送っていない）・外部要因の観測なし。
  - S0 / S3: `AwaitingEntry`。
  - 楽観並行（`TrySave`）で書けたこと。
- それ以外（到達済み・発注の記録がある・S0 / S3 の `Active`・並行に更新された）は閉じない（`LeftOpen`）。例外も `LeftOpen` として返す。
  **予約の見送りは巻き戻さない。** 巡回サマリで、閉じたら Information、閉じなかったら Warning（Runbook への案内つき）を 1 件ずつ出す。
- 順序は「予約が先・記録が後」。逆にすると、記録を閉じた後に予約の更新が落ち、Reserved のまま突合が後で「発注済み」と確定したとき保護の無い建玉ができる。

### 決定 3: 承認の古さの検査は足さない。人手の後始末は Forgone を標準にする

- 再投入の時刻と承認時刻の差で発注を断る検査は足さない（しきい値が計画に無い。解放の経路は決定 1 で閉じる）。
- runbook の人手の後始末は、承認の経路の予約を Forgone へ移すことを標準にし、DELETE は取らない（機構のレグだけ DELETE）。
  新規建ての S1 の行・AwaitingEntry を閉じる SQL を置く。

### 決定 4: 予約が見送り済みの取り残しは、新規建ての併存の判定の前に閉じる（AwaitingEntry にも効く回収の手段）

- 同じ銘柄・同じ方向の新規建ての併存の判定（`HasConflictingStopLossMethod`。予約の前・送信の直前の両端）で塞いでいる記録のうち、
  **そのエントリーの予約が Forgone**（送っていないと確定した終端。IADR-0398 の契約）のものは、決定 2 と同じ条件で閉じてから数え直す。
  閉じたら Warning を残す。閉じられない・例外のときは塞いだまま（安全側）。
- 拾う取り残し: 突合が予約を見送りにした後・記録を閉じる前に止まった行（S1 も S0 / S3 の AwaitingEntry も）、送る前の見送りで楽観並行の書き込みに負けた行、
  人が予約だけを Forgone にした行。AwaitingEntry は警告を持たないが、害（別の手法の新規建てを見送らせる）が出る場面そのものでここが閉じるため、
  警告・定期点検は足さない（害の出る時点で回収するので、時間で回す点検より先に効く）。
- 予約が Reserved（送ったか不明）・Completed・行が無い記録は閉じない。

## 採らなかった案

- **すべての予約を Forgone にする**: 機構のレグを撃ち直せなくなり、無防備の建玉が残る（決定の害が大きい側）。
- **削除したまま保護の記録だけ閉じる**: 再投入されると S1 が武装されずに送られる（「行が無いときだけ武装する」は、閉じた行が残っていれば武装しない）。
  承認の古さも確かめない。
- **承認の古さの検査**: しきい値（何分前までの承認なら送るか）が計画に無く、決めるのは利用者である。Forgone で解放の経路の害は消える。
- **ガードが「予約が Forgone の S1」を自動で閉じる・AwaitingEntry の取り残しに警告や定期点検を足す**: 害（併存の見送り）が出るのは新規建ての判定の時だけであり、
  決定 4 がその場で回収する。時間で回す経路を別に持つと二重になる。S1 の取り残しは幽霊の疑いの警告（IADR-0538）でも人へ回る。
- **見送り（`OrderDispatchForgone`）を突合から発行して台帳の承認を終端にする**: 発行の経路（理由の型・Worker の出口）を足す変更で、
  統制への算入は承認した ET 日の終わりに外れるため利得が小さい。範囲外とした（残余）。

## 結果

- 門が開いた後は、BeforeSend 相当の新規建ては突合が未発注と確定した時点で予約が Forgone、S1 / AwaitingEntry が Completed になり、
  同じ銘柄の別手法の新規建てを塞がない。`_error` から再投入しても発注しない。
- 門が閉じている現在の配備では挙動は変わらない。
- 残余リスク:
  - 予約を Forgone にした後・記録を閉じる前に止まると記録が残る（予約はもう走査に載らない）。同じ銘柄・同じ方向の別の手法の新規建てが来た時点で決定 4 が閉じる。
    それまでの間、記録は残る（S1 は 24 時間後の幽霊の警告も出る。AwaitingEntry には警告が無いが、併存の判定以外に効かない）。
  - 出どころの列を持たない旧い予約のうち、保護の記録を持たない承認（決済・S2 の新規建て）は機構のレグと区別できず、削除される（是正前と同じ）。
  - 人が runbook に反して承認の経路の予約を DELETE すると、再投入で古い承認が送られる（承認の古さの検査は無い）。
  - リスク管理の承認の記録は解かれない（算入は ET 日の終わりに外れる）。
  - 計画の文面「解放（再発注の許可）」は、承認の経路の予約については実装と食い違う（環流の要否は PR で報告）。
