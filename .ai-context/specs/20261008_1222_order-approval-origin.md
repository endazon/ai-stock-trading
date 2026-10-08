---
title: S1 の決済の前に取り消すのを判断の手仕舞いに限り、利用者の成行の手仕舞い・維持率割れの自動縮小は取り消さず差し引く —— 発注の記録に承認の出どころを持たせる（#1222）
type: spec
status: accepted
related_ids: [FR-10, UC-06, UC-02, ADR-0050, ADR-0003, IADR-0515, IADR-0466, IADR-0461, IADR-0344, IADR-0357, IADR-0495, IADR-0211, IADR-0486]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0050_decision-close-nets-in-flight-closes-and-stop-line-exit-only-without-mechanical-stop.md (決定 1)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 手仕舞いと損切りは止めない)
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md (UC-06 利用者の手仕舞い・維持率割れの自動縮小)
---

# S1 の決済と、利用者の手仕舞い・維持率割れの自動縮小（#1222）

## 起点

- [#1222](https://github.com/endazon/ai-stock-trading/issues/1222)（起票元 #1204・第 4 回全体監査 B-18「ADR-0050 決定 1 の残余」）。
- 計画 ADR-0050 決定 1（2026-09-30 Accepted）: 判断の手仕舞いは処理中の決済（S1 の決済・手仕舞いのレグ・**利用者の手仕舞い**）を差し引いた残りだけを送る。
  🔴 損切り（S1 の決済）も、判断の手仕舞いが処理中であることを理由に止まってはならない（FR-10）。
- FR-10: kill switch・日次損失ロックアウト・一時停止のいずれも手仕舞いと損切りを止めない。
- UC-06: 利用者の手仕舞い（#847 以後は成行が既定）。維持率割れの自動縮小はシステムが自ら決済を発注する「動かす統制」で、AI を介在させない。
- IADR-0466 の残余（`.ai-context/adr/IADR-0466_s1-close-cancels-in-flight-decision-close.md:107-109`）:
  利用者の成行の手仕舞い（UC-06）・維持率割れの自動縮小は判断の手仕舞いと見分けられず、取り消す（出し直さない）。
- planning#741（ADR-0050 **決定 2** の裁定）とは別の決定であり、裁定を待たない。**計画の裁定は要らない**（ADR-0050 決定 1 が「処理中の決済には利用者の手仕舞いを含む」と既に定めている）。

## 現況（origin/develop 4c142a40 で確認）

| # | 事実 | 場所 |
| --- | --- | --- |
| 1 | S1 の決済の前の取消は、非終端の Close の記録から**保護の機構が出したもの**（DecisionId・OrderId の導出）を除いた残りを全部取り消す | `SoftwareStopExecutor.YieldDecisionClosesAsync` |
| 2 | 承認の経路の決済（`OrderApproved`）の書き手は 3 つ: 発注前審査（判断）・`PositionCloseService`（利用者）・`MaintenanceMarginReductionService`（自動縮小）。後 2 者の DecisionId は `Guid.NewGuid()` | RiskManagementService |
| 3 | `OrderApproved` には `FromTradeDecision`（#1176・IADR-0495）があるが、**既定 false は「判断ではない」と「旧いメッセージ」を区別できない**。利用者・自動縮小を**積極的に**名指しする値が無い | `OrderApproved.cs` |
| 4 | 発注の記録（`ExecutionRecord` / `executed_orders`）に出どころの列は無い。前例: `StopFloorSource`（#1122・列の追加だけ・既存行 null＝分からない） | `ExecutedOrderRow.cs`・マイグレーション `AddExecutedOrderStopFloorSource` |
| 5 | 判断側の差し引き（IADR-0461 `CountInFlightClosesAsync`）は、生きていると証券会社が答えた処理中の決済だけを、残り（数量 − 約定）で数え、S0/S3 の保護レグを除く | `OrderExecutionAppService.cs` |

## 設計（IADR-0515）

1. **出どころの契約**: `Shared.Contracts` に `OrderApprovalOrigin`（`Unknown = 0` / `TradeDecision = 1` / `OwnerClose = 2` / `MaintenanceMarginReduction = 3`。序数固定・末尾へ追加）を足し、
   `OrderApproved.Origin`（既定 `Unknown`）で運ぶ。書き手 3 つがそれぞれ明示する。旧いメッセージは `Unknown`。
2. **記録**: `ExecutionRecord.ApprovalOrigin`（null＝分からない）・`executed_orders.ApprovalOrigin`（integer NULL。**列の追加だけ**。既存行は null）。
   承認の経路の発注（`OrderExecutionAppService` の相 4）だけが書く（`Unknown` は null で書く）。保護の機構の記録・突合で確定した記録は null。
3. **S1 の取消（IADR-0466 決定 2 の改訂）**: 出どころが `OwnerClose` / `MaintenanceMarginReduction` で、記録の時刻（`ExecutedAt`）から
   `NettedCloseGrace`（2 分＝常駐ガード 4 巡回）以内の記録は**取り消さない**。それ以外（`TradeDecision`・null・猶予を過ぎたもの）は従来どおり取り消す。
   🔴 **出どころが分からない（null・`Unknown`）は判断の手仕舞いとして取り消す側へ倒す**（是正前と同じ。S1 は送られ、損切りは止まらない）。
   🔴 猶予を置くのは、自動縮小が指値（参照価格）で、利用者も指値を選べる（IADR-0357）ため。約定しない指値を差し引き続けると S1 が据え置かれ続け、損切りが実質止まる。
4. **S1 の差し引き（ADR-0050 決定 1 を S1 の側へ）**: 取消の段の後（取消でやり直す 1 回でも）、同じ銘柄・市場・決済の方向の非終端の Close の記録のうち
   利用者・自動縮小のものが**証券会社に生きている**なら、
   - 照会の順は「記録 → 注文照会 → 建玉照会（新しく）」。残り（数量 − 約定）を数える。
   - 処理中の決済の合計＝生きている非終端の Close の記録（保護の逆指値レグ〔S0/S3〕を除く）の残りの合計。
   - 送る数量＝min(残保護数量, 決済方向の建玉 − 処理中の決済の合計)。0 以下なら**据え置く**（送らない・失敗に数えない・待ち時間を置かない）。
   - 利用者・自動縮小の決済が 1 本も生きていない（無い・照会 null・例外・終端）なら何もしない（従来の挙動。建玉照会も注文照会も増やさない）。
   - 読み出しの失敗は差し引かずに送る（是正前と同じ側。損切りを止めない）。差し引きのための建玉照会が不明なら据え置く（既存の「建玉不明は据え置き」と同じ）。
5. 窓（規則 11）: 下の表。

## 窓の表（規則 11）

窓＝「S1 が処理中の決済を読んでから送るまで」。増える側＝その間に利用者の決済が載る/約定が進まない、減る側＝その間に利用者の決済が約定する（建玉が減る）。

| 形 | 増える側（読んだ後に利用者の手仕舞いが載る） | 減る側（注文照会の後・建玉照会の前に約定する） | 期待どおりか |
| --- | --- | --- | --- |
| 後の端だけ（拒否を見てから差し引き直す） | 1 回拒否（SIMULATE）／押さえない証券会社では二重に売る | 二重に売る（同上） | × |
| 前の端だけ（読む → 注文照会 → 建玉照会 → 送る） | 1 回拒否され、撃ち直し（30 秒＋ガード）で差し引いて通る＝IADR-0466 の増える側と同じ | 建玉は約定の後・残りは約定の前 → **差し引き過ぎ**（少なく売る）。次の巡回で残りを売る（二重に売らない） | ○（安全側） |
| 両端 | 増える側の遅れを 1 回分縮めるだけ。1 回の呼び出しで 2 本の成行の経路を足す | 前の端と同じ | 過剰 |

→ 前の端だけを採る（IADR-0466 決定 6 と同じ形）。照会の順を「建玉 → 注文」にすると減る側で差し引き不足（二重に売り得る）になるので採らない。

## 母集合（規則 9・10）

- 「判断の手仕舞いと見分けられない」の語で全文書を走査: `grep -rn "見分けられず\|見分けない" .ai-context/adr backend docs` →
  IADR-0466（残余・決定 2）・`SoftwareStopExecutor.cs` のコメント 2 か所・本書。IADR-0466 は Accepted の凍結記録なので本文は書き換えず、日付つき追記で IADR-0515 へ送る。
- `new OrderApproved(` の生成箇所（テスト以外）: 4 か所（審査・利用者・自動縮小・発注執行の突合の新規建て）。突合の新規建ては Open なので `Unknown` のまま。
- `new ExecutionRecord(` の本番の生成箇所: 5 か所（承認の相 4・保護レグ・保護喪失の成行・S1 の決済・突合）。承認の相 4 だけが出どころを書く。
- `FindPendingCloses` の読み手: 判断側（IADR-0461）と S1（IADR-0466）。判断側は出どころを使わない（全部差し引く）ので変えない。
- 本変更で新たに誤りになる自分の記述: `SoftwareStopExecutor.cs` の「利用者の成行の手仕舞い・維持率割れの自動縮小は記録から見分けられない」→ 書き換える。
  `IExecutedOrderStore` / `ExecutionRecord` のコメントは出どころに触れていない（誤りにならない）。

## 受け入れ基準 → 試験

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| 1 | 利用者の成行の手仕舞いが処理中 → 取り消さず、差し引いた残りだけを送る（二重に売らない） | T-10-2435・T-10-2436 |
| 2 | 維持率割れの自動縮小が処理中 → 同上 | T-10-2437 |
| 3 | 判断の手仕舞いは従来どおり取り消してから送る（混在でも） | T-10-2438 |
| 4 | 🔴 出どころが分からない（null・`Unknown`）と猶予を過ぎた利用者・自動縮小の決済は取り消す側＝損切りを止めない。利用者の決済が確かめられない（照会 null・例外・読み出しの失敗）なら差し引かず送る。差し引くための建玉照会が不明なら据え置く | T-10-2439・T-10-2440 |
| 5 | 🔴 保護の機構の見分け（IADR-0466 決定 2）が退行しない＝既存の T-10-1813〜T-10-1826 が無改変で緑。出どころは記録に残り（EF・インメモリ）、承認から記録へ写り、契約は旧い本文を「分からない」と読み、書き手 3 つが明示する | T-10-2441・T-10-2442・T-10-2443・T-10-2444 |

## 検証

- `dotnet build backend/backend.slnx`（警告 0）・`dotnet format --verify-no-changes`
- `dotnet test`: OrderExecutionService.Tests・RiskManagementService.Tests・Shared.Contracts.Tests（1 本ずつ・都度 bin/obj を消す）
- `node scripts/scripts.test.js`・check-trace-blocks・gen-knowledge-graph --check・check-test-traceability・check-adr-index-sync・check-commit-messages

## 残余

- 突合（`OrderReservationReconciler`）で確定した記録は出どころを持たない（予約の行に列を足していない）→ 利用者の手仕舞いでも取り消す側（是正前と同じ）。
- 列を足す前の記録・切り替え前のリスク管理が出した承認も分からない（同上）。
- 利用者・自動縮小の決済が全量を覆うあいだは最長 2 分 S1 を据え置く（猶予の起点は `ExecutedAt` で、約定の進みで前へ進む）。猶予の後は取り消して送る。
- 試験の採番: T-10-2429〜T-10-2434 は並行中の PR #1250 が使うため、T-10-2435 から採った。IADR は最大（0514）＋1 の 0515（0513 は PR #1250）。
