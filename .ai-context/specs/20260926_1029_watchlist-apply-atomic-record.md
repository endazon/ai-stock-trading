---
title: 監視銘柄の案の適用で、押し直しの挙動を文書と応答文で明かし、適用結果の記録を EF の上で原子的にする（#1029）
type: spec
status: accepted
related_ids: [FR-13, FR-14, FR-09, FR-07, ADR-0042, IADR-0432, IADR-0433, IADR-0240]
author: claude (Claude Code)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0042_discord-apply-ai-watchlist-proposal-and-revision-limit.md (決定 1・3)
---

# 仕様書: 監視銘柄の案の適用の押し直しと、適用結果の記録の原子性（#1029）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-13（監視銘柄の変更の監査）、FR-14（Discord の修正指示・例外は ADR-0042 決定 1 の 1 つ）、FR-07
- 関連 ADR: ADR-0042 決定 1（確定した案だけを適用・一部適用の内訳を監査ログへ）・決定 3（台帳）
- 関連 IADR: IADR-0433（適用・決定 7）、IADR-0432（試行の台帳・`SaveOrDetach`）、IADR-0240（窓口の版番号ガード＝層1）
- 起点 Issue: #1029（#1027 の再監査の非ブロック指摘 2 件＋追記 2 件）。新しい IADR は起こさず、IADR-0432・IADR-0433 へ日付つき追記を置く。

## 母集合（規則 1〜6・9）

- 台帳・ストアで `SaveChanges()` を直接呼ぶ箇所: `git grep -n "SaveChanges()" -- backend/Services/ReportService/Infrastructure/Persistence/*.cs`
  → `PolicyRevisionLedgers.cs` の 53（`Begin`）・74（`SaveOrDetach` 本体）・91（`MarkProposalConfirmed`）・108（`RecordWatchlistApply`）、
  `EfReportStore.cs` の 57（新規作成・既に `catch` で追跡を消す）・107（改訂。N1 の対象）・145（`Confirm`）・168（`ApplyReview`）。
  - 本件で `SaveOrDetach` へ寄せる: 91・108（issue の指摘）と 53（`Begin`。本番の呼び手は無く試験の種まきだけだが、同じ台帳の書き込みなので揃える）。
  - 除外: `Confirm`・`ApplyReview`（報告書の行の保存。同じ要求の中で後に続く台帳の書き込みが無い。`Confirm` の後の `MarkProposalConfirmed` は
    別の行で、`Confirm` が失敗すればそこへ到達しない）。57 は既に `ChangeTracker.Clear()` 済み。
- 例外の型で追跡の片付けを絞っている箇所: `git grep -n "catch (DbUpdate" -- backend/Services/ReportService` → `EfReportStore.cs:109`（N1）と
  `ReportEndpoints.cs:55`（409 への写像。片付けではない＝除外）。
- 押し直しの応答文: `git grep -n "この操作では確定していない"` → `PolicyApprovalCommandHandler.cs:53` の 1 件だけ。
- 他レーンとの領域: report-service の変更は `PolicyRevisionLedgers.cs`・`EfReportStore.cs`・`ReportDbContext.cs`（台帳の行の設定 1 行）と
  マイグレーションだけ。`PolicyApprovalCommandHandler.Breakdown`（Finnhub の推定の文言。ADR-0043 のレーン）には触らない。

## 決定する挙動

| # | 状況 | 挙動 |
| --- | --- | --- |
| 1 | `/policy` の確認ボタンで、この要求では確定を確かめられなかった（同じプロセスで先に `/report approve` 済み・照会の失敗の後の押し直し・版落ち） | 従来どおり案を引かず適用しない。応答に「この版を既に確定していて入れ替えがまだなら、設定画面から変更してください」を添える |
| 2 | 同じ試行の適用の内訳を 2 つの書き手（別の DbContext）が同時に記録する | 先に保存した方だけが記録し `true`。後の方は `WatchlistAppliedAt` の同時実行トークンで衝突し、**上書きせず** `false`（409「記録済み」）。行は追跡から外す |
| 3 | `RecordWatchlistApply`・`MarkProposalConfirmed`・`Begin` の保存が失敗する | 行を追跡から外してから例外を上げる（`SaveOrDetach`。#1026 の F1 と同じ） |
| 4 | `EfReportStore.UpsertDraft` の改訂の保存が `DbUpdateException` 以外（接続の失敗等）で失敗する | 例外の種類を問わず変更の追跡を消してから上げる（N1）。続く台帳の SaveFailed の保存が失敗した下書きを保存しない |

- 1 のサーバー側の案（報告書サービスが「この版で確定済み・未適用」と返したら、窓口の二重押下でも適用する）は採らない。理由は IADR-0433 の追記。
- 2 は一意制約（PeriodKey, ReportVersion）ではなく同時実行のトークンを選ぶ。理由は IADR-0432 の追記。マイグレーション
  `PolicyRevisionWatchlistApplyConcurrency` はスキーマを変えず、モデルのスナップショットだけを進める（トークンは SQL の WHERE 句の問題で列は同じ）。

## 受け入れ基準 → テスト（T-10-1480〜T-10-1489）

| # | 基準 | テスト |
| --- | --- | --- |
| 1 | 確定を確かめられなかった押下では案を引かず、応答が設定画面を案内する（同じプロセスの `/report approve` の後・照会の失敗の後の押し直し） | `PolicyApprovalCommandHandlerTests`（T-10-1480） |
| 2 | 別の DbContext の 2 つの書き手: 後の方は `false`・先の内訳は上書きされない・後の方の DbContext は続けて保存できる | `PolicyRevisionLedgerTests`（T-10-1481） |
| 2 | 同じ DbContext の 2 つの書き手: 後の方は `false`・先の内訳のまま | 同（T-10-1482） |
| 2 | 行モデル: `WatchlistAppliedAt` が同時実行のトークン（Npgsql のモデル） | 同（T-10-1483） |
| 3 | `RecordWatchlistApply` の保存の失敗で行を切り離す（続く保存が失敗した内訳を保存しない） | 同（T-10-1484） |
| 3 | `MarkProposalConfirmed` の保存の失敗で行を切り離す | 同（T-10-1485） |
| 3 | `TryBegin` の保存の失敗で行を切り離す（続く保存が失敗した試行を保存せず、数にも入らない）＝N2 | 同（T-10-1486） |
| 4 | 改訂の保存が `DbUpdateException` 以外で失敗しても、下書きは保存されず台帳は SaveFailed＝N1 | 同（T-10-1487） |

- 変異注入で各試験が効くことを確かめ、結果を `docs/tests/FR-10_risk-controls-tests.md` に記録する。

## 完了の定義

- `dotnet build` / `dotnet test`（ReportService・NotificationService）/ `dotnet format --verify-no-changes` が通る。
- 静的検査（trace ブロック・試験のトレーサビリティ・コミット規約）が通る。
