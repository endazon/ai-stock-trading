---
title: BeforeSend の故障注入で作った予約が、その ET 取引日のあいだ保有建玉数 1 件と段階資金・日次発注枠を占有することを IADR-0488 と発注経路の Runbook に書く（#1173）
type: spec
status: accepted
related_ids: [FR-10, FR-05, FR-04, ADR-0045, IADR-0488, IADR-0346, IADR-0390, IADR-0463, IADR-0246, IADR-0117, IADR-0362, IADR-0444, IADR-0356, IADR-0398]
author: claude (Claude Code)
created: 2026-10-06
updated: 2026-10-06
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements (FR-10 リスク統制・FR-05 発注執行)
  - planning:projects/ai-stock-trading/07_adr (ADR-0045 決定1・決定2)
---

# 仕様書: BeforeSend の故障注入の予約が当日の統制枠を占有することの文書化（#1173）

## 起点となる計画書（トレーサビリティ）

- 機能要求: FR-10（リスク統制・二重発注をしない）・FR-05（発注執行）・FR-04（取引判断の入力）
- 関連 ADR: ADR-0045 決定1（解放の門を開ける基準）・決定2（門は取引環境ごと）
- 関連 IADR: IADR-0488（故障注入。本件で結果欄へ追記）・IADR-0346（承認済みで終端でない新規建てを当日分だけ統制へ算入）・
  IADR-0390（判断の入力の未約定の新規建て）・IADR-0463（審査で必ず落ちる銘柄は LLM 前に見送る）・IADR-0246（市場の現地取引日）・
  IADR-0117 改定6（届いたか不明は予約を据え置く）・IADR-0362 / IADR-0444（解放の門）・IADR-0356 / IADR-0398（見送りで台帳の押さえを解く）
- 起票: [#1173](https://github.com/endazon/ai-stock-trading/issues/1173)（🟡。PoC 2026-10-05 の #856 BeforeSend 注入の観測）
- 基点コミット: `origin/develop` `b52dddcd`

## 目的・背景

#856 の BeforeSend 注入で作った AAPL 719 株の予約（送信されていない）について、リスク管理は承認時に新規建てとして台帳へ記録し、
その ET 取引日のあいだ「承認済みで終端でない新規建て」として数え続けた。AAPL は判断で Hold、他の銘柄は `MaxPositionsExceeded` で
LLM 前に見送られ、段階資金の残枠も約 $240k 減った。IADR-0488 は「新規建てなら BeforeSend でも建玉は生じない」とだけ書き、
Runbook「突合の判定を実機で確かめる」もこの拘束に触れていない。**PoC が注入する日を選べるよう、拘束の範囲・期間・解けない理由を書く。**

## 対象範囲

- 対象（文書だけ）:
  1. IADR-0488 の「結果」へ日付つき追記ブロック `［2026-10-06 追記 / #1173］`、`updated:` を前進。索引行（`.ai-context/adr/README.md`）へも同じ追記を足す。
  2. `docs/operations/broker-execution-paths-runbook.md` の「突合の判定を実機で確かめる」に拘束の節を足し、手順 10（後始末）に「予約行を消しても当日の拘束は解けない」を足す。trace ブロックへ #1173 と本仕様書を足し、`updated:` を前進。
- 対象外:
  - 所有者の操作で拘束を解く口（下の「要裁定」）。**実装しない。** 裁定までは現状（拘束を受け入れる）を正とする。
  - コードの変更・試験の追加・GitHub への投稿（裁定の起票は利用者が行う）。
  - 新しい IADR（実装の判断を新たにしていない。現状の挙動の記述と、裁定待ちの明示だけである）。

## 事実の確認（`origin/develop` `b52dddcd` のコードで確かめた）

| # | 事実 | 根拠（file:line） |
| --- | --- | --- |
| 1 | 拘束は**承認の時点**で始まる。リスク管理は承認を `approved_orders` に追記する（送信の成否と無関係） | `backend/Services/RiskManagementService/Infrastructure/Persistence/EfPortfolioLedgerStore.cs:25` |
| 2 | 統制の入力「承認済みで終端でない新規建て」は `approved_orders`（`PositionEffect.Open`）を `order_activity` へ左結合し、**行が無い**か `TerminalAt` が空のものを返す。予約（発注執行の DB）は読まない | `backend/Services/RiskManagementService/Infrastructure/Persistence/EfWorkingEntryOrderSource.cs:19-24` |
| 3 | `order_activity` の `TerminalAt` を立てるのは、終端の約定状態（OrderExecuted）・取消（OrderCancelled）・見送り（OrderDispatchForgone）だけ | `backend/Services/RiskManagementService/Infrastructure/Persistence/EfOrderActivityStore.cs:42-43, 63, 79`、ハンドラ `Infrastructure/Steps/OrderActivityProjectionHandlers.cs:36, 54, 67` |
| 4 | 届いたか不明（BeforeSend の注入はこの経路を通る）は、予約を Reserved のまま据え置き、確定も見送りも**発行しない**（例外で終わる） | `backend/Services/OrderExecutionService/Features/OrderExecution/DispatchApprovedOrder/OrderExecutionAppService.cs:423-446` |
| 5 | 突合が「未発注」と答えても、門が閉じていれば据え置く。門が開いていても `Release` は予約行を消すだけで、リスク管理へは何も発行しない | `backend/Services/OrderExecutionService/Features/OrderExecution/ReconcileOrderReservations/OrderReservationReconciler.cs:156-184`、`Infrastructure/Persistence/EfOrderReservationStore.cs:91-96`、`Hosted/OrderReservationReconciliationService.cs:151`（発行は「発注済み」確定の OrderExecuted だけ） |
| 6 | 算入は**承認時刻の市場の現地取引日が今日と同じもの**に限る。米国株は `America/New_York` の暦日（ET 0 時が境界） | `backend/Services/RiskManagementService/Features/RiskManagement/PortfolioProjection.cs:202-203`、`Common/Abstractions/TradingDay.cs:32-33, 47-48` |
| 7 | 算入されるもの: 日次発注累計（`DailyOrderedAmount`）と段階資金の累計（`InvestedCapital`）へ「残数量 × 承認価格（基準通貨）」、保有建玉数へ 1（その銘柄に約定済みの建玉が無いとき） | `PortfolioProjection.cs:130-148`。同日再エントリーの入力（`SymbolsTradedToday`）には入れない（`:148`） |
| 8 | 審査: 段階資金上限 `InvestedCapital + 発注代金 > 段階の発注可能額` で `StageCapitalCapExceeded`、日次発注枠で `DailyOrderAmountExceeded`、保有建玉数 `OpenPositionCount >= MaxOpenPositions` で `MaxPositionsExceeded` | `backend/Services/RiskManagementService/Domain/RiskEvaluator.cs:118-124, 280-293`、`Domain/EntryStateBlockers.cs:47-48, 96-97` |
| 9 | 判断の入力: 段階の残枠は `発注可能額 − InvestedCapital`（LLM の「段階残枠」） | `backend/Services/RiskManagementService/Features/RiskManagement/GetSizingContext/SizingContextService.cs:21` |
| 10 | 判断: 保有 0・未約定なしの銘柄は、審査で必ず落ちる（`MaxPositionsExceeded` を含む）なら LLM を呼ばずに見送る | `backend/Services/TradeDecisionService/Features/TradeDecision/DecideTrade/TradeDecisionAppService.cs:374-391` |
| 11 | 判断: 注入した銘柄は「未約定の新規建て注文あり」として提示され、同方向の新規建てを重ねない規則が付く（観測の AAPL Hold） | `TradeDecisionPromptBuilder.cs:126-132`、入力は `GetWorkingEntryOrders/WorkingEntryOrdersService.cs`（統制と同じ `ProjectWorkingEntries`） |
| 12 | 拘束を解く自動の経路は、ET の日付が変わること（事実 6）**だけ**である。突合・門・予約行の削除はいずれもリスク管理の算入を変えない（事実 2・5） | 上の各行 |

結論:
- **期間**: 承認の時点から、承認時刻の ET 暦日が終わる（ET 0 時）まで。予約の状態（Reserved のまま・突合の判定・人による予約行の削除）に依らない。
  翌 ET 日には算入されなくなる。予約そのものは門が閉じている限り Reserved のまま残る（統制の枠は占有しない）。
- **止まるもの（その ET 日）**: 保有建玉数 1 件（上限到達なら、保有 0 の他銘柄は LLM 前に見送り・審査でも拒否）、段階資金と日次発注枠のうちその承認の発注代金ぶん
  （2026-10-05 の観測では約 $240k＝段階の発注可能額の約 25%）、注入した銘柄の同方向の新規建て（判断が「未約定あり」として Hold を選ぶ）。
- **止まらないもの**: 手仕舞い・保護逆指値・損切り（統制は新規建てだけに掛かる）。翌 ET 日以降の取引。

## 要裁定（実装しない。利用者が起票する）

注入したと分かっている予約の拘束を、所有者の操作で解く口を設けるか。二重発注の防止（「不明なら発注済みとして扱う」）に触れるため裁定が要る。
**裁定までは a（拘束を受け入れる）を正として文書に書く。**

| 案 | 内容 | 利点 | 欠点・危険 |
| --- | --- | --- | --- |
| a | 口を設けない。拘束を受け入れ、注入は取引を減らしてよい日に行う（現状） | コード変更なし。二重発注の原則に一切触れない。拘束は ET 日で自然に解ける（事実 6） | 注入した日の新規建てが止まるか極小になる。PoC の日程が制約される |
| b | 所有者のコマンド: 指定の Reserved・未送信の `DecisionId` について、証券会社に注文が無いことを照会で確かめてから、予約を Forgone へ移し `OrderDispatchForgone`（「確実に未発注」に分類する新しい理由）を発行する。リスク管理は既存の見送りの経路（`OrderDispatchForgoneLedgerHandler` → `MarkForgone`・`RecordForgone`）で押さえを解く | 当日中に枠を戻せる。既存の見送りの経路（予約を消さず Forgone へ移す＝IADR-0398 と同じ形）を再利用でき、新しい台帳の書き手は増えない | 人の操作で「未発注」を主張する口を作る。照会の誤り（備考の往復しない注文・遅着）で生きた注文の押さえを解くと二重発注。ADR-0045 の門（実機の記録が揃うまで未発注の解放を自動化しない）と同じ論点を、所有者の操作という別の扉で開けることになる。`ConfirmsNoOrderPlaced` の allowlist（`Domain/OrderDispatchForgoneLifecycle.cs:51`）へ理由を足す判断も要る |
| c | 故障注入のスイッチで作った予約に印を付け、ET 日の切り替わりで自動に失効させる（Forgone へ移し見送りを発行） | 人の操作が要らない。印は注入の発火時にしか付かないので自然発生の予約には効かない | 拘束が解けるのは ET 0 時＝**リスク管理の算入が自然に切れる時刻と同じ**で、当日の取引への効果は無い（得るのは予約行の後始末の自動化だけ）。故障注入の判定が予約・突合のコードへ漏れる（IADR-0488 決定4「発注執行・予約・突合は 1 行も変えない」に反する）。印の付け忘れ・誤付与の検証が新たに要る |

推奨（参考。裁定は利用者）: **a**。拘束は ET 日で自動に解け、PoC は注入日を選べば影響を避けられる。c は当日の取引に効かず IADR-0488 の決定4 を崩す。
b は効果があるが、門（ADR-0045）が閉じている理由そのもの（「未発注」の判定の実機の記録がまだ無い）を所有者の操作で迂回する形になるため、
少なくとも #856 の BeforeSend/AfterSend の記録が期待どおりに揃い門を開ける判断が出た後に、門と合わせて検討するのが筋である。

## 設計（文書の変更）

1. IADR-0488「結果」の末尾に `［2026-10-06 追記 / #1173］` のブロック: 事実の要約（期間・止まるもの・自動では解けない理由）と、裁定までは現状を受け入れる旨・案 a/b/c は本仕様書。
   決定の本文は書き換えない（凍結）。「残余」の BeforeSend の行も書き換えず、追記ブロックから参照する。
2. Runbook「突合の判定を実機で確かめる」に小節「BeforeSend の注入はその日の新規建ての枠を占有する」を足す（表示テキストに計画 ID・IADR・issue を書かない）:
   - 何が・いつまで（ET 0 時）・その日の取引への効果。
   - 運用: 注入日は新規建てが減ってよい日を選ぶ／取引を観察したいセッションでは注入しない／ET の終盤に注入すれば占有は短い（ただし突合の判定までの最悪 3 時間と通常取引時間の終わりの兼ね合い）。
   - 確かめ方: リスク管理の DB の読み取り（`approved_orders` と `order_activity` の結合）、`GET /risk-controls/working-entry-orders`。
   - してはならないこと: リスク管理の DB（`approved_orders`・`order_activity`・`trade_fills`）を手で書き換えて枠を戻さない（統制の台帳であり、手順が無い）。
     予約行の削除（既存の手順 10）は枠を戻さない。承認を `_error` キューから再投入しない（既存）。
   - 現状は拘束を受け入れる運用であり、解く口は無い（判断待ち）。
3. 手順 10 の BeforeSend に「予約行を消しても当日の枠は戻らない（ET 0 時に戻る）」を 1 文足す。

## 受け入れ基準

- [x] 事実 1〜12 を file:line で確かめ、本仕様書に記録した。
- [ ] IADR-0488 の結果欄に `［2026-10-06 追記 / #1173］` があり、`updated: 2026-10-06`。索引行にも同じ追記がある。
- [ ] Runbook に拘束・期間・効果・運用・確かめ方・してはならないこと・判断待ちが書かれ、表示テキストに計画 ID・IADR・issue 参照が無い。trace ブロックに #1173 と本仕様書がある。`updated: 2026-10-06`。
- [ ] `check-trace-blocks`・`gen-knowledge-graph --check`・`check-cross-repo-refs`・`check-plan-id-qualification`・`check-doc-links`・`check-adr-index-sync`・`check-adr-index-addendum-loss`・`check-commit-messages`・`scripts.test.js`・gitleaks が通る。

## 母集合（規則 9・10）

- 「BeforeSend」の拘束を書くべき文書: `git grep -c "BeforeSend" -- docs .ai-context/adr` → Runbook（9）、IADR-0488（8）、索引行（1）、試験仕様書 `docs/tests/FR-10_risk-controls-tests.md`（4。故障注入の試験の記述で、統制の挙動を主張しないので対象外）。
  `git grep -c "故障注入" -- docs` で足される運用仕様書 `docs/operations/operations.md`（1）は Runbook への参照だけで手順を持たないので対象外。
- 「建玉は生じない」（誤解を招く側の文字列）: BeforeSend の文脈では IADR-0488 決定3 の 1 箇所だけ（凍結。追記ブロックで補う）。他のヒット（IADR-0355・FR-10 機能仕様書・試験仕様書）は乖離の取り込み・保護レグの文脈で無関係。
- 本変更で新たに誤りになる自分の記述: Runbook 手順 10 の「予約行を消す」は後始末として正しいが、枠が戻ると読める余地があるため 1 文を足す（設計 3）。

## 検証

- 文書の変更だけのため、ビルド・単体試験は対象外（`/verify` のうち文書系の検査を実行）。
