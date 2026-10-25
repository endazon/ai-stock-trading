---
title: 予約の解放（門・人手）で新規建ての保護の記録が閉じず、同じ銘柄の別手法の新規建てを見送り続ける（#1343）
type: spec
status: done
related_ids: [FR-05, FR-10, NFR-09, ADR-0040, ADR-0045, IADR-0057, IADR-0211, IADR-0344, IADR-0362, IADR-0398, IADR-0428, IADR-0444, IADR-0481, IADR-0488, IADR-0538, IADR-0551]
author: claude (Claude Code)
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-05 発注・FR-10 逆指値なしの建玉を持たない・NFR-09 未確定データの保持)
  - planning:projects/ai-stock-trading/07_adr/ADR-0045_reservation-release-criterion-per-trading-env.md (解放の基準)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (S0〜S3)
---

# 仕様書: 予約の解放で新規建ての保護の記録を閉じる（#1343）

> 本仕様書は実装着手前に作成した（着手 2026-10-17）。基点は AST `origin/develop` `31b1a087`。
> `git rev-parse --is-shallow-repository` → `false`。

## 起点

- [#1343](https://github.com/endazon/ai-stock-trading/issues/1343)（#856 の 2026-10-14 のコメント「BeforeSend の後始末の手順を見直した」で発覚）。
- 事象 1: 門による解放（`OrderReservationReconciler.cs:173-187`）は予約行を消すだけ。送る前の見送り（`OrderExecutionAppService.cs:421-427`）は
  S1 の行と AwaitingEntry を閉じる。ガードは発注記録の無い未確定の S1 を永久に据え置く（`ProtectiveStopGuard.cs:299-301`）。
  その結果、同じ銘柄・同じ方向の別の手法の新規建てを見送り続ける（`StopLossMethodCoexistenceGate.cs:49-56`。AwaitingEntry も `State != Completed` で載る）。
- 事象 2: 解放した新規建ては `_error` の再投入でしか送られず、再投入では承認の古さを確かめない（止めるのは Forgone だけ。`OrderExecutionAppService.cs:116-123`）。
- 事象 3: runbook と IADR-0488 の残余に、発注記録の無い S1 を閉じる手順が無い（runbook は #856 の後続 PR〔IADR-0538〕で S1 の節が足されたが、AwaitingEntry と門が開いたときの形は無い）。

## 計画との照合

- NFR-09・ADR-0045 は「照合が未発注として解放（再発注の許可）してよいのは、取引環境ごとに実機の記録で基準を満たした後」と定める。
  **解放が再発注を許可しなければならないとは定めていない。** 解消は「照合または利用者の判断による」。
  本件は門の開閉・基準を変えず、門が開いたときに**承認の経路の予約を再発注させない側**（より安全側）へ寄せる。計画 ADR に反しない。
- ただし、計画の文面の「解放（再発注の許可）」は、承認の経路の予約については実装と食い違う（実装は見送りとして閉じる）。**環流の要否を報告する**（未決事項）。
- ADR-0040: S1〜S3 の扱い・本番での拒否は変えない。

## 母集合（規則 9・10）

「解放」を誤りの側の語として全文書を走査した（`grep -rn "Release(\|解放（\|再発注の許可\|予約行を削除\|予約行を消" backend docs .ai-context/adr`）。

| 箇所 | 扱い |
| --- | --- |
| `OrderReservationReconciler.cs`（`reservations.Release`） | 是正の本体 |
| `ProtectiveStopGuard.cs:494/538/570`・`SoftwareStopExecutor.cs:488`・`OrderExecutionAppService.cs:928/972/1279` の `Release` | **対象外**。機構自身が「確実に未発注」を見て自分のレグの予約を消す（撃ち直すため） |
| `IOrderReservationStore.cs`（`Release`・`MarkReservationForgone` の契約） | 呼び出し側の追加を書く |
| `docs/operations/operations.md` §発注予約の自動リコンサイル ③・解放の門・幽霊の S1 の Warning | 追随 |
| `docs/operations/broker-execution-paths-runbook.md` §自動リコンサイル・滞留した予約を人が解決する・S1 の記録を閉じる・故障注入の後始末 | 追随（事象 3） |
| `docs/tests/FR-10_risk-controls-tests.md` T-10-601・T-10-1554 | 追随（期待が変わる） |
| IADR-0488 §残余・IADR-0538 決定 2 / 採らなかった案 | 日付つき追記（凍結記録の本文は書き換えない） |
| `docs/tests/FR-10` T-10-600 節の地の文「解放は再発注の許可である」 | 門を閉じる理由の説明であり、機構のレグには今も当てはまる。**変えない** |
| 保護逆指値ガードのレグを解放する試験（`ProtectiveStopGuardIndeterminateCloseTests` T-10-601 相当） | 対象外（機構のレグは削除のまま。回帰で緑を確かめる） |

## 方針

1. **解放の形を予約の出どころで分ける**（IADR-0551 決定 1）。
   - 承認の経路＝`ApprovalOrigin` が非 null（相 2 だけが書く）か、`protective_stop_orders` に `EntryDecisionId` がその DecisionId の行がある（列を足す前の行・AAPL の形）。
     → `MarkReservationForgone`（Reserved → Forgone）。再投入は相 1 の直後の Forgone の抑止で送らない（事象 2 を、解放した経路について閉じる）。
   - それ以外（保護の機構のレグ）→ 従来どおり `Release`（削除）。ガード・S1 が同じ DecisionId で撃ち直すため。
2. **予約を Forgone にした後に保護の記録を閉じる**（決定 2）。S1 は未確定・未到達・試行 0・外部要因の観測なしのときだけ、AwaitingEntry はそのまま、
   楽観並行で `Completed` へ。発注の記録がある・それ以外の状態は閉じない（LeftOpen）。失敗しても予約は巻き戻さない。結果は巡回サマリで Information / Warning。
3. 承認の古さの検査（再投入の時刻と承認時刻の差で断る）は**足さない**（決定 3）。しきい値が計画に無く、Forgone で解放の経路は閉じる。
   人手の後始末は runbook で Forgone を標準にする（DELETE は取らない）。
4. 門の値・helm の描画・計器の outcome 名は変えない（`released` のまま）。
5. ［2026-10-17 追記 / PR のレビュー 🟡］予約が Forgone の取り残し（AwaitingEntry を含む）は、新規建ての併存の判定の前に閉じる（IADR 決定 4。T-10-2715）。
   順序（予約が先）は、見送りの書き込みが落ちたら記録を閉じないことで固定する（T-10-2714）。

## 受け入れ基準（試験への写像）

| # | 基準 | 試験 |
| --- | --- | --- |
| 1 | 門による解放で S1 / AwaitingEntry が閉じ、予約は Forgone | T-10-2703・T-10-2704・T-10-1554 |
| 2 | 同じ銘柄の別手法の新規建てが塞がれなくなる（門が閉じていれば塞がれたまま） | T-10-2705 |
| 3 | 再投入しても古い承認では発注しない | T-10-2706・T-10-2707 |
| 4 | 承認の経路の決済も Forgone・機構のレグは削除のまま | T-10-2708・T-10-2709・既存の ProtectiveStopGuard 系 |
| 5 | 到達済みの S1・門が閉・判定不能・閉じる処理の失敗・見送りの失敗・判別の失敗では壊さない | T-10-2710〜T-10-2714・T-10-2716 |
| 6 | runbook に発注記録の無い S1（と AwaitingEntry）を閉じる手順・門が開いたときの形 | 文書（trace ブロック規約） |
| 7 | 見送り済みの取り残しは新規建ての判定の前に閉じる | T-10-2715 |

変異 UF1〜UF7 は `docs/tests/FR-10_risk-controls-tests.md` の節に記録した（すべて赤）。

## 窓（規則 11）

本件の窓は「予約を Forgone にしてから保護の記録を閉じるまで」。順序は予約が先（逆だと、記録を閉じた後に予約の更新が落ちて、
Reserved のまま突合が後から「発注済み」と確定したとき保護の無い建玉ができる）。窓の中で止まった場合は記録が残り、S1 は幽霊の疑いの警告（IADR-0538）が拾う。
窓の両端を突き合わせる形（記録を先に閉じて予約が落ちたら戻す）は、戻しが落ちたときに同じ害が出るため採らない。
この順序は、予約の見送りの書き込みを落とす注入で固定した（T-10-2714。見送りが落ちたら記録は閉じない）。

## PoC での確認

- 配備しても門は閉じたまま（`Reconciliation__ReleaseOnNotPlaced__Simulate=false`）なので、配備だけでは挙動は変わらない。
- #856 の AAPL の後始末（オーナーが DB で予約を Forgone・S1 を Completed）は、本件の門が開いたときの形と同じであり矛盾しない。後始末の前に配備しても、
  門が閉じている限り本件のコードは AAPL の行に触れない。
