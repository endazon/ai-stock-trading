---
title: 効いている確定済み日報は /policy で版を足して再確定できる。なお効いているかの判定と確定を 1 保存操作で行い、移った後は確定・改訂・作り直しを断り、通知と KB を追随させる（#1373 段 B）
type: spec
status: done
related_ids: [FR-07, FR-14, FR-09, FR-08, FR-15, FR-06, UC-03, ADR-0068, ADR-0003, ADR-0042, ADR-0052, ADR-0065, ADR-0033, IADR-0431, IADR-0432, IADR-0491, IADR-0526, IADR-0548, IADR-0549]
author: claude (Claude Code)
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0068_daily-policy-reconfirm-by-adding-version.md (決定 1・5・6・7、フォローアップ 1・2 の残り)
  - planning:projects/ai-stock-trading/07_adr/ADR-0065_report-draft-saved-as-unindexed-document-viewed-on-sc03.md (決定 1・2・5)
  - planning:projects/ai-stock-trading/07_adr/ADR-0052_report-regenerate-by-owner-command.md (決定 1・2・3)
  - planning:projects/ai-stock-trading/07_adr/ADR-0042_discord-apply-ai-watchlist-proposal-and-revision-limit.md (決定 3)
---

# 仕様書: 効いている確定済み日報の再確定（#1373 段 B）

> 本仕様書は実装着手前に作成した（着手 2026-10-17）。基点は AST `origin/develop` `da66f0a1`（段 A＝PR #1374 のマージ後）。
> 計画: ADR-0068（planning `origin/main` `55e464a`）。段 A は [20261017_1373_daily-policy-version-history](20261017_1373_daily-policy-version-history.md)・IADR-0548。

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **FR-07**（確定前の方針は適用しない）、**FR-14**（`/policy`）、FR-09（通知）、FR-08（KB）、FR-06（作り直し）、FR-15（Stage 0）、UC-03。
- 計画 ADR: **ADR-0068 決定 1・5・6・7**、フォローアップ 1・2 の残り。前提: ADR-0003・ADR-0042 決定 3・ADR-0052 決定 1〜3・ADR-0065 決定 1・2・5・ADR-0033 決定 2。
- 起票: #1373（`Closes #1373`）。環流 planning#803。
- 新規 IADR: **IADR-0549**（develop の最大は IADR-0548）。IADR-0431 の「確定済みは改訂しない」は日付付きの追記で改める。

## 母集合（規則 9）

段 A の仕様書の母集合の表（17 行）のうち、段 B で扱うのは 5・7・11・13・14 である。走査は段 A と同じ（`GetLatestConfirmed|ReportState.Confirmed|store\.(Get|List)`）
に、`AlreadyConfirmed`・`ReportConfirmed(`・`ReportDraftPresented(`・`IsDraftCopy`・`reportState` を足して引き直した。

| # | 箇所 | 段 B の扱い |
| --- | --- | --- |
| 5 | `ReportPolicyRevisionService.ResolveTarget`（確定済みは 409） | 効いている確定済み日報なら版 n+1 のドラフト（`StartRevisionDraft`）。それ以外の確定済みは 409（文を改める）。移った後の版 n+1 は 409（NoLongerEffective） |
| 5' | 同 `AutoDailyPending` の文 | 効いている日報の会話キーと再確定のやり方（承認待ちの版があればその版）を書き足す |
| 7 | `ReportRegenerationService`（確定済みは断る） | 確定済みは従来どおり断る。移った後の版 n+1 は断る（NoLongerEffective）。効いている間の版 n+1 は作り直せる |
| 11 | `ReportKnowledgeReingestService`（`List()` の確定済み） | 期間キーごとの最新の確定版（写し）を対象にする。失効の印つきのドラフトの写しは消さない |
| 13 | 一覧・取得・レビュー（REST・`ReportOwnerRead`）・通知サービスの `/report show` | 一覧・取得は行の現在の状態のまま。レビューに `confirmedVersion`・`noLongerEffective` を足し、`/report show` が効いている版と確定の可否を出す |
| 14 | 確定時の KB 保存（`ConfirmReport/Endpoint`） | 再確定は確定版の写しを最新の確定版で置き換える（入れ直しを 1 つの期間キーで）。初回は従来の保存 |
| 14' | `ReportConfirmed` / 通知 | 確定した版と、再確定ならそれまでの版を運び、通知が「効いている版が変わった」と書く |
| + | `/policy` の版を足したドラフト | 提示の通知（`ReportDraftPresented` の `ReconfirmingFromVersion`）で「確定まで版 n が効く」と書く |
| + | 効いている日報が移ったとき | 移る前の日報の版 n+1 のドラフトの写しを失効の印つきで残す（ADR-0065 決定 5） |
| + | `Stage0DecisionRecord` | 方針の期間キーと版を持たせる（下の判断） |
| + | `IReportStore` の実装（EF・InMemory）と試験の代役 4 件 | `StartRevisionDraft`・`GetLatestConfirmedVersion`・`ListLatestConfirmedVersions` を足す |

除外: 母集合の表 1〜4・6・8〜10・12・15〜17 は段 A で済んでいるか、版 n+1 で答えが変わらない（6 は週報・月報の親だけ、12 は行の版の規則が保たれる）。

## Stage 0 の記録に方針の版を持たせるか（判断）

- `Stage0StrategyIdentity.ComputeContentHash` は記録の**列挙した項目だけ**を正規形に並べる。新しい項目は列挙に足さない限りハッシュに入らない。
- 方針の本文は入力の指紋（`InputFingerprint`）に既に入っており、同じ入力なら同じ指紋である。版番号を同一性に足すと、同じ入力の記録の戦略 ID が版の有無で変わる。
- したがって **戦略 ID の導出に影響しない形で持たせられる** → この PR に入れる（`Stage0DecisionRecord.Policy`。末尾・既定 null・ハッシュに入れない）。別 issue は起こさない。

## 方針

1. **保存 API**（IADR-0548 の案のとおり）: `StartRevisionDraft(draft, expectedVersion)` と、`Confirm` の中の「なお効いている」の判定。どちらも 1 回の保存。
   - `Confirm`: 写しの通し番号の最大 S を**最初に**読み、同じ期間キーに確定版があれば（＝再確定）S までの写しで効いている期間キーを確かめる。違えば `ReportNoLongerEffectiveException`（409）。
2. **`/policy`**: 上の母集合の 5・5'。版を足したときの応答の文に「確定までは版 n が効き続ける」を書き、提示の通知を出す。回数は既存の枠（IADR-0432）。
3. **作り直し**: 母集合の 7。回数は作り直しの枠（ADR-0052 決定 1）。
4. **読み手**: 母集合の 11・13・14・14'。
5. **ADR-0065 の写し**: 版 n+1 のドラフトの写しは従来の経路（承認待ちで 1 件・確定で確定版の後に消す）。失効は `reportState=draft` を保ったまま `reportDraftStatus=expired` を持つ写しを作り、元の写しを消す（属性は作成の 1 回だけで持つ規律。基盤の棚卸しは `reportState=draft` 以外を確定版の写しに数えるため、`reportState` は変えない）。
6. **本文の状態の行**: 版 n+1 の本文は確定済みの本文の状態の行だけを未確定へ戻す（`ReportBodyStatus.MarkDraft`）。

## 受け入れ基準 → 試験（ID は docs/tests の FR-10 §効いている確定済み日報に版を足して再確定する）

| 基準 | 試験 |
| --- | --- |
| 効いている確定済み日報への `/policy` は版 n+1 のドラフト・確定まで版 n が効く・通知 | T-10-2685（`ReportPolicyRevisionServiceTests`） |
| 未確定の版 n+1 への `/policy` はそのドラフトを改訂・確定で効く版が変わる | T-10-2686 |
| 移った後は確定・改訂を断る（ドラフトは残す） | T-10-2687・T-10-2692・T-10-2693 |
| 効いていない確定済みの 409 の文・長さ | T-10-2688・T-10-1314・T-10-1524 |
| AutoDailyPending の案内 | T-10-2689 |
| 上限に達した日は再確定も断る | T-10-2690 |
| 本番の経路で `/policy` → 確定 → 取引判断の方針と版・as-of が再確定の前後で正しい版 | T-10-2691（`PolicyRevisionWiringTests`） |
| 並行した確定の競合（新しい日付が先に勝つ／版 n+1 が先に勝つ） | T-10-2693・T-10-2694（`ReportConfirmedVersionHistoryTests`） |
| 版を足せるのは効いている確定済み日報だけ | T-10-2695 |
| 作り直しは効いている間だけ | T-10-2696（`ReportRegenerationServiceTests`） |
| KB の置き換え・初回は従来・入れ直しは確定版・失効の写しは残る | T-10-2697〜T-10-2700（`ReportReconfirmKnowledgeTests`） |
| 本文の状態の行の往復 | `ReportBodyStatusTests` |
| 通知の文（版を足した・再確定） | `NotificationFormatterTests` |
| `/report show` の表示 | `HttpReportReviewControllerTests` |
| Stage 0 の記録の方針の版・戦略 ID を変えない | T-15-140（`Stage0DecisionRecorderTests`・`Stage0DecisionRecordTests`） |
| イベント・proto は非破壊 | `event-schemas.baseline.json`・`ReportConfirmedContractTests`・`scripts/proto-contract-baseline.json` |

## 検証

- `dotnet test`: ReportService・NotificationService・TradeDecisionService・Shared.Contracts の試験プロジェクト。AuditService・BacktestService はビルド。`dotnet format --verify-no-changes`。
- 変異（RC1〜RC3。docs/tests に記録）。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` と文書系の検査。

## 残余

- 版を足すドラフトの作成時の判定は保存の外で行う（並行すると効いていない日報に版 n+1 のドラフトができ得るが、確定は必ず断る）。
- 再確定の写しの置き換えは、入れ直しが実行中なら行わない（次の入れ直しで揃う）。
- 一覧・取得は行の現在の状態（版 n+1 のドラフト）を返す。効いている確定版はレビュー照会と `daily-policy` で読む。
