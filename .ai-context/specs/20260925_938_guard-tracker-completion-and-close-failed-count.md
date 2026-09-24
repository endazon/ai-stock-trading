---
title: 保護逆指値ガードの記憶を完了した記録について捨て、確実に未発注の手仕舞い失敗を「手仕舞い」件数に混ぜない（#938）
type: spec
status: accepted
related_ids: [FR-10, FR-11, UC-02, UC-06, IADR-0117, IADR-0210, IADR-0369, IADR-0370]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10「逆指値が未受理・失効した場合は建玉を持たない」)
---

# 仕様書: ガードの記憶の後始末と、手仕舞い失敗の件数を分ける（#938。PR #916 監査 F4・F5）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-10（逆指値なしの建玉を持たない）、FR-11（監査・可観測性）
- ユースケース（UC）: UC-02（損切り）、UC-06（手仕舞い）
- 画面（SC）: なし
- 関連 ADR: なし（計画 ADR の決定は変えない）
- 関連 IADR: IADR-0369 決定 3（`CloseRejectionTracker`）・決定 5（件数を混ぜない）、IADR-0117 改定 9
  （`HeldCloseNotificationTracker`）、IADR-0370（乖離の取り込みが保護記録を終端化する経路）。
  本作業の記録は IADR-0369 への日付つき追記（新しい IADR は起こさない——決定 3・5 の作法をそのまま広げるだけで、新しい判断が無い）
- 計画書リンク: 上記 plan_refs

## 目的・背景

### F4: 記録を完了させても、プロセス内の記憶が残る

`CloseRejectionTracker`（singleton・非永続。`EntryDecisionId` ごとの拒否の数えと通知時刻）は、`Forget` を
`Replaced`（逆指値の再発注に成功）と `CompleteAsClosed`（手仕舞いが受理された）でしか呼ばない。
ガードが記録を `Completed` にする他の経路——建玉消滅で残存逆指値を取り消す／逆指値が `Filled`／失効かつ建玉残 0——
では消えない。典型は「成行が 1〜3 回拒否された後、利用者が証券会社の画面で手仕舞った」場合で、次の巡回が
建玉消滅の分岐で記録を完了させても、その `EntryDecisionId` の記憶は再起動まで残る。

`EntryDecisionId` はエントリーごとに一意なので、残った記憶が別の建玉の判断に効くことは無い（誤動作ではない）。
影響はプロセスの寿命のあいだ辞書が単調に増えることである。

### F5: 確実に未発注の手仕舞い失敗が「手仕舞い」に数えられる

`ReplaceOrCloseAsync` の末尾は、成行手仕舞いが `BrokerUnavailableException`（確実に未発注）で失敗したとき
（および発注先が成行の能力を持たないとき）、`ProtectiveStopCoverageLost(Remediation=None)` を出したうえで
`Outcome.ClosedOut` を返す。常駐の巡回ログは `ClosedOut` を「手仕舞い {ClosedOut}」と出すため、**手仕舞えていない建玉が
「手仕舞い」の件数に入る**。記録は `Active` のままで、次の巡回が撃ち直す。通知（Critical・「建玉の解消にも失敗しました」）は
正しく出ている。壊れているのは件数（可観測性）だけである。

## 対象範囲

- 対象:
  - `ProtectiveStopGuard`: 記録を完了させる全経路での記憶の後始末（F4）、`Remediation=None` の件数の分離（F5）
  - `HeldCloseNotificationTracker`: 同型の残りの是正（issue が「あわせて引く」とした分）
  - `ProtectiveStopGuardResult` への件数の追加（末尾・既存の位置は動かさない）と巡回ログ
- 対象外:
  - S1 の記憶（`SoftwareStopExecutor` / `UnresolvedCloseNotificationTracker`）: 別の機構・別の記憶であり、
    `CloseRejectionTracker` と `HeldCloseNotificationTracker` は S1 の行について何も覚えない（下の母集合を参照）
  - 記憶を永続化すること（IADR-0369 決定 3・IADR-0117 改定 9 のとおり非永続のまま）

### 是正の母集合（自分で引いた結果と除外理由）

**(1) 保護記録を `Completed` にする全経路**（`grep -rn "ProtectiveStopState.Completed" backend/Services/OrderExecutionService --include=*.cs`、テスト・読み取りを除く）:

| 箇所 | 記録の種類 | 記憶が残るか | 扱い |
| --- | --- | --- | --- |
| `ProtectiveStopGuard.MarkCompleted`（建玉消滅→取消・`Filled`・失効かつ建玉 0・S1 の残保護 0） | S0 / S1 | S0 は残る（issue の F4） | 🔴 対象: `MarkCompleted` の中で両方の記憶を捨てる |
| `ProtectiveStopGuard.CompleteAsClosed`（`MarkCompleted` を呼ぶ） | S0 | 既に捨てている | 上に寄せる（`MarkCompleted` が捨てる） |
| `ProtectiveStopDriftAdopter.ReduceBooks`（乖離の取り込みで残保護 0） | S0 / S1 | 🔴 **S0 は残る（issue が挙げていない経路。#918 で入った）** | 🔴 対象: ガードの巡回の冒頭で、記憶している記録の状態を引き直して捨てる（下の設計） |
| `SoftwareStopExecutor`（:316・:459） | S1 のみ | 残らない（S0 の記憶は S1 の行を覚えない） | 除外 |
| `OrderExecutionAppService`（:475） | S1 のみ（建玉が生じなかった） | 残らない | 除外 |

**(2) 記憶の書き込み点**: `CloseRejectionTracker.Record` / `MarkNotified` は `RejectedClose` / `HoldRejectedClose`（どちらも
S0 の `ReplaceOrCloseAsync` の中）だけ。`HeldCloseNotificationTracker.MarkNotified` は `AddHeldCloseNotification`（S0 の据え置き）だけ。
したがって記憶の対象は S0 の行だけである。

**(3) `HeldCloseNotificationTracker` の残り**（キーは `CloseDecisionId`）: `Forget` は `CompleteAsClosed` と常駐の発行失敗の補償だけ。
次の経路で残る。
- 据え置き中に記録が完了する（建玉消滅・失効かつ建玉 0・乖離の取り込み）
- 据え置いた `CloseDecisionId` の記録が突合で**拒否**と確定し、`RejectedClose` が試行番号を進める（古いキーは二度と引かれない）
- 突合の解放（NotPlaced。門が開いたとき）の後に逆指値の再発注が成功して `Replaced` になる（同上）

**(4) `ClosedOut` を返す箇所**（`grep -n "Outcome.ClosedOut" ProtectiveStopGuard.cs`）:
`CompleteAsClosed`（受理＝正しい）、S1 の `ClosePlaced` / `PartiallyClosed`（成行を出した＝正しい）、`ReplaceOrCloseAsync` の末尾の
`Remediation=None`（🔴 誤り。F5）。

## 設計

### F4

1. `MarkCompleted(stop)` が記録を保存した後、`_closeRejections.Forget(stop.EntryDecisionId)` と
   `_heldCloseNotifications.ForgetEntry(stop.EntryDecisionId)` を呼ぶ（完了の出口を 1 つに寄せる。issue の案）。
2. `RejectedClose` と `Replaced` では、その試行の `CloseDecisionId` の据え置きの記憶を捨てる（その手仕舞いレグは結果が確定したか、
   解放されて別の試行へ移った）。
3. **ガードの外で完了した記録**（乖離の取り込み）のため、巡回の冒頭（巡回対象が 0 件で早期に戻るより前）に、
   どちらかの記憶に載っている `EntryDecisionId` ごとに `stops.Find` で記録を引き直し、
   **`Active` の記録が無い**（`null`＝行が無い、または `Completed`）ときだけ両方の記憶を捨てる。
   - 🔴 **原則 A**: 引き直しが例外で失敗したら「分からない」であり「無い」ではない。**捨てない**（警告をログして次の巡回で引き直す）。
     `Find` の `null` は照会が成功して行が無いという答え（ストアは完了した行を消さない）であり、「無い」として扱ってよい。
   - 記憶が空なら照会は 1 回も起きない（平常時の追加の DB 往復は 0）。
   - 依存（DI）は変えない（ガードは既にストアと両方の記憶を持つ）。
4. `HeldCloseNotificationTracker` は `CloseDecisionId` ごとに `EntryDecisionId` も覚える（`MarkNotified` の引数を 1 つ増やす）。
   `ForgetEntry(entryDecisionId)` と、記憶している `EntryDecisionId` の一覧を持つ。`CloseRejectionTracker` も一覧を持つ。

### F5

- `Outcome.CloseFailed` を足し、`Remediation=None` の分岐はそれを返す。
- `ProtectiveStopGuardResult` の**末尾**へ `int CloseFailed = 0` を足す（IADR-0369 決定 5 と同じ作法・既存の位置は動かさない）。
- 常駐の巡回ログは「手仕舞い失敗（未発注・建玉残存） {CloseFailed}」を別枠で出し、`CloseFailed > 0` の巡回も警告の対象にする
  （従来は `ClosedOut > 0` が条件を満たしていたため、分けた後に出なくならないようにする）。

## 受け入れ基準

- [x] F4: 記録を `Completed` にする全経路（ガード内の 3 経路・ガード外の乖離の取り込み）で `CloseRejectionTracker` の記憶が消える（テストで固定）
- [x] F4（あわせて）: `HeldCloseNotificationTracker` の記憶も、据え置きが解決・完了した経路で消える（テストで固定）
- [x] F4: 記録を引き直せない（例外）ときは記憶を捨てない（原則 A。テストで固定）
- [x] F5: `Remediation=None` の巡回が `ClosedOut` に数えられず、別の件数として巡回ログに出る（テストで固定）

## テスト方針

xUnit・注入した時計（実時間の待ちは使わない）。

| ID | 対象 |
| --- | --- |
| T-10-752 | 拒否 1 回の後、ガード内の 3 経路（建玉消滅→取消・逆指値 `Filled`・失効かつ建玉 0）で記録が完了したら、拒否の記憶が 0 件（否定形） |
| T-10-753 | 据え置き（届いたか不明）の後、建玉が消えて記録が完了したら据え置きの記憶が 0 件。据え置いたレグが突合で拒否と確定したとき・解放されて逆指値を張り直せたときも古いキーが残らない（否定形） |
| T-10-754 | ガードの外（乖離の取り込み）で記録が完了したら、次の巡回（巡回対象 0 件でも）で両方の記憶が消える |
| T-10-755 | 🔴 原則 A: 記録の引き直しが例外で失敗したら記憶を捨てない（数えが 0 へ戻って成行を撃ち直す側へ倒れない）。`Active` の記録の記憶も捨てない |
| T-10-756 | 成行手仕舞いが確実に未発注で失敗（`BrokerUnavailableException`）したら `ClosedOut == 0`・`CloseFailed == 1`（否定形）。受理された手仕舞いは `ClosedOut` のまま（対） |
| T-10-757 | 常駐の巡回ログが「手仕舞い 0」「手仕舞い失敗（未発注・建玉残存） 1」を別枠で出す |

各テストはコミット後に変異（捨てる処理を外す／引き直しの例外で捨てる／`CloseFailed` を `ClosedOut` へ戻す／ログから別枠を外す）で赤になることを確かめる。

## 計画書との差異

- 差異: なし（ガードの簿記と可観測性の是正）。

## 未決事項

- なし。
