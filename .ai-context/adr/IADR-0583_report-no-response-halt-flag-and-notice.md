---
title: IADR-0583 日報の無応答時の「停止」は、方針の照会が直近の確定済み日報を返したまま停止の印を載せ、取引判断の合流点が全体を見送る。期限は日報の生成境界の後の最初の米国市場の開場で数え、継続・停止とも報告書サービスの常駐が期間キーごとに 1 回通知する
type: impl-adr
status: Accepted
related_ids: [FR-07, FR-04, FR-09, FR-11, UC-01, UC-02, UC-03, ADR-0075, ADR-0003, ADR-0070, ADR-0062, IADR-0071, IADR-0096, IADR-0462, IADR-0582]
author: claude (Claude Code)
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0075_report-no-response-halt-skips-trade-decisions.md（決定 1〜4）
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md（FR-07）
related_specs:
  - ../specs/20261024_1423_report-no-response-halt.md
---

# IADR-0583: 日報の無応答時の「停止」を効かせ、無応答を通知する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-24
- 決定者: Claude Code（計画 ADR-0075 フォローアップ 1・2 が実装に委ねた点 —— 期限を数える市場・合流のさせ方・通知の文面と重複の抑え方）

## 起点・関連

- 起点: #1423 M-6。環流 planning#831、裁定 2026-10-23（利用者）→ 計画 ADR-0075（Accepted）。
- 前提（覆さない）: [IADR-0071](./IADR-0071_report-service-remaining.md) 決定 2（`Reports:NoResponseBehavior`・`ReportNoResponsePolicy.Decide`）、
  [IADR-0096](./IADR-0096_notify-daily-policy-unconfirmed.md)（日報未確定の通知）、[IADR-0462](./IADR-0462_ledger-position-query-status-and-pre-llm-skips.md)（LLM を呼ぶ前の見送りの出口）。
- 置き換える: IADR-0071 決定 2 の「期限検知→停止の実強制はスケジューラ（#22）が読む seam」（実装は無かった）。

## コンテキスト

1. develop `c9471235` では `Reports:NoResponseBehavior` を起動時にログへ出すだけで、`ReportNoResponsePolicy.Decide` の呼び出し元は試験だけだった。
   「停止」を選んでも止まらず、継続のときの無応答の通知も無かった。
2. 計画 ADR-0075: 停止は、承認待ちの日報が期限を過ぎた間、取引判断の全体を見送る（既存の「方針が無いときの見送り」へ合流させてよい）。
   損切り・利用者の手仕舞い・閉場前の手仕舞いは止めない。確定で自動再開。継続・停止とも無応答を通知する。期限を数える市場は実装が決める。
3. 🔴 閉場前の手仕舞い（ADR-0070 決定 2）は、方針を読めないとき（有効なら）全建玉を手仕舞う。方針の照会が null を返す形で停止を表すと、
   この分岐に当たる（ADR-0075 決定 2 は「停止の間も方針の照会は直近の確定済み日報を返す」前提で書かれている）。

## 決定

1. **期限**: 日報 D の期限は、D の生成境界（`Reports:AutoGeneration` の日報の時刻・JST。既定 16:00）より**後**の最初の**米国市場**の開場
   （共有カーネルの `MarketHours.NextOpen`。休場日・半日・夏時間は規則計算）。日本株は無効（ADR-0062）なので米国で数える。
2. **承認待ち**: 最新の確定済み日報より開始日が新しい未確定の日報（`IReportStore.GetLatestUnconfirmed`）。効いている日報の再確定の下書き
   （`/policy` の改訂。開始日は同じ）は含めない。状態は Drafting / PendingApproval / ChangesRequested を区別しない（確定していないことが条件）。
3. **停止の表し方**: 方針の照会（REST `GET /reports/daily-policy`・gRPC `GetConfirmedDailyPolicy`）は**直近の確定済み日報を返したまま**、
   `NoResponseHalted`（REST は末尾の bool・gRPC は `optional bool no_response_halted = 5`）を立てる。取引判断の合流点（`DecideAsync`）は、
   方針が在って印が立っていれば、LLM を呼ぶ前に見送る（理由 `DailyReportNoResponseHalted` を末尾に新設）。
   - 既存の `DailyPolicyUnconfirmed` へは合流させない: 同じ理由だと「確定済みの日報が無い」通知が鳴り、本文が事実と食い違う。台帳と計数で区別できる方が読める。
   - 閉場前の手仕舞い・Stage 0 の記録（as-of）は印を読まない。as-of の応答には印を載せない。
4. **通知**: 報告書サービスの常駐 `DailyReportNoResponseMonitorService`（既定 5 分・既定で有効。`Reports:NoResponseNotice:Enabled=false` で外す）が
   同じ判定（`DailyReportNoResponseEvaluator`）を読み、期限を過ぎた承認待ちを**期間キーごとに 1 回** `DailyReportNoResponse` として発行する。
   - 通知は停止なら Critical（取引判断を止めている）、継続なら Warning（古い方針で続けている）。監査は日報の方針と同じ固定の相関。
   - 重複の抑止は in-memory（IADR-0096 と同じ。再起動で同じ期間キーに最大 1 回重なる）。発行に失敗したら記録を戻して次の巡回で再試行する。
   - 確定（解除）は通知しない（`ReportConfirmed` の通知が既にある）。
5. 既定（継続・未設定・不正値）は印を立てない（従来どおり取引を続ける）。

## 結果・残余リスク

- 「停止」を選んだ利用者の意図どおり、期限を過ぎた未確定の間は取引判断が出ない。継続でも無応答が利用者に届く。
- 🔴 停止の間は保有中の銘柄の AI 判断による決済も出ない（ADR-0075 が受け入れたリスク）。
- 方針の照会のたびに未確定の最新行を 1 回引く（本文を読まない射影）。
- 通知の重複抑止は in-memory。日報の自動生成が無効でも、期限は既定の生成境界（16:00 JST）で数える。
