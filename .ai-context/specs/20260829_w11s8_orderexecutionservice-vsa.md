---
title: OrderExecutionService を単一プロジェクト＋VSA 樹形へ移送する（W11 段 4-8）
type: spec
status: approved
related_ids: [NFR, IADR-0259, IADR-0263, IADR-0264, IADR-0265]
author: endazon (with Claude Code)
created: 2026-08-29
updated: 2026-08-29
plan_refs: []
---

# 仕様書: OrderExecutionService の単一プロジェクト＋VSA 移送（W11 段 4-8）

> **11 サービス移送波の 8 本目**である。1 本目（AuditService・[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)）・
> 2 本目（ConfigurationService・[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md)）・
> 3〜7 本目（CostControl / Backtest / MarketMonitor / Notification / InformationCollection。
> InformationCollection は develop へ未マージのため本ブランチの base〔`58854e3`〕には含まれない）で
> 確定した判断の型をそのまま適用した。**新しい判断軸は生じなかった**（末尾「IADR を作らない判断」参照）。
> OrderExecutionService は `Domain/`・`Features/`（ポート複数）・`Infrastructure/`（Persistence /
> ExternalServices / Steps の 3 区分すべて）・`Hosted/`・`Common/Abstractions/` のすべてを持つ、
> MarketMonitorService（w11s5）と同型の「全区分を持つ」サービスである。

## 起点

- 起点 ID: **`NFR`（無採番）**。構造移送＝メタ作業であり、`.claude/rules/traceability.md`
  「起点 ID の種別」の無採番許容ケース **2** に当たる（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)
  が確定済みの判断を継承する。環流はしない）。
- 上流: [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)（1 本目の 5 決定）・
  [IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md)（2 本目。特に決定3
  「Domain を持つサービスの型」）・[IADR-0265](../adr/IADR-0265_domain-project-count-checker-dynamic-lower-bound.md)
  （検査の下限の動的化。本 PR は手で触っていない）・[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)
  （樹形・写像方針表）

## 着手前に読んだもの

- `CLAUDE.md` / `.claude/rules/traceability.md` / `.claude/rules/traceability.repo.md` /
  `docs/DEFINITION_OF_DONE.md`
- [IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)（決定5＝`Hosted/` は入口に留める）・
  [IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定3（Domain/Features 切り分け基準）・
  [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) ／
  [IADR-0265](../adr/IADR-0265_domain-project-count-checker-dynamic-lower-bound.md)
- 先行 7 本の作業仕様書のうち、本ブランチから読める分（`.ai-context/specs/2026082*vsa*.md`）:
  [20260829_w11s4a](20260829_w11s4a_auditservice-vsa.md) / [20260829_w11s4b](20260829_w11s4b_configurationservice-vsa.md) /
  [20260829_w11s4c](20260829_w11s4c_costcontrolservice-vsa.md) / [20260829_w11s4d](20260829_w11s4d_backtestservice-vsa.md) /
  [20260829_w11s5_marketmonitorservice-vsa](20260829_w11s5_marketmonitorservice-vsa.md)（**必読・最も近い先例。
  Persistence＋Hosted 両方を持つ全区分型）／[20260829_w11s6_notificationservice-vsa](20260829_w11s6_notificationservice-vsa.md)。
  🔴 **`w11s7_informationcollectionservice` の仕様書はこのブランチには存在しない**（#592 は develop へ
  未マージのため base に含まれない。指示にある「直前」という位置づけは全体の波の順序であり、
  本ブランチのローカル履歴上の直前ではない）。

## 対象範囲

- 対象: `backend/Services/OrderExecutionService/`（8 csproj → 2 csproj）、`backend/backend.slnx`、
  `backend/Tests/AiStockTrading.IntegrationTests/`（参照張り替え・必須作業）、`docker-compose.yml`、
  `scripts/k8s-local-images.sh`
- 対象外: 他サービス（次の PR 以降）、`backend/Shared/` `backend/TestSupport/`（据え置き集合）

## 着手前の母集合の引き直し（`.claude/rules/traceability.repo.md` 規則1〜10）

**母集合は記憶で挙げず、誤りになる側の文字列で全追跡ファイルを走査して引いた**（規則1・2・9・10）。
走査した語は `OrderExecutionService\.(Api|Application|Domain|Infrastructure)` /
`OrderExecutionService[/\\](src|tests)` の 2 本（バックスラッシュ形も走査。規則2）。

| 項目 | 実測 | 親の前提との一致 |
| --- | --- | --- |
| 移送前の .cs（src + tests） | **104**（src 71・tests 33） | 一致 |
| 移送前の csproj | **8**（src 4・tests 4。`Domain` あり） | 一致 |
| migration | **5 本**（InitialCreate / AddOrderDispatchReservations / AddReservationRetentionIndex / AddOrderLifecycleEvents / AddProtectiveStopOrders）。`DbContext` 1 個（`OrderExecutionDbContext`） | 一致 |
| 🔴 **BackgroundService** | **実測 6 件**（`grep ": BackgroundService"` + `AddHostedService<>` の呼び出し箇所を突合）: `OrderReservationRetentionService` / `OrderReservationReconciliationService` / `OrderFillPollingService` / `BrokerPositionSnapshotService` / `ProtectiveStopGuardService` / `BrokerAvailabilityProbeService` | 🔴 **親の前提「8 件」と不一致（実測 6）**。`Program.cs` の `AddHostedService<>` 呼び出し（複数行にまたがる 1 箇所を含む）を全数突合したが 6 を超えない。**「`Infrastructure/Persistence/` と `Hosted/` はどちらも実体がある」という結論自体は変わらない**（migration 5 本・BackgroundService 6 件のいずれも 0 ではない）ため、樹形の判断には影響しない。実測を正とし、以降はこの節を根拠に扱う |
| `list-test-projects.js --count`（base `58854e3`） | **35**（クリーンな作業ツリーで実測） | 親の前提と一致 |
| `OrderExecutionService` を参照する他サービスの `ProjectReference`（`backend/Services` 配下） | 0 件（`BacktestService.csproj` に散文コメント 1 件のみ・実体参照ではない） | — |
| `backend/Tests/AiStockTrading.IntegrationTests` の参照 | **`.csproj` に 1 件**（27 行目・`Aliases` 属性なし）・**`.cs` に 1 件**（`OrderExecutionPipelineE2ETests.cs` が `using OrderExecutionService.Application.Ports;` を持つ） | 🔴 **`.csproj` の張り替えは親の指示どおりだが、`.cs` の `using` も追随が必要と判明**（想定外・後述） |
| `docker-compose.yml` / `scripts/k8s-local-images.sh` の build args | 各 1 箇所（`SERVICE_PROJECT` / `SERVICE_DLL`。両方とも本 PR で追随した） | 一致 |
| `docs/` 配下の OrderExecutionService パス参照（`OrderExecutionService\.(Api\|Application\|Domain\|Infrastructure)` / `OrderExecutionService[/\\](src\|tests)`） | **3 件**（`docs/tests/FR-19_trading-guards-tests.md` 1 件・`docs/tests/FR-10_risk-controls-tests.md` 2 件・`docs/operations/live-trading-cutover-runbook.md` 1 件〔パス〕）。いずれも旧テストプロジェクト名／旧パスの記述で、**本 PR で是正した**（生きた文書なので凍結記録の除外に当たらない） | — |
| `.ai-context/adr/` 配下の同パターン参照 | **1 件**（[IADR-0140](../adr/IADR-0140_broker-provider-axis.md) の散文「`OrderExecutionService.Infrastructure` は同名の `internal enum BrokerProvider`…」）。**凍結記録の点在時点の記述であり、`OrderExecutionService.Infrastructure` という名前空間の木自体は移送後も存続する（ブローカ関連の実装は `Infrastructure.ExternalServices` へ移ったがなお `.Infrastructure` の子）ため書き換えていない** | — |
| `.ai-context/specs/` 配下の同パターン参照 | 実測 **7 件**（5 ファイル）。いずれも point-in-time の記録（`.claude/rules/traceability.repo.md` 除外規定）であり未更新。内訳: `20260710_order-execution.md`（当時の新規プロジェクト名の宣言）・`20260803_354_wolverine-migration.md`（当時のテストクラス名）・`20260729_270_moomoo-fill-polling.md`（当時の実装パス）・`20260803_353_standard-project-layout.md`（当時の実測列挙）・`20260828_w9f1_architecture-tests-dual-inspection.md`（当時の実測パス） | — |
| `deploy/helm/ai-stock-trading/files/pipeline.json` の consumer 参照 | 1 件（`OrderExecutionService.Infrastructure.Steps.OrderApprovedHandler`）。**`Infrastructure.Steps` 名前空間は移送で変えていないため、書き換え不要（現物のまま正しい）** | — |
| `backend/Services/BacktestService/Infrastructure/ExternalServices/BarDataOptions.cs` のコメント | 1 件（「`MoomooPreflight`（OrderExecutionService.Infrastructure）」）。**`MoomooPreflight` は移送後も `OrderExecutionService.Infrastructure.ExternalServices`（`.Infrastructure` の子）にあるため、この粒度の記述は書き換え不要** | — |

### 母集合の走査で見つかった「想定外」

1. 🔴 **`backend/Tests/AiStockTrading.IntegrationTests/OrderExecutionPipelineE2ETests.cs` が
   `using OrderExecutionService.Application.Ports;` を持っていた。** 親の指示は `.csproj` の
   `ProjectReference` パス張り替えのみを必須作業と明記していたが、**参照先の名前空間そのものが
   移送で変わる**ため、この `using` を直さないと `IExecutedOrderStore`（同ファイルが使う唯一の
   ポート型）が解決できず `CS0246` になる。`OrderExecutionService.Features.OrderExecution` へ
   書き換えた（1 行のみ。extern alias の宣言・テスト本文のロジックには触れていない）。
2. 🔴 **`internal static` の**メンバー**単位の見落とし（申し送り事項の実例）。** クラス単位の
   `^internal ` 一括置換では、`MoomooBrokerAdapter.MapState` / `MMApiMoomooTradeClient.{MapState,
   MapMarket,MapAccountType}` のような**インデントされた `internal static` メソッド**を拾えなかった
   （1 回目のビルドで `CS0117` として 23 件検出）。`Tests/MMApiMoomooTradeClientMappingTests.cs` /
   `Tests/MoomooBrokerAdapterTests.cs` が `<Type>.<StaticMember>(` の形で直接呼び出しており、
   `<Type>.<StaticMember>(` の grep で洗い出して該当 4 メソッドのみを public 化した
   （同クラスの `MapMarketBack` / `MapMarket`〔`MoomooBrokerAdapter` 側〕/ `MapSide` はテストが
   呼んでいないため internal のまま据え置いた）。

## 設計

### 判断1: 集約は 1 つ（`OrderExecution`）とし、`_Shared/` は作らない
（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定1 の適用。新しい判断ではない）

発注執行・約定ポーリング・建玉突合・予約保持・保護逆指値ガード・ブローカ稼働観測はいずれも
`OrderDispatchReservation` / `ExecutionRecord` / `ProtectiveStopOrder` を中心に据える不可分な概念であり、
操作フォルダの兄弟を作る決定（3 段目のスライス分割）は採らない
（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) 決定1）。集約は `OrderExecution` 1 つとし、
`Features/OrderExecution/` 直下に平らに置いた。集約名はサービス名から `Service` 接尾辞を落とした形
（先行 5〜7 本目と同じ規則）。

### 判断2: `Domain/` と `Features/OrderExecution/` の切り分け（IADR-0264 決定3 の適用。新しい判断ではない）

[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定3 の基準
（**Domain ＝フレームワーク・DI・I/O に触れず業務概念そのものを表す型。ポート・アプリケーションサービス・
エンドポイント・DTO・ストアは Features/<集約>/**）と、同決定の 🔴 注記（**移送で型の層を変えない**）を
そのまま適用した。

| 元のプロジェクト | 型 | 置き場 |
| --- | --- | --- |
| `OrderExecutionService.Domain`（6 ファイル） | `ExecutionRecord` / `OrderLifecycleEvent` / `OrderStatusLifecycle` / `ProtectiveStopIds` / `ProtectiveStopOrder` / `SlippageCalculator`（エンティティ・値オブジェクト・純粋な計算） | **`Domain/`**（そのまま） |
| `OrderExecutionService.Application/Ports/`（`IClock` 以外の 6 インターフェース） | `IClientOrderIdBroker` / `IExecutedOrderStore` / `IOrderLifecycleStore` / `IOrderReservationStore` / `IProtectiveStopOrderStore` / `IReservationBrokerProbe` | **`Features/OrderExecution/`**（決定3「ポートは Features」） |
| `OrderExecutionService.Application/Services/` | `OrderAmendmentService` / `OrderExecutionAppService` / `OrderReservationReconciler` / `OrderDispatchResult` / `OrderDispatchReservationConflictException` | **`Features/OrderExecution/`** |
| `OrderExecutionService.Application/Reconciliation/` | `ReconciliationOptions` / `ReconciliationPolicy`（純関数だが元の層＝Application を維持） / `PositionReconciliationOptions` | **`Features/OrderExecution/`**（決定3 🔴 注記どおり、Domain 由来でない限り層を上げない） |
| `OrderExecutionService.Application/StopGuard/` | `ProtectiveStopGuard` / `ProtectiveStopGuardOptions` | **`Features/OrderExecution/`** |
| `OrderExecutionService.Application/Polling/` | `OrderFillPoller` / `FillPollingOptions` | **`Features/OrderExecution/`** |
| `OrderExecutionService.Application/Availability/` | `BrokerAvailabilityProbeOptions` | **`Features/OrderExecution/`** |
| `OrderExecutionService.Api/`（`Program.cs` の合成のみ・独立エンドポイントクラスなし） | — | 該当なし（HTTP 面はヘルスチェックのみで専用 `Endpoint.cs` を持たない。[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) が明記する「HTTP 面を 1 本も持たないサービス」の 1 例） |

### 判断3: `IClock` / `SystemClock` は `Common/Abstractions/`
（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定3 のそのままの適用）

I/O を持たない技術プリミティブであり、1〜7 本目と同じ理由づけでそのまま適用した。新しい判断ではない。

### 判断4: ストア実装（EF / InMemory）は「本番実装の Infrastructure 区分」に合わせて対で置く
（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) の既定の適用。w11s5 判断5 と同じ運用ルール）

| ポート | 本番実装 | InMemory 実装 | 置き場 |
| --- | --- | --- | --- |
| `IExecutedOrderStore` | `EfExecutedOrderStore` | `InMemoryExecutedOrderStore` | `Infrastructure/Persistence/` |
| `IOrderLifecycleStore` | `EfOrderLifecycleStore` | `InMemoryOrderLifecycleStore` | `Infrastructure/Persistence/` |
| `IOrderReservationStore` | `EfOrderReservationStore` | `InMemoryOrderReservationStore` | `Infrastructure/Persistence/` |
| `IProtectiveStopOrderStore` | `EfProtectiveStopOrderStore` | `InMemoryProtectiveStopOrderStore` | `Infrastructure/Persistence/` |
| `IReservationBrokerProbe` | `MoomooReservationBrokerProbe` | `IndeterminateReservationBrokerProbe`（既定の no-op 実装。Program.cs で常時いずれかを登録） | `Infrastructure/ExternalServices/`（永続化ポートではないため `Persistence/` ではない） |

`OrderExecutionDbContext` / `OrderExecutionDbContextFactory` / 4 つの Row 型 / `Migrations/` 一式（5 本）は
`Infrastructure/Persistence/` へそのまま移した。

### 判断5: moomoo（OpenD）ブローカ関連の一式は `Infrastructure/ExternalServices/`
（IADR-0259 の既定「Adapters/ → Infrastructure/ の該当区分」の適用。新しい判断ではない）

`BrokerFactory` / `BrokerSelection` / `IMoomooTradeClient` / `LiveTradingGate` / `MMApiMoomooTradeClient` /
`MoomooBrokerAdapter` / `MoomooBrokerOptions` / `MoomooClientOrderId` / `MoomooReservationBrokerProbe`
（旧 `Infrastructure/Composable/Adapters/` の 9 ファイル）はいずれも I/O を伴う外部ブローカ接続の
アダプタ層であり、そのまま `Infrastructure/ExternalServices/` に移した。

### 判断6: Wolverine ハンドラは `Infrastructure/Steps/`、BackgroundService は `Hosted/`
（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定5・
[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) 決定1 の適用。新しい判断ではない）

`OrderApprovedHandler` / `OrderAmendmentDispatcher`（Wolverine ハンドラ）は `Infrastructure/Steps/`
（名前空間は先行整合済みのため変更なし）、6 件の BackgroundService（判断7 参照）とその専用設定
（`RetentionOptions` は Shared.Contracts 由来のため対象外。サービス固有の Options はすべて元の層＝
Application にあったため判断2 の帰結で `Features/OrderExecution/` へ、`Hosted/` には同居させていない
——[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) の写像方針表の既定と異なり、
w11s5 の `MonitorOptions` は元々 `Infrastructure/Polling/` にあったため `Hosted/` へ同居したが、
本サービスの Options 群は元々 `Application/*` にあったため決定3 🔴 注記により層を変えず `Features/` へ
置いた。両者は前提〔元の層〕が違うため矛盾しない）は `Hosted/` に置いた。

### 判断7: BackgroundService 6 件（親の前提「8 件」を実測で訂正）

| クラス | 元の namespace | 新 namespace |
| --- | --- | --- |
| `OrderReservationRetentionService` | `...Infrastructure.Retention` | `...Hosted` |
| `OrderReservationReconciliationService` | `...Infrastructure.Reconciliation` | `...Hosted` |
| `OrderFillPollingService` | `...Infrastructure.Polling` | `...Hosted` |
| `BrokerPositionSnapshotService` | `...Infrastructure.Reconciliation` | `...Hosted` |
| `ProtectiveStopGuardService` | `...Infrastructure.StopGuard` | `...Hosted` |
| `BrokerAvailabilityProbeService` | `...Infrastructure.Availability` | `...Hosted` |

`Program.cs` の `AddHostedService<>` 呼び出し箇所（複数行にまたがる 1 箇所を含む）を全数突合し、
上記 6 件で尽きることを確認した（母集合の引き直し節参照）。

### 判断8: `internal` → `public` は「Tests が直接参照する型・**メンバー**」＋その CS0053 連鎖に限る
（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定4 の適用。
member 単位の見落としは「想定外」2 参照）

| 型・メンバー | 直接参照の根拠 |
| --- | --- |
| `OrderExecutionDbContext` | 7 ファイルが `typeof(DbContextOptions<OrderExecutionDbContext>)` / `AddDbContext<OrderExecutionDbContext>` / `GetRequiredService<OrderExecutionDbContext>` で直接使用 |
| `EfExecutedOrderStore` / `EfOrderLifecycleStore` / `EfOrderReservationStore` / `EfProtectiveStopOrderStore` | 各 `Ef*StoreTests.cs` が `new Ef...Store(db)` で直接構築 |
| `ExecutedOrderRow` / `OrderDispatchReservationRow` / `OrderLifecycleEventRow` / `ProtectiveStopOrderRow` | 直接参照ではなく **CS0053 連鎖**（`OrderExecutionDbContext` の `public DbSet<T>` が `T` の可視性を要求する） |
| `BrokerFactory` / `BrokerSelection`（`BrokerVendor` / `BrokerEnvironment` 含む） | `BrokerFactoryTests.cs` / `BrokerSelectionTests.cs` が静的メソッド呼び出し・値の直接比較で使用 |
| `IMoomooTradeClient`（`MoomooAccountType` / `MoomooOrderKind` / `MoomooMarket` / `MoomooSide` / `MoomooOrderState` / `MoomooPositionSnapshot` / `MoomooOrderRequest` / `MoomooOrderResult` / `MoomooOrderSnapshot` 含む） | 4 ファイルが `private sealed class FakeClient : IMoomooTradeClient` のように**インターフェースを直接実装**（内部型が他アセンブリから実装できるためには型自体が public である必要がある。CS0053 系連鎖でメンバー型もすべて public 化） |
| `LiveTradingGate` | `LiveTradingGateTests.cs` が `LiveTradingGate.Ensure` / `.LiveTradingReleased` を静的参照 |
| `MMApiMoomooTradeClient`（クラス） | `MMApiMoomooTradeClientMappingTests.cs` / `MoomooBrokerAdapterTests.cs` が静的メソッド呼び出しで参照 |
| `MMApiMoomooTradeClient.MapState` / `.MapMarket` / `.MapAccountType`（🔴 メンバー単位） | 上記 2 ファイルが `MMApiMoomooTradeClient.MapState(...)` 等を直接呼び出し（「想定外」2） |
| `MoomooBrokerAdapter`（クラス） | `MoomooBrokerAdapterTests.cs` が `new MoomooBrokerAdapter(...)` で直接構築 |
| `MoomooBrokerAdapter.MapState`（🔴 メンバー単位） | 同ファイルが `MoomooBrokerAdapter.MapState(...)` を直接呼び出し |
| `MoomooBrokerOptions` | `MoomooBrokerOptionsTests.cs` が `MoomooBrokerOptions.FromConfiguration(...)` を直接呼び出し |
| `MoomooReservationBrokerProbe` | `MoomooReservationBrokerProbeTests.cs` が `new MoomooReservationBrokerProbe(...)` で直接構築 |
| `OrderReservationRetentionService` / `OrderReservationReconciliationService` / `OrderFillPollingService` / `BrokerPositionSnapshotService` / `ProtectiveStopGuardService` / `BrokerAvailabilityProbeService`（6 件） | 各 `*ServiceTests.cs` が `new <Service>(...)` またはビルダーヘルパで直接構築 |
| `IndeterminateReservationBrokerProbe` | 元々 `public`（据え置き） |

`internal` のまま据え置いたもの（Tests から直接参照されない）: `OrderExecutionDbContextFactory`
（`dotnet ef` がリフレクションで発見するため public 化不要。w11s5 の `MarketMonitorDbContextFactory` と
同じ判断）・`MoomooClientOrderId`・`MoomooPreflight`（**呼び出しは確認できたので public 化**した。
`MoomooBrokerOptionsTests.cs` が `MoomooPreflight.Validate(...)` を直接呼ぶため上表に含む）・
`MoomooBrokerAdapter.MapMarketBack` / `.MapMarket` / `.MapSide`（テストが呼ばない 3 メンバーのみ内部据え置き）。
`InternalsVisibleTo` は新設していない（旧 3 csproj にあった計 3 エントリはすべて削除した）。

### 判断9: 名前空間の書き換え

[IADR-0261](../adr/IADR-0261_namespace-alignment-to-platform.md) で `OrderExecutionService.*` へ
先行整合済み（`.Api` 接頭辞は元から無い）。フォルダ移動に伴い変えたのは以下のみ。

- `OrderExecutionService.Application.{Ports,Services,Reconciliation,StopGuard,Polling,Availability}`
  （`IClock` は除く） → `OrderExecutionService.Features.OrderExecution`
- `OrderExecutionService.Application.Ports.IClock` / `OrderExecutionService.Application.Adapters.SystemClock`
  → `OrderExecutionService.Common.Abstractions`
- `OrderExecutionService.Application.Adapters`（`IClock`/`SystemClock` 以外の InMemory 実装） →
  `OrderExecutionService.Infrastructure.Persistence`（4 型）または
  `OrderExecutionService.Infrastructure.ExternalServices`（`IndeterminateReservationBrokerProbe` の 1 型）
- `OrderExecutionService.Infrastructure.Adapters` → `OrderExecutionService.Infrastructure.ExternalServices`
- `OrderExecutionService.Infrastructure.{Availability,Polling,Reconciliation,Retention,StopGuard}`
  → `OrderExecutionService.Hosted`
- `OrderExecutionService.Infrastructure.{Persistence,Steps,Migrations}` は不変。`OrderExecutionService.Domain` も不変。

## Tests 統合（4 → 1）で変えていないことの証跡

**中身は 1 行も変えていない**（`git mv` のみ・変更は namespace 宣言・using の書き換えに限定）。
w11s5 のようなテストダブルの重複統合は不要だった——旧 4 テストプロジェクトの非 `*Tests.cs` ヘルパは
`ExecutionWorkerWebApplicationFactory.cs` 1 本のみで、`FakeClock` 等の重複名はすべて各 `*Tests.cs`
内の **private nested class**（アセンブリレベルでは衝突しない）であることを確認した
（トップレベル `public class` の名前重複が 0 件であることも実測）。

### テスト件数の突合（移送前後を実測。削っていないことの証跡）

移送前は旧テストプロジェクトが存在する段階で個別 `dotnet test` を実行して実測した
（本 PR 着手直後・クリーンな作業ツリー）。

| テストアセンブリ | 移送前 | 移送後 |
| --- | ---: | ---: |
| `OrderExecutionService.Api.Tests` | 1 | — |
| `OrderExecutionService.Application.Tests` | 112 | — |
| `OrderExecutionService.Domain.Tests` | 7 | — |
| `OrderExecutionService.Infrastructure.Tests` | 182 | — |
| **`OrderExecutionService.Tests`** | — | **302** |
| 合計 | **302** | **302** |

1 + 112 + 7 + 182 = 302 = 移送後の合格件数と**完全一致**。減った件・増えた件は 0。
`[Fact]`/`[Theory]` 属性の総数でも裏を取った: 移送前 1+92+6+143 = **242** ／ 移送後 **242**（一致）。
`.cs` ファイル数: 移送前 104（src 71・tests 33）／ 移送後 **104**（`git mv` のみで増減なし）。

## `list-test-projects.js --count` の突合

- 移送前: **35**（base `58854e3`＝develop への NotificationService〔#591〕マージ直後）
- 移送後: **32**
- 差分: **-3**（旧 4 テストプロジェクト → 新 1 テストプロジェクトの差分と一致）

## `IntegrationTests` の参照張り替え（本サービス固有の必須作業）

`backend/Tests/AiStockTrading.IntegrationTests/AiStockTrading.IntegrationTests.csproj` 27 行目を
CostControlService の移送後の形を手本に張り替えた。

```diff
-    <ProjectReference Include="..\..\Services\OrderExecutionService\src\OrderExecutionService.Api\OrderExecutionService.Api.csproj" />
+    <ProjectReference Include="..\..\Services\OrderExecutionService\OrderExecutionService.csproj" />
```

**`Aliases` 属性は無い**（発注執行 Worker は `Program` を無名〔global〕参照する。手本の
`CostControlWorker` のような別名付与は行っていない・タスクの指示どおり）。`extern alias` の宣言
（`RiskManagementWorker` / `ReportWorker` / `CostControlWorker`）とテスト本文（アサーション・
シナリオ）には**触れていない**。

🔴 **母集合の走査で判明した追加作業**: `OrderExecutionPipelineE2ETests.cs` が
`using OrderExecutionService.Application.Ports;` を持っており、移送で名前空間が変わるため
`OrderExecutionService.Features.OrderExecution` へ 1 行書き換えた（「想定外」1 参照。**ロジック・
アサーションは無変更**）。

`dotnet build backend/backend.slnx` は `AiStockTrading.IntegrationTests` を含め 0 Warning / 0 Error で
成功し、`dotnet test` は `OrderExecutionPipelineE2ETests` を含む同プロジェクトの 8 件が
Docker 不在で失敗するのみであることを確認した（後述「受け入れ基準」）。

## `has-pending-model-changes`

```
$ dotnet ef migrations has-pending-model-changes --project backend/Services/OrderExecutionService --startup-project backend/Services/OrderExecutionService
Build started...
Build succeeded.
No changes have been made to the model since the last migration.
```

**エンティティ FQN 文字列（`OrderExecutionDbContextModelSnapshot.cs` の
`modelBuilder.Entity("...")`）は移送前から `OrderExecutionService.Infrastructure.Persistence.*` であり
（[IADR-0261](../adr/IADR-0261_namespace-alignment-to-platform.md) で先行整合済み）、本 PR のフォルダ移動でも
名前空間が変わらないため、migration の CLR 型名文字列・`MigrationId`・ファイル名のいずれも 1 文字も
変更していない。** 11 個の migration 関連ファイル（5 migration × 2 + snapshot 1）すべてを base
`58854e3` の内容とバイト単位で diff し、**完全一致**を確認した（`Infrastructure/Persistence/Migrations/`
への物理移動のみ）。

## `DomainLayerDependencyTests` の下限（[IADR-0265](../adr/IADR-0265_domain-project-count-checker-dynamic-lower-bound.md)。
手で触っていない）

`RepositoryLayout.cs` / `DomainLayerDependencyTests.cs` は**本 PR で 1 行も変更していない**
（`git status` で確認可能。無変更）。`UnmigratedServicesWithDomainProjectCount` は
`backend/Services/<Svc>/src/` の実在と `.Domain` 接尾辞ディレクトリの実在を実ツリーから動的に数える
ため、OrderExecutionService の移送（`src/OrderExecutionService.Domain/` の消滅）により**自動的に** 1 件減る。

実測（`backend/Services/*/src/` を列挙し `.Domain` 接尾辞ディレクトリの有無を確認）:
移送前 **5**（InformationCollectionService・OrderExecutionService・ReportService・RiskManagementService・
TradeDecisionService。BacktestService・MarketMonitorService は既に移送済みのため対象外）→
移送後 **4**（InformationCollectionService・ReportService・RiskManagementService・TradeDecisionService）。
`dotnet test` で `AiStockTrading.Architecture.Tests` の `DomainLayerDependencyTests` /
`DomainSourceDependencyTests` を含む全 88 件が緑であることを実測済み。**手で下限を書き換える操作は
行っていない。**

## IADR を作らない判断

**本 PR では新しい IADR（`IADR-0266`）を作らない。** [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)・
[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定3・
[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) の写像方針表を参照するだけで、
本 PR の判断1〜9 すべてが機械的に導けたためである（w11s5・w11s6 と同じ判断）。

- 判断1〜7・9 はいずれも先行 IADR・先行 PR の**そのままの適用**。
- 判断8（`internal`→`public`）は IADR-0263 決定4 の**そのままの適用**であり、対象が多い（メンバー
  単位の見落としを含む）のは moomoo ブローカ関連のテスト表面が広いためで、決定4 が明示的に想定していた
  帰結（member-level の連鎖含む）そのものである。
- 母集合の走査で見つけた「想定外」（`IntegrationTests` の `using` 追随・`internal static` メンバーの
  見落とし）はいずれも**移送手順上の実務**であり、樹形・可視性・依存規律に関する**新しい設計判断ではない**
  （判断8 の適用範囲を「メンバー単位まで見る」という既存の指示どおり徹底しただけ）。

## 受け入れ基準

- [x] `dotnet build backend/backend.slnx` が 0 warning / 0 error で通る
      （`dotnet build-server shutdown` → `bin`/`obj` 全消去 → `dotnet restore` → フルビルドで確認済み。
      1 回目のビルドは `internal static` メンバー〔想定外2〕により CS0117 が 23 件出たが、
      member 単位の可視性修正のみで解消した）
- [x] `dotnet test backend/backend.slnx --no-build` の失敗が `AiStockTrading.IntegrationTests` の 8 件のみ
      （`OrderExecutionPipelineE2ETests` の 1 件を含め、いずれも Docker 不在の環境制約
      〔`Failed to connect to Docker endpoint at 'unix:///var/run/docker.sock'`〕。
      `OrderExecutionService.Tests` は 302/302 全緑）
- [x] `dotnet format backend/backend.slnx --verify-no-changes` が通る（exit 0）
- [x] `dotnet ef migrations has-pending-model-changes` が「変更なし」を返す
- [x] `list-test-projects.js --count` が 35 → 32（base `58854e3`）
- [x] `coverage-floor.json` の床（79.00%）を割らない（実測 82.25%。Release ビルド・
      `bin`/`obj`/`cov` 清掃後・32 レポート＝`list-test-projects --count` と一致）
- [x] 検査器一式が緑（`check-doc-links` / `check-adr-index-sync` / `check-cross-repo-refs` /
      `check-plan-id-qualification` / `check-trace-blocks` / `check-test-traceability` /
      `check-banned-libraries` / `check-reading-budget` / `check-consumer-endpoint-names` の
      いずれも直接終了コードで確認し EXIT:0）
- [x] `DomainLayerDependencyTests` の下限が自動追随し（5 → 4）、`RepositoryLayout.cs` /
      `DomainLayerDependencyTests.cs` を手で編集していないことを確認した
- [x] `node scripts/scripts.test.js` が緑（294 テスト。`scripts.repo.test.js` は
      `scripts.test.js` から自動読み込みされるモジュールであり単独実行では何も検査しない
      〔ファイル冒頭のコメントで確認〕ため、294 件の中に repo 固有テストが含まれている）
- [x] `pgrep -c dotnet` が 0 であることを確認した（作業終了前）

## 計画書との差異

- 差異: なし。本件は構造移送のみで振る舞いを変えていない（IADR-0259 決定7）。

## 残り 3 本のサービスへの申し送り（本 PR で踏んだ落とし穴・再利用可能な手順）

1. 🔴🔴 **`internal static` の**メンバー**単位の見落としを、クラス単位の一括置換の後に必ず別途
   確認すること。** `sed -E 's/^internal /public /'`（行頭アンカー）はクラス宣言の `internal` は
   拾えるが、インデントされた `internal static` メソッド／プロパティは拾えない。**1 回目のビルドで
   `CS0117`（メンバーが見つからない）が出たら、`grep -rn "^\s\+internal " <対象フォルダ>` で
   インデント付き `internal` を再走査し、`<Type>.<StaticMember>(` の形でテストが直接呼んでいる
   ものだけを member 単位で public 化すること。** 本 PR では `MoomooBrokerAdapter.MapState` /
   `MMApiMoomooTradeClient.{MapState,MapMarket,MapAccountType}` の 4 メンバーがこれに当たった
   （クラス自体は既に public 化されていたため、メンバーの可視性だけが独立して問題になった）。
2. 🔴 **サービス固有の統合テスト（`backend/Tests/AiStockTrading.IntegrationTests/` 等）が対象サービスの
   旧名前空間へ `using` を持っていないか、`.csproj` の参照張り替えとは別に確認すること。** 本 PR では
   `OrderExecutionPipelineE2ETests.cs` が `using OrderExecutionService.Application.Ports;` を持っており、
   `.csproj` のパス張り替えだけでは `CS0246` になった。`extern alias` を使う参照（`RiskManagementWorker`
   等）には触れる必要が無いが、**無名（global）参照のサービス自身の `using` は移送のたびに確認が要る**。
3. **インターフェースをテストの private nested class が直接実装している場合、そのインターフェースと
   シグネチャに現れるすべての型（引数・戻り値の record/enum）が芋づる式に public 化対象になる。**
   本 PR の `IMoomooTradeClient`（1 インターフェース＋9 個の関連 record/enum）がこれに当たった。
   `: I<Interface>` で終わる `private sealed class Fake...` / `Stub...` パターンをテストで grep すると
   早期に対象範囲を確定できる。
4. **`Domain/` を持たないサービスの Options 群（本サービスの `ReconciliationOptions` 等）は、元の層が
   `Application/*` である限り `Features/<集約>/` へ平らに置く。** `Infrastructure/<層>/` 直下に元々
   あった Options（w11s5 の `MonitorOptions` 等）だけが `Hosted/` へ同居する対象であり、**元の層で
   判断する**（判断6 参照）。
5. **BackgroundService の件数は `AddHostedService<>` の呼び出し箇所を `Program.cs` で全数突合して
   実測すること。** 親の指示にある実測値（本 PR では「8 件」）が実際の移送対象と食い違うことがある
   （本 PR は 6 件だった）。**食い違いに気付いても、それ自体は移送方針を変える理由にならない**
   （`Infrastructure/Persistence/` と `Hosted/` のどちらも実体を持つという結論は 6 件でも変わらない）が、
   仕様書には実測値をそのまま書き、親の前提を無批判に転記しないこと。
6. 移送前のテスト件数は、旧プロジェクトが消える前に個別 `dotnet test` で実測しておく（1〜7 本目の
   申し送りを継続して踏襲。本 PR でも有効だった）。`[Fact]`/`[Theory]` 属性の総数と `.cs` ファイル数でも
   裏を取ること。
