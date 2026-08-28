---
title: ConfigurationService を単一プロジェクト＋VSA 樹形へ移送し、ConfigurationService.Client を廃止する（W11 段 4-2）
type: spec
status: approved
related_ids: [NFR, IADR-0259, IADR-0260, IADR-0263, IADR-0264]
author: endazon (with Claude Code)
created: 2026-08-29
updated: 2026-08-29
plan_refs: []
---

# 仕様書: ConfigurationService の単一プロジェクト＋VSA 移送と `*.Client` 廃止（W11 段 4-2）

> **11 サービス移送波の 2 本目**である。1 本目（AuditService・#583）で確定した判断の型
> （[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定 1〜5）を
> 適用しつつ、**AuditService には無かった 2 つの固有事情**——`Domain/` を持つこと、
> **他サービスへ公開するクライアントライブラリ `ConfigurationService.Client` を持つこと**——を
> 本 PR で確定する。結論は [IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md)。

## 起点

- 起点 ID: **`NFR`（無採番）**。構造移送＝メタ作業であり、`.claude/rules/traceability.md`
  「起点 ID の種別」の無採番許容ケース **2** に当たる（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)
  が計画の非機能要件表を全行読んで確定済みの判断を継承する。環流はしない）。
- 併せて **#526（`ConfigurationService.Client` が標準構成から逸脱している）を解消する**
  ——[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) 決定 9 が
  「移行に吸収する」と定めた帰結の実施である（gRPC 化は同決定により本波では行わない）。

## 着手前に読んだもの

- `CLAUDE.md` / `.claude/rules/traceability.md` / `.claude/rules/traceability.repo.md` /
  `docs/DEFINITION_OF_DONE.md`
- [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)（1 本目で確定した 5 決定）・
  [20260829_w11s4a_auditservice-vsa](20260829_w11s4a_auditservice-vsa.md)（1 本目の手順と落とし穴）
- [IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)（樹形・写像方針・決定 2 と決定 9）・
  [IADR-0260](../adr/IADR-0260_shared-kernel-for-cross-service-domain-types.md)（共有カーネルの憲章）・
  [IADR-0261](../adr/IADR-0261_namespace-alignment-to-platform.md)・
  [IADR-0258](../adr/IADR-0258_structure-aware-checkers-dual-layout.md)
- **#526 の本文**（「呼び出し元が複数あるなら、それぞれの `Infrastructure` に**別々の値で**置くのが正しい」＝
  複製は計画側が承知のうえで選んだ形であることを確認した）
- 基盤の実物（読み取り専用）: `/home/user/microservices-platform/src/platform/backend/Services/`
  の `NotificationService`（csproj の 3 行・Tests の作り）と **`AuthorizationService`**
  （`Domain/` と `Features/` の切り分けの実例。1 本目では見ていない）

## 対象範囲

- **ConfigurationService**: 10 csproj（src 5・tests 5）→ **2 csproj**（本体 1・Tests 1）
- **`ConfigurationService.Client` の廃止**: 6 ファイルを呼び出し元 2 サービスの `Infrastructure` へ移す
  （TradeDecisionService / CostControlService。いずれも**未移送＝旧樹形**のまま触る）
- **`VersionedAssumptions` の移送**: `ConfigurationService.Domain` → `AiStockTrading.Shared.Kernel`
- `backend/backend.slnx` / `docker-compose.yml` / `scripts/k8s-local-images.sh` /
  `backend/Tests/AiStockTrading.Architecture.Tests`（Domain プロジェクト数の下限）/ `docs/` 2 件
- **対象外**: 他 9 サービスの移送（次の PR 以降）、gRPC 化（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)
  決定 9 が別 issue へ切り出すと明示）、`Microsoft.Extensions.Http.Resilience` / `HybridCache` への
  置き換え（振る舞いの変更。同決定 7）、`Infrastructure.Persistence` / `Infrastructure.Migrations`
  の名前空間（EF の型名文字列。触らない）

## 着手前の実測（母集合）

**母集合は記憶で挙げず、誤りになる側の文字列で全追跡ファイルを走査して引いた**
（`.claude/rules/traceability.repo.md`「是正・追随の母集合の取り方」規則 2・9・10）。
走査した語は `ConfigurationService\.(Api|Application|Domain|Infrastructure|Client)` /
`ConfigurationService/(src|tests)` / `VersionedAssumptions` の 8 本。

| 項目 | 実測 |
| --- | --- |
| 移送前の .cs（ConfigurationService src + tests） | 33（src 22・tests 11） |
| 移送前の csproj | 10（src 5・tests 5） |
| migration | 1 本（+ Designer + ModelSnapshot） |
| `ConfigurationService.Client` を参照する csproj | **4 本 / 2 サービス**（`TradeDecisionService.{Api,Infrastructure}` / `CostControlService.{Api,Infrastructure}`）＋ `ConfigurationService.Client.Tests` |
| `AssumptionsChangedHandler`（Wolverine）を発見範囲に入れているサービス | **CostControlService のみ**（`typeof(AssumptionsChangedHandler).Assembly`。TradeDecisionService は入れていない＝購読していない） |
| `VersionedAssumptions` を使う .cs | 17（うち呼び出し元 2 サービス側は 4） |
| `list-test-projects.js --count` | **50**（クリーンな作業ツリーで実測） |
| `deploy/helm/.../pipeline.json` の consumer 参照 | 0 件（対象外） |

### 母集合の走査で見つかった「想定外」

1. 🔴 **`ConfigurationService.Domain` に残っている型は `VersionedAssumptions` ただ 1 つである。**
   [IADR-0260](../adr/IADR-0260_shared-kernel-for-cross-service-domain-types.md) が
   `TradingAssumptions` / `TradingAssumptionsDefaults` / `CostCalculator` を共有カーネルへ移した結果である
   （`ConfigurationService.Domain.Tests/VersionedAssumptionsTests.cs` の冒頭コメントが自ら明記している）。
2. 🔴 **`.Client` の廃止は `VersionedAssumptions` の置き場を必ず動かす。** `.Client` は
   `ConfigurationService.Domain` を `ProjectReference` して `VersionedAssumptions` を得ており、
   単一プロジェクト化で `ConfigurationService.Domain.csproj` が消えると**呼び出し元から到達できなくなる**。
   これは案 A（廃止）でも案 B（据え置き）でも同じであり、**「据え置けば楽」は成立しない**。
3. 🔴 **`DomainLayerDependencyTests` が「Domain プロジェクトは実測 9 件」を下限として持つ。**
   `ConfigurationService.Domain.csproj` の消滅で 8 件になる。**これは移送方式に関わらず不可避**
   （層をプロジェクトで表さなくなるのが本移行の目的そのものであるため）。
4. `DomainSourceDependencyTests` は**ソース領域**（新旧両樹形のフォルダ）を数える下限 9 を別に持つ。
   `VersionedAssumptions` が共有カーネルへ出ると ConfigurationService の Domain 領域が空になり 8 件になる。
5. TradeDecisionService は `AssumptionsChanged` を**購読していない**（上表）。`.Client` を複製するとき、
   ハンドラまで機械的に複製すると **Wolverine のアセンブリ走査が新しい購読を発見して振る舞いが変わる**
   （[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) 決定 7 違反）。**複製の内訳は
   呼び出し元ごとに変える必要がある。**

## 設計

### 判断 1: `.Client` は本 PR で廃止する（案 A）

**根拠と代償は [IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定 1 に書いた。**
要点だけ再掲する。

- 計画側（#526 本文・[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) 決定 2 / 決定 9）が
  「呼び出し元の `Infrastructure` へ移す」「呼び出し元ごとに**別々の値で**置くのが正しい」と
  **複製を承知のうえで選んでいる**ことを本文で確認した。
- 据え置き（案 B）は上表「想定外 2」により**楽にならない**——`VersionedAssumptions` の移送は
  どちらでも要り、しかも `ConfigurationService` が本番プロジェクトを 2 本持ったまま残る。
- 呼び出し元 4 csproj の参照張り替えは案 B でも（`.Client` → `Shared.Kernel` 追加として）
  発生するため、**同じ場所を 2 回触る**ことになる。

### 判断 2: `VersionedAssumptions` は `AiStockTrading.Shared.Kernel` へ移す

[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定 2。
共有カーネルの憲章（[IADR-0260](../adr/IADR-0260_shared-kernel-for-cross-service-domain-types.md)）は
「サービス境界をまたいで消費される型の置き場」であり、同 ADR が `VersionedAssumptions` を除外した理由は
**「消費側は認可された経路（`ConfigurationService.Client`）越しに使う」**——その経路を本 PR が廃止するため、
**除外の前提そのものが消える**。テスト（`VersionedAssumptionsTests.cs`）も
`AiStockTrading.Shared.Kernel.Tests` へそのまま移す（[IADR-0260](../adr/IADR-0260_shared-kernel-for-cross-service-domain-types.md)
が同じ移送で採った作法）。

### 判断 3: ConfigurationService は `Domain/` を作らない（実態がそうなる）

判断 2 の帰結として、ConfigurationService の Domain 層は**型が 1 つも残らない**。
[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定 2 の裏返し
（「フォルダ移送そのものを理由に Domain を新設しない」）を適用し、
**フォルダ移送そのものを理由に `Application.State` の型を `Domain/` へ昇格させることもしない**。
`AssumptionsChangeEntry` は 1 本目の `AuditEntry`（`State/` → `Features/<集約>/`）と同じ扱いにする。

**ただし「Domain を持つサービス」の型は本 PR で確定する**（3 本目以降が参照する。
[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定 3）。
基盤の実物 2 例（`NotificationService` / `AuthorizationService`）を読み、境界は
**「フレームワーク・DI・I/O に触れず、業務概念そのものを表す型（エンティティ・値オブジェクト・
純粋な業務規則）が `Domain/`、ポート・アプリケーションサービス・エンドポイント・DTO・
ストアが `Features/<集約>/`」**であることを確認した。

### 判断 4: 複製の内訳は呼び出し元ごとに変える

| ファイル | TradeDecision | CostControl | 理由 |
| --- | --- | --- | --- |
| `IAssumptionsProvider` / `IAssumptionsCacheInvalidator` / `IAssumptionsSource` | ○ | ○ | どちらも解決口を使う |
| `HttpAssumptionsClient` / `CachedAssumptionsProvider` / `DefaultAssumptionsProvider` | ○ | ○ | 同上 |
| `AssumptionsClientExtensions`（DI 拡張） | ○ | ○ | どちらも `AddAiStockTradingAssumptions` を呼ぶ |
| **`AssumptionsChangedHandler`（Wolverine ハンドラ）** | **✕** | ○ | **TradeDecision は購読していない**（上表）。複製するとアセンブリ走査で購読が生まれ振る舞いが変わる |

置き場は [IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) 決定 9 が指定する
`Infrastructure/ExternalServices/`（旧樹形では `src/<Svc>.Infrastructure/ExternalServices/`）。
**ハンドラだけは `Composable/Steps/`（名前空間 `<Svc>.Infrastructure.Steps`）へ置く**
——[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定 5
（Wolverine ハンドラ集合は `Infrastructure/Steps/`）に従う。

### 写像表（ConfigurationService）

| 移送前 | 移送後 | 根拠 |
| --- | --- | --- |
| `src/*.Api/Program.cs` `appsettings*.json` | ルート直下 | 決定 1 の樹形 |
| `src/*.Api/Foundation/Endpoints/AssumptionsEndpoints.cs` | `Features/Assumptions/` | 集約は 1 つ（`Assumptions`）。3 段目は作らない |
| `src/*.Application/Services/AssumptionsService.cs` | `Features/Assumptions/` | 集約内の唯一のアプリケーションサービス |
| `src/*.Application/Ports/IAssumptions{Store,ChangeLog}.cs` | `Features/Assumptions/` | 同一集約からのみ使うポート。`_Shared/` は作らない（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定 1） |
| `src/*.Application/State/AssumptionsChangeEntry.cs` | `Features/Assumptions/` | 1 本目の `AuditEntry` と同じ扱い |
| `src/*.Application/AssumptionsConcurrencyException.cs` | `Common/Exceptions/` | 決定 1 の樹形が `Common/` に `Exceptions/` を名指ししている |
| `src/*.Application/Ports/IClock.cs` `Adapters/SystemClock.cs` | `Common/Abstractions/` | [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定 3（I/O を持たない技術プリミティブは抽象と実装を同居） |
| `src/*.Application/Adapters/InMemoryAssumptions{Store,ChangeLog}.cs` | `Infrastructure/Persistence/` | 1 本目の判断 4（`InMemoryAuditEventStore`）と同じ |
| `src/*.Infrastructure/Foundation/Persistence/**` | `Infrastructure/Persistence/` | 決定 1 の樹形。**名前空間は不変** |
| `src/*.Infrastructure/Migrations/**` | `Infrastructure/Persistence/Migrations/` | 1 本目と同じ。**名前空間は不変** |
| `src/*.Domain/VersionedAssumptions.cs` | `backend/Shared/AiStockTrading.Shared.Kernel/Trading/` | 判断 2 |
| `tests/*.{Api,Application,Domain,Infrastructure,Client}.Tests/**` | `Tests/`（フラット）ほか | [IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) 決定 4。Domain.Tests は共有カーネル側へ、Client.Tests は呼び出し元 2 サービスへ |

### 名前空間

[IADR-0261](../adr/IADR-0261_namespace-alignment-to-platform.md) で `ConfigurationService.*` へ整合済み。
**変えるのはフォルダ移動に伴う `Api.*` / `Application.*` / `Client.*` 系だけ**である。

- `ConfigurationService.Api.Endpoints` → `ConfigurationService.Features.Assumptions`
- `ConfigurationService.Application[.{Ports,Services,State}]` → `ConfigurationService.Features.Assumptions`
- `ConfigurationService.Application.Ports`（`IClock`）・`.Application.Adapters`（`SystemClock`）
  → `ConfigurationService.Common.Abstractions`
- `ConfigurationService.Application`（例外）→ `ConfigurationService.Common.Exceptions`
- `ConfigurationService.Application.Adapters`（InMemory 2 本）→ `ConfigurationService.Infrastructure.Persistence`
- `ConfigurationService.Client.{Ports,Adapters,Extensions}` → `<呼び出し元>.Infrastructure.ExternalServices`
- `ConfigurationService.Client.Steps` → `CostControlService.Infrastructure.Steps`
- `ConfigurationService.Domain` → `AiStockTrading.Shared.Kernel.Trading`
- 🔴 **`ConfigurationService.Infrastructure.Persistence` / `.Infrastructure.Migrations` は触らない**
  （EF の `modelBuilder.Entity("...")` が完全修飾名を文字列で持つ）。

### `internal` → `public` の判定（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定 4）

Tests が**直接参照する型だけ**を `public` にする（DI 経由のインターフェース越しは対象外）。
`InternalsVisibleTo` は新設せず、旧 csproj の 6 エントリはすべて削除する。
判定結果は本文末尾「実施結果」に記す。

## 手順

1. 作業仕様書（本書）を作成する。
2. `VersionedAssumptions` を共有カーネルへ移し、テストも移す。
3. `.Client` を呼び出し元 2 サービスへ複製し、`.Client` / `.Client.Tests` を削除する。
4. ConfigurationService を `git mv` で再配置し、csproj を新規作成する（旧 9 本は削除）。
5. 名前空間・`using` を移送先へ合わせる（Persistence / Migrations は据え置き）。
6. `internal` → `public` を Tests の直接参照だけに絞って適用する。
7. `backend.slnx` / `docker-compose.yml` / `scripts/k8s-local-images.sh` / Architecture.Tests の
   下限 / `docs/` を追随させる。
8. ビルド・テスト・`dotnet format`・`has-pending-model-changes`・検査器一式・カバレッジで確認する。

## 受け入れ基準

- [x] `dotnet build backend/backend.slnx` が 0 warning / 0 error（実測 `0 Error(s)` / `0 Warning(s)`）
- [x] `dotnet test backend/backend.slnx` の失敗が `AiStockTrading.IntegrationTests` の 8 件のみ（Docker 不在）
- [x] `dotnet format backend/backend.slnx --verify-no-changes` が通る（出力なし）
- [x] `dotnet ef migrations has-pending-model-changes` が
      「No changes have been made to the model since the last migration.」を返す
- [x] `list-test-projects.js --count` が **50 → 46**（5 本削除・1 本新設）
- [x] テストの合格件数が移送前後で説明できる（下表。**複製した 23 件が増えるだけで、失われた件は 0**）
- [x] `coverage-floor.json` の床（79.00%）を割らない（実測 **82.30%**・16879/20510 行・レポート 46 件）
- [x] 検査器一式（`scripts/README.md` 掲載分）が緑

### テスト件数の突合（削っていないことの証跡）

**移送前後を同一手順で実測した**（移送前は `git stash push -u` でクリーンな `develop` 相当へ戻して測定）。

| テストアセンブリ | 移送前 | 移送後 | 差 | 説明 |
| --- | ---: | ---: | ---: | --- |
| `ConfigurationService.Api.Tests` | 8 | — | | 統合 |
| `ConfigurationService.Application.Tests` | 5 | — | | 統合 |
| `ConfigurationService.Infrastructure.Tests` | 5 | — | | 統合 |
| **`ConfigurationService.Tests`** | — | **18** | | 8 + 5 + 5 = 18（**一致**） |
| `ConfigurationService.Domain.Tests` | 5 | — | | 共有カーネル側へ移送 |
| `AiStockTrading.Shared.Kernel.Tests` | 22 | **27** | +5 | 22 + 5 = 27（**一致**） |
| `ConfigurationService.Client.Tests` | 24 | — | | 呼び出し元 2 サービスへ移送 |
| `CostControlService.Infrastructure.Tests` | 47 | **71** | +24 | 47 + 24 = 71（**全 24 件。一致**） |
| `TradeDecisionService.Infrastructure.Tests` | 264 | **287** | +23 | 264 + 23 = 287。**23 = 24 − 1**（購読ハンドラのテスト 1 件は取引判断へ複製しない。決定 4） |
| 合計 | 380 | 403 | **+23** | 増分は**取引判断側への複製ぶんのみ**。**減った件は 0** |

`AiStockTrading.Architecture.Tests` は 74 → 82（+8）。`*.Client` 再出現の退行防止検査
（[#526](https://github.com/endazon/ai-stock-trading/issues/526)「退行防止」）を新設したぶんである。

### `internal` → `public` にした型（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定 4 の適用）

| 型 | 理由 |
| --- | --- |
| `ConfigurationDbContext` | `Tests/EfAssumptionsStoreTests.cs` が `new DbContextOptionsBuilder<ConfigurationDbContext>()` で、`ConfigurationWorkerWebApplicationFactory` が `GetRequiredService` 等で直接参照する |
| `AssumptionsRow` / `AssumptionsChangeRow` | `public DbSet<T>` が要求する（**CS0053 を実際に出して確認した**。片方を internal に戻すと `Inconsistent accessibility: property type 'DbSet<AssumptionsRow>' is less accessible` が出る） |
| `EfAssumptionsStore` / `EfAssumptionsChangeLog` | Tests が `new EfAssumptionsStore(db)` / `new EfAssumptionsChangeLog(db)` で直接構築する |

据え置き（`internal` のまま）: `AssumptionsSerialization` / `ConfigurationDbContextFactory` /
`SingletonKeys` / `AssumptionsEndpoints` / `UpdateAssumptionsRequest`（いずれも Tests から直接参照されない。
エンドポイントは HTTP 経由でのみ触る）。`InternalsVisibleTo` は新設せず、旧 csproj の 6 エントリはすべて削除した。

### 最終樹形（ConfigurationService）

```
backend/Services/ConfigurationService/
├── ConfigurationService.csproj          # 単一プロジェクト（Compile/Content/None Remove="Tests/**" の 3 行つき）
├── Program.cs ・ appsettings.json ・ appsettings.Development.json
├── Features/Assumptions/                # 集約は 1 つ。3 段目（操作）も _Shared/ も作らない
│   ├── AssumptionsEndpoints.cs ・ AssumptionsService.cs ・ AssumptionsChangeEntry.cs
│   └── IAssumptionsStore.cs ・ IAssumptionsChangeLog.cs
├── Common/
│   ├── Abstractions/IClock.cs ・ SystemClock.cs
│   └── Exceptions/AssumptionsConcurrencyException.cs
├── Infrastructure/Persistence/          # 🔴 名前空間は不変（EF の型名文字列）
│   ├── ConfigurationDbContext.cs ・ ConfigurationDbContextFactory.cs ・ PersistenceRows.cs
│   ├── AssumptionsSerialization.cs ・ EfAssumptionsStore.cs ・ EfAssumptionsChangeLog.cs
│   ├── InMemoryAssumptionsStore.cs ・ InMemoryAssumptionsChangeLog.cs
│   └── Migrations/ …                    # 🔴 名前空間は不変
└── Tests/ConfigurationService.Tests.csproj ＋ 6 ファイル
```

**`Domain/` は無い**（判断 3。唯一の型 `VersionedAssumptions` が共有カーネルへ出たため）。
`Hosted/` も無い（`BackgroundService` を持たない）。

## 計画書との差異

- 差異: なし。#526 の**スコープのうち gRPC 化だけ**を積み残すが、これは
  [IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) 決定 9 が
  「gRPC 化は行わない・別 issue へ切り出す」と**計画側で先に決めている**ためであり、逸脱ではない。
  切り出し先として [#584](https://github.com/endazon/ai-stock-trading/issues/584) を起票した
  （起票前に `grpc` で既存 issue を検索し 0 件を確認）。同 issue には #526 の「呼び出し元ごとの
  タイムアウト・リトライを結合テストで固定する」と `Http.Resilience` / `HybridCache` への
  置き換えも送っている。

## 残り 9 サービスへの申し送り（踏んだ落とし穴・再利用可能な手順）

1. 🔴 **`git stash` で「移送前」を測ったら、戻したあとに旧樹形の `bin/` `obj/` が残る。**
   これらは追跡外なので `git stash pop` では消えず、**新しい単一プロジェクトが旧プロジェクトの
   `obj/**/AssemblyInfo.cs` まで拾って `CS0579`（Duplicate attribute）で落ちる**（本 PR で実際に踏んだ。
   `dotnet ef` が「Build failed」としか言わないため原因が見えにくい）。**旧 `src/` `tests/` を
   ディレクトリごと消してからビルドし直すこと。** 1 本目の申し送り 1（`git stash -u` を使う）と対になる。
2. **`.Client` のようなクライアントライブラリを持つサービスでは、まず「その型がどこから来ているか」を辿る。**
   本 PR では `.Client` → `<Svc>.Domain` → `VersionedAssumptions` という鎖があり、**廃止しようがしまいが
   型の置き場が動く**ことが分かった。**「据え置けば触る範囲が減る」は、辿る前に決めると誤る。**
3. **複製を求められたら、複製の内訳を呼び出し元ごとに検討する。** Wolverine のハンドラは
   **アセンブリに置いた時点で発見される**ため、対称に複製すると購読が増える＝振る舞いが変わる（判断 4）。
   購読の有無は `UseAiStockTradingRabbitMq(..., typeof(X).Assembly)` の実引数で実測すること。
4. **`internal` → `public` は「まず 1 つ public にしてビルドし、連鎖をコンパイラに拾わせる」**
   （1 本目の申し送り 3 と同じ。本 PR でも `DbSet<T>` の CS0053 連鎖が出た）。
5. **アーキテクチャ検査の下限（Domain プロジェクト数・Domain ソース領域数）は移送のたびに動く。**
   `*.Domain.csproj` を持つサービスを移送すると `DomainLayerDependencyTests` が必ず落ちる
   （**移送方式に関わらず不可避**）。**失敗メッセージに「なぜ減ったか」を書いてから下げること**——
   下限だけ黙って下げると、次の読み手が「退行を追認した」のか「移送の正常な結果」なのか区別できない。
6. **`Common/` の下位区分は樹形の文言に従う。** 例外は `Common/Exceptions/`、I/O を持たない技術
   プリミティブは `Common/Abstractions/`（1 本目の判断 3）。
7. **移送でファイルを消したら `dotnet ef migrations has-pending-model-changes` を必ず打つ。**
   `--project` と `--startup-project` の両方に単一プロジェクトのパスを渡す（1 本目の申し送り 8）。
8. **`docs/` には IADR 番号を表示テキストで書かない**（trace ブロックへ入れる）。本 PR では
   `docs/data/trading-assumptions.md` / `docs/tech/tech-requirements.md` /
   `docs/integration/20260718_msp-frontend-integration-requirements.md` の 3 件を追随させ、
   IADR は frontmatter 直後の trace ブロックへ足した。
9. **`docker-compose.yml` の `SERVICE_PROJECT` / `SERVICE_DLL` と `scripts/k8s-local-images.sh` を
   必ず両方直す**（1 本目の申し送り 10）。**加えて、この値を引き写している文書があれば直す**——
   本 PR では `docs/integration/…` が MSP 側へ登録すべき build args としてこの 2 値を持っていた
   （母集合の規則 9: 誤りになる側の文字列 `ConfigurationService/src` で全走査して見つけた）。
