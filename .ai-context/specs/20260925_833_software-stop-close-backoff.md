---
title: S1 の決済が続けて売れないとき、行ごとの待ち時間を永続化してハンドラとガードの両方が守り、到達の記録は消さずに撃ち直しを続ける
type: spec
status: accepted
related_ids: [FR-10, FR-12, UC-02, ADR-0040, IADR-0344, IADR-0389, IADR-0380, IADR-0369, IADR-0057]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 損切り)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1 の S1)
---

# 仕様書: S1 の決済の拒否連発に行ごとの待ち時間を入れる（#833 項目 2）

## 起点

- [#833](https://github.com/endazon/ai-stock-trading/issues/833) の **項目 2 のみ**。項目 1 は PR #932（IADR-0389）で着地済み、
  項目 3（古い写しの上書き）は別 PR（本 PR の上に積む）、項目 4 は着地済み（下の「項目 4 の再検証」）。
- 2026-09-25 に `origin/develop` = `3d9b91c2` で引き直した（`git rev-parse --is-shallow-repository` → `false`）。

## 🔴 実測（コードで確認・`3d9b91c2`）

| 箇所 | 現在の挙動 |
| --- | --- |
| `backend/Services/OrderExecutionService/` 全体 | `Backoff` / `NextAttemptAt` / `RetryAfter` の出現は **0 件**（`grep -rniE 'backoff\|NextAttemptAt\|RetryAfter'`） |
| `SoftwareStopExecutor.cs:334-337` | 拒否のたびに `Attempt` を進めるだけ。`attempt % 3 == 0` で **`TriggeredAt` / `TriggeredPrice` を消す**（打ち切り） |
| `SoftwareStopExecutor.cs:344-348` | 打ち切りの回だけ `CloseRejected`（Critical） |
| `ProtectiveStopGuardOptions.cs:12` | ガードは 30 秒ごとに到達済みの行を `TryCloseAsync` で撃ち直す |
| `MarketMonitorAppService.cs:37-70` | 開場中は損切りラインを越えている限り**毎巡回（60 秒）** `StopLossTriggered` を出す（クールダウンと独立） |
| `SoftwareStopReArmer.cs:97-106` | 受理 → 0 約定で失効した決済を再武装した直後、次のガード巡回が即座に撃ち直す（IADR-0389 §結果「ループに上限が無い」） |

→ **開場中に拒否が続くと**「ガードの 30 秒 × 3 回で打ち切り → 次の 60 秒の到達で再武装 → また 3 回」を繰り返し、
**毎分 3 件前後の成行と、3 回ごとの Critical** が持続する。受理 → 即失効のループには回数の歯止めすら無い。

🔴 **打ち切りそのものが「出口を塞ぐ」**: 打ち切りは `TriggeredAt` を消すため、**価格がラインの内側へ戻ると
二度と撃たない**（次の到達が来ないから）。IADR-0344 決定 4「一度到達したら価格が戻っても決済する」と矛盾する。

## 射程

1. **行ごとの待ち時間を永続化する**: `protective_stop_orders` に `CloseFailures`（int・非 null・既定 0）、
   `NextCloseAttemptAt`（timestamptz・null）、`LastTriggerSeenAt`（timestamptz・null）を足す（additive な migration 1 本）。
2. **待ち時間は 30 秒から倍々・上限 15 分**: 連続失敗 n 回目の後は `min(30 秒 × 2^(n−1), 15 分)`。
3. **ハンドラ（到達）とガード（巡回）の両方が守る**: 判定は共有の `SoftwareStopExecutor.TryCloseAsync` の**成行を送る直前**に置く
   （記録済みの結果での確定・残保護数量 0 での完了・建玉照会・持ち分の確定は待ち時間中も行う＝待ち時間が止めるのは**新しい成行だけ**）。
4. 🔴 **到達の記録を消さない（打ち切りの撤去）**: 拒否が何回続いても `TriggeredAt` は残し、待ち時間を置いて撃ち直しを**続ける**。
   価格が戻って到達が途絶えても、ガードが撃ち直す（出口を塞がない）。
5. **Critical は抑止しない・埋もれさせない**: `CloseRejected`（Critical）は**連続失敗 3 回目**で出し（従来の初回と同じ回数）、
   以後は**連続失敗 4 回ごとに 1 回**（待ち時間が上限に達した後はおよそ 1 時間ごと）。失敗のたびの `LogError` は従来どおり出す。
6. **「失敗」の定義**: ①決済が受理されなかった（`Rejected` / `Cancelled` / `Expired` が返った）②受理された決済が
   **1 株も約定しないまま**終端した（`SoftwareStopReArmer` の再武装）。**1 株でも約定した再武装は前進**として数えを 0 へ戻す。
7. **新しい到達の窓では数えをやり直す**: 市場監視の到達は開場中しか来ない（IADR-0380）。**前回の到達から 5 分以上空いた到達**
   （＝閉場を挟んだ・価格が一度戻った）を受けたら、数えと待ち時間を 0 へ戻して**すぐ撃つ**。
   連続した到達（60 秒間隔）では戻さない——戻すと待ち時間が 60 秒ごとに消え、拒否連発が再発する。

### 射程外（やらないこと）

- #833 項目 3（楽観並行の版番号）。本 PR の上に積む別 PR。
- #833 項目 4（据え置きの Critical）。着地済み（下）。
- S0 の成行手仕舞いの撃ち直し（`ProtectiveStopGuard` / `CloseRejectionTracker`。IADR-0369）。**PR #944 / #945 が同ファイルを編集中**のため触らない。
- 閉場中に成行を送らない判定（市場の開場判定は市場監視にしか無い）。閉場中の撃ち直しは待ち時間で間引かれるだけである（下の残余）。

## 項目 4 の再検証（着地済み・本 PR では変えない）

`SoftwareStopExecutor.cs:429-454` `NotifyIfStalled`: 到達から `DefaultSettlementGrace`（15 分・`:54`）を過ぎても据え置きなら
`LogError` ＋ `SoftwareStopOutcome.CloseStalled`（Critical・`SoftwareStopExecuted.cs:56`）を出し、行は Active のまま。
重複は `StalledNotifiedAt`（`:434` / `:443`）で抑止。試験 `SoftwareStopExecutorTests.到達済みで決済できない状態が猶予を過ぎたらCriticalを一度だけ出す`。
issue の提案「1 窓 1 回」ではなく「1 行 1 回」で入っている。**無期限の据え置きが無音で続く経路は閉じている**ので項目 4 は完了とみなす。
残余（1 行 1 回のため、長期の据え置きで再通知しない）は IADR-0344 追記(14) に記録する。

## 🔴 母集合（走査したファイルと除外理由）

走査: `grep -rn -E '到達 ?1 ?回あたり|次の(損切りライン)?到達で(再試行|再開)|次の到達まで|MaxCloseAttemptsPerTrigger|3 試行で打ち切'`
（`--include=*.cs --include=*.md --include=*.json`・`obj` と確定済み `.ai-context/specs/` を除く）と
`grep -rn 'CloseRejected\|CloseUnfilled' backend --include=*.cs`。

- **採る（変更する）**:
  - `Domain/ProtectiveStopOrder.cs` / `Infrastructure/Persistence/ProtectiveStopOrderRow.cs` / `EfProtectiveStopOrderStore.cs` — 3 列。
  - `Infrastructure/Persistence/Migrations/*_AddSoftwareStopCloseBackoff.*` と `OrderExecutionDbContextModelSnapshot.cs`（生成）。
  - `ExecuteSoftwareStops/SoftwareStopExecutor.cs` — 待ち時間の判定・拒否の分岐・到達の窓。
  - `ExecuteSoftwareStops/SoftwareStopReArmer.cs` — 0 約定の再武装を失敗として数える。
  - `Shared.Contracts/Events/SoftwareStopExecuted.cs` — `CloseRejected` の注記（「次の到達で再試行する」は偽になる）。
  - `NotificationService/.../NotificationFormatter.cs` — `CloseRejected`（「次の損切りライン到達で再試行します」）と
    `CloseUnfilled`（「次の巡回で決済を撃ち直します」）の文面。ゴールデン `NotificationTemplateGoldenTests.cs`。
  - `AuditService/Domain/AuditEntryFactory.cs` — `CloseUnfilled` の結末文（「次の巡回で撃ち直す」）。
  - `Tests/.../SoftwareStopExecutorTests.cs` — 打ち切りを固定していた試験（`決済が拒否され続けたら到達1回あたり3試行で打ち切りCriticalを出す`）。
  - `docs/functional/FR-10_risk-controls.md`（「決済が拒否された」行）・`docs/tests/FR-10_risk-controls-tests.md`（T-10-348 の行・新節）。
  - `.ai-context/adr/IADR-0344_s1-software-stop-loss.md`（追記(14)）・`IADR-0389_...md`（追記）・`.ai-context/adr/README.md`（索引）。
- **除外（理由）**:
  - `GuardProtectiveStops/ProtectiveStopGuard.cs:50-56` と `CloseRejectionTracker.cs:12-15` の注記
    （「S1 の上限は到達 1 回あたりで、次の到達で自ら再武装する」）は本 PR で**偽になる**が、**PR #944 / #945 が同ファイルを編集中**のため触らない。
    挙動には関与しない注記である。両 PR のマージ後に追随する（PR 本文と IADR-0344 追記(14) に明記）。
  - `.ai-context/adr/IADR-0369_...md:90,100-102` — 他 IADR の本文（凍結）。IADR-0344 追記(14) が後継の正本。**PR #944 / #945 も編集中。**
  - `.ai-context/specs/20260918_820_*` ほか確定済みの作業仕様書 — 凍結記録。
  - `Hosted/ProtectiveStopGuardService.cs` — 待ち時間中の据え置きは従来の「据え置き」件数に入る（ガードの `_ => Outcome.Unknown`）。**PR #945 が編集中。**

## 受け入れ基準（テスト ID は T-10-790..T-10-794）

| ID | 基準 |
| --- | --- |
| T-10-790 | 拒否が続く行は、連続失敗 n 回目の後 `min(30 秒 × 2^(n−1), 15 分)` が過ぎるまで**ハンドラからもガードからも**成行を送らない。過ぎたら送る |
| T-10-791 | 🔴 拒否が何回続いても `TriggeredAt` は残り、**価格が戻って到達が途絶えても**ガードの巡回が撃ち直し、受理されれば決済が通る（出口を塞がない） |
| T-10-792 | `CloseRejected`（Critical）は連続失敗 3 回目で出て、以後 4 回ごとに 1 回出る。失敗のたびに `LogError` が出る |
| T-10-793 | 前回の到達から 5 分以上空いた到達では数えと待ち時間が 0 へ戻り即座に撃つ。60 秒間隔の到達では戻らない |
| T-10-794 | 再武装は 0 約定なら失敗として数えて待ち時間を置き、1 株でも約定していれば数えを 0 へ戻す。**同じ銘柄の別の行（AAPL 715 株 / 713 株の 2 行）の待ち時間には影響しない** |

## 稼働中の表への影響（`protective_stop_orders`・Active 2 行）

migration は**列の追加だけ**（`CloseFailures integer NOT NULL DEFAULT 0` / `NextCloseAttemptAt timestamptz NULL` /
`LastTriggerSeenAt timestamptz NULL`）。既存 2 行は `0 / NULL / NULL` で読まれる＝**待ち時間なし・次の到達は新しい窓として扱う**。
行の書き換え・削除・インデックスの作り直しは無い。Down は 3 列の削除。
