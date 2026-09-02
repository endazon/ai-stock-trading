---
title: #613 第2弾 Features/<集約>/<操作>/ の 3 段化 —— ReportService の移送
type: spec
status: draft
related_ids:
  - NFR
  - IADR-0259
  - IADR-0276
  - IADR-0289
  - MSP:ADR-0065
  - MSP:ADR-0068
author: endazon (with Claude Code)
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md
  - planning:projects/microservices-platform/07_adr/ADR-0068_three-level-slice-split-rule.md
---

# 仕様書: #613 第2弾 —— ReportService の 3 段化移送

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（構成是正・保守性の非機能作業）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 起点 ID: `NFR`（無採番。`.claude/rules/traceability.md` 無採番許容ケース 2 ——
  ソースツリーの割り方であり計画の非機能要件表に当たる番号が無い。
  [IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) と同じ判断）
- 関連 ADR: platform `ADR-0065` 決定 2・決定 3／platform `ADR-0068` 決定 1〜5
- 移送規則の正本: [IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) 決定 1〜6
  （**本 PR で新規 IADR は作らない**。判断が要った点は同 IADR への日付付き追記で残す）
- 第 1 弾の指示書: [20260903_613_vsa-three-tier-risk-management](20260903_613_vsa-three-tier-risk-management.md)
  §残る 10 サービスの割り当て表（`ReportService` の行）
- 計画書リンク: <https://github.com/endazon/project-planning/blob/main/projects/microservices-platform/07_adr/ADR-0068_three-level-slice-split-rule.md>

## 目的・背景

第 1 弾（PR #652・[IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md)）で移送規則が確定し、
`RiskManagementService` で実地に検証された。本作業（第 2 弾）は同じ規則を **`ReportService`** へ当てる。

実測（移送前・`develop` `b8367987`）:

| 観点 | 値 |
| --- | ---: |
| `Features/Reports/` の `.cs` | 25 |
| `Features/Reports/` の操作ディレクトリ | 0 |
| HTTP 端点 | 11 |
| `Tests/` の `.cs` | 68 |
| `Tests/` のサブディレクトリ | `Golden/` のみ（テストは全件フラット） |

## 対象範囲

- 対象:
  - `ReportService` の `Features/Reports/<操作>/` 3 段化（11 操作）
  - `ReportService` の `Tests/` を本体の樹形の鏡写しへ再配置
  - [IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) §フォローアップ 1 の裁定を同 IADR へ追記
  - 計画側（platform `ADR-0065`／`ADR-0068`）へ「操作」の定義の明確化を環流（issue 1 本）
- 対象外:
  - 他 9 サービスの移送（後続 PR）
  - `Hosted/`・`Infrastructure/` の移動（[IADR-0276](../adr/IADR-0276_claude-md-vsa-correction-and-hosted-placement.md) 決定 2・
    [IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) 決定 2）
  - 2 段目（集約）の切り直し
  - 振る舞い・公開面（ルート・認可・応答形）・DI 登録・wire 契約の変更
  - 新規 IADR の起草・`.ai-context/adr/README.md`（索引）の変更（並行 PR との競合回避）

## 設計

### 操作フォルダ（3 段目・11 個）

登録表 `Features/Reports/ReportEndpoints.cs` の**登録順**をそのまま保つ。

| # | グループ | verb + path | 操作フォルダ | 同居させる型 |
| ---: | --- | --- | --- | --- |
| 1 | read（OwnerOrService） | GET `/daily-policy` | `GetConfirmedDailyPolicy` | — |
| 2 | owner（OwnerOnly） | GET `` | `ListReports` | — |
| 3 | owner | GET `/monthly-bootstrap` | `GetMonthlyBootstrap` | — |
| 4 | owner | POST `/pnl-summary` | `SummarizePnl` | `PnlSummaryRequest` |
| 5 | owner | POST `/{periodKey}/draft` | `DraftReport` | `DraftReportRequest` |
| 6 | owner | GET `/{periodKey}` | `GetReport` | — |
| 7 | owner | PUT `/{periodKey}` | `UpsertReportDraft` | `UpsertReportRequest` |
| 8 | owner | GET `/{periodKey}/review` | `GetReportReview` | — |
| 9 | owner | POST `/{periodKey}/present` | `PresentReport` | — |
| 10 | owner | POST `/{periodKey}/request-changes` | `RequestReportChanges` | — |
| 11 | owner | POST `/{periodKey}/confirm` | `ConfirmReport` | `ConfirmReportRequest` |

### 2 段目に残すもの（根拠つき・全件）

| ファイル | 残す根拠 |
| --- | --- |
| `ReportEndpoints.cs` | 登録表（`MapGroup("/reports")`・タグ・例外→HTTP の共通フィルタ・read/owner の 2 グループ・`Program.cs` が呼ぶ `MapReportEndpoints`）。加えて **2 操作以上が使う私的ヘルパ** `ActorOf`（9・10・11）／`ReviewResult`・`RejectionMessage`（9・10）を `internal static` として残す（[IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) 決定 3） |
| `ReviewCommandRequest.cs`（切り出し） | **2 操作（9・10）が共有する要求レコード**。第 1 弾の `KillSwitchRequest.cs` / `PauseRequest.cs` と同型に、2 段目の独立ファイルへ出す |
| `ReportAppService.cs` | 8 操作が使う（`AppSvc` を取る操作すべて） |
| `ConfirmedDailyPolicy.cs` | 唯一の参照元 `ReportAppService` が 8 操作共有で 2 段目に固定される |
| `ReportDraftService.cs`（`DraftRequest` / `DraftResult` を含む） | 操作 5 に加え `ReportAutoGenerator`（`Hosted/` 経路）が使う |
| `ReportAutoGenerator.cs`・`ReportNarrativePromptBuilder.cs`・`ReportNarrativePurpose.cs`・`ReportNarrativeTimeouts.cs`・`IReportNarrativeDrafter.cs` | `Hosted/ReportAutoGenerationService.cs`・`Infrastructure/ExternalServices/` から使われる |
| `IReportStore.cs`・`VersionedReport.cs` | `ReportAppService` ＋ `Infrastructure/Persistence/` 2 実装 |
| `ILlmGovernanceReporter.cs`・`ILlmUsageReporter.cs`・`IReportDraftPresentedNotifier.cs` ＋ 供給ポート 11 本（`IPeriodFillSource` `IPeriodEndFxRateSource` `IFxSourceStatusSource` `IOpenPositionSource` `ITradeRationaleSource` `IStageProgressSource` `IOpenDUptimeSource` `ILlmUsageRecordSource` `IBorrowFeeRecordSource` `IBuyInInferenceRecordSource` `IMarginReductionRecordSource`） | `Program.cs` の DI ＋ `Infrastructure/ExternalServices/` の実装 ＋ `Hosted/` |

### 割り当て表からの逸脱（明示）

第 1 弾の割り当て表は 2 段目に残るものとして `ReportEndpoints.cs` を
「登録表（11 操作＋**5 DTO**＋`ReviewResult`/`RejectionMessage`/`ActorOf`）」と書いていた。
これは**移送前のファイルの中身の記述**であり、5 DTO をすべて 2 段目へ固定する指示ではない。
[IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) 決定 3（1 操作専属の処理は 3 段目・
共通部分は 2 段目）に従い、**1 操作しか使わない 4 DTO は 3 段目へ下ろし、2 操作が共有する
`ReviewCommandRequest` だけを 2 段目へ残す**。第 1 弾が `KillSwitchRequest` / `PauseRequest` に対して
採ったのと同じ扱いである。

### `Tests/` の鏡写し（[IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) 決定 5）

| 行き先 | ファイル数 | 内容 |
| --- | ---: | --- |
| `Tests/Domain/` | 26 | `Domain/` の型だけを対象にするテスト（`ReportRenderer` 系 5・`PnlAggregator` ほか集計器・`ReportPeriod` / `ReportSchedule` / `ReportPolicyYaml` / `ReportReviewStateMachine` ほか） |
| `Tests/Features/Reports/` | 13 | `ReportAppService`（`ReportServiceTests`）・`ReportDraftService` 2・`ReportAutoGenerator` 5・散文ポート 3・`ReportEndpointsTests`（登録表の全端点を横断する）・`ReportConfirmationFlowTests` |
| `Tests/Infrastructure/Persistence/` | 2 | `EfReportStoreTests`・`ReportBodyPersistenceTests` |
| `Tests/Infrastructure/ExternalServices/` | 14 | `Http*Source` 系 10・`HttpReportNarrativeDrafter` 2・`PublishingLlmReportersTests`・`ReportKnowledgeMapperTests` |
| `Tests/Hosted/` | 2 | `ReportAutoGenerationOptionsTests`・`ReportAutoGenerationServiceLoggingTests` |
| `Tests/`（直下・据え置き） | 11 | `Program.cs` の配線テスト 8（`*WiringTests`）・`HealthEndpointTests`・テスト土台 2（`ReportWorkerWebApplicationFactory`・`TestAuthHandler`） |
| `Tests/Golden/`（据え置き） | 6（`.md`） | ゴールデンのテストデータ。**1 バイトも変えない**。`ReportService.Tests.csproj` の Golden 複写指定はテストプロジェクト直下の相対指定であり、据え置きなら csproj を触らずに済む |

- **テストの名前空間は `ReportService.Tests` のまま据え置く**（決定 5）。
- 3 段目の型（4 DTO）を名指しするテストは存在しない（実測: `PnlSummaryRequest` / `DraftReportRequest` /
  `UpsertReportRequest` / `ConfirmReportRequest` / `ReviewCommandRequest` を参照する `.cs` は
  `ReportEndpoints.cs` 以外に無い。`NotificationService` 側の同名 `record` は**自前の private 定義**であり
  本サービスの型を参照していない）。したがってテスト側の `using` 追加は 0 行を見込む。

## 受け入れ基準

- [ ] `dotnet build backend/backend.slnx` が成功し、**警告 0・エラー 0**
- [ ] `dotnet test backend/backend.slnx --filter "Category!=Integration"` の**テスト件数がアセンブリ単位で移送前と同数**
- [ ] 端点の verb + path・所属グループ・**登録順序**の集合が移送前後で 1:1（機械 diff）
- [ ] `AiStockTrading.Architecture.Tests` が緑（`Domain/` ソース走査件数が下限以上）
- [ ] `dotnet format backend/backend.slnx --verify-no-changes` が差分なし
- [ ] `node scripts/check-trace-blocks.js` / `check-test-traceability.js` / `check-doc-links.js` /
      `check-adr-index-sync.js` が OK
- [ ] `Tests/Golden/*.md` が 1 バイトも変わっていない（`git diff` に内容差分が現れない）
- [ ] 公開面（ルート・認可ポリシー・応答形・`Program.cs` の DI 登録・wire 契約）に差分が無い

## テスト方針

**テストは 1 件も追加・削除・改変しない**（純粋な移送）。移送が振る舞いを変えていないことは、
既存テストが無改修で緑であること自体で示す。とくに次の 3 つが公開面の固定として効く。

1. `ReportEndpointsTests`（11 端点の OwnerOnly / OwnerOrService 認可・upsert・版番号付き冪等確定・
   確定イベント発行・KB 保存を `WebApplicationFactory` で通す）
2. `ReportTemplateGoldenTests`（テンプレート出力のバイト一致）
3. `*WiringTests` 8 本（`Program.cs` の DI 解決）

端点集合の 1:1 は、テストとは別に **`EndpointDataSource` の実列挙**を移送前後で突き合わせて確かめる
（一時テストで HTTP メソッド ＋ ルートパターン ＋ 認可ポリシー ＋ 登録順を出力し、差分を取る。
一時テストはコミットしない）。

## 移送後の実測

<!-- 移送完了時に記入する -->

## 計画書との差異

- 差異: なし。platform `ADR-0065` 決定 2・決定 3 と `ADR-0068` 決定 1〜5 の形を
  [IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) の解釈のまま採る。
- ただし `ADR-0068` の「操作」の定義に曖昧さが残る（下記）ため、計画側へ環流する。

## 未決事項

- 🔴 **[IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) §フォローアップ 1（HTTP 端点を持たない
  5 サービスの扱い）は本 PR で裁定し、同 IADR へ日付付きで追記する**（案 B ＝「操作」は HTTP 端点に限る）。
  `ReportService` 自身は HTTP 端点を 11 本持つため裁定の影響を受けないが、**後続の弾が読みを割らないよう
  本 PR で確定させる**。
- 併せて計画側（platform `ADR-0065` 決定 2 の「操作」の語義）へ明確化を環流する。
