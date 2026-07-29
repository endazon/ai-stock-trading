---
title: 日報/週報/月報の自動生成スケジューラ（提示まで・確定は OwnerOnly のまま）
type: spec
status: review
related_ids: [FR-06, FR-07, FR-16, UC-03, UC-04, UC-05, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-29
updated: 2026-07-29
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md
  - ../../planning/projects/ai-stock-trading/04_workflows/03_reporting-cycle.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# 仕様書: 日報/週報/月報の自動生成スケジューラ

> 利用者指示・設計承認（2026-07-29）。**(a) ドラフト生成 → 提示（Present）までを自動化し、確定（confirm）は
> OwnerOnly のまま**。`ReportReviewStateMachine`・認可・楽観排他は不変。
>
> 本 PR（1/2）はスケジューラ本体。**Discord 投稿は PR 2/2** に分離する。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-06（報告書生成）、FR-07（対話的確定）、FR-16（数値集計）
- ユースケース: UC-03（月報）・UC-04（週報）・UC-05（日報）
- 業務フロー: `04_workflows/03_reporting-cycle.md`（status: **fixed**）— 生成タイミング・休場日の扱い・
  上位方針欠落時の挙動を確定済み
- ADR: [ADR-0003](../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md)（**Accepted**・
  「方針の確定には必ず利用者との対話を要する。完全無人での方針変更は行わない」）
- 関連 IADR: [IADR-0024](../adr/IADR-0024_report-confirmation-and-policy.md)（報告書サービス基盤・版番号付き冪等確定）／
  [IADR-0032](../adr/IADR-0032_report-generation.md)（数値はコード集計・散文は LLM）／
  [IADR-0042](../adr/IADR-0042_report-review-state-machine-and-detail-rendering.md)（対話的確定の状態機械）／
  [IADR-0071](../adr/IADR-0071_report-service-remaining.md)（報告書残・無応答既定・KB 保存）／
  [IADR-0095](../adr/IADR-0095_watchlist-authoritative-wiring.md)（権威源への s2s 同期照会・fail-safe フォールバック）／
  [IADR-0103](../adr/IADR-0103_observed-drawdown-supply.md)（定時ドライバの実装作法）／
  本作業で新規 [IADR-0115](../adr/IADR-0115_report-auto-generation-scheduler.md)
- 対象 Issue: [#280](https://github.com/endazon/ai-stock-trading/issues/280)（`Refs #280`）・
  傘 [#279](https://github.com/endazon/ai-stock-trading/issues/279) ギャップ #2

## 現状（この変更の直前・実コードで確定）

| 面 | 実態 |
| --- | --- |
| `ReportService.Worker/Program.cs` | `AddHostedService` **0 個**。WebApplication のみで常駐ジョブを持たない |
| 生成経路 | OwnerOnly の HTTP のみ（`POST /reports/{periodKey}/draft` → `present` → `confirm`） |
| 生成物の永続 | `POST /{periodKey}/draft` は Markdown を**返すだけで保存しない**。`ReportRow` に本文の列が無い |
| 約定の供給 | `DraftRequest.Fills` は HTTP 要求で外から渡される。report-service 自身に取得経路が**無い** |
| 実運用 | 確定方針は `daily-2026-07-27` の 1 件で停止。`ContinueLastConfirmed` で取引だけが継続 |
| 週報・月報 | 型（`ReportKind.Weekly`/`Monthly`）と集計・描画はあるが、**駆動する側が無い** |

## 目的

1. 日報・週報・月報のドラフトが**閉場後に自動生成され、提示（`PendingApproval`）される**。
2. **確定の信頼モデルを一切変えない**。`confirm` は OwnerOnly の既存経路のみ（ADR-0003 準拠）。
3. 生成した Markdown 本文が永続化され、利用者がレビューできる。
4. 期間の約定を権威源（risk-management 取引台帳）から取得し、供給不達でも生成が止まらない。
5. **既定無効（opt-in）**。有効化しない限り現行挙動とバイト等価。

## 設計

### 1. 生成境界（純関数・`ReportService.Domain/ReportSchedule.cs`）

**時刻境界は JST（UTC+9）固定**。`PortfolioProjection.TradingDayOffset = +9h` と整合させる。
市場別の取引日境界の解釈は [#249](https://github.com/endazon/ai-stock-trading/issues/249) の管轄で、本 PR では扱わない。

```
ReportSchedule.Due(instant, options, holidays) -> IReadOnlyList<DueReport>
```

営業日判定は純関数（土日 ＋ 構成注入の休場日集合）。`TradeDecisionService` の `MarketCalendar`（IADR-0023）と同型で、
既定は空集合＝週末のみ。

| 種別 | due の条件（JST 基準） | `PeriodDate` | `PeriodStart` |
| --- | --- | --- | --- |
| 日報 | 閉場境界 `DailyAt` を過ぎた**直近の営業日**（当日が営業日かつ `now >= DailyAt` なら当日、そうでなければ直前の営業日。探索は 7 日で打ち切り） | その営業日 | 同左 |
| 週報 | 当 ISO 週の**最終営業日**が存在し、その `WeeklyAt` を過ぎている | 当週最終営業日 | 当週の月曜 |
| 月報 | 当月の**最終営業日**が存在し、その `MonthlyAt` を過ぎている | 当月最終営業日 | 当月 1 日 |

既定時刻: 日報 **16:00** / 週報 **16:30** / 月報 **17:00**（いずれも JST・構成可能）。東証の大引け 15:30 の後に置く。

**バックフィルは当期のみ**。日報は「直近の閉場済み営業日 1 件」、週報・月報は「当週・当月の最終営業日」までで、
それより前の期間は遡らない。ダウンタイムが週や月をまたいだ場合、その期間の報告書は生成されない（既知の制約）。
日報について直近営業日まで遡るのは、**確定した日報が翌営業日の取引方針になる**（`03_reporting-cycle.md`）ため、
夜間再起動で前営業日ぶんを取りこぼすと運用が止まるのを避けるため。

### 2. 冪等・再起動耐性

- 状態は専有 DB（`report_svc`）**のみ**。プロセス内メモリに「生成済み」を持たない。
- `PeriodKey`（`daily-2026-07-29` / `weekly-2026-W31` / `monthly-2026-07`）が自然キー。
  `store.Get(periodKey) is not null` なら**何もしない**（生成も提示もしない）。
- 新規生成は `UpsertDraft(report, expectedVersion: 0)`。多重レプリカが同時に走っても主キー競合で片方が負け、
  例外を捕捉して当該期間をスキップする（二重生成しない）。
- 巡回間隔は `PeriodicTimer`（既定 300 秒）。境界時刻を過ぎていて未生成なら生成するため、
  巡回のタイミングに厳密さを要求しない（cron 的な「その瞬間」に依存しない）。

### 3. 生成 → 提示（確定はしない）

`ReportAutoGenerator.RunOnceAsync`（`ReportService.Application`・BackgroundService とは分離してテスト可能にする）:

```
due = ReportSchedule.Due(clock.UtcNow, options, holidays)
foreach d in due:
    if store.Get(d.PeriodKey) is not null: continue                // 冪等
    basedOn = store.GetLatestConfirmed(ParentKind(d.Kind))         // 日報→週報 / 週報→月報 / 月報→前月報
    fills   = fillSource.GetFillsAsync(d.PeriodFrom, d.PeriodTo)   // fail-safe: 失敗は空
    draft   = draftService.BuildDraftAsync(...)                    // 数値=コード集計 / 散文=LLM
    version = store.UpsertDraft(report with Body=draft.Markdown, expectedVersion: 0)
    store.ApplyReview(d.PeriodKey, Present, version, actor: "report-scheduler")
```

- **`Confirm` は呼ばない。** 生成物は `ReviewState.PendingApproval` で止まり、`ReportState` は `Draft` のまま。
  `GetLatestConfirmed(Daily)` は影響を受けず、取引方針は利用者が確定するまで変わらない（ADR-0003）。
- `ApplyReview` の actor は `report-scheduler`（in-process のドメイン操作。HTTP の OwnerOnly 認可は不変で、
  提示は「システムが利用者へ提示する」という計画書のシーケンスそのもの）。
- **`PolicySummary` は上位方針の継続案**（`BasedOn` の確定済み方針を引き継ぎ、要確定である旨を明記）。
  LLM に新しい方針文を提案させることは本 PR では**行わない**（IADR-0115 決定 4）。散文（振り返り・評価）は
  従来どおり `IReportNarrativeDrafter` が担い、Markdown 本文に入る。
- 上位方針が未確定なら `BasedOn = null` とし、その旨を方針文へ明記する（`03_reporting-cycle.md`「上位方針の欠落」）。

### 4. 本文 Markdown の永続化

`TradingReport.Body`（`string`・既定空）／`ReportRow.Body`（nullable text）／Migration を追加する。
既存行は `null` → 読み出し時に空文字（後方互換）。HTTP の `PUT /{periodKey}` は本文を受け取らず、既存の
upsert 経路では `Body` を空のまま保つ（既存 API の互換を壊さない）。

### 5. 約定の供給（権威源への s2s 同期照会）

- **risk-management 側**: `GET /risk-controls/fills?from=YYYY-MM-DD&to=YYYY-MM-DD` を `OwnerOrService` で追加する
  （読み取りのみ。書き込み系 OwnerOnly は不変）。`IPortfolioLedgerStore.GetFills()`（承認 Intent × 約定の結合）を
  `PortfolioProjection.TradingDay`（JST）で期間フィルタして返す。**新規テーブル・新規イベントは無い。**
- **report-service 側**: ポート `IPeriodFillSource`。既定実装は no-op（空列）。`RiskManagement:BaseUrl` 設定時のみ
  HTTP 実装を選択する（IADR-0095 と同型・s2s トークンは `AddAiStockTradingServiceToken`）。
- **fail-safe**: 非 2xx・timeout・例外・不正応答はすべて**空列**へ倒し、数値 0 の報告書を生成する。
  生成そのものは止めない（報告書は発注判断を行わないため、欠測は過大発注に繋がらない）。
- **通貨**: 台帳の `AveragePrice` はローカル通貨（IADR-0107）。`PeriodTradeFill.Price` は基準通貨（円）建てのため、
  `AveragePrice × FxRateToBase` を渡す。
- 実約定が台帳へ入るかは [#270](https://github.com/endazon/ai-stock-trading/issues/270)（moomoo の fill 伝播）依存。
  本 PR は**構造の結線**まで。

### 6. 構成（すべて既定安全側）

| キー | 既定 | 意味 |
| --- | --- | --- |
| `Reports:AutoGeneration:Enabled` | `false` | 常駐スケジューラを起動するか（**opt-in**） |
| `Reports:AutoGeneration:IntervalSeconds` | `300` | 巡回間隔 |
| `Reports:AutoGeneration:DailyAtJst` | `16:00` | 日報の生成境界（JST） |
| `Reports:AutoGeneration:WeeklyAtJst` | `16:30` | 週報の生成境界（JST） |
| `Reports:AutoGeneration:MonthlyAtJst` | `17:00` | 月報の生成境界（JST） |
| `Reports:AutoGeneration:Holidays` | `[]` | 休場日（`yyyy-MM-dd` の配列・既定は週末のみ） |
| `Reports:AutoGeneration:Markets` | `[]` | フロントマターの対象市場表記 |
| `RiskManagement:BaseUrl` | 未設定 | 約定の権威源。未設定＝no-op（空列） |

不正・非正値はすべて既定へ倒す（`TimeOnly` のパース失敗、`IntervalSeconds <= 0` 等）。
Helm / values / デプロイ資材は**本 PR では触らない**（稼働中環境不変・有効化は別途）。

## 影響範囲

| 対象 | 変更 |
| --- | --- |
| `ReportService.Domain` | `ReportSchedule`（新規・純関数）、`TradingReport.Body`（追加） |
| `ReportService.Application` | `IPeriodFillSource`（新規ポート）、`NoOpPeriodFillSource`、`ReportAutoGenerator`（新規） |
| `ReportService.Worker` | `ReportAutoGenerationService`（BackgroundService・既定無効）、`HttpPeriodFillSource`、`ReportRow.Body` ＋ Migration、Program.cs 配線、introspection 自己申告 |
| `RiskManagementService.Worker` | `GET /risk-controls/fills`（OwnerOrService・読み取りのみ） |
| `Shared.Contracts` | **不変**（新規イベント無し） |
| Helm / compose / values | **不変** |

## テスト（受け入れ基準の写像）

| # | 観点 | テスト |
| --- | --- | --- |
| 1 | JST 境界・営業日 | `ReportScheduleTests`: 15:59 JST は当日の日報 due でない／16:00 で due／構成休場日を除外 |
| 2 | 直近営業日への遡り | 月曜 09:00 JST に金曜ぶんの日報が due になる（7 日打ち切り） |
| 3 | 週報・月報 | 週最終営業日 16:30 以降のみ due／週末（土日）でも当週ぶんは due のまま／月末最終営業日 17:00 以降のみ due |
| 4 | 冪等 | 同一巡回の 2 回実行で 2 件目が生成されない／既存 `PeriodKey` があれば生成も提示もしない |
| 5 | 確定ゲート | 自動生成の結果が `ReviewState.PendingApproval` かつ `ReportState.Draft` で止まる／`GetConfirmedDailyPolicy()` が変化しない |
| 6 | 上位方針 | 直近の確定済み週報が `BasedOn` に入る／未確定なら `null` かつ方針文に明記 |
| 7 | 約定 fail-safe | `IPeriodFillSource` が例外・空・不正応答でも生成が続き数値が 0 になる |
| 8 | 本文の永続 | 生成後に `Get(periodKey)` の `Body` が Markdown を保持する（EF 往復） |
| 9 | s2s 照会 | `GET /risk-controls/fills` が期間で絞る／`OwnerOrService` で 200・無ロールで 403 |
| 10 | 既定無効 | `Enabled` 未設定で常駐が登録されない（＝現行挙動） |

## 受け入れ基準（`docs/DEFINITION_OF_DONE.md` と併せて）

- [ ] 有効化時に日報・週報・月報のドラフトが自動生成され `PendingApproval` で止まる
- [ ] 確定（`Confirmed`）へは自動で遷移しない。`ReportReviewStateMachine` は変更しない
- [ ] JST 境界・休場ガード・`PeriodKey` 冪等・再起動耐性がテストで固定されている
- [ ] 約定の供給不達で生成が止まらない（fail-safe）
- [ ] 既定無効で現行挙動とバイト等価。Helm / values は不変
- [ ] `dotnet build` / `dotnet test` / `dotnet format` が green・CI / gitleaks が green

## スコープ外（PR 2/2 以降）

- Discord 投稿（提示・確定の通知イベント ＋ notification 側 Consumer ＋ `PromptSafetySanitizer` の共有化）
- 自動確定（ADR-0003 に反するため実装しない）
- 市場別の取引日境界（#249）／moomoo の fill 伝播（#270）／Discord の環境固有 ID 投入（#279 ギャップ #3）
