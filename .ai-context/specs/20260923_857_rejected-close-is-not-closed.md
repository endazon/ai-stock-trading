---
title: 保護喪失時の成行手仕舞いが「確認できた拒否」で返ったら、手仕舞い済みを主張しない（#857）
type: spec
status: accepted
related_ids: [FR-10, FR-11, UC-02, UC-06, ADR-0003, ADR-0040, IADR-0057, IADR-0117, IADR-0210, IADR-0344, IADR-0369]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-24
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10「逆指値が未受理・失効した場合は建玉を持たない」)
  - planning:projects/ai-stock-trading/04_workflows/02_event-driven-trading.md (業務フロー 02「逆指値が成立しない場合の扱い」)
---

# 仕様書: 確認できた拒否を「手仕舞い済み」と扱わない（#857）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-10（損切りの実行機構・逆指値なしの建玉を持たない）、FR-11（監査）
- ユースケース（UC）: UC-02（損切り）、UC-06（手仕舞い）
- 画面（SC）: なし
- 関連 ADR: ADR-0003（AI は統制を上書きできない）、ADR-0040 決定 1（損切りの実行機構）
- 関連 IADR: IADR-0210 決定 3・決定 4（保護レグと建玉解消）、IADR-0117 改定 7・改定 9（届いたか不明の扱い）、
  IADR-0344 決定 5-6（S1 の決済試行上限）、IADR-0057（発注 3 相）、本作業の決定は IADR-0369
- 計画書リンク: 上記 plan_refs（隣接クローン `../project-planning` または GitHub 上で読む）

## 目的・背景

保護逆指値を張れない／失効したとき、システムは建玉を成行で手仕舞う（IADR-0210 決定 3・4）。
この成行手仕舞いが**証券会社に確認できる形で拒否された**（`PlaceMarketOrderAsync` が `OrderStatus.Rejected`
などの終端を**返した**）場合でも、呼び出し側は戻り値の状態を見ずに成功として扱っていた。

- `ProtectiveStopGuard.ReplaceOrCloseAsync`: `closeOrder` が非 null でありさえすれば `ExecutionRecord` を保存し、
  記録を `Completed` にして `ProtectiveStopCoverageLost(Remediation=PositionClosed)` を発行していた。
- `OrderExecutionAppService.CloseUnprotectedPositionAsync`: 同じく状態に依らず `PositionClosed` を返していた。

帰結は 3 つ。①通知が「手仕舞いました」と言うのに建玉は残っている、②記録が `Completed` になるため
**以後の巡回がこの建玉を見ない**（IADR-0210 の「逆指値なしの建玉を持たない」が無音で破れる）、
③手仕舞いレグの承認行が取引台帳へ足され、30 分の窓のあいだ在庫を押さえる（実際には何も送られていないのに）。

これは利用者の立てた原則「**確実に未発注**と**送ったが不明**を混同しない」の裏側であり、
**確認できた拒否**（＝確実に約定していない）を**手仕舞い済み**（＝確実に決済された）と混同していた。

## 対象範囲

- 対象:
  - `ProtectiveStopGuard.ReplaceOrCloseAsync` の成行手仕舞い（今回送った結果・**既に記録済みの結果**の両方）
  - `OrderExecutionAppService.CloseUnprotectedPositionAsync`（エントリー同時の逆指値が未受理だったときの手仕舞い）
  - `ProtectiveStopRemediation` への `CloseRejected` 追加と、その通知文面・監査要約
  - 同じ理由で拒否され続ける成行を 30 秒ごとに送り続けないための**撃ち直しの上限**
- 対象外:
  - 保護レグ（逆指値）そのものの送信結果が不明なとき（孤立した逆指値）—— #853
  - 乖離の取り込みに伴う保護記録・ブローカー側保護注文の追随 —— #858
  - 終端かつ**部分約定あり**の手仕舞いレグを取引台帳へ届けること（後述「残余リスク」）

### 是正の母集合（自分で引いた結果と除外理由）

`.claude/rules/traceability.repo.md`「是正・追随の母集合の取り方」規則 2・9・10 に従い、
**誤りの側の文字列**（`PlaceMarketOrderAsync` の呼び出しと、その戻り値を状態で分岐していない箇所）で
全ソースを走査した（`grep -rn "PlaceMarketOrderAsync" backend --include=*.cs`。テストを除く本番経路は 4 箇所）。

| # | 箇所 | 状態を見ているか | 本作業の対象 |
| --- | --- | --- | --- |
| 1 | `OrderExecutionAppService.cs:242`（承認済み Close の成行発注） | 見ていないが、**記録・イベントは `OrderExecuted` 経路**であり「決済済み」を主張しない（終端 Rejected はそのまま記録され、台帳は減らない） | 対象外（誤りが無い） |
| 2 | `OrderExecutionAppService.cs:641`（`CloseUnprotectedPositionAsync`） | 見ていない＝**`PositionClosed` を主張する** | 🔴 対象 |
| 3 | `SoftwareStopExecutor.cs:258`（S1 の成行決済） | **見ている**（`Settle` が受理 3 状態だけを成功とし、それ以外は試行番号を進めて上限で打ち切る） | 対象外（既にこの形。本作業はこの形を S0 へ写す） |
| 4 | `ProtectiveStopGuard.cs:360`（S0 の成行手仕舞い） | 見ていない＝**`PositionClosed` を主張する** | 🔴 対象 |

加えて規則 10（是正で新たに誤りになる自分の記述の引き直し）として、
`ProtectiveStopRemediation` を `switch` / パターンで読む全箇所を走査した（4 箇所）。

| 箇所 | 既定の落ち先 | 本作業での扱い |
| --- | --- | --- |
| `NotificationService/.../NotificationFormatter.cs` | `_ =>`「解消にも失敗しました」 | 🔴 明示の腕を足す（既定に落ちると「拒否された」ではなく「解消にも失敗」と読ませる） |
| `AuditService/Domain/AuditEntryFactory.cs` | `_ => string.Empty` | 明示の腕を足す（監査要約に「拒否・建玉残存」を残す） |
| `OrderExecutionService/Hosted/ProtectiveStopGuardService.cs` | 肯定形の照合のみ（`CloseDispatchIndeterminate` 限定） | 変更しない（新しい値は照合されない＝正しい） |
| `RiskManagementService/.../ProtectiveStopLedgerHandlers.cs` | **Remediation では分岐しない**（レグの有無で判定） | 変更しない。`CloseIntent = null` で運ぶため**承認行は足されない**（これが正しい） |

## 設計

### 決定 1: 「生きていない終端」で返った手仕舞いは `PositionClosed` を主張しない

判定は既存の純関数 `OrderStatusLifecycle.AbandonsUnfilledRemainder`（`Cancelled` / `Rejected` / `Expired`。
**`Filled` を含まない**）を使う。新しい述語は作らない。真のとき:

- 記録（`protective_stop_orders`）は **`Active` のまま**残し、**試行番号だけを進める**（次の巡回は新しい
  `CloseDecisionId` で改めて評価する）。`RemainingProtected` は動かさない。
- 発注結果（`ExecutionRecord`）は**保存する**（拒否の一次証跡。終端なので約定追跡には載らない）。
- 予約は **`MarkCompleted` で確定する**。確認できた拒否は「不明」ではないため `Reserved` で据え置かない
  ——据え置くと次の巡回が入口 (b) で永久に据え置き、**確定した事実を不明として扱う**ことになる。
- `ProtectiveStopCoverageLost(Remediation=CloseRejected)` を発行する（Critical）。
  **`CloseIntent` は null**（送った成行は生きていないため、取引台帳に在庫を押さえさせてはならない）。
  `CloseDecisionId` は**載せる**（拒否された発注記録との相関に要る）。この非対称な形は
  `ProtectiveStopEventPayloadTests` で固定する。

### 決定 2: 入口 (a)（記録済みの手仕舞いレグ）でも状態を見る

`store.FindByDecisionId(closeDecisionId)` が返す記録が終端（未約定残を放棄）なら、そこでも完了させない。
送信直後に行の更新だけが失われたクラッシュ窓では、同じ `CloseDecisionId` の**拒否された記録**が残るため、
ここを塞がないと次の巡回が「記録があるから手仕舞い済み」と読む（決定 1 と同じ誤り）。

### 決定 3: 撃ち直しの上限は 3 回。数えはプロセス内に持つ

同じ理由で拒否され続ける成行を 30 秒ごとに送り続けない（IADR-0344 決定 5-6 の S1 と同じ上限 3）。

［2026-09-24 追記 / PR #916 監査］ 同じなのは**回数だけ**である。S1 の上限は到達 1 回あたりで、使い切ると
`TriggeredAt` を消して次の到達で自ら再武装する。本作業の上限は保護記録ごとの累計で再武装が無い
（戻るのは再起動・逆指値の再発注の成功・手仕舞いの約定だけ）。詳細は IADR-0369 決定 3 の同日追記。

- 数えは `CloseRejectionTracker`（singleton・**非永続**）が `EntryDecisionId` ごとに持つ。
  既存の `HeldCloseNotificationTracker`（IADR-0117 改定 9）と同じ作法であり、**再起動で数えが消える＝
  再起動後は改めて 3 回試す**。倒れ方が「もう一度手仕舞いを試みる」側であり、fail-loud の向きに一致する。
  永続列を足さない理由は IADR-0369 に書く。
- 上限に達した行は**成行を送らない**。ただし**無音にしない**——このプロセスが未通知なら即座に、
  以後は 1 時間ごとに `CloseRejected` を発行し直す（`CloseDecisionId` は null＝この巡回では 1 本も送っていない）。
- **逆指値の再発注は上限の対象外**である（保護の回復は常に試みてよい）。再発注に成功したら数えを 0 へ戻す。
- 記録は `Active` のままなので、**建玉は巡回対象から外れない**（本 issue の中心）。

### 決定 4: 出口を入口のガードで塞がない

上限は**成行手仕舞い（出口）**だけに掛ける。逆指値の再発注・建玉消滅時の取消・S1 の決済には掛けない。
また上限に達しても記録を閉じない（閉じると巡回から外れ、無音になる）。

### 変更するファイル

| ファイル | 変更 |
| --- | --- |
| `backend/Shared/AiStockTrading.Shared.Contracts/Events/ProtectiveStopCoverageLost.cs` | `ProtectiveStopRemediation.CloseRejected`（末尾・序数 4） |
| `backend/Services/OrderExecutionService/Features/OrderExecution/GuardProtectiveStops/CloseRejectionTracker.cs` | 新規（プロセス内の数えと再通知の間隔） |
| `.../GuardProtectiveStops/ProtectiveStopGuard.cs` | 決定 1〜4 の実装。`ProtectiveStopGuardResult` に `CloseRejected` 件数を足す |
| `.../DispatchApprovedOrder/OrderExecutionAppService.cs` | 決定 1（エントリー同時の手仕舞い） |
| `backend/Services/OrderExecutionService/Program.cs` | `CloseRejectionTracker` の singleton 登録 |
| `backend/Services/NotificationService/Features/Notifications/NotificationFormatter.cs` | 件名と本文の腕 |
| `backend/Services/AuditService/Domain/AuditEntryFactory.cs` | 監査要約の腕 |

## 受け入れ基準

- [ ] 🔴 二重決済でショート化しない（T-10-402 / T-10-403 / T-10-406〜T-10-409 が緑のまま）
- [ ] 成行手仕舞いが拒否されたとき、通知が「手仕舞いました」と言わない（Remediation が `PositionClosed` でない）
- [ ] 拒否された建玉が**巡回対象から外れない**（記録が `Active` のまま・次の巡回でも評価される）
- [ ] 拒否のときに取引台帳へ手仕舞いレグの承認行が足されない（`CloseIntent` が null）
- [ ] 同じ拒否が続いても成行の送信は 3 回で止まり、それでも Critical の通知は止まらない
- [ ] 記録済みの手仕舞いレグが終端（拒否）なら、それを根拠に完了させない

## テスト方針

xUnit・注入した時計（実時間の待ちは使わない。#885 / #900 / #901）。

| ID | 対象 |
| --- | --- |
| T-10-634 | ガードの成行手仕舞いが拒否で返ったら記録は `Active` のまま・`Completed` を主張しない（否定形） |
| T-10-635 | 同・通知は `CloseRejected` で `CloseIntent` を運ばない（台帳が在庫を押さえない）／予約は Completed |
| T-10-636 | 拒否が続いても成行の送信は 3 回で止まる。上限後も 1 時間ごとに Critical を出し続ける（時計を進めて確認） |
| T-10-637 | 記録済みの手仕舞いレグが終端（拒否）なら完了させない（クラッシュ窓の再入） |
| T-10-638 | 拒否のあと逆指値の再発注に成功したら `Replaced` になり、撃ち直しの数えが 0 へ戻る |
| T-10-639 | エントリー同時の逆指値が未受理→成行手仕舞いが拒否されたら `PositionClosed` を主張しない（発注側） |
| T-10-640 | 通知の件名・本文が「手仕舞いました」と言わず Critical のまま／監査要約に拒否と建玉残存が残る／列挙の序数と往復 |

［2026-09-24 追記 / PR #916 監査］ 監査の指摘で次の 2 件を足し、T-10-636 に上限の値の固定を足した。

| ID | 対象 |
| --- | --- |
| T-10-684 | エントリー時の拒否（`RejectedAtEntry`）の通知が、巡回・撃ち直し・上限・再通知を約束せず「この 1 回だけ・手で手仕舞う」と言う（否定形）。滞留側の約束は残る（対の表明） |
| T-10-685 | 上限に達した巡回の `CloseRejected` の発行に失敗したら、通知の記憶だけを消して次の巡回で出し直す。拒否の数えは残る（成行を重ねない） |

## 計画書との差異

- 差異: なし。FR-10 の「逆指値なしの建玉を持たない」を**より忠実に**するための是正であり、
  計画の規律を変更していない（拒否された手仕舞いを成功と数えていた実装側の誤りを直す）。

## 未決事項

- **終端かつ部分約定ありの手仕舞いレグ**（例: 一部約定してから `Cancelled`）は、約定追跡が終端記録を
  ポーリングしないため `OrderExecuted` が出ず、**部分約定が取引台帳へ届かない**。これは本 issue 以前からの
  性質であり（`PositionClosed` を主張していた従来も台帳は減らなかった）、本作業では直さない。
  次の巡回は**ブローカーの建玉照会**から数量を引き直すため、**売り過ぎは起きない**。
  乖離として検知される（IADR-0118）。追随の要否は #858 の作業で再評価する。
