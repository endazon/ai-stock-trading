---
title: CostControlService を単一プロジェクト＋VSA 樹形へ移送する（W11 段 4-3・Domain を持つ場合の型の初適用）
type: spec
status: approved
related_ids: [NFR, IADR-0259, IADR-0263, IADR-0264, IADR-0265]
author: endazon (with Claude Code)
created: 2026-08-29
updated: 2026-08-29
plan_refs: []
---

# 仕様書: CostControlService の単一プロジェクト＋VSA 移送（W11 段 4-3）

> **11 サービス移送波の 3 本目**である。1 本目（AuditService・[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)）・
> 2 本目（ConfigurationService・[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md)）で
> 確定した判断の型を適用しつつ、**AST でこのサービスが初めて「`Domain/` を実際に持つ移送」**になる
> ([IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定3の初適用。
> 同 ADR は ConfigurationService 自身が移送の帰結で Domain を失ったため、AST 内での実例を残せなかった）。
> 加えて、**下限のハードコードを動的化する追加作業**（[IADR-0265](../adr/IADR-0265_domain-project-count-checker-dynamic-lower-bound.md)）を行う。

## 起点

- 起点 ID: **`NFR`（無採番）**。構造移送＝メタ作業であり、`.claude/rules/traceability.md`
  「起点 ID の種別」の無採番許容ケース **2** に当たる（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)
  が計画の非機能要件表を全行読んで確定済みの判断を継承する。環流はしない）。
- 上流: [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)（1 本目の 5 決定）・
  [IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md)（2 本目。特に決定3
  「Domain を持つサービスの型」・決定5「下限は実測で読む」）・
  [IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)（樹形・写像方針表・決定5「BackgroundService
  はルート直下 Hosted/」）・[IADR-0258](../adr/IADR-0258_structure-aware-checkers-dual-layout.md)
  （構造依存の検査器の新旧両対応）

## 着手前に読んだもの

- `CLAUDE.md` / `.claude/rules/traceability.md` / `.claude/rules/traceability.repo.md` /
  `docs/DEFINITION_OF_DONE.md`
- [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) /
  [IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) /
  [IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) /
  [IADR-0258](../adr/IADR-0258_structure-aware-checkers-dual-layout.md)
- [20260829_w11s4a_auditservice-vsa](20260829_w11s4a_auditservice-vsa.md) /
  [20260829_w11s4b_configurationservice-vsa](20260829_w11s4b_configurationservice-vsa.md)（手順・落とし穴の申し送り）
- 基盤の実物（読み取り専用）: `/home/user/microservices-platform/src/platform/backend/Services/`
  の `NotificationService` と `AuthorizationService`（`Domain/` と `Features/` の切り分けの実例。
  2 本目が既に読んで基準を確定済みのため、本 PR は同じ基準を適用するのみで再読はしていない）

## 対象範囲

- 対象: `backend/Services/CostControlService/`（8 csproj → 2 csproj）、`backend/backend.slnx`、
  `docker-compose.yml`、`scripts/k8s-local-images.sh`、
  `backend/Tests/AiStockTrading.IntegrationTests/AiStockTrading.IntegrationTests.csproj`
  （`CostControlWorker` extern alias の参照先。**母集合の走査で見つけた想定外**。後述）、
  `backend/Tests/AiStockTrading.Architecture.Tests/`（`RepositoryLayout.cs` /
  `DomainLayerDependencyTests.cs`。IADR-0265 の追加作業）
- 対象外: 他 9 サービス（次の PR 以降）、`backend/Shared/` `backend/TestSupport/`（据え置き集合）、
  `Infrastructure.Persistence` / `Infrastructure.Migrations` の名前空間（EF の型名文字列。触らない）

## 着手前の母集合の引き直し（`.claude/rules/traceability.repo.md` 規則1〜10）

**母集合は記憶で挙げず、誤りになる側の文字列で全追跡ファイルを走査して引いた**（規則1・2・9・10）。
走査した語は `CostControlService\.(Api|Application|Domain|Infrastructure)` /
`CostControlService/(src|tests)` の 2 本。

| 項目 | 実測 |
| --- | --- |
| 移送前の .cs（src + tests） | 60（src 37・tests 23） |
| 移送前の csproj | 8（src 4・tests 4） |
| migration | 1 本（+ 3 Designer + 1 ModelSnapshot） |
| `list-test-projects.js --count` | **46**（クリーンな作業ツリーで実測） |
| `CostControlService` を参照する他サービスの `ProjectReference`（`backend/Services` 配下） | 0 件 |
| `CostControlService` を参照する `backend/Tests` 配下の `ProjectReference` | **1 件**
  （`AiStockTrading.IntegrationTests.csproj` が `CostControlService.Api.csproj` を
  `Aliases="CostControlWorker"` で参照。`ServiceTokenSyncQueryE2ETests.cs` が
  `extern alias CostControlWorker; … CostControlWorker::Program` で使用） |
| `deploy/helm/.../pipeline.json` の CostControlService 関連 consumer 参照 | 0 件（対象外） |
| `docs/` 配下の CostControlService パス参照（`CostControlService\.(Api\|Application\|Domain\|Infrastructure)`
  等） | 1 件（`docs/data/cost-entries.md`。サービス名の言及のみでパス参照ではないため対象外） |

### 母集合の走査で見つかった「想定外」

1. 🔴 **`AiStockTrading.IntegrationTests` が `CostControlService.Api.csproj` を extern alias
   （`CostControlWorker`）で参照していた。** 1 本目（AuditService）・2 本目（ConfigurationService）の
   いずれもこの参照を持たず、両仕様書の「対象範囲」に前例が無い。**`backend/Services` 配下だけを
   見る走査では見つからない**（母集合の規則3「拡張子で絞らない・パスの除外だけで取る」を破ると
   見落とす典型）。`backend/Tests` も含めて全文走査して発見した。対応は「設計」節参照。
2. `CostControlService.Infrastructure/ExternalServices/` と
   `CostControlService.Infrastructure/Composable/Steps/AssumptionsChangedHandler.cs` は
   [IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定1・決定4により
   **既に移送後の形（フォルダ名・名前空間）で存在していた**——タスク文どおり、
   `src/CostControlService.Infrastructure/ExternalServices/` を
   `Infrastructure/ExternalServices/` へ動かすだけで済んだ（想定外ではなく、申し送りどおり）。
3. `CostControlService.Infrastructure/Composable/Adapters/AssumptionsCostLimitsProvider.cs` の
   名前空間は `CostControlService.Infrastructure.Adapters`（`.ExternalServices` ではない）。
   フォルダは `ExternalServices/` へ移すため、名前空間も合わせて変更が要った
   （「フォルダ名を移送後の形に合わせてある」対象には**含まれていなかった**——2 本目の申し送りが
   指した 6 ファイルには入っていない、本サービス固有の 1 ファイル）。

## 設計

### 判断1: `Domain/` と `Features/CostControl/` の切り分け（IADR-0264 決定3の初適用）

[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定3の基準
（**Domain ＝フレームワーク・DI・I/O に触れず業務概念そのものを表す型。ポート・アプリケーションサービス・
エンドポイント・DTO・ストアは Features/<集約>/**）を、そのまま適用した。**AST 内での初適用**である。

| 元の場所 | 型 | 判定 | 置き場 |
| --- | --- | --- | --- |
| `CostControlService.Domain/CostGovernor.cs` | `CostCategory`（enum）・`CostControlState`（enum）・`CostControlDecision`（record）・`CostGovernor`（統制判定の純関数） | I/O・DI 皆無。80%/100% しきい値判定の純粋な業務規則 | **`Domain/`** |
| `CostControlService.Domain/CostReview.cs` | `CostReview`（費用÷資金比率の純関数） | 同上 | **`Domain/`** |
| `CostControlService.Application/Services/CostControlAppService.cs` | `CostControlAppService`（アプリケーションサービス） | ポート（`ICostLedger`/`ICostLimitsProvider`/`IClock`）に依存し計上・統制判定を編成する | **`Features/CostControl/`** |
| `CostControlService.Application/Ports/{ICostLedger,ICostLimitsProvider,IProcessedMessageStore}.cs` | ポート（インターフェース） | ポートは Features 側（決定3） | **`Features/CostControl/`** |
| `CostControlService.Application/State/{MonthlyCostUsage,RecordCostResult}.cs` | 結果 DTO | DTO は Features 側（決定3） | **`Features/CostControl/`** |
| `CostControlService.Api/Foundation/Endpoints/CostControlEndpoints.cs` | エンドポイント | エンドポイントは Features 側（決定3） | **`Features/CostControl/`** |

集約は 1 つ（`CostControl`。エンドポイントのルートグループ `/costs`・`WithTags("CostControl")` に
揃えた）で、[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定1
（`_Shared/` は操作フォルダの兄弟が実在する場合だけ作る）により `_Shared/` は作らず、
`Features/CostControl/` 直下へ平らに置いた。

**Domain と Features の境界は「移送時点の層」をそのまま引き継いだ**（`Domain/` にあったものを
そのまま `Domain/` へ、`Application` にあったものをそのまま `Features/` へ）。フォルダ移送を理由に
層を変える判断（例: `RecordCostResult` を Domain へ昇格する）は行っていない
（[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定3の明記どおり）。

### 判断2: 技術プリミティブ（`IClock`）は `Common/Abstractions/`（1 本目の判断3を踏襲）

`IClock`／`SystemClock` は 1 本目（AuditService）と同じ理由（I/O を持たない技術プリミティブ）で
`Common/Abstractions/` に置いた。

### 判断3: `ICostLimitsProvider` の 2 実装（`AssumptionsCostLimitsProvider` / `DefaultCostLimitsProvider`）は
`Infrastructure/ExternalServices/` へ揃える（新規判断・本 PR 固有）

写像方針表の既定は「`Adapters/` → `Infrastructure/` の該当区分」であり、`ICostLimitsProvider` の
実装 2 本はどちらも `Infrastructure/` に置くべきだが、**該当区分（Persistence/Steps/ExternalServices）が
どれかは自明ではなかった**——どちらも DB・メッセージングを持たない。

- **`AssumptionsCostLimitsProvider`**: `IAssumptionsProvider`（`Infrastructure/ExternalServices/`。
  旧 `ConfigurationService.Client`〔[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md)
  決定1〕由来）を呼び出す薄いアダプタであり、費用上限の実解決は設定サービスへの照会に依存する。
- **`DefaultCostLimitsProvider`**: 外部依存を持たない既定値の供給元。ソース中のコメントが
  「位置づけは同層の `InMemoryCostLedger` と同じ」と明記しており、著者自身が
  「テスト・縮退用の代替実装」という性質を認識していた。

`ICostLimitsProvider` の**もう一方の実装が既に外部前提条件解決の文脈（`ExternalServices/`）に
属する**ため、同一ポートの 2 実装を分断せず並べて置くことを優先し、**`DefaultCostLimitsProvider` も
`Infrastructure/ExternalServices/` に置いた**（`InMemoryCostLedger`/`InMemoryProcessedMessageStore`
とは異なり、`Infrastructure/Persistence/` へは置かなかった——`ICostLedger`/`IProcessedMessageStore`
は永続化ポートだが `ICostLimitsProvider` は永続化ポートではないため、1 本目の判断4
〔`InMemoryAuditEventStore` → `Infrastructure/Persistence/`〕をそのまま流用しなかった）。

### 判断4: `InMemoryCostLedger` / `InMemoryProcessedMessageStore` は `Infrastructure/Persistence/`
（1 本目の判断4を踏襲）

`ICostLedger`/`IProcessedMessageStore` は永続化ポートであり、EF 実装（`EfCostLedger`/
`EfProcessedMessageStore`）と同じ `Infrastructure/Persistence/` に揃えた。

### 判断5: `ProcessedMessageRetentionService`（`BackgroundService`）は **ルート直下 `Hosted/`**
（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) 決定・利用者裁定5）

1 本目・2 本目はいずれも `BackgroundService` を持たなかったため、**`Hosted/` の実例は本 PR が
AST 内で初めて作る**。[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) の樹形図が
明示する「`Hosted/`（AST 固有: `BackgroundService` はルート直下）」をそのまま適用した——
旧 `Infrastructure/Composable/Retention/ProcessedMessageRetentionService.cs` を
`Infrastructure/` 配下ではなく `CostControlService/Hosted/ProcessedMessageRetentionService.cs`
へ置いた（名前空間も `CostControlService.Infrastructure.Retention` → `CostControlService.Hosted`
へ変更。DI 登録側の `AddHostedService<ProcessedMessageRetentionService>()` は型の変更のみで
配線自体は不変）。

### 判断6: `AiStockTrading.IntegrationTests` の extern alias 参照は据え置き名で追随

母集合の走査で見つけた想定外1（前掲）への対応。`ProjectReference` のパスを
`Services\CostControlService\src\CostControlService.Api\CostControlService.Api.csproj` から
`Services\CostControlService\CostControlService.csproj` へ張り替えた。**`Aliases="CostControlWorker"`
は変更していない**——[IADR-0128](../adr/IADR-0128_standard-project-layout.md) 世代の先例（
`RiskManagementWorker`・`ReportWorker`）が「extern alias 名はテスト本文の変更を避けるため据え置く」
としているのと同じ理由で、`ServiceTokenSyncQueryE2ETests.cs` 側は 1 行も変更していない
（`Program` は移送後もトップレベルステートメントの暗黙クラスであり、`RootNamespace` の変更の影響を
受けないため、参照は変更なしで解決する）。

### 写像表（CostControlService）

| 移送前 | 移送後 | 根拠 |
| --- | --- | --- |
| `src/*.Api/Program.cs` `appsettings*.json` | ルート直下 | 決定1の樹形 |
| `src/*.Api/Foundation/Endpoints/CostControlEndpoints.cs` | `Features/CostControl/` | 判断1 |
| `src/*.Application/Services/CostControlAppService.cs` | `Features/CostControl/` | 判断1 |
| `src/*.Application/Ports/{ICostLedger,ICostLimitsProvider,IProcessedMessageStore}.cs` | `Features/CostControl/` | 判断1 |
| `src/*.Application/State/{MonthlyCostUsage,RecordCostResult}.cs` | `Features/CostControl/` | 判断1 |
| `src/*.Domain/{CostGovernor,CostReview}.cs` | `Domain/` | 判断1 |
| `src/*.Application/Ports/IClock.cs` `Adapters/SystemClock.cs` | `Common/Abstractions/` | 判断2 |
| `src/*.Application/Adapters/{InMemoryCostLedger,InMemoryProcessedMessageStore}.cs` | `Infrastructure/Persistence/` | 判断4 |
| `src/*.Application/Adapters/DefaultCostLimitsProvider.cs` | `Infrastructure/ExternalServices/` | 判断3 |
| `src/*.Infrastructure/Composable/Adapters/AssumptionsCostLimitsProvider.cs` | `Infrastructure/ExternalServices/` | 判断3 |
| `src/*.Infrastructure/ExternalServices/**`（6 ファイル） | `Infrastructure/ExternalServices/` | IADR-0264 決定1（既に移送後の形。動かすだけ） |
| `src/*.Infrastructure/Composable/Steps/**`（2 ファイル） | `Infrastructure/Steps/` | IADR-0263 決定5 |
| `src/*.Infrastructure/Composable/Retention/ProcessedMessageRetentionService.cs` | `Hosted/` | 判断5 |
| `src/*.Infrastructure/Foundation/Persistence/**` | `Infrastructure/Persistence/` | 決定1の樹形。**名前空間は不変** |
| `src/*.Infrastructure/Migrations/**` | `Infrastructure/Persistence/Migrations/` | 1 本目・2 本目と同じ。**名前空間は不変** |
| `tests/*.{Api,Application,Domain,Infrastructure}.Tests/**` | `Tests/`（フラット） | IADR-0259 決定4 |

### 名前空間

[IADR-0261](../adr/IADR-0261_namespace-alignment-to-platform.md) で `CostControlService.*` へ
先行整合済み。変えたのはフォルダ移動に伴う以下のみ。

- `CostControlService.Api.Endpoints` → `CostControlService.Features.CostControl`
- `CostControlService.Application[.{Ports,Services,State}]` → `CostControlService.Features.CostControl`
- `CostControlService.Application.Ports`（`IClock`）・`.Application.Adapters`（`SystemClock`）
  → `CostControlService.Common.Abstractions`
- `CostControlService.Application.Adapters`（`InMemoryCostLedger`/`InMemoryProcessedMessageStore`）
  → `CostControlService.Infrastructure.Persistence`
- `CostControlService.Application.Adapters`（`DefaultCostLimitsProvider`）・
  `CostControlService.Infrastructure.Adapters`（`AssumptionsCostLimitsProvider`）
  → `CostControlService.Infrastructure.ExternalServices`
- `CostControlService.Infrastructure.Retention`（`ProcessedMessageRetentionService`）
  → `CostControlService.Hosted`
- 🔴 **`CostControlService.Infrastructure.Persistence` / `.Infrastructure.Migrations` は触らない**
  （EF の `modelBuilder.Entity("...")` が完全修飾名を文字列で持つ）。
- `CostControlService.Infrastructure.ExternalServices`・`.Infrastructure.Steps` は
  [IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定1・決定4で
  既に整合済みのため不変。

### `internal` → `public` の判定（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定4）

Tests が**直接参照する型だけ**を public にした（DI 経由のインターフェース越しは対象外）。
`InternalsVisibleTo` は新設せず、旧 4 csproj のエントリはすべて削除した。**「まず1つ public にして
ビルドし、連鎖をコンパイラに拾わせる」**（1 本目・2 本目の申し送り）を実際に踏襲し、
`CostControlDbContext` を public にした直後に `CS0053`（`DbSet<CostEntryRow>` / `DbSet<ProcessedMessageRow>`
の連鎖）をコンパイラが実際に出したため、その 2 型も public にした。

| 型 | 理由 |
| --- | --- |
| `CostControlDbContext` | `Tests/EfProcessedMessageStoreTests.cs` 等が `new CostControlDbContext(options)` で、`CostControlWorkerWebApplicationFactory.cs` が `GetRequiredService<CostControlDbContext>()` 等で直接参照する |
| `CostEntryRow` / `ProcessedMessageRow` | `public DbSet<T>` が要求する（**CS0053 を実際に出して確認した**） |
| `EfCostLedger` / `EfProcessedMessageStore` | `Tests/EfCostLedgerTests.cs` / `Tests/EfProcessedMessageStorePurgeTests.cs` 等が `new EfCostLedger(db)` / `new EfProcessedMessageStore(options)` で直接構築する |
| `AssumptionsCostLimitsProvider` | `Tests/AssumptionsCostLimitsProviderTests.cs` / `Tests/VersionedCostLimitsTests.cs` / `Tests/CostControlWiringTests.cs` が `new AssumptionsCostLimitsProvider(...)` で直接構築、または型として参照する |
| `DefaultAssumptionsProvider` | `Tests/AssumptionsClientRegistrationTests.cs` が `.Should().BeOfType<DefaultAssumptionsProvider>()` で型引数として参照する |
| `HttpAssumptionsClient` | `Tests/HttpAssumptionsClientTests.cs` が `new HttpAssumptionsClient(...)` で直接構築する |
| `CachedAssumptionsProvider` | `Tests/CachedAssumptionsProviderTests.cs` が `new CachedAssumptionsProvider(...)` で直接構築する |
| `ProcessedMessageRetentionService` | `Tests/ProcessedMessageRetentionServiceTests.cs` が `new ProcessedMessageRetentionService(...)` で直接構築する |

据え置き（`internal` のまま）: `CostControlEndpoints`（Program.cs と同一アセンブリで解決・Tests は
HTTP 経由でしか触らない）・`RecordCostRequest`（同上）・`CostControlDbContextFactory`（`dotnet ef` が
リフレクションで発見。Tests は参照しない）・`AssumptionsClientExtensions.DefaultCacheTtl`・
`CachedAssumptionsProvider.Unresolved`（Tests から直接参照されない）。

## Tests 統合（4 → 1）で変えていないことの証跡

**中身は 1 行も変えていない**（`git mv` のみ・変更は namespace 宣言・using・alias の書き換えに
限定。テストロジック・アサーション・`[Fact]`/`[Theory]` の数は不変）。

### テスト件数の突合（移送前後を実測。削っていないことの証跡）

移送前は各旧テストプロジェクトを個別に `dotnet test` して実測した（本 PR 着手直後・クリーンな
作業ツリーで測定。`git stash` は使っていない——旧プロジェクトがまだ存在する段階で先に測定したため）。

| テストアセンブリ | 移送前 | 移送後 |
| --- | ---: | ---: |
| `CostControlService.Api.Tests` | 9 | — |
| `CostControlService.Application.Tests` | 33 | — |
| `CostControlService.Domain.Tests` | 10 | — |
| `CostControlService.Infrastructure.Tests` | 71 | — |
| **`CostControlService.Tests`** | — | **123** |
| 合計 | **123** | **123** |

9 + 33 + 10 + 71 = 123 = 移送後の合格件数と**完全一致**。減った件・増えた件は 0。

## `list-test-projects.js --count` の突合

- 移送前: **46**
- 移送後: **43**
- 差分: **-3**（旧 4 テストプロジェクト → 新 1 テストプロジェクトの差分と一致。タスク文の
  「46 → 43」と一致）

## 受け入れ基準

- [x] `dotnet build backend/backend.slnx` が 0 warning / 0 error で通る
- [x] `dotnet test backend/backend.slnx` の失敗が `AiStockTrading.IntegrationTests` の 8 件のみ
      （Docker 不在の環境制約。1 回の実行で `TradeDecisionService.Api.Tests` が 1 件フレーキーに
      落ちたが、単体実行および全体再実行では 51/51 合格しており、本 PR の変更とは無関係の
      既知の不安定性〔`.ai-context/specs/20260807_357_flaky-tracked-session-timeout.md`〕である
      ことを実測で確認した）
- [x] `dotnet format backend/backend.slnx --verify-no-changes` が通る
- [x] `dotnet ef migrations has-pending-model-changes`（`--project`/`--startup-project` とも
      `backend/Services/CostControlService`）が「No changes have been made to the model since the
      last migration.」を返す
- [x] `list-test-projects.js --count` が 46 → 43
- [x] `coverage-floor.json` の床（79.00%）を割らない（実測は本文末尾）
- [x] 検査器一式（`scripts/README.md` 掲載分）が緑
- [x] `IADR-0265`（下限の動的化）を実装し、`DomainLayerDependencyTests` が実測 7 件を下限として
      緑になることを確認した（8 → 7。手書きの数値編集はしていない）

## 計画書との差異

- 差異: なし。本件は構造移送のみで振る舞いを変えていない（IADR-0259 決定7）。

## 残り 8 サービスへの申し送り（本 PR で踏んだ落とし穴・再利用可能な手順）

1. 🔴 **`backend/Services` 配下だけでなく `backend/Tests` 配下も含めて `ProjectReference` の
   母集合を走査すること。** `AiStockTrading.IntegrationTests` が extern alias でサービスの
   `.Api.csproj` を参照している例が、1 本目・2 本目には無く本 PR で初めて出た
   （`CostControlWorker`）。**現在判明している他の extern alias 参照**（`RiskManagementWorker`・
   `ReportWorker`・発注執行の無名参照）は未移送サービスに対するものなので、それぞれの移送時に
   同じ走査を繰り返すこと。
2. **`Domain/` を持つサービスでは、[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md)
   決定3の基準（フレームワーク・DI・I/O に触れない業務概念＝Domain、ポート・アプリケーション
   サービス・エンドポイント・DTO・ストア＝Features）をそのまま適用すればよい。** 本 PR がこの
   基準の AST 内初適用であり、機械的に迷わず判定できた。
3. **同一ポートの複数実装は、置き場の一貫性を優先してよい。** `ICostLimitsProvider` の 2 実装
   （`AssumptionsCostLimitsProvider`／`DefaultCostLimitsProvider`）は、片方が「外部依存あり
   （ExternalServices 相当）」・片方が「外部依存なし（Persistence の InMemory 系と同種）」で
   一見別区分に見えたが、**もう一方の実装が属する区分に揃えた**——2 分岐した置き場は読み手の
   探索コストを増やす。
4. **`BackgroundService` は必ず `Hosted/`（ルート直下）へ置く**（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)
   決定・利用者裁定5）。`Infrastructure/` 配下には置かない——1 本目・2 本目は該当が無かったため
   本 PR が AST 内での初適用である。
5. **`internal` → `public` は「まず1つ public にしてビルドし、連鎖をコンパイラに拾わせる」**
   （1 本目・2 本目の申し送りと同じ）。本 PR でも `DbSet<T>` の CS0053 連鎖が実際に出た。
6. **移送前のテスト件数は、旧プロジェクトが消える前に個別 `dotnet test` で実測しておく。**
   `git stash -u` に頼らずとも、着手直後（未変更のクリーンな状態）に先に測っておけば、
   後から「stash からの再現」を気にする必要がない。
7. **`DomainLayerDependencyTests` の下限は [IADR-0265](../adr/IADR-0265_domain-project-count-checker-dynamic-lower-bound.md)
   により動的化済みなので、以後の移送では何もしなくてよい。** 移送すれば
   `RepositoryLayout.UnmigratedServicesWithDomainProjectCount` が自動的に追随する。
   **`DomainSourceDependencyTests` の 2 下限は依然として手動確認が要る**——本 PR は
   「新旧樹形の和集合で数えるため Domain を持つサービスの移送では変化しない」ことを実測したが、
   複数集約や Domain 新設を伴う将来の移送では変わり得るため、**毎回実測してから判断すること**
   （IADR-0265 決定4）。
