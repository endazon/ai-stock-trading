---
title: IADR-0389 受理で完了させた S1 の保護記録を、約定追跡が「確認できた終端かつ未約定」を観測したときだけ再武装する
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-12, UC-02, ADR-0040, ADR-0016, IADR-0344, IADR-0113, IADR-0210, IADR-0118, IADR-0057, IADR-0357]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1 の S1)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10)
---

# IADR-0389: 受理で完了させた S1 の保護記録を、確認できた「終端かつ未約定」でだけ再武装する

- 状態: Accepted
- 日付: 2026-09-23
- 決定者: claude（起票 [#833](https://github.com/endazon/ai-stock-trading/issues/833) 項目 1。利用者レビューは PR で受ける）

## 起点・関連

- 関連する計画書 ID: **ADR-0040 決定 1 の S1**、FR-10、FR-12、UC-02。
- 対象 Issue: [#833](https://github.com/endazon/ai-stock-trading/issues/833) の **項目 1 のみ**（項目 2・3 は残す。項目 4 は着地済み）。
- 関連する実装仕様書: [20260923_833_rearm-accepted-close-not-filled](../specs/20260923_833_rearm-accepted-close-not-filled.md)
- 関連 IADR: [IADR-0344](IADR-0344_s1-software-stop-loss.md)（S1 本体。**本 IADR は決定 5-5 を補う**）、
  [IADR-0113](IADR-0113_moomoo-fill-polling.md)（約定追跡）、[IADR-0118](IADR-0118_broker-position-reconciliation.md)（照会の null＝不明）、
  [IADR-0210](IADR-0210_broker-side-stop-loss-unification.md)（保護記録・ガード）、[IADR-0057](IADR-0057_order-dispatch-idempotency.md)（予約）。

## コンテキストと課題

IADR-0344 決定 5-5 は「受理（`Accepted` / `PartiallyFilled` / `Filled`）→ 記録を保存し予約を確定、行を Completed、
`ClosePlaced` を発行」と決めた。**受理を終端として帳簿を閉じている。**

moomoo の模擬取引の注文は**当日限り**で、0 約定のまま失効（`Expired`）・取消（`Cancelled`）され得る。そのとき:

- 建玉は 1 株も減っていないのに `RemainingProtected = 0` / `State = Completed` になっている。
- 完了行は `FindActive` にも `FindActiveSoftwareStops` にも載らないので、**ガードも到達ハンドラも二度と見ない**。
- 約定追跡（`OrderFillPoller`）は決済レグを終端化して `OrderExecuted` を出すが、**保護記録に触らない**。
- その口座の有効な保護記録が 0 件になると、ガードは巡回対象ゼロで早期 return して建玉を照会しないため、
  帰属不明建玉の検知（IADR-0344 追記(9) 決定 3）も走らない。

→ **建玉は無保護・記録は完了・Critical はゼロ。** `ProtectiveStopGuard.cs` のコメントが自認し「追随は #880」と
書いていたが #880 は未マージであり、稼働中の PoC（AAPL 707 株・S1・ライン 338.51）がこの配置に該当する。

決めるのは 2 つ。**(a) どこで気づくか**、**(b) 気づいた後に帳簿へ何を書くか**である。

## 検討した選択肢

1. **(a-1) `ClosePlaced` の発行と帳簿の減算を「約定してから」へ遅らせる**（#833 の提案）——
   受理で押さえている取引台帳の承認行（`ProtectiveStopLedgerHandlers` が `ClosePlaced` で `AppendApproval`）が
   決済の往復ぶん遅れる。その間、過剰決済ガードは同じ建玉を二重に売れてしまう（#848 で塞いだ穴が開く）。**却下**。
2. **(a-2) 約定追跡（`OrderFillPoller`）に相乗りする**（採用）——
   非終端の記録を引き直して終端化するのは**リポジトリ中この 1 箇所だけ**であり、
   「未約定で終わった」を**ブローカーに確認できる唯一の点**である。新しい常駐を足さない。
3. **(a-3) 新しい常駐（決済レグの監視）を足す** —— 照会の重複・周期の二重管理・ガードとの競合。**却下**。
4. **(b-1) 建玉照会の純額から保護を復元する** —— IADR-0344 追記(7) が**撤去した `Restore`** そのものである。
   純額では「自分の建玉が戻った」と「他人の建玉が現れた」を区別できず、3 巡にわたり不可逆な売り過ぎを作った。**却下**。
5. **(b-2) 自分が出した決済注文の終端状態から、売れなかった株数だけを戻す**（採用）——
   純額を一切見ない。根拠は**その注文 1 件の事実**であり、他人の建玉と取り違えようがない。

## 決定

1. **`ClosePlaced` の発行時点も、受理時の帳簿の減算も動かさない**（IADR-0344 決定 5-5 は有効）。
   受理で押さえ、**未約定のまま終端したら戻す**（選択肢 a-1 を採らない）。
2. **フック点は `OrderFillPoller.PollOnceAsync` の終端化直後**（`store.UpdateOutcome` が成功した後）である。
   🔴 **`UpdateOutcome` の後に置く**——先に再武装すると `UpdateOutcome` が失敗した巡回で記録が非終端のまま残り、
   次の巡回が**同じレグで二度目の再武装**をする。
3. 🔴 **再武装してよいのは `OrderStatusLifecycle.AbandonsUnfilledRemainder(status)`
   （`Cancelled` / `Rejected` / `Expired`）だけ**である。**`Filled` は含めない**
   （`IsTerminal` を使うと全量約定でも戻ってしまう。問いは「未約定残が二度と約定しないか」であり、
   IADR-0357 がこの述語を分けた理由がそのまま効く）。
   照会が `null`（不明）・例外は**何も書かない**（`OrderFillPoller` の既存の fail-safe。IADR-0118 と同じ規律）。
4. **決済レグから保護記録を引くのに列を足さない。** `SoftwareCloseDecisionId(entry, attempt)` は決定的なので、
   記録の `Symbol` / `Market` / 反対方向で候補行を引き（`FindActiveSoftwareStops` ＋ `FindCompletedSoftwareStops`）、
   `attempt` を行の現在値から下へ最大 50 ぶん再導出して突き合わせる。
   **一致しないことは異常ではない**（約定追跡は全注文を見る。S0 の手仕舞いは `protective-close:` で名前空間が違う）。
5. **再武装の規則**: `U = 記録の発注数量 − 確定した約定数量` とし、
   `RemainingProtected = min(現在値 + U, エントリーの約定数量)`、`State = Active`。
   🔴 **上限（エントリーの約定数量。記録が無ければ行の承認数量）を必ず掛ける**——同じレグを二度観測しても
   行の主張がエントリーの建玉を超えない。**到達の記録（`TriggeredAt` / `TriggeredPrice`）は消さない**
   （IADR-0344 決定 4「一度到達したら価格が戻っても決済する」）ので、次のガード巡回が新しい試行 ID で撃ち直す。
6. **再武装は注文を 1 株も出さない。** 実際に売るのは従来どおり `SoftwareStopExecutor.TryCloseAsync` であり、
   そこは送る前に建玉を照会し（`null` は据え置き）`ProtectiveStopNetting.ReconcileShares` を通す。
   戻した主張が実建玉より多ければ、**売る前に**外部要因の観測として削られる（IADR-0344 追記(7)）。
7. **再武装したら必ず 1 回知らせる**: `SoftwareStopExecuted(CloseUnfilled)`（**Critical**）＋ `LogError`。
   序数は末尾へ足す（`CloseUnfilled = 9`。`8` は並行 PR #918 の `StopCancelUnconfirmed` が採っている。IADR-0134 決定 2）。
8. **不明を無音にしない**: 決済レグの照会が `null` に倒れ続ける間、
   `UnresolvedCloseNotificationTracker`（プロセス内・1 時間間隔・非永続）で Critical ログを出す。
   🔴 **再武装も完了もしない**（「送ったが不明」と「確実に未約定」を絶対に混ぜない）。
   非永続なのは `HeldCloseNotificationTracker` と同じ理由である——再起動後の最初の巡回で必ず鳴らす。
9. **失敗は巡回を止めない**: 再武装は 1 レグずつ try/catch で囲み、失敗は `LogError`（Critical）に落として次へ進む。

## 理由

- **受理で押さえて、未約定なら戻す**方が、**約定まで押さえない**方より安全である。前者の失敗様式は
  「保護が一時的に過小になる（戻すまでの 1 巡回）」、後者は「同じ建玉を二重に売る」であり、後者は不可逆である。
- **約定追跡に相乗りする**のは、そこが「未約定で終わった」を**確認**できる唯一の点だからである。
  ガードは S1 行のブローカー照会をしない（IADR-0344 決定 6）ため、完了した行の決済レグを見る手段を持たない。
- **列を足さない**のは、#833 項目 3（楽観並行の版番号）が migration を伴い、同一 PR に混ぜるなと
  issue 自身がトリアージで書いているためである。決定的 ID の再導出で足りる。

## 結果

- 良い影響: 受理のまま失効・取消された決済で**建玉が無音のまま無保護になる経路が閉じる**。
  行が `Active` へ戻るので、帰属不明建玉の検知（ガードの毎巡回）も再び走る。
- 悪い影響・トレードオフ:
  - **戻るまでに最大 1 巡回（既定 30 秒）＋ブローカーが終端を返すまでの遅れがある。** 失効は閉場後に返るため、
    実際の再武装は翌営業日の建て直しではなく**その日の閉場後**に起き、次の開場まで決済は出ない（時間外の成行は拒否される）。
  - **受理 → 失効 → 再武装 → 受理 → 失効 のループに上限が無い。** 試行上限（`MaxCloseAttemptsPerTrigger = 3`）は
    **拒否**の分岐にしか無く、受理された試行は数えられていない。#833 項目 2（行ごとの待ち時間）が入るまで残る。
    ただし閉場中の再送は `Rejected` に倒れるため、そちらの上限で 3 回に収まるのが実測上の通常経路である。
  - **追跡上限（`FillPolling:MaxTrackingHours`・既定 24 時間）を過ぎた決済レグは照会対象から外れる。**
    その 1 件は再武装も Critical も受け取れない（リコンサイル・人手の領分。IADR-0074）。
  - **候補行の走査は `FindCompletedSoftwareStops` の上限 50 件・試行 50 ぶん**に区切っている。
    それを超える古いレグは引けない（実運用の試行数は 1 桁）。
- フォローアップ: #833 項目 2（バックオフ）・項目 3（楽観並行）は別 PR。#880（帰属不明建玉の常駐検知）は本 PR で
  必要性が下がるが不要にはならない（S2・人手の建玉は依然どの記録も主張しない）。

## 関連

- [IADR-0344](IADR-0344_s1-software-stop-loss.md) 決定 5-5 / 追記(7) / 追記(9)
- [IADR-0113](IADR-0113_moomoo-fill-polling.md) / [IADR-0357](IADR-0357_owner-close-market-order-cancel-path-and-expiry-notice.md)
- [作業仕様書](../specs/20260923_833_rearm-accepted-close-not-filled.md)

## ［2026-09-25 追記 / #833 項目2］受理 → 失効 → 再武装のループに待ち時間を掛けた

§結果の「**受理 → 失効 → 再武装 → 受理 → 失効 のループに上限が無い**」は、[IADR-0344](IADR-0344_s1-software-stop-loss.md)
追記(15) の行ごとの待ち時間で塞いだ。**本 IADR の決定は 1 つも覆らない**（再武装の条件・量・上限・通知は同じ）。変わったのは 2 点である。

1. **1 株も約定しなかった再武装は「続けて売れなかった」1 回として数える**（`CloseFailures` を 1 増やし、
   `NextCloseAttemptAt` を「今＋待ち時間」にする）。待ち時間は min(30 秒 × 2^(n−1), 15 分)。
   ループは止まらないが、撃ち直しの間隔が育つ（出口は塞がない）。
2. **1 株でも約定した再武装は前進として数えを 0 へ戻す**（残りはすぐ撃ってよい）。

決定 5 の「次のガード巡回が新しい試行 ID で撃ち直す」は、0 約定の再武装では**待ち時間の後の巡回**になる。
`CloseUnfilled` の通知文・監査の結末文も「待ち時間の後に撃ち直す」へ改めた（「次の巡回で撃ち直す」は偽になるため）。
作業仕様書 [20260925_833_software-stop-close-backoff](../specs/20260925_833_software-stop-close-backoff.md)。
