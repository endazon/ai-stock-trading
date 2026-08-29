---
title: ReportService を単一プロジェクト＋VSA 樹形へ移送する（W11 段 4-10）
type: spec
status: approved
related_ids: [NFR, IADR-0259, IADR-0263, IADR-0264, IADR-0265]
author: endazon (with Claude Code)
created: 2026-08-29
updated: 2026-08-29
plan_refs: []
---

# 仕様書: ReportService の単一プロジェクト＋VSA 移送（W11 段 4-10）

> **11 サービス移送波の 10 本目**である。1 本目（AuditService・[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)）・
> 2 本目（ConfigurationService・[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md)）・
> 3〜9 本目で確定した判断の型をそのまま適用した。**新しい判断軸は生じなかった**（末尾「IADR を作らない判断」参照）。
> ReportService は `Domain/`・`Features/`（ポート 12 本）・`Infrastructure/{Persistence,ExternalServices}`・
> `Hosted/`・`Common/{Abstractions,Exceptions}` を持つが、**Wolverine の consumer を 1 本も持たないため
> `Infrastructure/Steps/` は作らない**（実体が無いフォルダは作らない）。

## 起点

- 起点 ID: **`NFR`（無採番）**。構造移送＝メタ作業であり、`.claude/rules/traceability.md`
  「起点 ID の種別」の無採番許容ケース **2** に当たる（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)
  が確定済みの判断を継承する。環流はしない）。
- 上流: [IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)（樹形・写像方針表。決定5＝`Hosted/` は入口に留める）・
  [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)（1 本目の 5 決定。特に決定4＝
  `internal`→`public` は最小限）・[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定3
  （Domain / Features の切り分け基準）・[IADR-0265](../adr/IADR-0265_domain-project-count-checker-dynamic-lower-bound.md)
  （検査の下限の動的化。本 PR は手で触っていない）

## 着手前に読んだもの

- `CLAUDE.md` / `.claude/rules/traceability.md` / `.claude/rules/traceability.repo.md` / `docs/DEFINITION_OF_DONE.md`
- [IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) / [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) /
  [IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) / [IADR-0265](../adr/IADR-0265_domain-project-count-checker-dynamic-lower-bound.md)
- 先行の作業仕様書のうち**本ブランチから読める分**（`.ai-context/specs/2026082*vsa*.md`）:
  w11s4a / w11s4b / w11s4c / w11s4d / w11s5 / w11s6 / w11s7 / **w11s8（`OrderExecutionService`。
  alias 無し参照の張り替え・Persistence＋Hosted あり。最も近い先例）**。
  🔴 **`w11s9_tradedecisionservice` の仕様書は本ブランチに存在しない**（base は `bd39cbd`＝#593 の
  OrderExecutionService マージ直後であり、9 本目は develop へ未マージ）。指示にある「直前」は
  波全体の順序であってローカル履歴上の直前ではない。**読めないものを読んだことにしない。**
- 既に移送済みの実物（樹形・名前空間・csproj の手本）: `backend/Services/{AuditService,ConfigurationService,
  CostControlService,BacktestService,MarketMonitorService,NotificationService,OrderExecutionService}/`
- 基盤（`/home/user/microservices-platform`・読み取り専用）の `Features/` 名の実例

## 対象範囲

- 対象: `backend/Services/ReportService/`（8 csproj → 2 csproj）、`backend/backend.slnx`、
  `backend/Tests/AiStockTrading.IntegrationTests/`（**`Aliases="ReportWorker"` 付き参照の張り替え・必須作業**）、
  `docker-compose.yml`、`scripts/k8s-local-images.sh`
- 対象外: 他サービス（次の PR）、`backend/Shared/` `backend/TestSupport/`（据え置き集合）

## 着手前の母集合の引き直し（`.claude/rules/traceability.repo.md` 規則1〜10）

**母集合は記憶で挙げず、誤りになる側の文字列で全追跡ファイルを走査して引いた**（規則1・2・9・10）。
走査した語は 4 通り（規則2・#593 のレビュー指摘を踏襲）:
① `ReportService\.(Api|Application|Domain|Infrastructure)` ② `ReportService[/\\](src|tests)`
③ `\.\.\./` で始まる省略パス ④ `Composable|Foundation`（旧フォルダ名だけの記述）。

### 親から渡された前提の数え直し（規則9・10。**転記しない**）

| 項目 | 親の前提 | 自分で数え直した実測 | 判定 |
| --- | --- | --- | --- |
| `.cs` 件数 | 139 | **139**（src 86・tests 53） | **一致** |
| csproj | 8（`Domain` あり） | **8**（src 4・tests 4。`ReportService.Domain` 実在） | **一致** |
| migration | 3 本 | **3 本**（`20260710123313_InitialCreate` / `20260717181512_AddReviewState` / `20260729110333_AddReportBody`。関連ファイルは 3×2＋snapshot＝**7**） | **一致** |
| `: DbContext` 継承 | 1 | **1**（`ReportDbContext`。`grep ": DbContext"` の literal 一致で確認） | **一致** |
| `: BackgroundService` 継承 | 1 | **1**（`ReportAutoGenerationService`。`Program.cs` の `AddHostedService<>` 呼び出しも 1 箇所で突合） | **一致** |
| `IntegrationTests.csproj` の該当行 | 40 行目・`Aliases="ReportWorker"` | **40 行目・`Aliases="ReportWorker"`** | **一致** |
| `ServiceTokenSyncQueryE2ETests.cs` の `using ReportService.*` | 「確認すること」 | **0 件**（`extern alias ReportWorker;` と `ReportWorker::Program` のみ。`using` は 12 本すべて外部ライブラリ・共有物） | **追随不要**（8 本目で必要だった `.cs` 側の追随は本サービスでは発生しない） |

**結論: 親の前提はすべて一致した**（8 本目のような食い違いは無かった）。
`Infrastructure/Persistence/` と `Hosted/` はどちらも実体があるため両方作る。

### 走査で引いた母集合と処置

| 対象 | 実測 | 処置 |
| --- | --- | --- |
| `backend/backend.slnx` | 8 行（`Folder` 2 個 ＋ `Project` 8 本） | **是正**（2 本へ置換・フォルダ宣言は削除） |
| `backend/Tests/AiStockTrading.IntegrationTests/AiStockTrading.IntegrationTests.csproj` | 40 行目 1 件 | **是正**（パスのみ・`Aliases="ReportWorker"` 保持） |
| `docker-compose.yml` | 341-342 行（`SERVICE_PROJECT` / `SERVICE_DLL`） | **是正** |
| `scripts/k8s-local-images.sh` | 34 行（`report-service\|…csproj\|…dll`） | **是正** |
| `docs/` 配下のパス／名前空間参照（4 通りすべてで走査） | **0 件**（`docs/data/reports.md:20` の `ReportService` はサービス名の言及でパスでも名前空間でもない。`docs/tech/tech-requirements.md:99-100` は名前空間規約の説明で**据え置きが正しい**） | **是正なし** |
| `.ai-context/specs/` / `.ai-context/adr/` の同パターン | specs 実測 **42 件**（15 ファイル）・adr 実測 **11 件**（8 ファイル） | **凍結記録のため未更新**（`.claude/rules/traceability.repo.md` の除外規定。point-in-time の記録） |
| `.gitleaksignore` 24・30・31 行 | 3 件（`…/tests/ReportService.Domain.Tests/…` と `…/tests/ReportService.Worker.Tests/…`） | **未更新が正しい**。gitleaks の fingerprint は `<commit>:<当時のパス>:<rule>:<line>` であり**履歴上のパス**を指す。実際 30・31 行は現行ツリーに存在しない `ReportService.Worker.Tests` を指したままで（IADR-0128 の改名前）、**書き換えると fingerprint が一致しなくなり誤検知が復活する** |
| `scripts/scripts.repo.test.js` 862・864・888 行 | 3 件（`Services/ReportService/src/…`） | **未更新が正しい**。glob パターンの単体テストの**合成パス文字列**であり実ツリーを参照しない。同ファイルは移送済みの `AuditService/src/…` も合成パスとして持ち続けている（1 本目からの既定） |
| `backend/Tests/AiStockTrading.Architecture.Tests/DomainSourceDependencyTests.cs` 275・276・335 行 | 3 件 | **未更新が正しい**。`IsAllowedDomainNamespace` の**純粋な文字列判定**の入力例（`ReportService.Application` は「許可外」の例として渡す）。`ReportService` 自体は実ツリーに残るため fail-closed 判定は成立し続け、`ReportService.Infrastructure.Persistence` は移送後も実在する |
| `deploy/helm/ai-stock-trading/files/pipeline.json` | ReportService の consumer FQN **0 件**（本サービスは Wolverine consumer を持たない） | **是正なし** |
| `ReportService` を参照する他サービスの `ProjectReference` | **0 件**（`backend/Services` 配下の散文コメント 5 件のみ・実体参照ではない） | **是正なし** |
| `using X = …`（型エイリアス。9 本目の発見の再走査） | **4 件**（`using AppSvc = ReportService.Application.Services.ReportAppService;` が src 2・tests 2） | **是正**（`ReportService.Features.Reports.ReportAppService` へ。素の `using` 行走査では捕まらないので個別に走査した） |
| 部分修飾名（`\b(Application\|Api\|Infrastructure\|Domain)\.[A-Z]`） | src に **1 件**（`ConfirmedDailyPolicy.cs:7` の `ReportService.Domain.TradingReport`）＋ migration の `modelBuilder.Entity("ReportService.Infrastructure.Persistence.ReportRow")` **4 件** | **いずれも不変**（`ReportService.Domain` / `ReportService.Infrastructure.Persistence` は移送で変わらない） |
| `backend/Tests/` 配下の `ReportService` 裸文字列（`Path.Combine`・リテラル。規則4） | 上記 Architecture.Tests の 3 件と IntegrationTests の csproj 1 件で尽きる（`.cs` のパス文字列は **0 件**） | — |

### 走査で見つかった「想定外」（先行 9 本で未報告の型を含む）

🔴🔴 **1. 親名前空間の暗黙解決に依存していた参照が、移送で一斉に壊れる（本 PR 最大の是正）。**
C# は自分の名前空間の**祖先**を暗黙に探索する。移送前は

- `ReportService.Domain.Tests` の中から `ReportKind` 等が **`using` なしで**解決していた（祖先 `ReportService.Domain`）
- `ReportService.Application.{Ports,Services,State,Adapters}` と `ReportService.Application.Tests` の中から
  `ReportConcurrencyException`（`ReportService.Application` 直下）が **`using` なしで**解決していた

移送でテストが `ReportService.Tests` へ、例外が `ReportService.Common.Exceptions` へ移った瞬間、
**祖先関係が消えて一斉に `CS0246`／`CS0103` になる**。クリーンビルドで観測したエラー数の推移は
**4 → 89 → 54 → 6 → 0**（1 回のビルドでは全容が出ない。指示 (b) のとおり）。
是正は **`using` の追加のみ・27 ファイル**（`using ReportService.Domain;` を 19 ファイル、
`using ReportService.Common.Exceptions;` を 8 ファイル）で、**テスト本文・アサーションは 1 行も変えていない**。

［2026-08-29 追記 / #599］🔴 **当初の記載「22 ファイル（18 + 4）」は誤りだった**（レビュー指摘）。
`git diff origin/develop...HEAD --unified=0` で追加行を数え直した実測は
**`ReportService.Domain` が 19 ファイル・`ReportService.Common.Exceptions` が 8 ファイル＝計 27 ファイル**である
（同一ファイル内の重複を排除したファイル単位の数）。**是正の中身（`using` の追加のみ・本文無変更）は変わらない。**
数え漏れていたのは `Infrastructure/Persistence/EfReportStore.cs`・`Features/Reports/{IReportStore,ReportAutoGenerator,ReportEndpoints}.cs` など、
**テストではなく実装側のファイル**であった —— 「テストの名前空間フラット化で壊れる」と考えて
**テストだけを数えていた**のが原因である。**実装側も同じ親名前空間の暗黙解決に依存していた。**
🔴 **最後の 1 本への含意: `using` 追随はテストだけの現象ではない。実装ファイルも数える。**
**この型の事故は `using` の走査でも `grep` でも事前に見つからない**——**壊れる前のソースには
その `using` が存在しないからである**。クリーンビルドの 1 回目でしか出ない。

🔴 **2. ポート interface と同じファイルに同居する随伴型は、`I*` 名の走査で漏れる**（9 本目の申し送りの実例）。
`LlmUsage`（`readonly record struct`）は `ILlmUsageReporter.cs` に同居しており、
`PublishingLlmReportersTests.cs` は `LlmUsage` だけを名指しして `ILlmUsageReporter` は使わない。
そのため「ポート名が本文に出るか」で `using` の要否を決めると**この 1 ファイルだけ落ちる**。
`using ReportService.Features.Reports;` を追加した（**この 1 ファイルだけは想定外1 と別の原因**）。

🔴 **3. 部分修飾名は `using` の走査にも `<Svc>.<層>.` の走査にも現れない。**
`ReportAutoGeneratorTests.cs:659` が `new Services.ReportAppService(...)` と書いていた
（名前空間 `ReportService.Application.Tests` から祖先 `ReportService.Application` の子 `Services` を引く形）。
`\b(Application|Api|Infrastructure|Domain)\.[A-Z]` の走査では**先頭が `Services.` なので当たらない**。
`new ReportAppService(...)` へ書き換えた（同ファイルは `using ReportService.Features.Reports;` を持つ）。
**是正後、`(Application|Api|Infrastructure|Domain|Services|Ports|Adapters|State|Polling|Endpoints|
Foundation|Composable)\.[A-Z]` の 12 語で全再走査し、残りがメンバアクセス
（`.Services.GetRequiredService` / `.State.Should()` 等）と不変の FQN だけであることを確認した。**

**4. 一括置換スクリプトは対象外構文の出現数を前後で突合した**（9 本目で `using var` を壊した事故の再発防止）。
実測: `using var` **61 → 61**（不変）／型エイリアス `using X = ` **4 → 4**（不変・中身は FQN のみ更新）／
`namespace` 宣言行 **138 → 138**／`.cs` **139 → 139**。

## 設計

### 判断1: 集約は 1 つ・名前は `Reports`（`Features/Reports/`）

**機械的規則（サービス名から `Service` を落とす＝`Report`）ではなく実例を優先した**（指示どおり）。根拠:

| 実例 | 値 |
| --- | --- |
| HTTP ルート（`ReportEndpoints.cs:28`） | `app.MapGroup("/reports")` |
| 構成キー（`Program.cs:294` / helm `values-local.yaml:160`） | `Reports:NoResponseBehavior` / `Reports__BaseUrl` |
| 基盤（MSP）の `Features/` 名の実例 | `DocumentService`→`Documents` / `NotificationService`→`Notifications`（**ルート／複数形の業務名詞に合わせる**） |
| 本リポの先行実例 | `AuditService`→`AuditEvents` / `ConfigurationService`→`Assumptions` / `NotificationService`→`Notifications`（いずれも**サービス名の機械変形ではなくルート名詞**） |

操作フォルダの兄弟（3 段目のスライス分割）は採らない（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) 決定1）ため
`_Shared/` も作らない（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定1）。

### 判断2: `Domain/` と `Features/Reports/` の切り分け（[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定3 の適用）

基準（**Domain ＝フレームワーク・DI・I/O に触れず業務概念そのものを表す型。ポート・アプリケーションサービス・
エンドポイント・DTO・ストアは `Features/<集約>/`**）と 🔴 注記（**移送で型の層を変えない**）をそのまま適用した。

| 元 | 型 | 置き場 |
| --- | --- | --- |
| `src/*.Domain/`（27 ファイル） | エンティティ・値オブジェクト・純関数の集計器／レンダラ | **`Domain/`**（そのまま） |
| `src/*.Application/Ports/`（`IClock` 以外の 11） | `IBorrowFeeRecordSource` ほか | **`Features/Reports/`** |
| `src/*.Application/Services/`（6） | `ReportAppService` / `ReportAutoGenerator` / `ReportDraftService` / `ReportNarrativePromptBuilder` / `ReportNarrativePurpose` / `ReportNarrativeTimeouts` | **`Features/Reports/`** |
| `src/*.Application/State/`（2） | `ConfirmedDailyPolicy` / `VersionedReport` | **`Features/Reports/`**（純関数を含むが元の層＝Application を維持。決定3 🔴 注記） |
| `src/*.Api/Foundation/Endpoints/ReportEndpoints.cs` | エンドポイント＋要求 DTO 5 種 | **`Features/Reports/`** |
| `src/*.Application/ReportConcurrencyException.cs`（Application 直下） | 例外 | **`Common/Exceptions/`**（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) 決定1 の樹形が `Common/` に `Exceptions/` を名指しする。**2 本目 ConfigurationService の `AssumptionsConcurrencyException`（同じく Application 直下）と同型**） |

### 判断3: `IClock` / `SystemClock` は `Common/Abstractions/`

[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定3 のそのままの適用（I/O を持たない技術プリミティブ）。新しい判断ではない。

### 判断4: ストア実装は本番実装の Infrastructure 区分に合わせて対で置く

| ポート | 本番実装 | 既定／代替実装 | 置き場 |
| --- | --- | --- | --- |
| `IReportStore` | `EfReportStore` | `InMemoryReportStore`（旧 `Application/Adapters/`） | **`Infrastructure/Persistence/`** |
| `IPeriodFillSource` ほか供給ポート 6 種 | `Http*Source` / `Publishing*Reporter` / `MessageBus*Notifier` | `NoOp*` / `Unsupplied*` / `NoMarginReductionRecordSource`（旧 `Application/Adapters/`） | **`Infrastructure/ExternalServices/`** |

`ReportDbContext` / `ReportDbContextFactory` / `ReportRow` / `Migrations/`（3 本）は `Infrastructure/Persistence/` へ。
**`Infrastructure/Messaging/` は作らない** —— publish 系（`PublishingLlmUsageReporter` /
`PublishingLlmGovernanceReporter` / `MessageBusReportDraftPresentedNotifier`）は先行 7 本と同じく
`Infrastructure/ExternalServices/` へ置く（本リポの移送済み 7 サービスに `Messaging/` は 1 つも無い）。

### 判断5: BackgroundService は `Hosted/`、その専用 Options も `Hosted/`

`ReportAutoGenerationService`（唯一の `BackgroundService`）と `ReportAutoGenerationOptions` は
**どちらも元の層が `Infrastructure/Composable/Polling/`**（＝`Infrastructure/<層>/` 直下）であるため、
w11s5 の `MonitorOptions` と同型で `Hosted/` へ同居させる（w11s8 申し送り4 の判定基準「**元の層で判断する**」の適用）。

### 判断6: `Infrastructure/Steps/` は作らない

本サービスは Wolverine の consumer（`[WolverineHandler]` / `Handle(` を持つハンドラ）を **1 本も持たない**
（実測 0 件。`deploy/helm/ai-stock-trading/files/pipeline.json` にも本サービスの consumer FQN は無い）。
**実体が無いフォルダは作らない**（指示どおり・[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) の樹形は典型例であって必須宣言ではない）。

### 判断7: `internal` → `public` は「Tests が直接参照する型」＋ CS0053 連鎖に限る（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定4）

`^internal ` の行頭アンカーに加え、**`^\s+internal `（インデントされたメンバー宣言）も別途走査した**
（w11s8 申し送り1）。実測: 行頭 23 件・インデント 2 件（`ReportPolicyDraft` の入れ子クラス内の
`internal static readonly Regex BlankRun` / `Patterns`。**テストからの参照 0 件のため据え置き**）。

| 型 | public 化の根拠（テストからの直接参照） |
| --- | --- |
| `EfReportStore` | `EfReportStoreTests` / `ReportBodyPersistenceTests` が `new EfReportStore(db)` |
| `ReportDbContext` | 上記 2 ファイルが `new ReportDbContext(options)` / `DbContextOptionsBuilder<ReportDbContext>` |
| `ReportRow` | **CS0053 連鎖**（`ReportDbContext.Reports` が `public DbSet<ReportRow>`） |
| `HttpReportNarrativeDrafter` | `HttpReportNarrativeDrafterTests` / `…VisibilityTests` が直接構築 |
| `PlaceholderReportNarrativeDrafter` | `LlmGovernanceWiringTests` が `BeOfType<…>()` |
| `ReportKnowledgeMapper` | `ReportKnowledgeMapperTests` が `ReportKnowledgeMapper.ToDocument(...)`（静的） |
| `ReportAutoGenerationService` | `ReportAutoGenerationServiceLoggingTests` / `ReportAutoGenerationWiringTests` |
| `HttpBorrowFeeRecordSource` / `HttpBuyInInferenceRecordSource` / `HttpFxSourceStatusSource` / `HttpLlmUsageRecordSource` / `HttpPeriodFillSource` | 各 `Http*Tests` が直接構築 |
| `PublishingLlmUsageReporter` / `PublishingLlmGovernanceReporter` | `PublishingLlmReportersTests` が直接構築 |
| `MessageBusReportDraftPresentedNotifier` | `ReportAutoGenerationWiringTests` が `BeOfType<…>()` |

`internal` のまま据え置いたもの: `ReportDbContextFactory`（`dotnet ef` がリフレクションで発見。先行 2 本と同じ判断）・
`AuditPeriodRange`・`ReportEndpoints` と要求 DTO 5 種（`ReviewCommandRequest` / `UpsertReportRequest` /
`ConfirmReportRequest` / `PnlSummaryRequest` / `DraftReportRequest`。テストは HTTP 面越しに匿名オブジェクトで叩く）・
`ReportPolicyDraft` の入れ子 `internal static` 2 件。
**`InternalsVisibleTo` は新設せず、旧 csproj の 4 エントリはすべて削除した**（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定4）。

### 判断8: 名前空間の書き換え（**フォルダだけを動かし、要らない書き換えはしない**）

[IADR-0261](../adr/IADR-0261_namespace-alignment-to-platform.md) で `ReportService.*` へ先行整合済み。フォルダ移動に伴い変えたのは以下のみ。

| 旧 | 新 |
| --- | --- |
| `ReportService.Application.Ports`（`IClock` 除く）/ `.Services` / `.State` / `ReportService.Api.Endpoints` | `ReportService.Features.Reports` |
| `ReportService.Application.Ports.IClock` / `ReportService.Application.Adapters.SystemClock` | `ReportService.Common.Abstractions` |
| `ReportService.Application`（`ReportConcurrencyException`） | `ReportService.Common.Exceptions` |
| `ReportService.Application.Adapters.InMemoryReportStore` | `ReportService.Infrastructure.Persistence` |
| `ReportService.Application.Adapters`（残り 9 の `NoOp*`/`Unsupplied*`/`NoMargin*`） | `ReportService.Infrastructure.ExternalServices` |
| `ReportService.Infrastructure.Adapters` | `ReportService.Infrastructure.ExternalServices` |
| `ReportService.Infrastructure.Polling` | `ReportService.Hosted` |
| テスト 4 種（`.Api.Tests` / `.Application.Tests` / `.Domain.Tests` / `.Infrastructure.Tests`） | `ReportService.Tests` |

**不変**: `ReportService.Domain` / `ReportService.Infrastructure.Persistence` / `ReportService.Infrastructure.Migrations`。
→ **`ReportDbContextModelSnapshot` / 3 つの `*.Designer.cs` が持つエンティティ FQN
`"ReportService.Infrastructure.Persistence.ReportRow"` は 1 文字も変わらない。**

## 目標樹形

```
backend/Services/ReportService/
├── ReportService.csproj            (Sdk="Microsoft.NET.Sdk.Web")
├── Program.cs / appsettings.json / appsettings.Development.json
├── Domain/                         27
├── Features/Reports/               20（ポート 11・サービス 6・状態 2・エンドポイント 1）
├── Common/Abstractions/            2（IClock / SystemClock）
├── Common/Exceptions/              1（ReportConcurrencyException）
├── Infrastructure/
│   ├── Persistence/                5（+ Migrations/ 7）
│   └── ExternalServices/           21
├── Hosted/                         2
└── Tests/ReportService.Tests.csproj + 53 .cs + Golden/ 6 .md
```

（`Infrastructure/Steps/` と `Common/Behaviors/` は実体が無いので作らない。）

## `IntegrationTests` の参照張り替え（本サービス固有の必須作業）

```diff
-    <ProjectReference Include="..\..\Services\ReportService\src\ReportService.Api\ReportService.Api.csproj" Aliases="ReportWorker" />
+    <ProjectReference Include="..\..\Services\ReportService\ReportService.csproj" Aliases="ReportWorker" />
```

**`Aliases="ReportWorker"` は保持した**（手本 = 同ファイルの `CostControlService.csproj" Aliases="CostControlWorker"`）。
`ServiceTokenSyncQueryE2ETests.cs` の `extern alias ReportWorker;`（2 行目）と
`ReportWorker::Program`（41・100 行目）、およびテスト本文には**一切触れていない**。
同ファイルは `using ReportService.*` を 1 本も持たないため、8 本目で必要だった `.cs` 側の追随は発生しなかった（母集合の節で実測）。

## テスト件数の突合（移送前後を実測。削っていないことの証跡）

移送前は旧テストプロジェクトが存在する段階で個別 `dotnet test` を実行して実測した（着手直後・base `bd39cbd`）。

| テストアセンブリ | 移送前 | 移送後 |
| --- | ---: | ---: |
| `ReportService.Api.Tests` | 45 | — |
| `ReportService.Application.Tests` | 116 | — |
| `ReportService.Domain.Tests` | 278 | — |
| `ReportService.Infrastructure.Tests` | 143 | — |
| **`ReportService.Tests`** | — | **582** |
| 合計 | **582** | **582** |

`[Fact]`/`[Theory]` 属性数でも裏を取った: 移送前 43+101+215+111 = **470** ／ 移送後 **470**。
`.cs` ファイル数: 移送前 **139**（src 86・tests 53）／ 移送後 **139**（`git mv` のみ）。

## `list-test-projects.js --count`

- 移送前: **29**（base `bd39cbd`＝develop への OrderExecutionService〔#593〕マージ直後）
- 移送後: **26**（旧 4 → 新 1 の差分 −3 と一致）

🔴 **導出値はどの base に対する値かに依存する。** develop を後から取り込めば両方とも動く。

## IADR を作らない判断

**本 PR では新しい IADR（`IADR-0266`）を作らない。** 4〜9 本目と同じ判断である。
判断1〜8 のすべてが [IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) の写像方針表・
[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) の 5 決定・
[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定3 から機械的に導ける。

- 判断1（集約名 `Reports`）は「機械的規則より実例を優先する」という**既存の運用指示の適用**であり、新しい設計軸ではない。
- 判断6（`Infrastructure/Steps/` を作らない）は [IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) が既に明記する
  「樹形は典型例であり全ファイル必須の宣言ではない」の適用（`Endpoint.cs` を持たないサービスの扱いと同型）。
- 判断7（`internal`→`public`）は決定4 のそのままの適用。

## 計画書との差異

- 差異: なし。本件は構造移送のみで振る舞いを変えていない（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) 決定7）。

## 受け入れ基準（実測・すべて充足）

- [x] `dotnet build backend/backend.slnx --no-restore` が **0 Warning / 0 Error**
      （`build-server shutdown` → `bin`/`obj` 全消去 → 空ディレクトリ削除 → `dotnet restore` →
      `--no-restore` フルビルド。`Build succeeded. / 0 Warning(s) / 0 Error(s) / Time Elapsed 00:00:44.28`）
- [x] `dotnet test backend/backend.slnx --no-build` の失敗は **`AiStockTrading.IntegrationTests` の 8 件のみ**
      （`Failed: 8, Passed: 5, Total: 13`。全件 `Failed to connect to Docker endpoint at
      'unix:///var/run/docker.sock'` の環境制約。**`ReportWorker::Program` を使う
      `ServiceTokenSyncQueryE2ETests` も同じ理由で失敗しており、ビルドは通っている**）。
      **`ReportService.Tests` は 582/582 全緑。**
- [x] `dotnet format backend/backend.slnx --verify-no-changes` が **exit 0**（出力なし）
- [x] `dotnet ef migrations has-pending-model-changes` = **`No changes have been made to the model
      since the last migration.`**（exit 0）
- [x] **`MigrationId` 不変**: migration 関連 7 ファイル（3 migration × 2 ＋ snapshot）を base `bd39cbd` の
      内容と `diff` し**全件 IDENTICAL**。`[Migration("20260710123313_InitialCreate")]` /
      `[Migration("20260717181512_AddReviewState")]` / `[Migration("20260729110333_AddReportBody")]` も不変。
      エンティティ FQN `"ReportService.Infrastructure.Persistence.ReportRow"` も不変
- [x] カバレッジ **82.51%（16506/20006 行）/ floor 79.00%**（`coverage-floor.json` は未編集）。
      **レポート 26 件 = `list-test-projects --count` 26 件**（Release・`cov` 作り直し）
- [x] 検査器（終了コードを直接確認・すべて EXIT:0）: `check-doc-links`（578 件）/ `check-adr-index-sync` /
      `check-cross-repo-refs`（1894 件）/ `check-plan-id-qualification`（1938 件）/ `check-trace-blocks`（41 件）/
      `check-test-traceability`（463 件・旧樹形 159／新樹形 243）/ `check-banned-libraries` /
      `check-banned-settled-cash-sources` / `check-reading-budget` / `check-consumer-endpoint-names`（694 件）/
      `check-observability-assets` / `check-action-versions` / `check-workflow-job-refs` / `check-ai-workflow-config` /
      `gen-knowledge-graph --self-test`・`--check` / `validate-runtime-scaffold` /
      `validate-pipeline-config --self-test`・実データ / `scripts.test.js`（294 tests passed）
- [x] `DomainLayerDependencyTests` の下限は自動追随（`RepositoryLayout.cs` /
      `DomainLayerDependencyTests.cs` は**本 PR で 1 行も編集していない**）。
      `AiStockTrading.Architecture.Tests` 88/88 全緑
- [x] `pgrep -c dotnet` が 0（作業終了前）

## 最後の 1 本（RiskManagementService）への申し送り

1. 🔴🔴 **親名前空間の暗黙解決（想定外1）を最初から見込むこと。** RiskManagement は `.cs` **339 件**・migration **20 本**の最大サービスで、
   `Api`/`Application`/`Domain`/`Infrastructure` の 4 テストプロジェクトを持ち、`Domain.Tests` /
   `Application.Tests` が祖先解決に依存している可能性が高い。**1 回目のクリーンビルドで大量の
   `CS0246`/`CS0103` が出るのは正常**であり、是正は「対象ファイルへ `using <元の祖先 namespace>;` を
   足すだけ」で本文は触らない。**事前 grep では絶対に見つからない**（壊れる前のソースにその using が無い）。
2. 🔴 **migration 20 本・`extern alias ×2`。** `AiStockTrading.IntegrationTests.csproj` は
   `RiskManagementService.Api` と `RiskManagementService.Infrastructure` の**2 行**に
   `Aliases="RiskManagementWorker"` を与えている。**単一プロジェクト化で参照は 1 本に畳まれる**ため、
   2 行を 1 行へまとめ、`Aliases="RiskManagementWorker"` を保持すること。
   🔴 **`ReportWorker` と違い、`RiskManagementWorker` は `Program` 以外も指す。** 実測（本 PR で自分で走査した）:
   `TradeExecutionPipelineE2ETests.cs:13` と `PositionDriftStateConcurrencyE2ETests.cs:9,10` が
   `using X = RiskManagementWorker::RiskManagementService.Infrastructure.Persistence.{RiskManagementDbContext,
   EfPositionDriftStateStore};` の形で**別名越しに完全修飾した型エイリアス**を持つ。
   **この 3 行の FQN は `Infrastructure.Persistence` のままで正しい**（同 csproj のコメントは
   「`Infrastructure.Foundation.Persistence` で解決する」と書いているが、**IADR-0261 の名前空間整合で
   既に `Foundation` セグメントは消えており、コメントの方が古い**。実測で確認した。**コメントを転記しない**）。
   ただし `EfPositionDriftStateStore` は現在 `internal` の可能性があり、単一プロジェクト化で
   `InternalsVisibleTo` が消えると**統合テスト側から見えなくなる**。IADR-0263 決定4 の
   「Tests が直接参照する型」に統合テストも含めて判定すること。
3. 🔴 **`.gitleaksignore` は書き換えない。** fingerprint は `<commit>:<当時のパス>:<rule>:<line>` で
   **履歴上のパス**を指す。RiskManagement のエントリがあっても現行パスへ直すと誤検知が復活する。
4. **`scripts/scripts.repo.test.js` の合成パス文字列も書き換えない**（glob の単体テスト。実ツリー非依存）。
   `backend/Tests/AiStockTrading.Architecture.Tests/DomainSourceDependencyTests.cs` の
   `[InlineData("RiskManagementService.Domain")]` 等も**純粋な文字列判定の入力例**で、
   サービス名が実ツリーに残る限り成立し続ける。
5. **移送前のテスト件数は旧プロジェクトが消える前に個別 `dotnet test` で実測**し、
   `[Fact]`/`[Theory]` 属性数と `.cs` 数でも裏を取ること（1〜10 本目の申し送りを継続）。
6. **掃除系スクリプトは適用前後で対象外構文の出現数を突合**（`using var` 等。想定外4）。
7. **`list-test-projects --count` は base で変わる。** 本 PR の 29 → 26 は base `bd39cbd` に対する値である。
