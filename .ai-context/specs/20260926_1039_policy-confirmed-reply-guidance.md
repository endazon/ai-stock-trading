---
title: /policy が確定済みの報告書に当たったときの返答に、いつ・どうすれば改訂できるかを書く（#1039）
type: spec
status: accepted
related_ids: [FR-14, FR-09, FR-07, FR-13, SC-02, ADR-0003, ADR-0042, IADR-0431, IADR-0420]
author: claude (Claude Code)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0042_discord-apply-ai-watchlist-proposal-and-revision-limit.md
  - planning:projects/ai-stock-trading/05_screens/01_screens.md (SC-02 リスク設定画面・監視銘柄)
---

# 仕様書: /policy が確定済みの報告書に当たったときの返答（#1039）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-14（Discord の修正指示）、FR-09（通知）、FR-07（方針の確定は利用者との対話。ADR-0003）
- 画面: SC-02（リスク設定画面。監視銘柄の変更 UI は実装済み）
- 関連 IADR: IADR-0431（`/policy`。確定済みは改訂しない＝409）、IADR-0420（越境の契約テスト）
- 起点 Issue: #1039。新しい IADR は起こさない（挙動＝状態・保存の有無は変えず、返答の文だけを変えるため）。

## 事象と原因

2026-09-26（土）に `/policy` を実行すると「報告書 daily-2026-09-26 は確定済みのため改訂できません（確定済みの方針は変えられません）」
だけが返った。挙動は IADR-0431 のとおり（当日の日報が同日朝に確定済み）だが、利用者が次に何をすればよいかが書かれていない。

## 母集合（規則 1〜6・9）

- 文の出どころ: `git grep -n "確定済みのため改訂"` → `backend/Services/ReportService/Features/Reports/ReportPolicyRevisionService.cs` の
  1 箇所（`ResolveTarget` の AlreadyConfirmed）と `docs/blocked-tasks.md` の再測定手順（「確定済みのため改訂できません」が返ること）。
  - 通知サービス（`HttpPolicyRevisionController`）は 409 の `error` を**そのまま**見せる（300 文字で切る）。文は報告書サービスが作るので、
    変えるのは報告書サービスだけ。通知サービスの本体は変えない（切られないことを契約テストで固定する）。
  - `docs/blocked-tasks.md`: 文の先頭（「確定済みのため改訂できません」）を残すので、再測定手順の記述は真のまま。変えない。
- 他の「確定済み」の拒否: `ReportEndpoints.cs:121`（レビューの状態機械の AlreadyConfirmed「確定済みの報告書は変更できません。」）は
  `/report approve` 等のレビュー操作の応答であり `/policy` の返答ではない。対象外。
- 文の中身を裏づける規則（推測しないため、実装から引いた）:
  - `/policy` の対象は会話キー省略時は当日（JST）の日報（`ReviseAsync`）。
  - 当日の日報が無いとき: 営業日かつ自動生成が有効なら作らない（AutoDailyPending・生成境界 `Schedule.DailyAt` の後に自動生成される）。
    それ以外は直近の確定済み日報を土台に作る（`ResolveTarget` の新規作成の分岐）。
  - 自動生成の日報の方針は同種別の直近確定済み日報の継続案（`ReportAutoGenerator.GenerateAsync` の `previous` → `ReportPolicyDraft.CarryOver`）。
  - 未確定のドラフトは `/policy` で改訂できる（`ResolveTarget` の既存分岐）。
  - 監視銘柄の変更は SC-02（リスク設定画面。フロントの表示名「リスク設定」）。通知サービスの既存文は「設定画面から変更してください」。

## 決定する挙動

| # | 状況 | 返答（先頭は従来の文のまま） |
| --- | --- | --- |
| 1 | 対象が当日（JST）の日報・明日が営業日・自動生成が有効 | 「明日（YYYY-MM-DD・JST・営業日）は HH:mm JST 以降に、直近の確定済み日報の方針を引き継いだ日報 daily-… のドラフトが自動生成され、その後の /policy で改訂できます。」 |
| 2 | 対象が当日の日報・明日が休場日（週末・構成の休場日）または自動生成が無効 | 「明日（YYYY-MM-DD・JST）に /policy を実行すると、直近の確定済み日報を土台に日報 daily-… のドラフトを作り、それを改訂できます。」 |
| 3 | 対象が当日以外の会話キー | 「period を省略した /policy は当日（JST）の日報 daily-… を対象にします。」 |
| 共通 | 自動生成が有効な構成 | 「自動生成の日報のドラフトがある日は、確定するまでそのドラフトを /policy で改訂できます。」 |
| 共通 | すべて | 「監視銘柄はいまでもリスク設定画面から変更できます。」 |

- 明日＝JST の暦日 +1（`clock.UtcNow` を JST へ寄せた当日から計算）。時刻は構成値 `Schedule.DailyAt`。日付・時刻を文字列で埋め込まない。
- **409（AlreadyConfirmed）・AI を呼ばない・何も保存しない・試行の台帳に数えない**は変えない。
- 長さ: 会話キーが上限（32 文字）でも通知サービスの上限 300 文字に収まる（試験で固定）。

## 受け入れ基準 → テスト（T-10-1520〜T-10-1525）

| # | 基準 | テスト |
| --- | --- | --- |
| 1 | 営業日の明日: 境界の時刻（構成値）・明日の会話キー・自動生成・リスク設定画面を伝え、AI を呼ばない。明日の境界の前は実際に AutoDailyPending | `ReportPolicyRevisionServiceTests`（T-10-1520） |
| 2 | #1039 の事象（土曜）: 明日（日曜）の実行時に作ると伝え、実際に日曜の `/policy` は確定済みの方針を土台に作る | 同（T-10-1521） |
| 1 | 明日は JST の暦日で数える（UTC の日付ではない） | 同（T-10-1522） |
| 2・3 | 自動生成が無効な構成・当日以外の会話キー・32 文字の会話キーで 300 文字以内 | 同（T-10-1523） |
| 共通 | 本番の組み立てで 409 のまま・`error` に手段・LLM を呼ばず版と状態は変わらない | `PolicyRevisionWiringTests`（T-10-1524） |
| 共通 | 通知サービスは送り手の本物の文を切らずに見せる | `HttpPolicyRevisionControllerTests`（T-10-1525） |

## 完了の定義

- `dotnet test`（ReportService・NotificationService の該当テスト）と `dotnet format --verify-no-changes` が通る。
- 静的検査（trace ブロック・試験のトレーサビリティ・コミット規約）が通る。
