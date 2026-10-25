---
title: 確定済み日報の版の履歴を保存層に持ち、最新の確定版を二段で選び、Stage 0 は判断時点までに確定した版で組み直し、判断の記録に参照した方針の期間キーと版を残す（#1373 段 A）
type: spec
status: done
related_ids: [FR-07, FR-14, FR-15, FR-04, FR-11, UC-03, ADR-0068, ADR-0033, ADR-0052, ADR-0042, ADR-0065, ADR-0003, IADR-0431, IADR-0432, IADR-0528, IADR-0446, IADR-0541, IADR-0548]
author: claude (Claude Code)
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0068_daily-policy-reconfirm-by-adding-version.md (決定 2・3・4。決定 1・5・6 は段 B)
  - planning:projects/ai-stock-trading/07_adr/ADR-0033_stage0-evaluation-target-is-ai-decision-replay.md (決定 2)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-07 確定前の方針は取引に適用しない)
---

# 仕様書: 確定済み日報の版の履歴と二段の選び方・判断時点の組み直し・判断の記録の方針の版（#1373 段 A）

> 本仕様書は実装着手前に作成した（着手 2026-10-17）。基点は AST `origin/develop` `4b2ff956`。
> 計画: ADR-0068（planning `origin/main` `55e464a`。利用者裁定 2026-10-17・planning#803）。

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **FR-07**（確定前の方針は取引に適用しない）、FR-14（`/policy`）、FR-15（Stage 0）、FR-04 / FR-11（判断と監査の記録）、UC-03。
- 計画 ADR: **ADR-0068 決定 2・3・4**（本段）。決定 1・5・6 は段 B。前提: ADR-0033 決定 2・ADR-0052 決定 2・3・ADR-0042 決定 3・ADR-0065 決定 1・2。
- 起票: #1373（PoC 2026-10-17。確定した方針の矛盾を同日中に直せなかった）。環流 planning#803。
- 新規 IADR: **IADR-0548**（develop の最大は IADR-0547）。IADR-0431 の「確定済みは改訂しない」は段 B で改める（本段では参照だけ）。

## 段の分け方

- **段 A（本 PR・`Refs #1373`）**: 利用者から見た挙動は変えない。`/policy` は従来どおり確定済みを断る（409）。
  保存層に版の履歴を持たせ、選び方・判断時点の組み直し・判断の記録を版に対応させる。**ADR-0068 決定 3 の配備条件（Stage 0 が判断時点より後に確定した版を使わない）を本段の試験で固定する。**
- **段 B（別 PR・`Closes #1373`）**: `/policy` の再確定（決定 1）、判定と確定を 1 保存操作にすること、断る応答での会話キーの案内、通知（決定 6）、KB の置き換え、IADR-0431 の改め。

## 母集合（規則 9: 確定済み日報を読む箇所・選ぶ箇所）

`git grep -n -E "GetLatestConfirmed|ReportState.Confirmed|store\.(Get|List)\w*\(" backend/Services` と、他サービスからの `/reports/daily-policy`・`DailyPolicyRead` の照会を走査して引いた（記憶で挙げていない）。

| # | 箇所 | 読むもの | 段 A の扱い | 段 B で要る扱い |
| --- | --- | --- | --- | --- |
| 1 | `ReportAppService.GetConfirmedDailyPolicy`（REST `GET /reports/daily-policy`） | 最新の確定済み日報 | **履歴から二段で選ぶ**。応答に `periodKey`・`version` を足す | なし |
| 2 | `DailyPolicyReadGrpcService`（gRPC `DailyPolicyRead`） | 1 と同じ | `DailyPolicyRecord` に `period_key`・`version`（optional）を足す | なし |
| 3 | `ReportAppService.GetConfirmedDailyPolicyAsOf`（REST `as-of`） | 時刻 t までに確定した最新 | **履歴から、t までに確定した版だけで二段で選ぶ**。応答に `periodKey`・`version` を足す（`confirmedAt` は版の確定時刻） | なし |
| 4 | `ReportPolicyRevisionService`（:307 `GetLatestConfirmed(Daily)`） | 新規の日報の土台 | 履歴経由（答えは同じ） | 効いている日報の判定にもこれを使う |
| 5 | `ReportPolicyRevisionService`（:280-283 `Get` → 確定済みは 409） | 対象の行の状態 | 変えない | 効いている日報なら版 n+1 のドラフトへ |
| 6 | `ReportPolicyRevisionService`（:382）・`ReportRegenerationService`（:247） `Get(basedOn)` の親の方針 | 親の行が確定済みならその方針 | 変えない（日報の親は週報で、対象外） | 親が日報になる経路は無い。変えない見込み |
| 7 | `ReportRegenerationService`（:61-66 確定済みは断る） | 対象の行の状態 | 変えない | 版 n+1 のドラフトは作り直せる・移った後は断る |
| 8 | `ReportAutoGenerator`（:150/:152 `GetLatestConfirmed`） | 上位方針・前回の同種 | 履歴経由（答えは同じ） | 版 n+1 が確定していればその版を土台にする（二段の選び方で自動的に満たす） |
| 9 | `WeeklyGoalReferenceResolver`（週報だけ） | 週報 | 履歴経由（週報は 1 期間 1 版のまま） | なし（日報だけが対象） |
| 10 | `ReportAppService.BuildMonthlyBootstrap`（月報だけ） | 確定済み月報の有無 | 履歴経由 | なし |
| 11 | `ReportKnowledgeReingestService`（`List()` の確定済み） | 確定済みの行の本文 | 変えない（行の現在の状態） | 版 n+1 のドラフト中は行が Draft になる。**履歴の最新の確定版を入れ直す形へ改める** |
| 12 | `WatchlistProposal` の `IsConfirmedAtDraftVersion` | 行の版 | 変えない | 行の版の規則（確定は版を 1 進める）は保つ |
| 13 | 一覧・取得・会話キー一覧（REST・`ReportOwnerRead`） | 行の現在の状態 | 変えない | ドラフト中の表示（確定版と版 n+1 のドラフトの並び）を決める |
| 14 | 確定時の KB 保存（`ConfirmReport/Endpoint` → `ReportConfirmed`） | 確定した行 | 変えない | 最新の確定版で置き換える（決定 6） |
| 15 | TradeDecisionService `HttpDailyPolicyProvider`・`GrpcDailyPolicyProvider` | 1・2 の応答 | **期間キーと版を読み、`DailyPolicy.Reference` に持つ**（欠けていても方針は通す） | なし |
| 16 | TradeDecisionService `HttpAsOfDailyPolicySource`（Stage 0） | 3 の応答 | 同上 | なし |
| 17 | `TradeDecisionMade`・`TradeDecisionHeld`（監査台帳は payload を丸ごと保存） | 判断の記録 | **`Policy`（期間キー・版）を足す**（既定 null） | なし |

除外: `TradeDecisionForgoneBeforeLlm`（LLM を呼ぶ前の見送り。AI 判断をしていないので「判断が参照した方針」が無い。日報未確定の見送りもここに入る）。Stage 0 の記録（`Stage0DecisionRecord`）は判断時点の方針を as-of で組み直す側であり、記録の項目は変えない（戦略の同一性の導出を変えないため。下の残余）。

## 方針

1. **保存層（決定 2）**: 新しい表 `report_confirmed_versions` に、確定のたびに 1 行（確定した版の写し）を足す。
   - 鍵は `(Kind, Sequence)`。`Sequence` は種別ごとの確定の通し番号（1 から）。一意の索引 `(PeriodKey, Version)`。
   - 列: 期間キー・種別・開始日・版（確定後の行の版。`VersionedReport` の版と同じ）・確定時刻（非 null）・`BasedOn`・前提条件の版・方針の要約・本文・未供給の入力。
   - **確定済みの版の行は書き換えない**（ストアに更新の口を持たない）。`reports` の行（会話キーごとに 1 行）は従来どおり現在の状態（ドラフトまたは確定済み）を持つ。期間キーは変えない。
   - 確定は `reports` の行の更新と履歴の行の追加を **1 回の `SaveChanges`（1 トランザクション）** で行う。`Sequence` が衝突したら（同じ種別の確定が並行した）、追跡を外して読み直し、決め直す（最大 3 回）。
2. **二段の選び方（決定 2）**: 履歴から、1 段目は開始日が最大（同じ開始日は期間キーの降順）の期間キー、2 段目はその期間キーの中で確定時刻が最新、同時刻は版番号が大きい方。確定時刻だけで期間キーをまたいで並べない。
3. **判断時点（決定 3）**: `GetLatestConfirmedAsOf(kind, at)` は、確定時刻 ≤ at の版だけを対象に 2 と同じ二段で選ぶ。
4. **判断の記録（決定 4）**: 方針の応答（REST・gRPC・as-of）に期間キーと版を足し、取引判断は `DailyPolicyReference(PeriodKey, Version)` として持ち、`TradeDecisionMade`・`TradeDecisionHeld` の末尾の `Policy`（既定 null）へ載せる。
5. **移行**: マイグレーションで表を作り、既存の確定済みの行（`State = Confirmed` かつ `ConfirmedAt` あり）を、その時点の行の版のまま履歴へ写す（各期間キーの版 1 本＝その期間の最初の確定版）。`Sequence` は種別ごとに確定時刻・期間キーの順で振る。

## 受け入れ基準 → 試験

| 基準 | 試験 |
| --- | --- |
| 確定すると履歴に版が 1 行増え、確定済みの版の写しは後の確定で書き換わらない | `EfReportStoreTests` / `InMemoryReportStoreTests`（新規）: 確定の履歴 |
| 1 段目は開始日が最大の期間キー。確定時刻が新しくても古い期間キーの版を選ばない | 二段の選び方（期間キーをまたいで確定時刻で並べない） |
| 2 段目は確定時刻が最新の版、同時刻は版番号が大きい方 | 二段の選び方（同じ期間キーの版 n と n+1。同時刻） |
| **Stage 0: 判断時点より後に確定した版を使わない**（配備の条件） | `GetLatestConfirmedAsOf` の試験（版 n+1 の確定の前後の判断時点で版 n・n+1 を返す） |
| 並行した確定で通し番号が衝突しても、両方が確定し番号が重ならない | `EfReportStoreTests`（保存の直前に別の文脈で確定させる割り込み） |
| 既存の確定済みの行は版 1 本として選べる | マイグレーションの SQL は Postgres で確かめる（下の検証） |
| REST・gRPC の方針の応答に期間キーと版がある | `ReportService` の既存の方針の試験に足す |
| 取引判断が期間キーと版を読み、判断の記録に載せる。欠けた応答でも方針は通す | `HttpDailyPolicyProvider` / `GrpcDailyPolicyProvider` / `TradeDecisionAppService` の試験 |
| イベントは非破壊（旧 payload は `Policy` が null で読める） | `event-schemas.baseline.json` の更新と後方互換の試験 |

## 検証

- `dotnet build` / `dotnet test` を ReportService・TradeDecisionService・Shared.Contracts の試験プロジェクトに絞る（ディスクの制約）。`dotnet format --verify-no-changes`。
- `node scripts/check-proto-contracts.js`（baseline 更新）、文書系検査、`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`。
- マイグレーションの SQL を生成して読み、既存の確定済みの行の写し（`INSERT … SELECT`）を確かめる。
- 表を足したので、切替の保全対象の全数表（`scripts/cutover-count-reconcile.sh` の manifest。EF の ModelSnapshot と `scripts.repo.test.js` が突き合わせる）にも `report_confirmed_versions`（ledger・時刻列 `ConfirmedAt`）を足す。

## 残余

- Stage 0 の記録（`Stage0DecisionRecord`）には参照した方針の版を持たせていない。as-of の答えは判断時点の版そのものなので組み直せる。記録に残すかは段 B 以後に決める。
- 段 B の要件は IADR-0548「段 B への引き継ぎ」に書く。

［2026-10-17 追記 / #1373］**上の「受け入れ基準 → 試験」の表の試験名は、実装と食い違っていた**（PR #1374 の AI レビュー 🟢）。実際の置き場所は次のとおりである（表の本文は当時の記録として残す）。

| 基準 | 実際の試験 |
| --- | --- |
| 確定の履歴・二段の選び方・判断時点・並行した確定の通し番号・種別ごとの通し番号・InMemory の実装 | `backend/Services/ReportService/Tests/Infrastructure/Persistence/ReportConfirmedVersionHistoryTests.cs`（1 ファイル。`EfReportStoreTests` / `InMemoryReportStoreTests` には足していない。`InMemoryReportStoreTests` は作っていない） |
| 衝突の見分け方（PR #1374 の 2 コミット目） | 同上（`衝突は写しの鍵と版の一意制約違反だけ` ほか） |
| REST・gRPC の方針の応答 | `ReportEndpointsTests`・`DailyPolicyReadGrpcServiceTests` |
| 取引判断の読み手と判断の記録 | `ReportReadContractTests`・`GrpcStage4ReadsTests`・`Stage0AsOfExternalSourcesTests`・`TradeDecisionServiceTests`・`DecisionHeldReportTests` |
| イベントの後方互換 | `DecisionPolicyReferenceContractTests` と `event-schemas.baseline.json` |
