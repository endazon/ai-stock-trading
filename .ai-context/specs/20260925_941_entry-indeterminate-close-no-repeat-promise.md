---
title: エントリー時に送信結果が不明になった成行手仕舞いの通知から、無い再通知の約束を外す（#941）
type: spec
status: accepted
related_ids: [FR-10, FR-11, UC-02, UC-06, IADR-0117, IADR-0210, IADR-0369]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10「逆指値が未受理・失効した場合は建玉を持たない」)
---

# 仕様書: エントリー時の「届いたか不明」の通知に、無い再通知を約束させない（#941）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-10（逆指値なしの建玉を持たない）、FR-11（監査・通知）
- ユースケース（UC）: UC-02（損切り）、UC-06（手仕舞い）
- 画面（SC）: なし
- 関連 ADR: なし（計画 ADR の決定は変えない）
- 関連 IADR: IADR-0117 改定 7・改定 9（届いたか不明の据え置きと 1 時間ごとの再通知）、IADR-0210 決定 3・4、
  IADR-0369（PR #916 監査 F1 で `CloseRejected` の文面を `Cause` で分けた前例）。本作業の記録は IADR-0369 への日付つき追記
- 計画書リンク: 上記 plan_refs

## 目的・背景

`NotificationFormatter.From(ProtectiveStopCoverageLost)` の `CloseDispatchIndeterminate` の分岐は `Cause` を見ず、
常に「この通知は予約が解決されるまで約 1 時間ごと（と再起動のたび）に繰り返します」と書く。

`Cause = RejectedAtEntry` の経路（`OrderExecutionAppService.CloseUnprotectedPositionAsync` →
`IndeterminateClose`）では、これは事実でない。コードで確かめた事実:

1. **保護記録を作らない。** `protectiveStops.Save` を呼ぶのは逆指値が受理された分岐（`PlaceProtectiveStopAsync` の
   受理側）と S1 の行の武装・完了だけで、`ResolveUnprotectedEntryAsync` / `CloseUnprotectedPositionAsync` / `IndeterminateClose` は呼ばない。
2. **巡回しない。** 1 時間ごとの再通知は `HeldCloseNotificationTracker` だけが行い、それを使うのは
   `ProtectiveStopGuard`（`HoldIndeterminateClose` と入口 (b) の `RenotifyHeldCloseIfDue`）だけである。
   ガードは `stops.FindActive` が返す保護記録しか評価しない（1 件も無ければ建玉の照会もせずに戻る）。
3. **再起動・再配送でも出し直さない。** エントリーの `ExecutionRecord` は保護レグより前に保存・確定されるため、
   同じ `OrderApproved` の再配送は相 1（既存結果の再発行）で返り、保護喪失イベントは再発行しない（コードの注記どおり）。
4. `OrderReservationReconciler` は滞留した予約を拾うが、`ProtectiveStopCoverageLost` は発行しない
   （発注済みと確定したときの `OrderExecuted` と件数だけ）。

したがってこの通知は 1 回きりであり、次の 1 時間ごとの通知を待つ運用者には沈黙しか届かない。
PR #916 の F1（`CloseRejected` の同型の偽りの約束）と同じ壊れ方である。

あわせて、拒否の数えが戻る契機を「手仕舞いの**約定**」と書いている箇所を「**受理**」へ直す。
`CompleteAsClosed` は成行の戻り値が `Cancelled` / `Rejected` / `Expired` でない（＝`Accepted` / `PartiallyFilled` /
`Filled`）とき、および記録済みの手仕舞いレグが同じく終端でないときに呼ばれ、そこで `_closeRejections.Forget` する。
約定は待たない。

## 対象範囲

- 対象:
  - `NotificationFormatter` の `CloseDispatchIndeterminate` の本文を `Cause` で分ける
  - 「約定」→「受理」の文言: `CloseRejectionTracker.cs` の冒頭、`ProtectiveStopGuard.MaxConfirmedCloseRejections` の注記、
    IADR-0369 の追記表と索引行、#857 の仕様書（IADR・仕様書は日付つき追記で直し、本文は書き換えない）
  - 機能仕様（`docs/functional/FR-10_risk-controls.md`）の「届いたか不明」の行が経路を区別せずに再通知を述べている点
- 対象外:
  - エントリー時の経路に巡回・再通知を**足す**こと（保護レグを持たない建玉の扱いは #853 の射程。IADR-0117 改定 9 の
    「塞がないもの」と IADR-0369 残余リスク 3 項目めのとおり）
  - 監査要約（`AuditEntryFactory`）: 繰り返しを述べていないので変えない
  - #938（`CloseRejectionTracker` の記憶が他の完了経路で消えない・None が手仕舞い件数に混ざる）

### 是正の母集合（自分で引いた結果と除外理由）

誤りの側の文字列で走査した（`traceability.repo.md` 規則 2・9・10）。

`grep -rn "1 時間ごと\|1時間ごと" backend docs --include=*.cs --include=*.md`（テストを除く）:

| 箇所 | 判定 |
| --- | --- |
| `NotificationFormatter.cs` の `CloseDispatchIndeterminate`（「約 1 時間ごと（と再起動のたび）に繰り返します」） | **是正**（本作業の中心） |
| `NotificationFormatter.cs` の `CloseRejected`（滞留側の文） | 除外: PR #916 で既に `Cause` で分かれている |
| `ProtectiveStopGuard.cs` の注記 3 箇所 | 除外: ガード（滞留側）の挙動の記述で事実どおり |
| `docs/data/audit-events.md`（「複数回残り得る」） | 除外: 可能性の記述で偽ではない（ガード経路で起きる） |
| `docs/functional/FR-10_risk-controls.md` の「届いたか不明」の行 | **是正**: 経路を区別せず「約 1 時間ごと…出し直す」と言い切っている。エントリー時は出し直さないことを足す |
| `docs/operations/broker-execution-paths-runbook.md` の「保護逆指値ガード: …」の行 | 除外: ガードのログ行の説明で、ガード経路に限られる |
| `docs/tests/FR-10_risk-controls-tests.md` の T-10-409 / T-10-451 / T-10-636 / T-10-684 | 除外: ガードの巡回（④以降）または T-10-684 の否定形の記述 |

`grep -rn "手仕舞いの約定" .ai-context docs backend`（テスト名の同語は意味が別なので除外）:
IADR-0369 本文の表・`.ai-context/adr/README.md` の索引行・#857 の仕様書・`CloseRejectionTracker.cs`・`ProtectiveStopGuard.cs`
の 5 箇所。すべて是正する（issue が挙げた箇所と一致した）。

## 設計

- `CloseDispatchIndeterminate` の本文は前半（送信した・届いたか不明・重ねない・重ねる前に確かめる）を共通に保ち、
  後半を `Cause` で分ける。
  - `LapsedInFlight`: 従来どおり（約 1 時間ごと・再起動のたびに繰り返す／同じ CloseDecisionId は同じ 1 本の成行）。
  - `RejectedAtEntry`: 「エントリー時の経路には保護記録が無く、システムはこの建玉を巡回しません。この通知も繰り返しません
    （届くのはこの 1 回だけです）。」＋「証券会社の画面で手仕舞いの注文と建玉を確かめ、手仕舞いの注文が生きておらず建玉が
    残っていれば、手で手仕舞ってください。」再通知の説明文（同じ CloseDecisionId は…）は付けない（繰り返しが無いため）。
  - 件名は変えない（両経路とも「結果が未確認」で事実どおり）。
- 文面にはシステムが**しない**ことだけを書き、突合（リコンサイル）が後から解決し得るかどうかは書かない
  （配備の構成に依存し、本通知の射程外。書かないことは偽の約束にならない）。

## 受け入れ基準

- [x] `RejectedAtEntry` の `CloseDispatchIndeterminate` 通知が「1 時間ごと」「再起動のたび」「予約が解決されるまで」を述べず、
  「巡回しません」「この通知も繰り返しません」「この 1 回だけ」「手で手仕舞って」を述べる（否定形のテスト）
- [x] `LapsedInFlight` の文面は変わらない（T-10-451 の約束は残る。対の表明）
- [x] エントリー時の経路は保護記録を作らず、1 時間後のガードの巡回も同じ承認の再配送もこの通知を出し直さない（テスト）
- [x] 「手仕舞いの約定」の 5 箇所が「受理」へ直る（IADR・仕様書は日付つき追記）

## テスト方針

| ID | 対象 |
| --- | --- |
| T-10-750 | エントリー時の `CloseDispatchIndeterminate` の通知が再通知・巡回を約束せず「この 1 回だけ・手で確かめて手仕舞う」と言う（否定形）。滞留側の約束は T-10-451 のまま残る（対の表明） |
| T-10-751 | エントリー時に成行手仕舞いが届いたか不明で終わったとき、保護記録は無く、1 時間後のガードの巡回はイベントを 1 件も出さず、同じ承認の再配送も保護喪失を出し直さない（文面の主張の根拠をコードで固定する否定形） |

各テストはコミット後に変異（文面を無条件の約束へ戻す／エントリー時に保護記録を保存する）で赤になることを確かめる。

## 計画書との差異

- 差異: なし（通知の文面を実装の事実に合わせる是正）。

## 未決事項

- なし。
