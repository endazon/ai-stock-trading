---
title: MarketMonitorService を単一プロジェクト＋VSA 樹形へ移送する（W11 段 4-5・全区分を持つ場合の型）
type: spec
status: approved
related_ids: [NFR, IADR-0259, IADR-0263, IADR-0264, IADR-0265]
author: endazon (with Claude Code)
created: 2026-08-29
updated: 2026-08-29
plan_refs: []
---

# 仕様書: MarketMonitorService の単一プロジェクト＋VSA 移送（W11 段 4-5）

> **11 サービス移送波の 5 本目**である。1 本目（AuditService・[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)）・
> 2 本目（ConfigurationService・[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md)）・
> 3 本目（CostControlService・[20260829_w11s4c](20260829_w11s4c_costcontrolservice-vsa.md)）・
> 4 本目（BacktestService・作業ツリー `/home/user/wt/w11s4d`。**develop へ未マージのため本ブランチの
> base には含まれない**が、判断の型の申し送りは同ワークツリーの
> `.ai-context/specs/20260829_w11s4d_backtestservice-vsa.md` を読み取り専用で読んで継承した）で
> 確定した判断の型をそのまま適用する。**新しい判断軸は生じなかった**（末尾「IADR を作らない判断」参照）。
> MarketMonitorService は Domain・Features（ポート複数）・Infrastructure の全区分（Persistence /
> ExternalServices / Steps）・Hosted・Common/Abstractions のすべてを持つ、**移送波で最も要素の揃った型**である。

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
- [IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) /
  [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) /
  [IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) /
  [IADR-0265](../adr/IADR-0265_domain-project-count-checker-dynamic-lower-bound.md) /
  [IADR-0258](../adr/IADR-0258_structure-aware-checkers-dual-layout.md)
- [20260829_w11s4a](20260829_w11s4a_auditservice-vsa.md) / [20260829_w11s4b](20260829_w11s4b_configurationservice-vsa.md) /
  [20260829_w11s4c](20260829_w11s4c_costcontrolservice-vsa.md)（手順・落とし穴の申し送り。3 本目に累積分がある）
- **隣接作業ツリー `/home/user/wt/w11s4d`（読み取り専用・4 本目 BacktestService。develop へ未マージ）**の
  `.ai-context/specs/20260829_w11s4d_backtestservice-vsa.md` — **🔴 名前空間フラット化に伴う `using` 欠落は
  1 回目のビルド結果を鵜呑みにしない**という最重要申し送りはここで確認した
- 基盤の実物（読み取り専用）: `/home/user/microservices-platform/src/platform/backend/Services/NotificationService/`
  （2〜4 本目が既に読んで Domain/Features の基準を確定済みのため、本 PR は同じ基準を適用するのみで再読はしていない）

## 対象範囲

- 対象: `backend/Services/MarketMonitorService/`（8 csproj → 2 csproj）、`backend/backend.slnx`、
  `docker-compose.yml`、`scripts/k8s-local-images.sh`
- 対象外: 他サービス（次の PR 以降）、`backend/Shared/` `backend/TestSupport/`（据え置き集合）

## 着手前の母集合の引き直し（`.claude/rules/traceability.repo.md` 規則1〜10）

**母集合は記憶で挙げず、誤りになる側の文字列で全追跡ファイルを走査して引いた**（規則1・2・9・10）。
走査した語は `MarketMonitorService\.(Api|Application|Domain|Infrastructure)` /
`MarketMonitorService/(src|tests)` の 2 本。

| 項目 | 実測 |
| --- | --- |
| 移送前の .cs（src + tests） | 67（src 46・tests 21） |
| 移送前の csproj | 8（src 4・tests 4） |
| migration | **2 本**（`20260710015442_InitialCreate` / `20260718110304_AddMonitorSettingsChangeLog`）。`DbContext` 1 個（`MarketMonitorDbContext`） |
| `list-test-projects.js --count` | **43**（クリーンな作業ツリーで実測。タスク文の前提と一致） |
| `MarketMonitorService` を参照する他サービスの `ProjectReference`（`backend/Services` 配下） | 0 件 |
| `MarketMonitorService` を参照する `backend/Tests` 配下の `ProjectReference` / `extern alias` | **0 件**（`AiStockTrading.IntegrationTests.csproj` の `extern alias` は `RiskManagementWorker` / `ReportWorker` / `CostControlWorker` の 3 つのみ。MarketMonitor は該当しないことを実測で確認済み） |
| `deploy/helm/.../pipeline.json` の MarketMonitorService 関連参照 | 0 件（対象外） |
| `docker-compose.yml` / `scripts/k8s-local-images.sh` の build args | **各 1 箇所**（`SERVICE_PROJECT` / `SERVICE_DLL`。両方とも本 PR で追随した） |
| `docs/` 配下の MarketMonitorService パス参照（`MarketMonitorService\.(Api\|Application\|Domain\|Infrastructure)` / `MarketMonitorService/(src\|tests)`） | **0 件** |
| `.ai-context/adr/` 配下の同パターン参照（可視リンク・散文とも） | **1 件**（[IADR-0090](../adr/IADR-0090_frontend-watchlist-ui.md) の Markdown リンク 1 本。`check-doc-links.js` が破損リンクとして検出。**リンク先パスのみを新樹形へ更新した**（本文プロズ・決定内容は不変。リンクの張り替えは「凍結記録の書き換え」に当たらない）） |
| `.ai-context/specs/` 配下の同パターン参照 | 実測 **12 件**（6 ファイル）。**いずれも point-in-time の記録**（`.claude/rules/traceability.repo.md` 除外規定）であり未更新。内訳: `20260803_354_wolverine-migration.md`（3 件・当時のテストクラス名の記述）・`20260710_market-monitor-core.md`（2 件・新規プロジェクト名の当時の宣言）・`20260828_w9f1_architecture-tests-dual-inspection.md`（2 件・当時の実測パス）・`20260828_w11s3_namespace-alignment.md`（1 件・アセンブリ名の実測列挙）・`20260806_340_screens-reimplementation.md`（4 件・当時の変更ファイル一覧） |
| `backend/Services/TradeDecisionService/` の `MarketMonitorService.Domain` 言及（コメント 2 件） | **Domain 名前空間は移送で変えていないため現行のまま正しい**（`MonitoredSymbol（MarketMonitorService.Domain）と WatchedSymbol は同形` という注記。据え置き） |

### 母集合の走査で見つかった「想定外」

1. **appsettings.json / appsettings.Development.json の移送先を見落としかけた。** 旧 `src/MarketMonitorService.Api/`
   直下にあり、`Program.cs` と同様にルート直下へ移す必要があった（先行 4 本の申し送りに明記が無かった
   ため `git ls-files src` で確認して発見）。AuditService 等の実物（`backend/Services/AuditService/appsettings*.json`）
   と突き合わせて配置を確定した。
2. **旧 `tests/MarketMonitorService.Application.Tests/TestDoubles.cs` と
   `tests/MarketMonitorService.Infrastructure.Tests/TestDoubles.cs` が `FakeClock` / `FakeMarketDataSource` を
   重複定義していた。** 旧構成では別アセンブリだったため衝突しなかったが、Tests 統合（決定4）で同一
   アセンブリ・同一名前空間になり **CS0101（型の重複定義）** を起こす。差分を確認したところ
   `FakeClock` は完全に同一、`FakeMarketDataSource` はスタイル差のみ（式形式 vs ブロック形式・変数名）で
   意味的に同一だった。**1 ファイルへ統合し（`Tests/TestDoubles.cs`）、Infrastructure.Tests 側だけが持つ
   `FakeSchedule` も合流させた。** 内容は等価であり挙動は変えていない（詳細はファイル冒頭コメント）。
3. **`MonitorSettingsEndpointsTests.cs` が完全修飾名の部分参照 `Application.State.MonitorSettingsChangeType`
   を 3 箇所で使っていた。** 旧構成では自ファイルの名前空間 `MarketMonitorService.Api.Tests` の**親**
   `MarketMonitorService` の子として `Application` 名前空間が暗黙解決されていたため、`using` 無しで
   通っていた。Tests のフラット化（`MarketMonitorService.Tests`）で `Application` 名前空間自体が消滅した
   ため `CS0246` になる。`using MarketMonitorService.Features.MarketMonitor;` を追加し、部分参照を
   `MonitorSettingsChangeType` へ短縮した（4 本目の申し送り「namespace のフラット化で暗黙解決が失われる」の
   **using 欠落だけでなく完全修飾名の部分参照でも起きる**という新しい現れ方。詳細は後述「6 本目以降への
   申し送り」）。
4. **`internal` → `public` 化の対象が先行 3 本より多い（9 型）。** `MarketMonitorDbContext` を public化した
   ことで `DbSet<T>` の 4 プロパティが公開面となり、`T`（`MonitorSettingsRow` / `PriceBaselineRow` /
   `CooldownRow` / `MonitorSettingsChangeRow`）も CS0053 連鎖で public 化が必要だった（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)
   決定4が明示的に想定していた連鎖のケース）。

## 設計

### 判断1: 集約は 1 つ（`MarketMonitor`）とし、`_Shared/` は作らない
（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定1 の適用。新しい判断ではない）

監視設定・監視銘柄（watchlist）の CRUD と、監視 1 巡回のオーケストレーション（損切り・変動検知）は
互いに `MarketMonitorSettings` / `MonitoredSymbol` を共有する不可分な概念であり、操作フォルダの兄弟を
作る決定（3 段目のスライス分割）は採らない（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)
決定1）。したがって集約は `MarketMonitor` 1 つとし、`Features/MarketMonitor/` 直下に平らに置いた。
集約名はサービス名から `Service` 接尾辞を落とした形（`CostControlService` → `CostControl`、
`BacktestService` → `Backtest` と同じ規則）。

### 判断2: `Domain/` と `Features/MarketMonitor/` の切り分け（IADR-0264 決定3 の適用。新しい判断ではない）

[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定3 の基準
（**Domain ＝フレームワーク・DI・I/O に触れず業務概念そのものを表す型。ポート・アプリケーションサービス・
エンドポイント・DTO・ストアは Features/<集約>/**）と、同決定の 🔴 注記（**移送で型の層を変えない**）を
そのまま適用した。

| 元のプロジェクト | 型 | 置き場 |
| --- | --- | --- |
| `MarketMonitorService.Domain`（7 ファイル） | `HeldPosition` / `MarketMonitorSettings` / `MonitorDefaults` / `MonitorSettingsBounds` / `MonitoredSymbol` / `PriceMovementEvaluator` / `StopLossEvaluator`（いずれもエンティティ・値オブジェクト・純粋な評価器） | **`Domain/`**（そのまま） |
| `MarketMonitorService.Application/Ports/`（`IClock` 以外の 6 インターフェース） | `ICooldownStore` / `IMarketSchedule` / `IMonitorSettingsChangeLog` / `IMonitoredSymbolStore` / `IPositionStore` / `IPriceBaselineStore` | **`Features/MarketMonitor/`**（決定3「ポートは Features」） |
| `MarketMonitorService.Application/Services/` | `MarketMonitorAppService`（巡回オーケストレーション）・`MonitorRoundResult`・`MonitorSettingsService`・`MonitorWatchlistService` | **`Features/MarketMonitor/`** |
| `MarketMonitorService.Application/State/` | `MonitorSettingsChangeEntry`（変更履歴の記録単位。1 本目の `AuditEntry` と同型: 移送で層を変えず Features へ） | **`Features/MarketMonitor/`** |
| `MarketMonitorService.Api/Foundation/Endpoints/` | `MonitorSettingsEndpoints`（HTTP エンドポイント） | **`Features/MarketMonitor/`**（Api 層の吸収先は Features。[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) 決定1 のとおり `.Api` 接尾辞は廃止） |

### 判断3: `IClock` / `SystemClock` は `Common/Abstractions/`
（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定3 のそのままの適用）

I/O を持たない技術プリミティブであり、1〜3 本目と同じ理由づけでそのまま適用した。新しい判断ではない。

### 判断4: `WeekdayMarketSchedule` は `Infrastructure/ExternalServices/`
（IADR-0259 の既定「Adapters/ → Infrastructure/ の該当区分」の適用。判断3 との整理を明示する）

`WeekdayMarketSchedule`（`IMarketSchedule` の実装）は曜日判定のみで I/O を持たず、表面的には判断3
（技術プリミティブ）と似て見える。しかし以下の理由で判断3 とは区別し、`Infrastructure/ExternalServices/`
（旧 `Composable/Adapters/` の同居先である `HttpPositionStore` / `PlaceholderPositionStore` と同じ場所）
に置いた。

- **[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定3 の射程は
  「集約を跨いで使われ得る技術プリミティブ」に限る。** `IMarketSchedule` は本サービス 1 集約に閉じた
  業務ポートであり、`IClock` のような汎用プリミティブではない。
  実装自体のコメント（「時間帯・祝日を含む正確な市場カレンダーは #21 で差し替える」）が示すとおり、
  **将来 I/O を伴う実装へ置き換わることが前提の暫定実装**である。
- **同じ性質の先例が既にある。** `PlaceholderPositionStore`（I/O を持たない `IPositionStore` の
  フォールバック実装）は、I/O を持つ `HttpPositionStore` と同じ `Infrastructure/ExternalServices/` に
  同居している。`WeekdayMarketSchedule` も同型（「本来 I/O で解決すべき概念の、現時点での簡易実装」）
  であり、同じ置き場に揃えることが一貫する。

### 判断5: ストア実装（EF / InMemory）は「本番実装の Infrastructure 区分」に合わせて対で置く
（IADR-0259 の既定の適用。新しい判断ではない。「対で置く」という運用ルールを明示する）

各ポートには EF 実装（永続化）または Http/Placeholder 実装（外部照会）のどちらかが本番実装として
存在し、InMemory 実装（シード・テスト用）はその**本番実装と同じ Infrastructure 区分**に置いた。

| ポート | 本番実装 | InMemory 実装 | 置き場 |
| --- | --- | --- | --- |
| `IMonitoredSymbolStore` | `EfMonitoredSymbolStore` | `InMemoryMonitoredSymbolStore` | `Infrastructure/Persistence/` |
| `IPriceBaselineStore` | `EfPriceBaselineStore` | `InMemoryPriceBaselineStore` | `Infrastructure/Persistence/` |
| `ICooldownStore` | `EfCooldownStore` | `InMemoryCooldownStore` | `Infrastructure/Persistence/` |
| `IMonitorSettingsChangeLog` | `EfMonitorSettingsChangeLog` | `InMemoryMonitorSettingsChangeLog` | `Infrastructure/Persistence/` |
| `IPositionStore` | `HttpPositionStore` / `PlaceholderPositionStore` | `InMemoryPositionStore` | `Infrastructure/ExternalServices/`（永続化ポートではないため `Persistence/` ではない） |

`MarketMonitorDbContext` / `MarketMonitorDbContextFactory` / `MonitorSettingsSerialization` /
`PersistenceRows`（`SingletonKeys` 含む 4 行モデル）・`Migrations/` 一式（2 本）は
`Infrastructure/Persistence/` へそのまま移した。

### 判断6: Wolverine ハンドラは `Infrastructure/Steps/`、BackgroundService は `Hosted/`
（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定5・
[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) 決定1 の適用。新しい判断ではない）

`TradeDecisionMadeBaselineHandler`（Wolverine ハンドラ）は `Infrastructure/Steps/`（名前空間は
先行整合済みのため変更なし）、`MonitorPollingService`（`BackgroundService`）とその構成
`MonitorOptions` は `Hosted/` に置いた。`MonitorOptions` は `MonitorPollingService` 専用の設定保持
クラスであり他から参照されないため、同じ `Hosted/` へ同居させた（新しい判断ではなく、実態に沿った配置）。

### 判断7: `internal` → `public` は「Tests が直接参照する型・メンバー」＋その CS0053 連鎖に限る
（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定4 の適用）

移送前から `internal` だった型のうち、Tests から**コンストラクタ呼び出し・型引数としての使用**で
直接参照されていたものだけを `public` にした（DI 経由のインターフェース越しの解決は対象外）。

| 型 | 直接参照の根拠 |
| --- | --- |
| `MarketMonitorDbContext` | `EfStoreTests.cs` が `new DbContextOptionsBuilder<MarketMonitorDbContext>()` / `new EfMonitoredSymbolStore(NewContext(...))` で直接構築、`PositionStoreSelectionTests.cs` / `MonitorWorkerWebApplicationFactory.cs` が `typeof(MarketMonitorDbContext)` / `AddDbContext<MarketMonitorDbContext>` で型引数使用 |
| `EfMonitoredSymbolStore` / `EfPriceBaselineStore` / `EfCooldownStore` | `EfStoreTests.cs` が `new Ef...Store(db)` で直接構築 |
| `SingletonKeys` | `EfStoreTests.cs` が `SingletonKeys.Id` を静的参照 |
| `HttpPositionStore` | `HttpPositionStoreTests.cs` が `new HttpPositionStore(...)` で直接構築 |
| `PlaceholderPositionStore` | `PositionStoreSelectionTests.cs` が `.BeOfType<PlaceholderPositionStore>()` で型引数使用 |
| `MonitorPollingService` | `MonitorPollingServiceTests.cs` が `new MonitorPollingService(...)` で直接構築 |
| `MonitorOptions` | `MonitorPollingServiceTests.cs` が `Options.Create(new MonitorOptions())` で直接構築 |
| `MonitorSettingsRow` / `PriceBaselineRow` / `CooldownRow` / `MonitorSettingsChangeRow` | 直接参照ではなく **CS0053 連鎖**（`MarketMonitorDbContext` の `public DbSet<T>` プロパティが `T` の可視性を要求する。[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定4 が明示的に想定していたケース） |

`EfMonitorSettingsChangeLog` / `MarketMonitorDbContextFactory` / `MonitorSettingsSerialization` /
`WeekdayMarketSchedule` / `MonitorSettingsEndpoints` とその要求レコード（`MonitorSettingsUpdateRequest` 等）は
Tests から直接参照されないため `internal` のまま据え置いた。`InternalsVisibleTo` は新設していない
（旧 3 csproj にあった計 3 エントリはすべて削除した）。

### 判断8: 名前空間の書き換え

[IADR-0261](../adr/IADR-0261_namespace-alignment-to-platform.md) で `MarketMonitorService.*` へ
先行整合済み（`.Api` 接頭辞は元から無い）。フォルダ移動に伴い変えたのは以下のみ。

- `MarketMonitorService.Application.{Ports,Services,State}` / `MarketMonitorService.Api.Endpoints`
  → `MarketMonitorService.Features.MarketMonitor`（`IClock` は除く）
- `MarketMonitorService.Application.Ports.IClock` / `MarketMonitorService.Application.Adapters.SystemClock`
  → `MarketMonitorService.Common.Abstractions`
- `MarketMonitorService.Application.Adapters`（`IClock`/`SystemClock` 以外の InMemory 実装） →
  `MarketMonitorService.Infrastructure.Persistence`（4 型）または
  `MarketMonitorService.Infrastructure.ExternalServices`（`InMemoryPositionStore` の 1 型）
- `MarketMonitorService.Infrastructure.Adapters` → `MarketMonitorService.Infrastructure.ExternalServices`
- `MarketMonitorService.Infrastructure.Polling` → `MarketMonitorService.Hosted`
- `MarketMonitorService.Infrastructure.{Persistence,Steps,Migrations}` は不変。`MarketMonitorService.Domain` も不変。

## Tests 統合（4 → 1）で変えていないことの証跡

**中身は 1 行も変えていない**（`git mv` のみ・変更は namespace 宣言・using の書き換え、および
「想定外」2・3 で述べた最小限の実務対応〔テストダブルの重複統合・完全修飾名の短縮〕に限定）。

### テスト件数の突合（移送前後を実測。削っていないことの証跡）

移送前は各旧テストプロジェクトを個別に `dotnet test` して実測した（本 PR 着手直後・クリーンな
作業ツリーで測定。旧プロジェクトがまだ存在する段階で先に測定したため `git stash` は使っていない
——1 本目の申し送り6・4 本目の申し送り6 を踏襲）。

| テストアセンブリ | 移送前 | 移送後 |
| --- | ---: | ---: |
| `MarketMonitorService.Api.Tests` | 45 | — |
| `MarketMonitorService.Application.Tests` | 34 | — |
| `MarketMonitorService.Domain.Tests` | 24 | — |
| `MarketMonitorService.Infrastructure.Tests` | 17 | — |
| **`MarketMonitorService.Tests`** | — | **120** |
| 合計 | **120** | **120** |

45 + 34 + 24 + 17 = 120 = 移送後の合格件数と**完全一致**。減った件・増えた件は 0。

## `list-test-projects.js --count` の突合

- 移送前: **43**
- 移送後: **40**
- 差分: **-3**（旧 4 テストプロジェクト → 新 1 テストプロジェクトの差分と一致）

## 名前空間の実装解決に関する事故（4 本目の申し送りどおり踏んだ／踏まなかった罠）

4 本目（BacktestService）の最重要申し送り「1 回目のビルドは全容を報告しない」を踏まえ、**最初から**
`dotnet build-server shutdown` → `bin`/`obj` 全消去 → フルビルドの手順で検証した
（`git status` で確認可能。手戻りは発生していない）。実際に発生した名前空間関連の事故は
「想定外」3（完全修飾名の部分参照 `Application.State.X` が `CS0246` になる）の**1 種類のみ**であり、
4 本目が踏んだ「大量の `using` 欠落」は**発生しなかった**。理由は、MarketMonitorService の主要な
テストファイルの多くが元々 `MarketMonitorService.Api.Tests` / `.Application.Tests` の**直接の子**
ではなく、移送前から明示的な `using MarketMonitorService.Application.Ports;` 等を書いていたため
（BacktestService の `Domain.Tests` は `Domain` の直接の子であることに依存していたが、
本サービスの `Application.Tests` は `Application` の直接の子ではあるものの、多くのファイルが既に
`using MarketMonitorService.Application.Adapters;` 等を明示していた）。

## `has-pending-model-changes`

```
$ dotnet ef migrations has-pending-model-changes --project backend/Services/MarketMonitorService --startup-project backend/Services/MarketMonitorService
Build started...
Build succeeded.
No changes have been made to the model since the last migration.
```

**エンティティ FQN 文字列（`ModelSnapshot.cs` の `modelBuilder.Entity("...")`）は移送前から
`MarketMonitorService.Infrastructure.Persistence.*` であり（[IADR-0261](../adr/IADR-0261_namespace-alignment-to-platform.md)
で先行整合済み）、本 PR のフォルダ移動でも名前空間が変わらないため、migration の CLR 型名文字列・
`MigrationId`・ファイル名のいずれも 1 文字も変更していない。** `Infrastructure/Persistence/` への
物理移動のみである。

## `DomainLayerDependencyTests` の下限（[IADR-0265](../adr/IADR-0265_domain-project-count-checker-dynamic-lower-bound.md)。
手で触っていない）

`RepositoryLayout.cs` / `DomainLayerDependencyTests.cs` は**本 PR で 1 行も変更していない**
（`git status` で確認可能。無変更）。`UnmigratedServicesWithDomainProjectCount` は
`backend/Services/<Svc>/src/` の実在と `.Domain` 接尾辞ディレクトリの実在を実ツリーから動的に数える
ため、MarketMonitorService の移送（`src/MarketMonitorService.Domain/` の消滅）により**自動的に** 1 件減る。

実測（`backend/Services/*/src/` を列挙し `.Domain` 接尾辞ディレクトリの有無を確認）:
移送前 7（BacktestService・InformationCollectionService・MarketMonitorService・OrderExecutionService・
ReportService・RiskManagementService・TradeDecisionService。**本ブランチの base には 4 本目
BacktestService の移送が未マージのため含まれる**）→ 移送後 **6**（BacktestService・
InformationCollectionService・OrderExecutionService・ReportService・RiskManagementService・
TradeDecisionService）。`dotnet test` で `AiStockTrading.Architecture.Tests` の
`Domain_プロジェクトの探索が空振りしていない` を含む全 10 件（`DomainLayerDependencyTests`）・
全 45 件（`DomainSourceDependencyTests`）が緑であることを実測済み。**手で下限を書き換える操作は
行っていない。**

`DomainSourceDependencyTests` の 2 つの下限（Domain ソース領域数・走査対象ファイル数）も実測して
確認し、変えなかった。

| 検査 | 移送前 | 移送後 |
| --- | --- | --- |
| Domain ソース領域数（`DomainSourceDirectories`） | 8 | 8（変化なし） |
| 走査対象ファイル数 | 変化なし（7 ファイルは旧 `src/MarketMonitorService.Domain/` から新 `MarketMonitorService/Domain/` へ**移動しただけ**） |

`RepositoryLayout.DomainSourceDirectories` は現行構成（層＝プロジェクト）と VSA 構成（層＝フォルダ）の
**両方の形を数える和集合**であり（[IADR-0256](../adr/IADR-0256_domain-dependency-inspection-by-source-scan.md)）、
MarketMonitorService は移送前後のいずれの時点でもどちらか一方の形で数えられる（[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md)
のように移送で Domain 自体が空になるケースとは異なる。決定2 参照）。

## IADR を作らない判断

**本 PR では新しい IADR（`IADR-0266`）を作らない。** [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)・
[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定3・
[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) の写像方針表を参照するだけで、
本 PR の判断1〜8 すべてが機械的に導けたためである。

- 判断1（集約は 1 つ）は IADR-0263 決定1 の**そのままの適用**。
- 判断2（Domain/Features の切り分け）は IADR-0264 決定3 の**そのままの適用**（Domain を持つサービスの
  3 例目。4 本目 BacktestService が 2 例目）。
- 判断3（`IClock`/`SystemClock` → Common/Abstractions）は IADR-0263 決定3 の**そのままの適用**。
- 判断4（`WeekdayMarketSchedule` の置き場）は新しい判定基準を作らず、既存の先例
  （`PlaceholderPositionStore` が I/O 無しでも本番実装と同じ Infrastructure 区分に同居する）へ揃えた
  ——**基準の適用であって新設ではない**。
- 判断5（ストア実装を本番実装の区分に対で置く）は IADR-0259 の写像方針表の既定の**運用上の明確化**
  であり、新しい設計判断ではない。
- 判断6（Steps/Hosted の配置）は IADR-0263 決定5・IADR-0259 決定1 の**そのままの適用**。
- 判断7（`internal`→`public`）は IADR-0263 決定4 の**そのままの適用**（対象型数が多いのは
  DbContext の CS0053 連鎖が実際に発生したためであり、決定4 が明示的に想定していた帰結そのもの）。
- 母集合の走査で見つけた「想定外」（appsettings の移送先・テストダブルの重複定義・完全修飾名の
  部分参照）はいずれも**移送手順上の実務**であり、樹形・可視性・依存規律に関する**設計判断ではない**。

**強いて新規性を挙げるなら**、想定外3（完全修飾名の部分参照が `CS0246` になる）が
4 本目までの申し送りに無かった現れ方だが、これは IADR を要するほどの分岐点ではなく、
次のサービスへの申し送り（下記）に書けば十分と判断した。

## 受け入れ基準

- [x] `dotnet build backend/backend.slnx` が 0 warning / 0 error で通る
      （`dotnet build-server shutdown` → `bin`/`obj` 全消去 → フルビルドで確認済み）
- [x] `dotnet test backend/backend.slnx` の失敗が `AiStockTrading.IntegrationTests` の 8 件のみ
      （Docker 不在の環境制約。全体 5120 件中失敗 8 件・成功 5112 件）
- [x] `dotnet format backend/backend.slnx --verify-no-changes` が通る（exit 0）
- [x] `dotnet ef migrations has-pending-model-changes` が「変更なし」を返す
- [x] `list-test-projects.js --count` が 43 → 40
- [x] `coverage-floor.json` の床（79.00%）を割らない（実測 82.28%。Release ビルド・
      bin/obj/TestResults 清掃後・40 レポート）
- [x] 検査器一式（`scripts/README.md` 掲載分）が緑（実行コマンドの直接終了コードで確認。
      パイプで `tail` を経由すると `check-doc-links.js` の失敗〔想定外4 参照〕を見落とすため
      直接終了コードでの確認へ切り替えた）
- [x] `DomainLayerDependencyTests` の下限が自動追随し（7 → 6）、`RepositoryLayout.cs` /
      `DomainLayerDependencyTests.cs` を手で編集していないことを確認した
- [x] `node scripts/scripts.test.js` と `node scripts/scripts.repo.test.js` が緑（294 テスト）

## 計画書との差異

- 差異: なし。本件は構造移送のみで振る舞いを変えていない（IADR-0259 決定7）。

## 残り 6 本のサービスへの申し送り（本 PR で踏んだ落とし穴・再利用可能な手順）

1. 🔴 **`appsettings.json` / `appsettings.Development.json` の移送先を忘れないこと。** 旧
   `src/<Svc>.Api/` 直下にあり、`Program.cs` と同じくルート直下（`<Svc>/`）へ移す。先行 4 本の
   申し送りには明記が無かった（対象サービスに appsettings が無かったか、報告が漏れていた可能性が
   ある）。**`git ls-files src/<Svc>.Api` で移送前に非 `.cs` ファイルも含めて全数確認すること。**
2. 🔴 **テストダブル（フェイク・スタブ）が複数の旧テストプロジェクトに重複定義されていないか確認する。**
   旧構成では別アセンブリのため衝突しなかった同名型（本 PR は `FakeClock` / `FakeMarketDataSource`）が、
   Tests 統合で **CS0101（型の重複定義）** を起こす。`diff` で内容を比較し、等価なら 1 ファイルへ
   統合する（統合の根拠と等価性の確認結果を仕様書へ書くこと。挙動を変えたことにならないよう、
   一方を機械的に削除するのではなく中身を見て判断する）。
3. 🔴 **完全修飾名の部分参照（例 `Application.State.X`）は `using` の欠落と別の壊れ方をする。**
   フラット化前は自ファイルの名前空間（例 `<Svc>.Api.Tests`）の**祖先**である `<Svc>` の子として
   `Application` 名前空間が暗黙解決され、`Application.State.X` のような部分参照が `using` 無しで
   通っていた。フラット化後（`<Svc>.Tests`）は `Application` 名前空間自体が消えるため `CS0246` になる。
   **`grep -rn '\bApplication\.\|Api\.Endpoints\.\|Infrastructure\.Adapters\.\|Infrastructure\.Polling\.'`
   のような部分参照パターンの全数走査を、`using` 行の走査とは別に行うこと**（`using` だけを見ていると
   見落とす）。
4. 🔴 **`node scripts/xxx.js | tail -N` のようにパイプで出力を切ると、実際の終了コードが `tail`/`echo`
   のものにすり替わり、検査器の失敗（本 PR では `check-doc-links.js` の破損リンク 1 件）を
   見落とす。** 検査器は**必ず直接の終了コード**（`$?` をリダイレクトの直後で確認する、または
   `node scripts/x.js; echo "EXIT:$?"` の形）で確認すること。
   `.claude/rules/traceability.repo.md` 規則7（「走査の出力を加工して読まない」）と同種の事故である。
5. **移送で `internal` → `public` 化した型が `DbContext` を含む場合、`DbSet<T>` の `T`（行モデル）が
   CS0053 連鎖で public 化を要求されることを見込んでおくこと。** 1 回目のビルドで
   `MarketMonitorDbContext` を public にした直後は行モデル型（`MonitorSettingsRow` 等）がまだ
   `internal` のままで CS0053 は出なかった（`DbSet<T>` の `Set<T>()` 呼び出し式のみで、プロパティの
   戻り値型としての可視性チェックは C# コンパイラが別の段階で行うため、最初の `dotnet build` では
   検出されず**2 回目の完全ビルドで初めて検出された**）。行モデル型の可視性は DbContext を
   public 化した時点で先回りして確認すること。
6. **I/O を持たない業務ポートの実装（技術プリミティブではないもの）は、`Common/Abstractions/` ではなく
   その実装が将来担うべき Infrastructure 区分（多くは `ExternalServices/`）に、同じポートの他の実装と
   同居させる。** `IClock` のような汎用技術プリミティブとの区別は「集約を跨いで使われ得るか」
   「同じポートの本番実装が別に存在し、それと同居させるのが自然か」で判定する。
7. 移送前のテスト件数は、旧プロジェクトが消える前に個別 `dotnet test` で実測しておく（1 本目・
   4 本目の申し送りを継続して踏襲。本 PR でも有効だった）。
8. 🔴 **移送で移動したファイルを IADR がリンクしていると、そのリンク是正が
   `check-adr-index-sync.js` に引っかかる。** 本 PR では `IADR-0090` が
   `MonitorSettingsEndpoints.cs` を相対リンクで引いており、移送でパスが変わったため
   本文 1 行の是正が要った。同検査器は「実装ADR の本文を変更したのに索引行を変更していない」
   ものを落とすが、**パスの追随は索引行の要約に影響しない**ため、逃げ道である
   `[skip-adr-index]` をコミット本文へ宣言するのが正しい対処である（索引行を意味なく
   触るほうが有害。索引は「一覧を読む人が最初に見る要約」であり、決定の要約が変わって
   いないのに更新すると変更履歴が汚れる）。
   **範囲は `origin/develop..HEAD` の全コミット本文**を走査するので、宣言は当該コミットに
   限らず同 PR 内のどのコミットへ書いてもよい。
   **残り 6 本でも、移送したファイルを引いている IADR / 仕様書のリンクは同様に是正が要る。**
9. 🔴🔴 **ローカル検証だけで出る偽陽性: 空の残骸ディレクトリが「未移送」と誤判定される。**
   先行 PR を `git merge` で取り込むと、git は追跡ファイルを消すが**空になった親ディレクトリを
   残すことがある**（取り込み時点で `obj/` 等の未追跡物を含んでいた場合）。本 PR では
   develop 取り込み後に `backend/Services/BacktestService/src/{Api,Application,Domain,Infrastructure}`
   の 4 つが**中身 0 ファイルの空ディレクトリとして残り**、
   `RepositoryLayout.UnmigratedServicesWithDomainProjectCount`（`src/*.Domain` を
   **ディレクトリ名**で数える）が BacktestService を未移送と誤認して期待値 6 を返し、
   一方 csproj の**ファイル**走査は正しく 5 を返したため
   `DomainLayerDependencyTests.Domain_プロジェクトの探索が空振りしていない` が落ちた。
   **CI（fresh clone）では git が空ディレクトリを持たないため再現しない**——**ローカル限定の
   偽陽性**である。`find backend -type d -empty -delete` で解消し、88/88 全緑を確認した。
   **残り 6 本の検証でも develop 取り込みの直後に空ディレクトリを掃除すること。**
   なお `IADR-0265` が「期待値と実測を別経路で導く」設計にしてあるおかげで**両者が食い違った
   ことが検出できた**——同じ走査から両方を導いていたら、揃って誤ったまま静かに緑になっていた。
