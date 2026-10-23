---
title: 日報の無応答で「停止」を選んでいれば期限後の取引判断の全体を見送り、継続・停止のどちらでも無応答を知らせる（#1423 M-6）
type: spec
status: done
related_ids: [FR-07, FR-04, FR-09, FR-11, UC-01, UC-02, UC-03, ADR-0075, ADR-0003, ADR-0070, ADR-0062, IADR-0071, IADR-0096, IADR-0462, IADR-0583]
author: claude (Claude Code)
created: 2026-10-24
updated: 2026-10-24
---

# 仕様書: 日報の無応答時の「停止」を効かせ、無応答を知らせる（#1423 M-6）

> 本仕様書は AST `origin/develop` `c9471235` を起点に、実装の前に作成した。判断は [IADR-0583](../adr/IADR-0583_report-no-response-halt-flag-and-notice.md) に残す。

## 起点（トレーサビリティ）

- 起点: #1423 M-6。環流 planning#831、裁定 2026-10-23（利用者）→ 計画 ADR-0075（Accepted）。
- 計画: ADR-0075 決定 1〜4、FR-07（Must。無応答時は継続〔既定〕・設定で「停止」）、UC-03、`04_workflows/03_reporting-cycle`。
- 前提（覆さない）: IADR-0071 決定 2（`Reports:NoResponseBehavior`・`ReportNoResponsePolicy.Decide`）、IADR-0096（日報未確定の通知）、
  IADR-0462（LLM を呼ぶ前の見送りの出口）、ADR-0070 決定 2（閉場前の手仕舞いは方針を読めないとき全建玉を手仕舞う）。
- 範囲外: SC-03 の表示（計画が求めていない）。日本株の開場（ADR-0062 で無効）。

## 受け入れ基準

1. **期限**: 日報 D の期限は、D の生成境界（`Reports:AutoGeneration` の日報の時刻・JST）より**後**の最初の**米国市場の開場**（`MarketHours.NextOpen`）。
2. **承認待ち**: 最新の確定済み日報より新しい（開始日が大きい）未確定の日報があること（確定済みの日報の再確定〔`/policy` の改訂〕の下書きは含めない）。
3. **停止**: 設定が `Halt` で、承認待ちが期限を過ぎたら、取引判断は定時・価格変動の両方で**全体を見送る**（新規建ても出口の判断も。LLM を呼ばない）。
   - 🔴 **方針の照会は直近の確定済み日報を返したまま**、停止の印（`NoResponseHalted`）を**別に載せる**（REST・gRPC）。null を返す形にすると、
     閉場前の手仕舞いの常駐が「方針を読めない」と読み、有効なら全建玉を手仕舞う（ADR-0075 決定 2 と食い違う）。
   - 見送りの理由は新設の `DailyReportNoResponseHalted`（台帳・計数）。日報未確定の通知（「確定済みの日報がない」）は鳴らさない。
4. **再開**: 日報を確定すれば、次の照会から印は消える（利用者の再開操作なし）。
5. **保護は止めない**: 損切り・利用者の手仕舞い・閉場前の手仕舞い・維持率割れの自動縮小は判断の合流点を通らない（印も読まない）。
6. **通知**: 継続・停止のどちらでも、期限を過ぎた承認待ちを報告書サービスの常駐（5 分）が検知し、**期間キーごとに 1 回**
   `DailyReportNoResponse` を発行する（Discord: 継続は Warning、停止は Critical。監査に記録）。
7. 既定（継続・未設定・不正値）は従来どおり取引を続ける（印を立てない）。

## 母集合（規則 9）

| 引いた文字列 | 場所 | 判定 |
| --- | --- | --- |
| `NoResponseBehavior`・`ReportNoResponsePolicy` | `Program.cs`（ログだけ）・`ReportNoResponsePolicy.cs` | 結線する（評価器・常駐）。「スケジューラ #22 の seam」の注記を是正 |
| `GetConfirmedDailyPolicy` | REST・gRPC・取引判断の Http/Grpc の受け手・契約テスト | 印を足す（末尾・既定 false） |
| `IDailyPolicyProvider` の消費者 | 判断の合流点・閉場前の手仕舞い・Stage 0 の記録（as-of） | 合流点だけが印を読む。手仕舞いは読まない。as-of は対象外 |
| `DecisionForgoneBeforeLlmReason` / `DecisionSkipReason` | 写像・語彙の試験・文書 | 末尾へ足して追随 |
| 新イベント | 通知・監査・契約の基準・型名・標本 | 追随 |

## 試験の写像（T-10-3054〜）

各試験の ID と対応は、テスト仕様書（`docs/tests/FR-10_risk-controls-tests.md` の該当節）に置く。
