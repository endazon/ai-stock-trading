---
title: BacktestService を単一プロジェクト＋VSA 樹形へ移送する（W11 段 4-4・永続化を持たないサービスの型）
type: spec
status: approved
related_ids: [NFR, IADR-0259, IADR-0263, IADR-0264, IADR-0265]
author: endazon (with Claude Code)
created: 2026-08-29
updated: 2026-08-29
plan_refs: []
---

# 仕様書: BacktestService の単一プロジェクト＋VSA 移送（W11 段 4-4）

> **11 サービス移送波の 4 本目**である。1 本目（AuditService・[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)）・
> 2 本目（ConfigurationService・[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md)）・
> 3 本目（CostControlService・[20260829_w11s4c](20260829_w11s4c_costcontrolservice-vsa.md)）で確定した判断の型を
> そのまま適用する。**新しい判断軸は生じなかった**（末尾「IADR を作らない判断」参照）。

## 起点

- 起点 ID: **`NFR`（無採番）**。構造移送＝メタ作業であり、`.claude/rules/traceability.md`
  「起点 ID の種別」の無採番許容ケース **2** に当たる（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)
  が確定済みの判断を継承する。環流はしない）。
- 上流: [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)（1 本目の 5 決定）・
  [IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md)（2 本目。特に決定3
  「Domain を持つサービスの型」）・[IADR-0265](../adr/IADR-0265_domain-project-count-checker-dynamic-lower-bound.md)
  （検査の下限の動的化）・[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)（樹形・写像方針表）

## 着手前に読んだもの

- `CLAUDE.md` / `.claude/rules/traceability.md` / `.claude/rules/traceability.repo.md` /
  `docs/DEFINITION_OF_DONE.md`
- [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) /
  [IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) /
  [IADR-0265](../adr/IADR-0265_domain-project-count-checker-dynamic-lower-bound.md) /
  [IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) /
  [IADR-0258](../adr/IADR-0258_structure-aware-checkers-dual-layout.md)
- [20260829_w11s4a](20260829_w11s4a_auditservice-vsa.md) / [20260829_w11s4b](20260829_w11s4b_configurationservice-vsa.md) /
  [20260829_w11s4c](20260829_w11s4c_costcontrolservice-vsa.md)（手順・落とし穴の申し送り。3 本目に累積分がある）
- 基盤の実物（読み取り専用）: `/home/user/microservices-platform/src/platform/backend/Services/NotificationService/`
  （2〜3 本目が既に読んで Domain/Features の基準を確定済みのため、本 PR は同じ基準を適用するのみで再読はしていない）

## 対象範囲

- 対象: `backend/Services/BacktestService/`（8 csproj → 2 csproj）、`backend/backend.slnx`、
  `docker-compose.yml`、`scripts/k8s-local-images.sh`
- 対象外: 他 7 サービス（次の PR 以降）、`backend/Shared/` `backend/TestSupport/`（据え置き集合）

## 着手前の母集合の引き直し（`.claude/rules/traceability.repo.md` 規則1〜10）

**母集合は記憶で挙げず、誤りになる側の文字列で全追跡ファイルを走査して引いた**（規則1・2・9・10）。
走査した語は `BacktestService\.(Api|Application|Domain|Infrastructure)` / `BacktestService/(src|tests)` の 2 本。

| 項目 | 実測 |
| --- | --- |
| 移送前の .cs（src + tests） | 60（src 20・tests 40） |
| 移送前の csproj | 8（src 4・tests 4） |
| migration | **0 件**（`Migrations/` を持たない。`DbContext` も 0 件。後述「DbContext を持たないことの確認」） |
| `list-test-projects.js --count` | **43**（クリーンな作業ツリーで実測。タスク文の前提と一致） |
| `BacktestService` を参照する他サービスの `ProjectReference`（`backend/Services` 配下） | 0 件 |
| `BacktestService` を参照する `backend/Tests` 配下の `ProjectReference` | **0 件**
  （`AiStockTrading.IntegrationTests.csproj` に `Backtest` を含む行は無い。3 本目が踏んだ `CostControlWorker`
  extern alias のような罠は本サービスには無いことを実測で確認済み） |
| `deploy/helm/.../pipeline.json` の BacktestService 関連 consumer 参照 | 0 件（対象外） |
| `docs/` 配下の BacktestService パス参照（`BacktestService\.(Api\|Application\|Domain\|Infrastructure)`） | 5 件
  （`docs/functional/FR-15_backtest.md` 1 件・`docs/tests/FR-15_backtest-tests.md` 4 件。いずれも**旧プロジェクト名を
  散文で言及するのみ**で、対象範囲（前掲）に入っていないため未更新。2 本目・3 本目も同様に `docs/` の
  プロジェクト名言及は移送 PR の対象外としており、本 PR もその扱いを継承した） |

### 母集合の走査で見つかった「想定外」

1. 🔴 **本サービスは `DbContext` を 1 つも持たない。** `grep -rl "DbContext" backend/Services/BacktestService/`
   は 0 件。`Migrations/` ディレクトリも無い。`BacktestService.Api.csproj` のコメント自身が
   「DB もメッセージバスも持たない（永続化は無く、verdict の実 publish は #82）」と明記しており、
   実測と設計意図が一致する。**したがって `dotnet ef migrations has-pending-model-changes` は実行不能
   （対象外）である**（後述「合否判定」）。
2. **`BacktestService.Domain.Tests.csproj` が他サービス（`RiskManagementService.Domain`）を
   `ProjectReference` していた。** 理由はコメントに明記: 「Stage 0 の許容 DD が運用の DD 停止ライン
   （`TradingDefaults`）と同値であることを固定するためのテスト専用参照」。**移送でフォルダの深さが
   1 段浅くなる**（`tests/BacktestService.Domain.Tests/` → `Tests/`）ため、相対パスを
   `..\..\..\RiskManagementService\...`（3 段上）から `..\..\RiskManagementService\...`（2 段上）へ
   張り替える必要があった。1〜3 本目にはこの参照が無く、本 PR で初めて出た踏み分けである。
3. **Wolverine ハンドラ・`BackgroundService` のいずれも持たない。** メッセージングを持たない
   （`grep -rln "Wolverine|IMessageBus|IConsumer"` が 0 件）ため `Infrastructure/Steps/` は作らない。
   `BackgroundService` も無いため `Hosted/` も作らない。
4. **`internal` 型はクラス 1 つ・メンバー 6 つのみ**（`MMApiMoomooHistoryKLineClient` 本体と、
   その `internal const`/`internal static` メンバー 6 つ、および `MoomooBarDataPreflight`）。
   1〜3 本目は主要な型の大半が `internal` だったが、本サービスは**移送前から大半の型が
   `public`** であった（旧 3 プロジェクト分割時点で既に `internal` を絞り込んでいたと見られる）。

## 設計

### 判断1: `Domain/` と `Features/Backtest/` の切り分け（IADR-0264 決定3 の適用。新しい判断ではない）

[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定3 の基準
（**Domain ＝フレームワーク・DI・I/O に触れず業務概念そのものを表す型。ポート・アプリケーションサービス・
エンドポイント・DTO・ストアは Features/<集約>/**）と、同決定の 🔴 注記（**移送で型の層を変えない。
現に Application にある型を Domain へ上げる／Domain にある型を Features へ下ろすのは設計判断であり、
フォルダ移送の射程外**）を、そのまま適用した。

| 元のプロジェクト | 型 | 置き場 |
| --- | --- | --- |
| `BacktestService.Domain`（11 ファイル） | `PriceBar` / `BacktestContext` / `IBacktestStrategy` / `BacktestConfig` /
  `BacktestRun` / `BacktestSimulator` / `BacktestCostModel` / `BacktestMetrics` / `DataCutoffPolicy` /
  `DeflatedSharpeRatio` / `KillSwitch` / `NormalDistribution` / `ProbabilityOfBacktestOverfitting` /
  `SampleMoments(Calculator)` / `SecurityUniverse` / `UniverseMembership` / `Stage0Gate*` / `Stage0Promotion` /
  `SymbolAnonymizer` / `TrialLedger` / `WalkForwardSplitter` 等 | **`Domain/`**（そのまま） |
| `BacktestService.Application`（`Stage0GateService.cs` / `BacktestRunner.cs`） | `Stage0GateService`
  （DSR/PBO/カットオフを合成するオーケストレータ。ポート・I/O は持たないが、**旧 Application 層に
  あった以上 Features へ**——判断1の 🔴 注記どおり層は変えない）・`BacktestRunner`（`IBarDataSource`
  というポートに依存する編成役） | **`Features/Backtest/`** |
| `BacktestService.Application/Ports/`（`IBarDataSource.cs` / `IHistoricalBarSource.cs`） | ポート
  （インターフェース）と付随 DTO（`HistoricalBarLoad` / `HistoricalBarGap`） | **`Features/Backtest/`**
  （決定3「ポートは Features」） |
| `BacktestService.Application/BacktestEvaluatedFactory.cs` | `Stage0Decision` を契約イベントへ写す
  純関数のファクトリ（1 本目の `AuditEntryFactory` と同型: 集約全体から使われ、リフレクション等の
  完全性検査は無いが、性質としてはアプリケーション層の写像関数） | **`Features/Backtest/`** |

集約は 1 つ（`Backtest`。エンドポイントを持たないサービスだが、`Features/Backtest/` として
まとめた）。[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定1
（`_Shared/` は操作フォルダの兄弟が実在する場合だけ作る）により、`_Shared/` は作らず
`Features/Backtest/` 直下へ平らに置いた。

### 判断2: `IBarDataSource` の 2 実装（`InMemoryBarDataSource` / `MaterializedBarDataSource`）は
`Infrastructure/ExternalServices/` へ（IADR-0259 既定「Adapters/ → Infrastructure/」の適用。新しい判断ではない）

両クラスは移送前から `BacktestService.Application/Adapters/` フォルダに置かれており、
[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) の写像方針表の既定
（「`Adapters/` → `Infrastructure/` の該当区分」）がそのまま当たる。永続化ポートではないため
`Persistence/` ではなく、**同じ `IBarDataSource` を実装する 2 クラスを分断しない**という
3 本目の申し送り（「同一ポートの複数実装は、置き場の一貫性を優先してよい」）を踏まえ、
`MaterializedBarDataSource` が実データ取得パイプライン（`IHistoricalBarSource` の実装群。
下記）と密接であることも合わせて `Infrastructure/ExternalServices/` に揃えた。

### 判断3: `Infrastructure.Composable.Adapters/` 配下 9 ファイルは `Infrastructure/ExternalServices/` へ

`BarDataOptions.cs`（構成 DTO・`MoomooBarDataPreflight` 起動時検査）・`HistoricalBarSourceFactory.cs`
（provider 選択の合成）・`IMoomooHistoryKLineClient.cs`（moomoo クライアントの抽象。Infrastructure 内部の
実装詳細インターフェースであり `Application/Ports/` には元々置かれていないため Features へは動かさない）・
`MMApiMoomooHistoryKLineClient.cs`（moomoo 実結合）・`MoomooHistoricalBarSource.cs` /
`NoOpHistoricalBarSource.cs` / `StooqDailyCsvParser.cs` / `StooqHistoricalBarSource.cs` /
`StooqSymbolMapper.cs`（いずれも `IHistoricalBarSource` の実装または補助）は、すべて外部 I/O
（HTTP・OpenD 接続）を持つか、その合成・構成に閉じた技術詳細であり、[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)
既定どおり `Infrastructure/ExternalServices/`（旧 `Composable/Adapters/` の `Composable` を廃止し吸収。
[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) 決定1「`Foundation/` / `Composable/` の区分は廃止」）
へ平らに置いた。

### 判断4: `Common/Abstractions/` は作らない（1〜2 本目にあった `IClock` 相当が本サービスには無い）

Program.cs は `TimeProvider.System` を直接使っており（`HistoricalBarSourceFactory.Create` の引数）、
サービス固有の技術プリミティブ抽象（`IClock` 等）を持たない。1 本目・2 本目の判断3・決定3
（技術プリミティブは `Common/Abstractions/`）を適用する対象が存在しないため、**空の枠を先回りで
作らない**（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定1・2 と
同じ立場）。

### 判断5: `Infrastructure/Persistence/` `Infrastructure/Steps/` `Hosted/` はいずれも作らない

- **`Infrastructure/Persistence/` を作らない理由**: `DbContext` を 1 つも持たず、永続化ポート
  （`ICostLedger` 相当）も存在しない（前掲「母集合の走査で見つかった想定外」1）。
- **`Infrastructure/Steps/` を作らない理由**: Wolverine ハンドラ・メッセージング購読のいずれも
  存在しない（実測 0 件。前掲「想定外」3）。
- **`Hosted/` を作らない理由**: `BackgroundService` を 1 つも持たない（実測 0 件）。

[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) の樹形図は典型例であり、
「HTTP 面を 1 本も持たないサービスでは `Endpoint.cs` が存在しない」と同じ理屈で、
**実態に無い区分は作らない**（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)
決定1・2 の一般原則の帰結であり、本 PR 固有の新しい判断ではない）。

### 判断6: `internal` → `public` は「Tests が直接参照する型・メンバー」に限る
（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定4 の適用。
member 粒度への自然な拡張であり新しい設計判断ではない）

移送前から `internal` だったのは以下のみ（他はすべて元から `public`）。いずれも
`BacktestService.Infrastructure.Tests`（移送後は統合 Tests）が直接参照していたため `public` にした。
`InternalsVisibleTo` は新設せず、旧 2 csproj（Api・Infrastructure）にあった計 3 エントリはすべて削除した。

| 型・メンバー | 理由 |
| --- | --- |
| `MMApiMoomooHistoryKLineClient`（クラス本体） | `Tests/MMApiMoomooHistoryKLineClientMappingTests.cs` が
  `MMApiMoomooHistoryKLineClient.MapRehabType(...)` 等を静的に直接呼ぶ |
| `MMApiMoomooHistoryKLineClient.DayKLType` / `.UsSecurityMarket` / `.OhlcvFields`（`internal const`） | 同上のテストが直接参照する |
| `MMApiMoomooHistoryKLineClient.MapRehabType(...)` / `.TryParseKLineDate(...)` / `.FormatDate(...)`
  （`internal static` メソッド） | 同上のテストが直接呼び出す |
| `MoomooBarDataPreflight`（クラス本体。`BarDataOptions.cs` 内で定義） | `Tests/MMApiMoomooHistoryKLineClientMappingTests.cs`
  の `MoomooBarDataPreflightTests` が `MoomooBarDataPreflight.Validate(...)` を直接呼ぶ
  （メソッド自体は元から `public`。クラスの可視性のみ広げた） |

CS0053 の連鎖は発生しなかった（`MMSPI_Qot` / `MMSPI_Conn` / `IMoomooHistoryKLineClient` はいずれも
`moomoo-api` パッケージ・自プロジェクトの `public` 型であり、クラスを `public` にしても
アクセス修飾子の不一致は生じない）。

### 判断7: 名前空間の書き換え

[IADR-0261](../adr/IADR-0261_namespace-alignment-to-platform.md) で `BacktestService.*` へ先行整合済み。
変えたのはフォルダ移動に伴う以下のみ。

- `BacktestService.Application`（`Stage0GateService` / `BacktestRunner` / `BacktestEvaluatedFactory` /
  `IBarDataSource` / `IHistoricalBarSource`） → `BacktestService.Features.Backtest`
- `BacktestService.Application`（`InMemoryBarDataSource` / `MaterializedBarDataSource`。**元から
  `.Adapters` サブ名前空間ではなくフラットな `BacktestService.Application` だった**）・
  `BacktestService.Infrastructure.Adapters`（9 ファイル） → `BacktestService.Infrastructure.ExternalServices`
- `BacktestService.Domain` は不変。

## Tests 統合（4 → 1）で変えていないことの証跡

**中身は 1 行も変えていない**（`git mv` のみ・変更は namespace 宣言・using の書き換えに限定）。

### テスト件数の突合（移送前後を実測。削っていないことの証跡）

移送前は各旧テストプロジェクトを個別に `dotnet test` して実測した（本 PR 着手直後・クリーンな
作業ツリーで測定。旧プロジェクトがまだ存在する段階で先に測定したため `git stash` は使っていない
——1 本目の申し送り6 を踏襲）。

| テストアセンブリ | 移送前 | 移送後 |
| --- | ---: | ---: |
| `BacktestService.Api.Tests` | 11 | — |
| `BacktestService.Application.Tests` | 16 | — |
| `BacktestService.Domain.Tests` | 125 | — |
| `BacktestService.Infrastructure.Tests` | 87 | — |
| **`BacktestService.Tests`** | — | **239** |
| 合計 | **239** | **239** |

11 + 16 + 125 + 87 = 239 = 移送後の合格件数と**完全一致**。減った件・増えた件は 0。

## `list-test-projects.js --count` の突合

- 移送前: **43**
- 移送後: **40**
- 差分: **-3**（旧 4 テストプロジェクト → 新 1 テストプロジェクトの差分と一致。タスク文の
  「43 → 40」と一致）

## 名前空間の実装解決に関する事故と教訓（🔴 残り 7 サービスへの最重要申し送り）

**namespace のフラット化（`<Svc>.Domain.Tests` → `<Svc>.Tests`）に伴い、C# の「直接の親名前空間からの
暗黙解決」が失われる。** 旧 `BacktestService.Domain.Tests` は `BacktestService.Domain` の**直接の子**
だったため、`using BacktestService.Domain;` が無くても `KillSwitch` 等の Domain 型を無条件に
参照できていた。移送後の `BacktestService.Tests` は `BacktestService` の子であって
`BacktestService.Domain` の子ではないため、**Domain 型・Features 型・Infrastructure 型を使う
すべてのテストファイルに明示的な `using` が要る**。

🔴 **落とし穴（本 PR で実際に踏んだ）**: 最初の `dotnet build` はエラーを一部しか報告せず
（5 ファイル・18 件のみ）、**ビルドサーバー（VBCSCompiler）を `dotnet build-server shutdown` で
明示的に落とし、`bin`/`obj` を全消去してからでないと、残り 9 ファイル・90 件の欠落 `using` が
可視化されなかった**（同じコマンドを再実行しても増分ビルドのキャッシュに隠れて偽陰性が出続けた）。
**このクラスの移送では、`using` 追加の要否を「最初の 1 回のビルド結果」だけで判断しないこと。**
`dotnet build-server shutdown` の後、対象サービスの `bin/obj` を消してから改めてフルビルドし、
それでも 0 エラーであることを確認してから「using 修正完了」と判断すること。

- 対応した箇所（すべて `using BacktestService.Domain;` の追加。対象 21 ファイル）:
  `BacktestCostModelTests` / `BacktestSimulatorTests` / `DataCutoffPolicyTests` / `SecurityUniverseTests` /
  `Stage0GateEvaluatorTests` / `BacktestMetricsTests` / `DeflatedSharpeRatioTests` / `KillSwitchTests` /
  `NormalDistributionTests` / `ProbabilityOfBacktestOverfittingTests` / `SampleMomentsTests` /
  `Stage0GateCriteriaTests` / `Stage0PromotionTests` / `SymbolAnonymizerTests` / `TrialLedgerTests` /
  `WalkForwardSplitterTests` / `Tests/Calibration/Stage0NoiseCalibration.cs`。
- 加えて `using BacktestService.Infrastructure.ExternalServices;` の追加（3 ファイル。
  `MaterializedBarDataSource` が Infrastructure 側へ移ったため）:
  `BacktestRunnerTests` / `MaterializedBarDataSourceTests` / `Stage0GateServiceTests`。
- 最終状態はクリーンな `dotnet build-server shutdown` → `bin`/`obj` 全消去 → `dotnet build
  backend/backend.slnx` で **0 Warning / 0 Error** を確認した。

## `has-pending-model-changes`（対象外の根拠）

```
$ dotnet ef migrations has-pending-model-changes --project backend/Services/BacktestService --startup-project backend/Services/BacktestService
Build started...
Build succeeded.
Your startup project 'BacktestService' doesn't reference Microsoft.EntityFrameworkCore.Design.
This package is required for the Entity Framework Core Tools to work. ...
```

**BacktestService は `DbContext` を 1 つも持たないため対象外である。** 根拠:

- `grep -rl "DbContext" backend/Services/BacktestService/`（ビルド成果物除く）→ 0 件
- `Migrations/` ディレクトリが元々存在しない（移送前後とも）
- `BacktestService.Api.csproj`（移送前）のコメントが「DB もメッセージバスも持たない（永続化は無く、
  verdict の実 publish は #82）」と明記
- `Microsoft.EntityFrameworkCore.Design` パッケージへの参照が無い（`dotnet ef` コマンド自体が
  「参照が無い」というエラーで実行不能であることを上記出力で実測した）

## `DomainLayerDependencyTests` の下限（[IADR-0265](../adr/IADR-0265_domain-project-count-checker-dynamic-lower-bound.md)。
手で触っていない）

`RepositoryLayout.cs` / `DomainLayerDependencyTests.cs` は**本 PR で 1 行も変更していない**
（`git status` で確認可能。無変更）。`UnmigratedServicesWithDomainProjectCount` は
`backend/Services/<Svc>/src/` の実在と `.Domain` 接尾辞ディレクトリの実在を実ツリーから動的に数える
ため、BacktestService の移送（`src/BacktestService.Domain/` の消滅）により**自動的に** 1 件減る
（3 本目終了時点 7 → 本 PR 後 6）。`dotnet test` で `AiStockTrading.Architecture.Tests` の
`Domain_プロジェクトの探索が空振りしていない` を含む全 88 件が緑であることを実測済み。
**手で下限を書き換える操作は行っていない。**

## IADR を作らない判断

**本 PR では新しい IADR（`IADR-0266`）を作らない。** [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)・
[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定3・
[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) の写像方針表を参照するだけで、
本 PR の判断1〜7 すべてが機械的に導けたためである。

- 判断1（Domain/Features の切り分け）は IADR-0264 決定3 の**そのままの適用**（Domain を持つサービスの
  2 例目）。
- 判断2・3（Adapters → Infrastructure）は IADR-0259 決定1 の写像方針表の**既定そのもの**。
- 判断4・5（無い区分は作らない）は IADR-0263 決定1・2 の**一般原則の帰結**であり、
  「Common/Abstractions・Persistence・Steps・Hosted のいずれも無いサービス」という**新しい組み合わせ**
  ではあるが、判定基準そのものは新設していない（「実態が無ければ作らない」という単一の原則を
  4 区分に機械的に適用しただけ）。
- 判断6（`internal` メンバーの public 化）は IADR-0263 決定4（型の可視性）を**メンバー粒度へ
  自然に拡張**したものである。決定4 の目的（「Tests が要る分だけ公開する」）は型・メンバーを
  区別しておらず、新しい設計判断を要さなかった。
- 母集合の走査で見つけた「想定外」（`DbContext` 0 件・`RiskManagementService.Domain` 参照・
  namespace フラット化に伴う `using` の欠落）はいずれも**移送手順上の実務**であり、
  樹形・可視性・依存規律に関する**設計判断ではない**（3 本目までの「型」を変える性質のものではない）。

**強いて新規性を挙げるなら**、判断6 の member 粒度拡張が最も新しいが、これは IADR を要するほどの
分岐点ではなく、次のサービスへの申し送り（下記）に書けば十分と判断した。

## 受け入れ基準

- [x] `dotnet build backend/backend.slnx` が 0 warning / 0 error で通る
- [x] `dotnet test backend/backend.slnx` の失敗が `AiStockTrading.IntegrationTests` の 8 件のみ
      （Docker 不在の環境制約）
- [x] `dotnet format backend/backend.slnx --verify-no-changes` が通る（exit 0）
- [x] `dotnet ef migrations has-pending-model-changes` は **DbContext 非保持のため実行不能
      （対象外）であることを実測で確認した**
- [x] `list-test-projects.js --count` が 43 → 40
- [x] `coverage-floor.json` の床（79.00%）を割らない（実測 82.28%。Release ビルド・
      bin/obj/TestResults 清掃後・40 レポート）
- [x] 検査器一式（`scripts/README.md` 掲載分）が緑
- [x] `DomainLayerDependencyTests` の下限が自動追随し（7 → 6）、`RepositoryLayout.cs` /
      `DomainLayerDependencyTests.cs` を手で編集していないことを確認した

## 計画書との差異

- 差異: なし。本件は構造移送のみで振る舞いを変えていない（IADR-0259 決定7）。

## 残り 7 サービスへの申し送り（本 PR で踏んだ落とし穴・再利用可能な手順）

1. 🔴 **namespace のフラット化に伴う `using` 欠落は、1 回目のビルド結果を鵜呑みにしない。**
   `dotnet build-server shutdown` → 対象サービスの `bin`/`obj` を全消去 → `dotnet build` を
   フルで回してから「エラー 0 件」を確認すること（前掲「名前空間の実装解決に関する事故と教訓」）。
   旧 `<Svc>.Domain.Tests` のような「直接の親から子への暗黙解決」に頼っていたファイルほど
   検出が遅れる。
2. **`DbContext` を持たないサービスがあり得る。** 持たない場合は `dotnet ef migrations
   has-pending-model-changes` を実際に実行し、「参照が無い」というエラーを実測で示した上で
   「対象外」と報告すること（黙って省略しない）。
3. **横断テストプロジェクトからの他サービス `ProjectReference`（`RiskManagementService.Domain` 等）は
   `backend/Services` 配下だけでなく `backend/Tests` 配下・サービス自身の Tests 双方を走査すること。**
   本 PR は自サービスの `*.Domain.Tests.csproj` が他サービスの Domain を参照する例だった
   （3 本目の `AiStockTrading.IntegrationTests` extern alias とは別種の想定外）。フォルダの深さが
   浅くなる分、相対パスの上り段数（`..\..\..\` → `..\..\`）を数え直すこと。
4. **永続化・メッセージング・`BackgroundService` のいずれも持たないサービスでは、
   `Infrastructure/Persistence` `Infrastructure/Steps` `Hosted` `Common/Abstractions` を
   すべて作らなくてよい。** 樹形図は典型例であり、実態に無い区分を先回りで作らない
   （1 本目の判断1・2 と同じ立場）。
5. **`internal` の可視性拡張は型だけでなくメンバー（`internal const` / `internal static` メソッド）にも
   及び得る。** Tests が直接参照するメンバーを `grep` で洗い出す際は、型宣言だけでなく
   `internal const` / `internal static` の行も対象にすること。
6. **移送前のテスト件数は、旧プロジェクトが消える前に個別 `dotnet test` で実測しておく**
   （1 本目の申し送り6 を継続して踏襲。本 PR でも有効だった）。
