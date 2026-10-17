---
title: IADR-0538 据え置いた「未発注」判定の Warning を同じ予約について初回と 24 時間ごとに畳み（継続は DecisionId つきの Information）、発注の記録が無いまま未確定で残る S1 を「幽霊の疑い」として識別して後始末へ案内する。行は自動で閉じない
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-10, NFR-09, ADR-0040, ADR-0045, IADR-0362, IADR-0365, IADR-0371, IADR-0444, IADR-0488, IADR-0550]
author: claude (Claude Code)
created: 2026-10-15
updated: 2026-10-17
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-05 発注・FR-10 逆指値なしの建玉を持たない)
  - planning:projects/ai-stock-trading/07_adr/ADR-0045_reservation-release-criterion-per-trading-env.md (決定 1 (b) held-not-placed の観測)
related_specs:
  - ../specs/20261015_856_held-warning-throttle-and-ghost-s1.md
---

# IADR-0538: 据え置き警告の畳み込みと「幽霊の S1」の識別（#856）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-15
- 決定者: claude が起案（起票 [#856](https://github.com/endazon/ai-stock-trading/issues/856) の 2026-10-14 のコメント。レビューは PR で受ける）

## 起点・関連

- 事象: 2026-10-05 の故障注入（`BeforeSend`。IADR-0488）で残った AAPL の予約（DecisionId `4908458f-…`）と S1 の記録が 10 日間残り、
  突合の Warning `…照会は「未発注」と答えましたが…据え置きます…` が**毎時**、S1 の要約（`残保護=未確定`）が 5 分ごとに出続けている。
  片付けは SQL 2 行で、予約を Forgone にするか DELETE にするかは利用者の判断待ちである。
- 関連 IADR:
  - [IADR-0362](IADR-0362_reservation-reconciliation-enabled-with-release-gate.md) 決定 1（据え置きを「警告でログする。無音にしない」）。本 IADR は**無音にしない**を保ち、Warning の頻度だけを変える。
  - [IADR-0371](IADR-0371_reconciliation-per-item-emission.md)（確定した 1 件の出口）。Critical の経路は対象外（1 件につき 1 回しか出ない）。
  - [IADR-0444](IADR-0444_release-gate-per-trading-environment.md) 決定 6（計器は予約の取引環境ごと）。計器は変えない。
  - [IADR-0365](IADR-0365_s1-stop-evaluation-liveness-summary.md) 決定 5（S1 の要約）。要約の頻度は変えず、印と Warning を足す。
- 作業仕様書: [20261015_856_held-warning-throttle-and-ghost-s1](../specs/20261015_856_held-warning-throttle-and-ghost-s1.md)（実測の表・母集合・窓の表・変異）。

## コンテキスト

1. 解放の門が閉じている配備では、「未発注」と判定された予約は `Reserved` のまま毎巡回載り直す。`Reserved` は保持期間のパージの対象外で、
   人が SQL で片付けるまで**永久に**同じ Warning が出る。Warning の意味（人が見よ）が薄まり、ほかの Warning を埋もれさせる。
2. ただし、この行の `DecisionId` は門を開ける判断の記録 (b)（運用仕様書）の入力であり、後始末の確認手順も grep する。消してはならない。
3. 送られていない新規建ての S1 の記録は、ガードが「これから約定し得る」とみなして据え置き、損切りラインに届くまで閉じない。
   要約には `残保護=未確定（エントリー約定待ち）` とだけ出て、正常な約定待ちと区別が付かない。閉じる手順も runbook に無かった。
4. 行を自動で閉じる・消すのは、二重発注の防止（予約）と無保護の建玉の防止（S1）に触れる。取引の安全と利用者の判断の領域である。

## 決定

### 決定 1: 据え置き Warning は同じ予約について初回と再通知の間隔ごとだけにする。継続は Information に必ず残す

- 同じ `DecisionId`・同じ取引環境の `HeldNotPlaced` が途切れずに続くあいだ、Warning は初回と `Reconciliation:HeldNotPlacedReminderHours`
  （既定 24・下限 1・上限 168）ごとにだけ出す。間の巡回は `DecisionId` つきの Information `…据え置きが続いています…` を出す。
- **窓の両端を突き合わせる**（規則 11）: 状態が変わった予約（取引環境の変化・別の判定・全件走査の巡回に載らない）は記憶を消し、
  次の据え置きを初回として Warning にする。続いていても間隔が過ぎたら Warning にする。バッチが満杯の巡回では載らなかった予約の記憶を消さない。
- 記憶はプロセス内。再起動後の最初の巡回は Warning（安全側）。
- 計器（`held-not-placed`）は今までどおり毎巡回数える。Critical の経路は畳まない。
- Warning の文面に後始末の手順（発注経路の Runbook の節）への案内を足す。既存の文言（`照会は「未発注」と答えましたが…据え置きます`）は変えない（grep の互換）。

### 決定 2: 発注の記録が無いまま未確定で残る S1 を「幽霊の疑い」として識別する。閉じない

- 定義: S1・`Active`・`RemainingProtected` が NULL・`TriggeredAt` が NULL・作成から `ProtectiveStopGuard:UnrecordedSoftwareStopAge`
  （既定 24 時間）以上・エントリーの発注記録（`executed_orders`）が無い。
- 要約の該当行に `発注記録なし（幽霊の疑い）` を付け、Warning を同じ行について初回と `UnrecordedSoftwareStopReminderInterval`（既定 24 時間）ごとに出す
  （Runbook「発注の記録が無いまま残った S1 の記録を閉じる」への案内つき）。該当しなくなった行は記憶を消す。
- 発注記録の照会は候補（未確定・未到達・経過済み）にだけ、要約の間隔に 1 回行う。
- 🔴 **行の状態は一切変えない**。閉じるのは利用者の判断と Runbook の SQL である。
- ［2026-10-17 追記 / #1343］解放の門が**開いている**取引環境では、突合が未発注と確定した時点で予約を Forgone にし、S1 の行・AwaitingEntry を閉じる（[IADR-0550](IADR-0550_release-forgoes-approval-reservations-and-closes-entry-protection.md)）。本決定（識別だけで閉じない）は門が閉じているあいだと、突合が閉じられなかった行について引き続き効く。決定 3 の予約の扱いは、Runbook では Forgone を標準にし DELETE は取らない形へ改めた（IADR-0550 決定 3）。

### 決定 3: Runbook に S1 の記録を閉じる手順を置く。予約の扱いは 2 案を並べ、利用者が選ぶ

- 読み取りで確かめる → 証券会社の画面で注文が無いことを確かめる → 1 つのトランザクションで「予約が先・S1 が後」に閉じる → 確かめる → 戻し方。
- 予約は Forgone（推奨。再投入されても発注しない）と DELETE（既存の手順。`_error` の承認の再投入で古い指値が送られる）を並べる。

## 採らなかった案

- **据え置き Warning を完全に抑える（Information も出さない）**: 観測 (b) の入力と後始末の確認の手掛かりが消える。
- **畳んだ巡回を Debug に落とす**: 既定のログレベルでは残らず、上と同じ。
- **幽霊の S1 を自動で閉じる・予約を自動で Forgone にする**: 取引の安全（二重発注・無保護の建玉）に触れ、利用者の判断事項である。
  #856 のコメント §6 の欠陥 1（解放時に保護の記録が閉じない）は設計を要し、本件の範囲外。
- **記憶を DB に持つ**: 再起動で Warning が 1 回増えるだけであり、移行を足すほどの利得が無い。

## 結果

- 2026-10-05 の AAPL のような据え置きでは、Warning は 1 日 1 回、継続は毎時の Information になる。計器と DecisionId の記録は変わらない。
- 送られていない新規建ての S1 は、作成から 24 時間後に要約と Warning で識別でき、Runbook の手順で片付けられる。
- 残余リスク: 再通知の間隔のあいだに同じ予約の状態が「据え置き」のまま別の意味を持つ変化（例: 証券会社側で注文が後から現れた）は
  照会の判定が変わらない限り検知しない。これは畳み込みの前も同じ（判定が同じなら同じ文面）である。
