---
title: TradeDecisionService を単一プロジェクト＋VSA 樹形へ移送する（W11 段 4-9）
type: spec
status: approved
related_ids: [NFR, IADR-0259, IADR-0263, IADR-0264, IADR-0265]
author: endazon (with Claude Code)
created: 2026-08-29
updated: 2026-08-29
plan_refs: []
---

# 仕様書: TradeDecisionService の単一プロジェクト＋VSA 移送（W11 段 4-9）

> **11 サービス移送波の 9 本目**である。1 本目（AuditService・
> [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)）・
> 2 本目（ConfigurationService・[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md)。
> **`ConfigurationService.Client` の呼び出し元として本サービスの `Infrastructure/ExternalServices/` へ
> 5 ファイルを複製済み**）・3〜7 本目（CostControl / Backtest / MarketMonitor / Notification /
> InformationCollection。develop へマージ済み・本ブランチの base に含まれる）・8 本目
> （OrderExecutionService・隣接作業ツリー `/home/user/wt/w11s8` 読み取り専用。**develop へ未マージのため
> 本ブランチの base には含まれない**）で確定した判断の型をそのまま適用した。**新しい判断軸は生じなかった。**
> TradeDecisionService は **ConfigurationService（2 本目）と同型（`ConfigurationService.Client` の
> 呼び出し元）＋ AuditService/InformationCollectionService と同型（`Domain/` を持つ・集約は 1 つ・
> `BackgroundService` 0 件で `Hosted/` は作らない）** の組み合わせである。

## 起点

- 起点 ID: **`NFR`（無採番）**。構造移送＝メタ作業であり、`.claude/rules/traceability.md`
  「起点 ID の種別」の無採番許容ケース **2** に当たる（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)
  が確定済みの判断を継承する。環流はしない）。
- 上流: [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)（1 本目の 5 決定）・
  [IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md)（2 本目。特に決定 1
  「`.Client` 呼び出し元への複製」・決定 3「Domain を持つサービスの型」）・
  [IADR-0265](../adr/IADR-0265_domain-project-count-checker-dynamic-lower-bound.md)（検査の下限の動的化。
  本 PR は手で触っていない）・[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)（樹形・写像方針表）

## 着手前に読んだもの

- `CLAUDE.md` / `.claude/rules/traceability.md` / `.claude/rules/traceability.repo.md` /
  `docs/DEFINITION_OF_DONE.md`
- [IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)（決定 1 樹形・決定 2 共有・決定 3 依存規律・
  決定 4 Tests 統合・決定 7 振る舞い不変）/
  [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)（決定 1〜5）/
  [IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md)（決定 1
  「`.Client` 呼び出し元への複製」・決定 3「Domain/Features 切り分け基準」）/
  [IADR-0265](../adr/IADR-0265_domain-project-count-checker-dynamic-lower-bound.md)
- 先行 8 本のうち本ブランチから読める分（`.ai-context/specs/2026082*vsa*.md`）:
  [20260829_w11s4a](20260829_w11s4a_auditservice-vsa.md)（**Domain 1 集約・最重要**）/
  [20260829_w11s4b](20260829_w11s4b_configurationservice-vsa.md)（**`.Client` 呼び出し元の複製が
  本サービスに実在することの根拠・最重要**）/ [20260829_w11s4c](20260829_w11s4c_costcontrolservice-vsa.md) /
  [20260829_w11s4d](20260829_w11s4d_backtestservice-vsa.md) /
  [20260829_w11s5](20260829_w11s5_marketmonitorservice-vsa.md) /
  [20260829_w11s6](20260829_w11s6_notificationservice-vsa.md)（`Hosted/` は `BackgroundService` の
  リテラル継承限定という判定基準） /
  [20260829_w11s7_informationcollectionservice-vsa](20260829_w11s7_informationcollectionservice-vsa.md)
  （母集合走査を `backend/` 全体の裸文字列でも行う規則・internal メンバー単位の見落とし）。
  🔴 **`w11s8_orderexecutionservice` の仕様書はこのブランチには存在しない**（develop へ未マージのため
  base に含まれない）。**隣接作業ツリー `/home/user/wt/w11s8`（読み取り専用）から `git show` で
  同ファイルを直接読んだ**（`.ai-context/specs/20260829_w11s8_orderexecutionservice-vsa.md`。
  コミット `e84c335`）。全区分（Domain/Features/Infrastructure 3 区分/Hosted/Common.Abstractions）を
  持つ最も近い先例であり、`internal static` **メンバー**単位の見落とし・`IntegrationTests` の `using`
  追随という 2 つの罠の教訓を得た。

## 対象範囲

- 対象: `backend/Services/TradeDecisionService/`（8 csproj → 2 csproj）、`backend/backend.slnx`、
  `docker-compose.yml`、`scripts/k8s-local-images.sh`、`docs/how-to/local-run.md`（旧パス参照 1 箇所）、
  `backend/Tests/AiStockTrading.Architecture.Tests/RetrievalSourceVocabularyTests.cs`
  （`RetrievalSourcePolicy.cs` への `Path.Combine` が旧パスを持つ。後述「想定外」）
- 対象外: 他サービス（次の PR 以降）、`backend/Shared/` `backend/TestSupport/`（据え置き集合）。
  `ServiceClientProjectAbolishedTests.cs` の `[InlineData("TradeDecisionService.Infrastructure", false)]`
  は実在プロジェクトへの参照ではなく `IsServiceClientProject` 述語のための任意の非該当文字列例であり
  対象外（後述「想定外」3）

## 着手前の母集合の引き直し（`.claude/rules/traceability.repo.md` 規則 1〜10）

**母集合は記憶で挙げず、誤りになる側の文字列で全追跡ファイルを走査して引いた**（規則 1・2・9・10）。
走査した語は `TradeDecisionService\.(Api|Application|Domain|Infrastructure)` /
`TradeDecisionService[/\\](src|tests)` の 2 本に加え、**規則 5「軸を 1 本で終わらせない」**を適用し
`TradeDecisionService` の裸文字列で `backend/` 全体・`docs/` 全体・`.ai-context/` 全体も再走査した
（7 本目の申し送り 1 を踏襲）。

| 項目 | 実測 | 親の前提との一致 |
| --- | --- | --- |
| 移送前の .cs（src + tests） | **126**（src 77・tests 49） | 🔴 **一致**（親の前提「126」は正しかった。8 本目の訂正〔BackgroundService 8→6〕とは異なり、本 PR では `.cs` 件数の前提訂正は不要だった） |
| 移送前の csproj | **8**（src 4・tests 4。`Domain` あり） | 一致 |
| migration | **0 本**（`find backend/Services/TradeDecisionService -iname "*migration*"` 0 件） | 一致 |
| `DbContext` | **0 件**（`grep -rl DbContext backend/Services/TradeDecisionService/` 0 件） | 一致 |
| `: BackgroundService`（リテラル継承） | **0 件**（`grep -rn ": BackgroundService" src/` 0 件・`Program.cs` に `AddHostedService` 呼び出しも 0 件） | 一致（親の前提「`Hosted/` は要否を自分で数えて決める」に対する実測結果） |
| Wolverine ハンドラ（Consumer/Steps 相当） | **2 件**（`InformationCollectedHandler` / `PriceMovementDetectedHandler`。旧 `Infrastructure/Composable/Steps/`、名前空間は既に `TradeDecisionService.Infrastructure.Steps` で先行整合済み） | — |
| `TradeDecisionService` を参照する他サービスの `ProjectReference`（`backend/Services` 配下） | **0 件**（`grep -rl TradeDecisionService backend/Services --include="*.csproj" \| grep -v "^backend/Services/TradeDecisionService/"` は `ReportService.Infrastructure.Tests.csproj` の 1 件のみだが**散文コメント**であり実体参照ではない） | — |
| `backend/Tests/AiStockTrading.IntegrationTests` の参照（`.csproj` の `ProjectReference` / `.cs` の `using`） | **0 件**（`extern alias` は `RiskManagementWorker` / `ReportWorker` / `CostControlWorker` の 3 つのみ。本サービスは該当しないことを実測で確認済み） | 一致（8 本目の OrderExecutionService とは異なり、本サービスは IntegrationTests に触れる必要が無い） |
| `docker-compose.yml` / `scripts/k8s-local-images.sh` の build args | 各 1 箇所（`SERVICE_PROJECT` / `SERVICE_DLL`。両方とも本 PR で追随した） | 一致 |
| 🔴 **`backend/` 全体を裸文字列 `TradeDecisionService` で再走査して発見した「想定外」**（対象パス配下の走査では見つからない） | **3 件**。後述「想定外」1〜3 | — |
| `docs/` 配下の TradeDecisionService パス参照。**4 パターンすべてで走査**（コーディネータからの追加指示。8 本目 #593 のレビューが「先頭を `...` で省略した表記」の穴を検出したため）: ①完全な名前空間・プロジェクト名 `TradeDecisionService\.(Api\|Application\|Domain\|Infrastructure)` ②パス表記 `TradeDecisionService/src/` ③`\.\.\./` で始まる省略パス ④旧フォルダ名だけ（`Composable\|Foundation`） | **①②で 1 件**（`docs/how-to/local-run.md:75` の `cd backend/Services/TradeDecisionService/src/TradeDecisionService.Api`。生きた文書なので本 PR で是正した）。**③は 0 件**（`docs/operations/live-trading-cutover-runbook.md` の `.../Composable/Adapters/{LiveTradingGate,MMApiMoomooTradeClient,MoomooBrokerOptions}.cs` は**すべて OrderExecutionService の moomoo ブローカ関連**であり本サービス対象外。8 本目〔OrderExecutionService〕の担当）。**④は本サービスに帰属する行が 0 件**（`docs/tech/tech-requirements.md:99-100` は名前空間の一般的な説明で据え置きが正しい・`docs/api/events-and-ports.md:130` と `docs/security/security.md:94,96` は `PlatformShim.Foundation`＝**TestSupport の据え置き集合**で対象外） | — |
| `docs/tests/FR-10_*.md` 配下の TradeDecisionService 裸文字列言及 | **2 件**（`FR-10_risk-guard-core-tests.md` 1・`FR-10_risk-controls-tests.md` 1）。いずれもサービス名を**地の文で述べるだけ**（例:「取引判断サービス（TradeDecisionService）が…」）でパス参照ではないため書き換え不要（後述「想定外」に含めず据え置き理由のみ記録） | — |
| `.ai-context/adr/` 配下の `TradeDecisionService\.(Api\|Application\|Domain\|Infrastructure)` 参照 | **11 件**（8 ファイル: IADR-0017・IADR-0212・IADR-0257・IADR-0135・IADR-0122・IADR-0260・IADR-0194・README.md 索引）。**いずれも凍結記録の point-in-time の記述**（決定当時の実測・構造の引用）であり、[IADR-0261](../adr/IADR-0261_namespace-alignment-to-platform.md)・6・7 本目と同じ判断ですべて据え置いた（個別の判定根拠は後述） | — |
| `.ai-context/specs/` 配下の同パターン参照 | 実測 **44 件**（19 ファイル）。いずれも point-in-time の記録（`.claude/rules/traceability.repo.md` 除外規定）であり未更新 | — |
| `deploy/helm/.../pipeline.json` の TradeDecisionService 関連参照 | **2 件**（`TradeDecisionService.Infrastructure.Steps.PriceMovementDetectedHandler` / `...InformationCollectedHandler`）。**`Infrastructure.Steps` 名前空間は移送で変えていないため書き換え不要**（8 本目と同型の判断） | — |
| `internal` 型のうち Tests が直接参照するもの | **30 型 + 2 メンバー**（後述「`internal`→`public` の判断」） | — |

［2026-08-29 追記 / #594］🔴 **本行の当初の記載「31 型 + 3 メンバー」は誤りだった**（レビュー指摘）。
`git diff origin/develop...HEAD` の実測は **型 30 件・メンバー 2 件**であり、**後述の判断 8 の表に
挙げた型の集合とちょうど一致する**（表は正しく、この要約行の数値だけが誤っていた）。
メンバー 2 件は `FxRateSourceFactory.ResolveMaxRateAge` / `.ResolveStaleRateWarning` である。
**数え直しの結果、表と要約が一致することを確認したうえで訂正した。**
| `list-test-projects.js --count`（base `f434ce5`） | **32**（クリーンな作業ツリーで実測） | — |

### 母集合の走査で見つかった「想定外」（`backend/` 全体の裸文字列走査で発見。7 本目の申し送り 1 を適用）

1. 🔴 **`backend/Tests/AiStockTrading.Architecture.Tests/RetrievalSourceVocabularyTests.cs` が
   `Path.Combine(RepositoryLayout.Root, "backend", "Services", "TradeDecisionService", "src",
   "TradeDecisionService.Application", "Services", "RetrievalSourcePolicy.cs")` で**旧パスを直接組み立てて
   `RetrievalSourcePolicy.cs` を読んでいた。** 7 本目（InformationCollectionService）が踏んだ罠と
   **同一ファイル内の対になる 2 経路**（`CollectionAllowlistPath` は 7 本目の移送で既に新パス
   `backend/Services/InformationCollectionService/Domain/SourceAllowlist.cs` へ是正済み。今回は
   もう片方の `RetrievalPolicyPath`）。`RetrievalSourcePolicy.cs` は判断 2（後述）により
   `Features/TradeDecision/` へ移るため、新パス
   `backend/Services/TradeDecisionService/Features/TradeDecision/RetrievalSourcePolicy.cs` へ 1 行修正した
   （本文の判定ロジック・語彙そのものは不変）。
2. **`.ai-context/adr/IADR-0260_shared-kernel-for-cross-service-domain-types.md` が
   「残るクロスサービス参照は `TradeDecisionService.Application` → `RiskManagementService.Domain`」と
   述べている。** これは**現在も実在するサービス間の `ProjectReference`**（`TradeDecisionAppService.cs`
   が `RiskManagementService.Domain` の `PositionSizer` 等を使う。IADR-0017 決定・#11 由来）であり、
   本 PR で消える`Application`層はフォルダとしては`Features/TradeDecision/`へ吸収されるが、
   **参照そのもの（`ProjectReference` の実体）は移送でも変わらない**——[IADR-0260](../adr/IADR-0260_shared-kernel-for-cross-service-domain-types.md)
   自身が「Application 層・本 ADR の射程外」と明記し許容した参照であるため、**この参照を消したり
   Shared.Kernel 側へ動かしたりする判断は本 PR の射程外**である。IADR-0260 の記述自体は
   「着手時（本 ADR 起草時点）の実測」を述べる凍結記録であり、`TradeDecisionService.Application` という
   フォルダ名の文字列を指しているのではなく**層の意味**（Application＝ここでは Features）を指しているため
   書き換えない（据え置き）。**新しい csproj（`TradeDecisionService.csproj`）が
   `RiskManagementService.Domain.csproj` への `ProjectReference` を引き継ぐ**（後述「csproj の変更」）。
3. **`ServiceClientProjectAbolishedTests.cs` の `[InlineData("TradeDecisionService.Infrastructure", false)]`
   は実在プロジェクトへの参照ではない。** `IsServiceClientProject` という**純粋な文字列述語**
   （`*.Client` サフィックスの判定のみ）のテストデータであり、「`.Client` で終わらない文字列の例」として
   偶然 `TradeDecisionService.Infrastructure` という当時実在したプロジェクト名を使っているに過ぎない
   （同じ役割は `SomeService.ClientPortfolio` のような架空の文字列でも果たせる）。移送後
   `TradeDecisionService.Infrastructure` という csproj 名は無くなるが、**このテストは csproj の実在を
   検査していない**（`RepositoryLayout.ServiceProjectFiles` から実測する別の `[Fact]` と、純粋な述語ロジックを
   固定する `[Theory]` は独立している）ため書き換え不要と判断した。

## 目標樹形（実施結果）

```
backend/Services/TradeDecisionService/
├── TradeDecisionService.csproj
├── Program.cs
├── appsettings.json / appsettings.Development.json
├── Domain/                          (7 ファイル。エンティティ・値オブジェクト・純粋な評価器/パーサ)
├── Features/TradeDecision/          (23 ファイル: Ports 15 + Services 7 + State 1)
├── Common/Abstractions/             (2 ファイル: IClock / SystemClock)
├── Infrastructure/ExternalServices/ (42 ファイル: 旧 Composable/Adapters 28
│                                       + 旧 ExternalServices〔IADR-0264 決定1・Assumptions 系〕5
│                                       + 旧 Application/Adapters の安全既定実装 9)
├── Infrastructure/Steps/            (2 ファイル: InformationCollectedHandler / PriceMovementDetectedHandler。
│                                       名前空間は先行整合済みのため無変更)
└── Tests/
    └── TradeDecisionService.Tests.csproj  (49 ファイル)
```

`Infrastructure/Persistence/` `Hosted/` `Common/Exceptions/` `Common/Behaviors/` は
**実体が無いため作らなかった**（母集合の実測どおり。`BackgroundService` 0 件・`DbContext` 0 件・
例外クラス 0 件・パイプライン振る舞い 0 件を確認済み）。

## 設計（判断とその理由）

### 判断 1: 集約は 1 つ（`TradeDecision`）とし、`_Shared/` は作らない
（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定 1 の適用。新しい判断ではない）

LLM 判断オーケストレーション・採算ゲート・為替換算・スクリーニング文脈組立・前提条件解決はいずれも
「1 巡回の取引判断」という不可分な概念に属し、操作フォルダの兄弟を作る決定（3 段目のスライス分割）は
採らない（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) 決定 1）。したがって集約は 1 つとし、
`Features/TradeDecision/` 直下に平らに置いた。**集約名は「サービス名から `Service` を落とす」機械的規則を
そのまま適用した**（`TradeDecisionAppService` → `TradeDecision` と一致。基盤〔MSP〕に同名サービスは無く、
構成セクション名との矛盾も無いことを確認済み——6・7 本目と同じ確認手順）。

### 判断 2: `Domain/` と `Features/TradeDecision/` の切り分け（IADR-0264 決定 3 の適用。新しい判断ではない）

[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定 3 の基準
（**Domain ＝フレームワーク・DI・I/O に触れず業務概念そのものを表す型。ポート・アプリケーションサービス・
DTO・ストアは Features/<集約>/**）と、同決定の 🔴 注記（**移送で型の層を変えない**）をそのまま適用した。

| 元のプロジェクト | 型 | 置き場 |
| --- | --- | --- |
| `TradeDecisionService.Domain`（7 ファイル） | `DecisionAggregator` / `LlmDecision` / `MarketSessions` / `PositionEffectResolver` / `ProfitabilityGate` / `ScreeningContextPlanner` / `TradeDecisionParser`（エンティティ・値オブジェクト・純粋な評価器・パーサ） | **`Domain/`**（そのまま） |
| `TradeDecisionService.Application/Ports/`（`IClock` 以外の 15 インターフェース） | `ICurrentPriceProvider` / `IDailyPolicyProvider` / `IDailyPolicyUnconfirmedNotifier` / `IFxRateProvider` / `IFxSourceStatusNotifier` / `IHeldPositionProvider` / `ILlmCompletionClient` / `ILlmGovernanceReporter` / `ILlmUsageReporter` / `IMarketCalendar` / `IProfitabilityAssumptionsProvider` / `IRetrievalContextProvider` / `IScreeningReductionReporter` / `ISizingContextProvider` / `IWatchlistProvider` | **`Features/TradeDecision/`**（決定 3「ポートは Features」） |
| `TradeDecisionService.Application/Services/` | `DecisionOrchestrationOptions` / `DecisionOrchestrator` / `ProfitabilityGateOptions` / `RetrievalSourcePolicy` / `ScreeningContextAssembler` / `TradeDecisionAppService` / `TradeDecisionPromptBuilder`（7 ファイル） | **`Features/TradeDecision/`** |
| `TradeDecisionService.Application/State/` | `DecisionTrigger` | **`Features/TradeDecision/`** |

### 判断 3: `IClock` / `SystemClock` は `Common/Abstractions/`
（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定 3 のそのままの適用）

集約を跨いで使われ得る、I/O を持たない技術プリミティブであり、1・5・7・8 本目と同じ理由づけで
そのまま適用した。新しい判断ではない。

### 判断 4: 安全既定（no-op/placeholder）のポート実装は、本番実装と同じ `Infrastructure/ExternalServices/` に対で置く
（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) の写像方針の既定＋5・7 本目の運用上の
明確化。新しい判断ではない）

旧 `Application/Adapters/` にあった `BaseCurrencyOnlyFxRateProvider` / `NoOpCurrentPriceProvider` /
`NoOpDailyPolicyUnconfirmedNotifier` / `NoOpHeldPositionProvider` / `NoOpLlmGovernanceReporter` /
`NoOpLlmUsageReporter` / `NoOpProfitabilityAssumptionsProvider` / `NoOpRetrievalContextProvider` /
`NoOpScreeningReductionReporter`（`SystemClock` を除く 9 型）は、**旧プロジェクトが `Application` であっても**、
各ポートの本番実装（`Http*Provider` ほか・`Market*Provider` ・`Publishing*`）と同じ
`Infrastructure/ExternalServices/` へ置いた（旧プロジェクト境界ではなく、**ポートの実装であるという
型の性質**で置き場を決める。7 本目「判断 4」と同型）。

`Infrastructure/Composable/Adapters/` にあった 28 ファイル（`AssumptionsProfitabilityProvider` /
`BojFxRateSource` / `CachingFxRateSource` / `ConfigurationWatchlistProvider` / `DecisionOptionsLoader` /
`FallbackFxRateSource` / `FredFxRateSource` / `FxOptions` / `FxRateSourceFactory` /
`FxSourceStatusTracker` / `HttpDailyPolicyProvider` / `HttpHeldPositionProvider` /
`HttpLlmCompletionClient` / `HttpSizingContextProvider` / `HttpWatchlistProvider` /
`KnowledgeBaseRetrievalContextProvider` / `MarketCalendar` / `MarketDataCurrentPriceProvider` /
`MarketFxRateProvider` / `NoOpFxRateSource` / `NoOpFxSourceStatusNotifier` / `PlaceholderProviders`
/ `ProfitabilityGateOptionsLoader` / `PublishingDailyPolicyUnconfirmedNotifier` /
`PublishingFxSourceStatusNotifier` / `PublishingLlmGovernanceReporter` / `PublishingLlmUsageReporter` /
`PublishingScreeningReductionReporter`）はそのまま `Infrastructure/ExternalServices/` へ移した
（旧 namespace `TradeDecisionService.Infrastructure.Adapters` → `TradeDecisionService.Infrastructure.ExternalServices`。
判断 6 参照）。

### 判断 5: `ConfigurationService.Client` 由来の 5 ファイル（IADR-0264 決定 1 で本サービスへ既に複製済み）を
そのまま `Infrastructure/ExternalServices/` に据え置く（IADR-0264 決定 1 の帰結。新しい判断ではない）

`Infrastructure/ExternalServices/AssumptionsClientExtensions.cs` / `CachedAssumptionsProvider.cs` /
`DefaultAssumptionsProvider.cs` / `HttpAssumptionsClient.cs` / `IAssumptionsProvider.cs`
（`IAssumptionsProvider` / `IAssumptionsCacheInvalidator` / `IAssumptionsSource` の 3 インターフェースを含む）
は、[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定 1 の PR で**既に**
`backend/Services/TradeDecisionService/src/TradeDecisionService.Infrastructure/ExternalServices/`
（名前空間 `TradeDecisionService.Infrastructure.ExternalServices`）へ配置済みだった。**本 PR は
このフォルダを新樹形の `Infrastructure/ExternalServices/` へそのまま吸収するだけで、内容・名前空間
いずれも変えていない**（IADR-0264 決定1 の「結果」節「移送時にそのままフォルダごと動かせる」を実施した）。
旧 `Composable/Adapters/`（判断 4）とは異なる旧サブフォルダ由来だが、**移送後の名前空間は既に一致していた**
ため衝突・改名は発生しなかった（型名の重複も 0 件を確認済み）。

### 判断 6: Wolverine ハンドラは `Infrastructure/Steps/`（名前空間不変）
（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定 5 の適用。新しい判断ではない）

`InformationCollectedHandler` / `PriceMovementDetectedHandler` は旧 `Infrastructure/Composable/Steps/`
から `Infrastructure/Steps/` へフォルダのみ移した。名前空間は `TradeDecisionService.Infrastructure.Steps`
のまま変えていない（[IADR-0261](../adr/IADR-0261_namespace-alignment-to-platform.md) で先行整合済み）。

### 判断 7: `Hosted/` は作らない（`BackgroundService` 0 件の実測に基づく）
（6・7 本目の判定基準「`BackgroundService` のリテラル継承限定」を適用した結果、対象が無かった）

`grep -rn ": BackgroundService" src/` および `Program.cs` の `AddHostedService<>` 呼び出しを走査したが
**いずれも 0 件**だった。親の前提（「`Hosted/` の要否は自分で数えて決める」）どおり、本サービスは
`Hosted/` フォルダを作らない。

### 判断 8: `internal` → `public` は「Tests が直接参照する型・メンバー」に限る
（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定 4 の適用。
8 本目の申し送り「member 単位の見落とし」を踏まえ、クラス単位の走査とは別にメンバー単位も走査した）

移送前から `internal` だった型のうち、Tests から**コンストラクタ呼び出し・`BeOfType<T>()`・静的メンバー
呼び出し**で直接参照されていたものだけを `public` にした（DI 経由のインターフェース越しの解決は対象外）。

| 型・メンバー | 直接参照の根拠 |
| --- | --- |
| `DefaultAssumptionsProvider` | `AssumptionsClientRegistrationTests.cs` が `BeOfType<DefaultAssumptionsProvider>()` |
| `CachedAssumptionsProvider` | 同ファイルの `BeOfType<CachedAssumptionsProvider>()`・`CachedAssumptionsProviderTests.cs` の `new CachedAssumptionsProvider(...)` |
| `HttpAssumptionsClient` | `HttpAssumptionsClientTests.cs` が `new HttpAssumptionsClient(...)` |
| `PlaceholderLlmCompletionClient` | `LlmCompletionClientSelectionTests.cs` が `BeOfType<PlaceholderLlmCompletionClient>()` |
| `PlaceholderDailyPolicyProvider` | `DailyPolicyProviderSelectionTests.cs` が `BeOfType<PlaceholderDailyPolicyProvider>()` |
| `PlaceholderSizingContextProvider` | `SizingContextProviderSelectionTests.cs` が `BeOfType<PlaceholderSizingContextProvider>()` |
| `HttpHeldPositionProvider` | `HttpHeldPositionProviderTests.cs` が `new HttpHeldPositionProvider(...)` / `BeOfType<...>()` |
| `ConfigurationWatchlistProvider` | `WatchlistProviderSelectionTests.cs` が `BeOfType<ConfigurationWatchlistProvider>()` |
| `BojFxRateSource` | `BojFxRateSourceTests.cs` が `new BojFxRateSource(...)`・`FxCalendarIndependenceTests.cs` が `typeof(BojFxRateSource)` |
| `CachingFxRateSource` | `CachingFxRateSourceTests.cs` が `new CachingFxRateSource(...)` |
| `PublishingDailyPolicyUnconfirmedNotifier` | `PublishingDailyPolicyUnconfirmedNotifierTests.cs` が `new PublishingDailyPolicyUnconfirmedNotifier(...)` |
| `MarketDataCurrentPriceProvider` | `MarketDataCurrentPriceProviderTests.cs` の `new MarketDataCurrentPriceProvider(...)`・`CurrentPriceProviderSelectionTests.cs` の `BeOfType<...>()` |
| `AssumptionsProfitabilityProvider` | `AssumptionsProfitabilityProviderTests.cs` が `new AssumptionsProfitabilityProvider(...)` |
| `FxRateSourceFactory`（クラス） | `FxRateSourceFactoryTests.cs` が `FxRateSourceFactory.Create(...)` を静的呼び出し |
| `FxRateSourceFactory.ResolveMaxRateAge` / `.ResolveStaleRateWarning`（🔴 メンバー単位） | 同ファイルおよび `CachingFxRateSourceTests.cs` が `FxRateSourceFactory.ResolveMaxRateAge(...)` / `.ResolveStaleRateWarning(...)` を直接呼び出し |
| `PublishingFxSourceStatusNotifier` | `PublishingFxSourceStatusNotifierTests.cs` が `new PublishingFxSourceStatusNotifier(...)` |
| `FredFxRateSource` | `FredFxRateSourceTests.cs` の `new FredFxRateSource(...)`・`FxCalendarIndependenceTests.cs` の `typeof(FredFxRateSource)` |
| `MarketFxRateProvider` | `FxWiringTests.cs` が `BeOfType<MarketFxRateProvider>()` |
| `HttpWatchlistProvider` | `WatchlistProviderSelectionTests.cs` / `HttpWatchlistProviderTests.cs` が `BeOfType` / `new` |
| `MarketCalendar` | `MarketCalendarTests.cs` の `new MarketCalendar(...)`・`FxCalendarIndependenceTests.cs` の `typeof(MarketCalendar)` |
| `FxSourceStatusTracker` | `FxSourceStatusTrackerTests.cs` が `new FxSourceStatusTracker()` |
| `PublishingLlmUsageReporter` | `PublishingLlmUsageReporterTests.cs` が `new PublishingLlmUsageReporter(...)` |
| `KnowledgeBaseRetrievalContextProvider` | 同名 Tests の `new(...)`・`RetrievalContextProviderSelectionTests.cs` の `BeOfType<...>()` |
| `FallbackFxRateSource` | `FallbackFxRateSourceTests.cs` が `new FallbackFxRateSource(...)` |
| `NoOpFxRateSource` | `FxWiringTests.cs` / `CachingFxRateSourceTests.cs` が `BeOfType<NoOpFxRateSource>()` |
| `HttpSizingContextProvider` | `HttpSizingContextProviderTests.cs` が `new HttpSizingContextProvider(...)` |
| `FxOptions` | `FxRateSourceFactoryTests.cs` が `new FxOptions { ... }` を多数箇所で直接初期化 |
| `BojFxOptions`（🔴 CS0053 連鎖。想定外 4 参照） | 直接参照ではなく、`FxOptions.Boj` が `public BojFxOptions` を公開するため、`FxOptions` の public 化に連鎖 |
| `FredFxOptions` | `FxRateSourceFactoryTests.cs` が `new FredFxOptions { ApiKey = "key" }` を直接初期化 |
| `HttpDailyPolicyProvider` | `HttpDailyPolicyProviderTests.cs` が `new HttpDailyPolicyProvider(...)` / `BeOfType` |
| `HttpLlmCompletionClient` | `HttpLlmCompletionClientTests.cs` / `HttpLlmCompletionClientFallbackBanTests.cs` が `new HttpLlmCompletionClient(...)` |

`internal` のまま据え置いたもの（Tests から直接参照されない）:
`PublishingScreeningReductionReporter`（テストは `IScreeningReductionReporter` の private fake 実装のみ）・
`PublishingLlmGovernanceReporter`（同型に `ILlmGovernanceReporter` の private fake）・
`NoOpFxSourceStatusNotifier`（同型に `IFxSourceStatusNotifier` の private fake。`PublishingFxSourceStatusNotifier`
とは異なりこちらは直接参照が無い）・`DecisionOptionsLoader` / `ProfitabilityGateOptionsLoader` /
`PlaceholderProviders`（`PlaceholderLlmCompletionClient` 等 3 クラスの入れ物ファイル。3 クラスは public 化・
`WarnOnce` メンバーは未参照のため internal のまま）・`CachedAssumptionsProvider.Unresolved`／
`AssumptionsClientExtensions.DefaultCacheTtl`（🔴 メンバー単位。テストからの直接参照なし）。
`InternalsVisibleTo` は新設していない（旧 4 csproj にあった計 3 エントリはすべて削除した）。

### 想定外 4: `FxOptions` の public 化が `BojFxOptions` へ連鎖した（CS0053）

`FxOptions`（`FxRateSourceFactoryTests.cs` が直接構築するため public 化対象）は
`public BojFxOptions Boj { get; set; }` を持つ。`FredFxOptions` は既にテストから直接参照されるため
公開対象だったが、**`BojFxOptions` はテストからの直接参照が無いにもかかわらず**、`FxOptions` の
public 化に伴う CS0053（公開メンバーが非公開の型を公開している）連鎖として public 化した
（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定 4 の
「public 化が別の型へ連鎖する場合…その連鎖もこの決定の範囲に含める」を適用。8 本目の
`IMoomooTradeClient` 関連 record/enum の連鎖と同型）。

### 判断 9: 名前空間の書き換え

[IADR-0261](../adr/IADR-0261_namespace-alignment-to-platform.md) で `TradeDecisionService.*` へ
先行整合済み。フォルダ移動に伴い変えたのは以下のみ。

- `TradeDecisionService.Application.{Ports,Services,State}`（`IClock` は除く） →
  `TradeDecisionService.Features.TradeDecision`
- `TradeDecisionService.Application.Ports.IClock` / `TradeDecisionService.Application.Adapters.SystemClock` →
  `TradeDecisionService.Common.Abstractions`
- `TradeDecisionService.Application.Adapters`（`IClock`/`SystemClock` 以外の 9 安全既定実装） →
  `TradeDecisionService.Infrastructure.ExternalServices`
- `TradeDecisionService.Infrastructure.Adapters` → `TradeDecisionService.Infrastructure.ExternalServices`
  （判断 5 の既存 `ExternalServices` フォルダへ合流。型名の重複は 0 件）
- `TradeDecisionService.Infrastructure.Steps` は不変（判断 6）。`TradeDecisionService.Infrastructure.ExternalServices`
  （判断 5 の 5 ファイル）も不変。`TradeDecisionService.Domain` も不変。

## csproj の変更（新設 `TradeDecisionService.csproj`）

旧 4 本の `ProjectReference` を統合し、パスを新ルートからの相対に付け替えた。

- `RiskManagementService.Domain.csproj`（判断 1・想定外 2 のとおり、Application 層由来の
  クロスサービス参照をそのまま引き継ぐ）: `..\RiskManagementService\src\RiskManagementService.Domain\RiskManagementService.Domain.csproj`
  （旧 `TradeDecisionService.Application.csproj` からの相対 `..\..\..\RiskManagementService\...` より
  1 階層浅くなる。8 本目・3 本目と同型の「未移送サービスへの相対パス短縮」）
- `AiStockTrading.Shared.Contracts` / `AiStockTrading.Shared.KnowledgeBase` /
  `AiStockTrading.Shared.Infrastructure` / `AiStockTrading.TestSupport.PlatformShim`:
  いずれも `..\..\Shared\...` / `..\..\TestSupport\...`（MarketMonitorService 等と同型の 2 階層）
- `WolverineFx.RabbitMQ` / `WolverineFx.RuntimeCompilation` / `Serilog.AspNetCore` /
  `OpenTelemetry.Extensions.Hosting` の `PackageReference` は旧 `.Api`/`.Infrastructure` から統合
- `FrameworkReference Include="Microsoft.AspNetCore.App"` は Web SDK に含意されるため明示的な
  再宣言はしない（`Sdk="Microsoft.NET.Sdk.Web"` を維持）
- `InternalsVisibleTo` は新設しない（旧 3 エントリを廃止）
- `<Compile Remove="Tests/**" />` ＋ `<Content Remove="Tests/**" />` ＋ `<None Remove="Tests/**" />` を追加

Tests 側 `TradeDecisionService.Tests.csproj` は旧 4 テストプロジェクトの `PackageReference` を統合し、
`ProjectReference` は `..\TradeDecisionService.csproj`（本体）＋
`..\..\..\TestSupport\AiStockTrading.TestSupport.{PlatformShim,Messaging,Metrics}.csproj`
（旧 `Infrastructure.Tests` の参照を引き継ぐ。`Metrics` は NFR-07/#287/IADR-0255 の業務メトリクス観測用）。

## 名前空間の実装解決に関する事故（本 PR 固有。4〜8 本目の申し送りに無い新規の罠）

4〜8 本目の最重要申し送り「1 回目のビルドは全容を報告しない」を踏まえ、最初から
`dotnet build-server shutdown` → `bin`/`obj` 全消去 → `dotnet restore` → `dotnet build --no-restore` の
手順で検証したが、**その手順を踏んでもなお 3 種類の「using 欠落」相当の事故が発生した**。

1. 🔴 **同一ファイル内の「companion 型」（インターフェースと同じファイルに定義された、インターフェースとは
   別名の型）が、機械的な走査から漏れた。** `ILlmUsageReporter.cs` は `ILlmUsageReporter` インターフェースと
   `LlmUsage`（record struct）を同じファイル・同じ名前空間に持つが、Tests から `LlmUsage` **だけ**を使い
   `ILlmUsageReporter` 自体は使わないファイル（`PublishingLlmUsageReporterTests.cs`）があり、
   「ポート名（`I` 始まり）のリストで判定する」走査では検出できなかった（CS0246）。
   **同様の companion 型は `Features/TradeDecision/` に 8 組ある**（`OrchestratedDecision` /
   `DecisionTriggerKind` / `DailyPolicy` / `LlmUsage` / `TradeCostAssessment` / `RetrievedContext` /
   `SizingContext` / `WatchedSymbol`）。**インターフェース名だけでなく、同じ移送先フォルダの全公開型名で
   走査すること。**
2. 🔴 **型エイリアス using（`using X = 旧名前空間;` / `using X = 旧名前空間.Y;`）は、単純な
   `using 旧名前空間;` の文字列一致走査に掛からない。** `using Orchestrated = TradeDecisionService.Application.Services;`
   （`LlmPurposeWiringTests.cs`）・`using AppSvc = TradeDecisionService.Application.Services.TradeDecisionAppService;`
   （4 ファイル: `InformationCollectedConsumerTests.cs` / `TradeDecisionServiceTests.cs` /
   `ScreeningContextDegradationTests.cs` / `PriceMovementDetectedConsumerTests.cs` / `Infrastructure/Steps/`
   の 2 ハンドラファイル）は、`grep -n "^using TradeDecisionService\.Application\."`
   （行頭が `using ` そのもの）では拾えるが、**`=` を含む行を除外するような走査だと見落とす**。
   **`grep -rn "TradeDecisionService\.(Application|Infrastructure\.Adapters)"`（行頭アンカーなし・
   using/エイリアス両方を拾う形）で最終確認すること。**
3. **テスト本文の完全修飾名（`TradeDecisionService.Application.Adapters.NoOpHeldPositionProvider` 等）は
   using 文の書き換えでは直らない。** `HttpHeldPositionProviderTests.cs` の `BeOfType<TradeDecisionService.Application.Adapters.NoOpHeldPositionProvider>()`
   がこれに当たった（5・7 本目の「完全修飾名の部分参照」と同型の罠が、本 PR でも 1 件再発した）。

いずれも `dotnet build backend/backend.slnx --no-restore` の**1 回のフルビルドで全件 CS0246/CS0234 として
検出できた**（8 本目の `internal static` メンバーのように 2 回目まで隠れる罠ではない）。合計 39 件のビルド
エラーを 4 ラウンドで解消し、最終的に 0 Warning / 0 Error に到達した。

## Tests 統合（4 → 1）で変えていないことの証跡

**中身は 1 行も変えていない**（`git mv` のみ・変更は namespace 宣言・using の書き換え、および
「想定外」1・4 と直前の「名前空間の実装解決に関する事故」1〜3 で述べた最小限の実務対応に限定。
テスト本体のロジック・アサーションは無変更——完全修飾名の型パス書き換え〔事故3〕を含め、
アサーションの対象・期待値は 1 つも変えていない）。

### テストダブルの重複定義（規則 2 に基づき確認）

旧 4 テストプロジェクトに `class Fake*` / `class Stub*` / `class Recording*` / `class Throwing*` の
重複名が無いか `grep -rhoE "class \w+"` で全数走査し、トップレベル `public class` の名前重複が
0 件であることを確認した（`AssumptionsTestDoubles.cs` の共有型を除きすべて各テストクラス内の
`private sealed class` としてネストされているため、Tests 統合後も CS0101 は発生しなかった）。

### テスト件数の突合（移送前後を実測。削っていないことの証跡）

移送前は旧テストプロジェクトが存在する段階で個別 `dotnet test --configuration Release` を実行して
実測した（本 PR 着手直後・クリーンな作業ツリー）。

| テストアセンブリ | 移送前 | 移送後 |
| --- | ---: | ---: |
| `TradeDecisionService.Api.Tests` | 51 | — |
| `TradeDecisionService.Application.Tests` | 101 | — |
| `TradeDecisionService.Domain.Tests` | 98 | — |
| `TradeDecisionService.Infrastructure.Tests` | 287 | — |
| **`TradeDecisionService.Tests`** | — | **537** |
| 合計 | **537** | **537** |

51 + 101 + 98 + 287 = 537 = 移送後の合格件数と**完全一致**。減った件・増えた件は 0。

### `[Fact]`/`[Theory]` 属性の総数（裏取り）

`grep -rhoE '^\s*\[(Fact|Theory)' Tests/*.cs` = **428**（`[Fact]` 390・`[Theory]` 38）。
移送前の同じ走査（旧 4 テストプロジェクト合計）も **428**（`[Fact]` 390・`[Theory]` 38）で完全一致。

### `.cs` ファイル数

移送前 126（src 77・tests 49）／移送後 **126**（`git mv` のみで増減なし。Tests は 1 プロジェクトへ
統合されたがファイル自体は減っていない）。

## `list-test-projects.js --count` の突合

- 移送前: **32**（base `f434ce5`）
- 移送後: **29**（予測。実測は「検証（実施後）」節で確定する）
- 差分: **-3**（旧 4 テストプロジェクト → 新 1 テストプロジェクトの差分と一致）

## `has-pending-model-changes`

対象外。本サービスは `DbContext` を 0 件持つ（移送前後とも `grep -rl DbContext` = 0）。
`TradeDecisionService.csproj` は `Microsoft.EntityFrameworkCore.Design` を参照しない。
黙って省略せず、この節に実測で記録する（DoD の指示どおり。6・7 本目と同型の実測構造）。

## `DomainLayerDependencyTests` の下限（[IADR-0265](../adr/IADR-0265_domain-project-count-checker-dynamic-lower-bound.md)。
手で触っていない）

`RepositoryLayout.cs` / `DomainLayerDependencyTests.cs` / `DomainSourceDependencyTests.cs` は
**本 PR で 1 行も変更していない**。`UnmigratedServicesWithDomainProjectCount` は
`backend/Services/<Svc>/src/` の実在と `.Domain` 接尾辞ディレクトリの実在を実ツリーから動的に数えるため、
TradeDecisionService の移送（`src/TradeDecisionService.Domain/` の消滅）により**自動的に** 1 件減る。

実測（`backend/Services/*/src/` を列挙し `.Domain` 接尾辞ディレクトリの有無を確認）:
移送前 **4**（OrderExecutionService・ReportService・RiskManagementService・TradeDecisionService。
InformationCollectionService・MarketMonitorService・ConfigurationService・CostControlService・
BacktestService・AuditService・NotificationService は既に移送済みのため対象外）→
移送後 **3**（OrderExecutionService・ReportService・RiskManagementService）。

## IADR を作らない判断

**本 PR では新しい IADR（`IADR-0266`）を作らない。** [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)・
[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定 1・決定 3・
[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) の写像方針表を参照するだけで、
本 PR の判断 1〜9 すべてが機械的に導けたためである（3〜8 本目と同じ判断）。

- 判断 1〜4・6〜9 はいずれも先行 IADR・先行 PR の**そのままの適用**。
- 判断 5 は IADR-0264 決定 1 が**本 PR の対象を名指しで予告していた**帰結の実施であり、新しい判断ではない。
- 想定外 1〜3（他ユニットテストの旧パス直接参照・凍結記録の据え置き判定・純粋述語テストデータの
  非該当判定）と想定外 4（`FxOptions`→`BojFxOptions` の CS0053 連鎖）はいずれも**移送手順上の実務**であり、
  樹形・可視性・依存規律に関する**新しい設計判断ではない**（8 本目の `IMoomooTradeClient` 連鎖と同型）。

## 受け入れ基準

- [x] `dotnet build backend/backend.slnx` が 0 warning / 0 error で通る（`dotnet build-server shutdown` →
      `bin`/`obj` 全消去 → `dotnet restore` → `dotnet build --no-restore` で確認済み。1〜4 ラウンド目は
      「名前空間の実装解決に関する事故」1〜3 により計 39 件のビルドエラーが出たが、いずれも移送手順上の
      実務対応で解消し、最終ラウンドは 0 Warning / 0 Error）
- [x] `dotnet test backend/backend.slnx --no-build` の失敗が `AiStockTrading.IntegrationTests` の 8 件のみ
      （実測: 全 29 プロジェクト中、失敗は `AiStockTrading.IntegrationTests` の 8 件〔Docker 不在〕のみ。
      他 28 プロジェクトすべて緑。`TradeDecisionService.Tests` は 537/537）
- [x] `dotnet format backend/backend.slnx --verify-no-changes` が通る（exit 0。実測 EXIT:0）
- [x] `has-pending-model-changes` は対象外（DbContext 0 件。実測証跡は上記に記録済み。
      `dotnet ef migrations has-pending-model-changes` は
      「doesn't reference Microsoft.EntityFrameworkCore.Design」で exit 1 になることを確認した）
- [x] `list-test-projects.js --count` が 32 → 29（base `f434ce5`。実測どおり）

  ［2026-08-29 追記 / #594］develop（`bd39cbd`＝OrderExecutionService #593 の移送を含む）
  を取り込んだため、**PR の最終状態では 29 → 26 になる**（#593 がテストプロジェクトを 3 本
  減らしたぶん、移送前後とも 3 少ない）。**どちらも正しく、基準が違うだけである。**
- [x] `coverage-floor.json` の床を割らない（実測 82.50%〔16505/20006 行〕・floor 79.00%・
      レポート 29 件＝`list-test-projects --count` と一致。`rm -rf cov` の後に取り直した）
- [x] 検査器一式（`scripts/README.md` 掲載分）が緑（`check-doc-links` / `check-adr-index-sync` /
      `check-cross-repo-refs` / `check-plan-id-qualification` / `check-trace-blocks` /
      `check-test-traceability` / `check-banned-libraries` / `check-reading-budget` /
      `check-consumer-endpoint-names` / `validate-pipeline-config --self-test` / 実データ本走のいずれも
      直接終了コードで確認し EXIT:0）
- [x] `DomainLayerDependencyTests` の下限が自動追随し（4 → 3）、`RepositoryLayout.cs` /
      `DomainLayerDependencyTests.cs` / `DomainSourceDependencyTests.cs` を手で編集していないことを
      `git diff --stat` で確認した（差分なし）。`AiStockTrading.Architecture.Tests` は 88/88 全緑
- [x] `node scripts/scripts.test.js` が緑（294 テスト。`REQUIRE_REPO_TESTS=1` を付けた実行でも同じ
      294 件・EXIT:0 を確認した）
- [x] `pgrep -c dotnet` が終了時点で 0 である（`dotnet build-server shutdown` 後に実測 0）

## 計画書との差異

- 差異: なし。本件は構造移送のみで振る舞いを変えていない（IADR-0259 決定 7）。

## 残り 2 本のサービスへの申し送り（Report・RiskManagement）

1. 🔴 **`ConfigurationService.Client` 廃止（IADR-0264 決定 1）の複製先だったサービスは、複製された
   ファイル群が既に新樹形のフォルダ名・名前空間で置かれていることを確認してから着手すること。**
   本 PR では `Infrastructure/ExternalServices/` が事前に存在し、旧 `Composable/Adapters/` との
   フォルダ名衝突・namespace 衝突が起きないかを確認する作業が必要だった（衝突は無かった。判断 5 参照）。
   CostControlService（3 本目）は既に完了済みのため、この申し送りは**残り 2 本には該当しない**
   （RiskManagementService・ReportService は `ConfigurationService.Client` の呼び出し元ではない）。
2. 🔴 **他ユニットのテスト（`backend/Tests/AiStockTrading.Architecture.Tests/` 等）が、移送対象サービスの
   ソースファイルを `Path.Combine` で直接読んでいないか、`backend/` 全体をサービス名の裸文字列で
   再走査して必ず確認すること。** 本 PR では `RetrievalSourceVocabularyTests.cs` が
   `RetrievalSourcePolicy.cs` の絶対パスをハードコードしており、通常の走査（`TradeDecisionService\.`
   接頭辞つき）では見つからなかった（7 本目の同型の罠が「対になる 2 経路」で再発した実例）。
3. **`internal` クラスの public 化が、直接参照されていない**構成用の入れ子型**へ CS0053 連鎖することがある。**
   本 PR の `FxOptions.Boj`（`BojFxOptions` 型）がこれに当たった——`FxOptions` 自体はテストから
   直接構築されるため public 化対象だが、`BojFxOptions` はテストから一度も直接参照されない。
   **public 化の対象を決めるときは、対象クラスの public プロパティ/フィールドの「型」も辿り、
   その型が internal のままなら連鎖的に public 化すること。**
4. **`.ai-context/adr/` の凍結記録がサービス間の `ProjectReference` の実在を述べている場合、
   移送でその参照自体が消えるわけではないことを確認してから「据え置き」と判定すること。**
   本 PR では IADR-0260 が「`TradeDecisionService.Application` → `RiskManagementService.Domain`」という
   クロスサービス参照を記録しており、**フォルダ名は移送で変わるが参照そのものは変わらない**——
   凍結記録の文字列は書き換えないが、**新しい csproj が実際にその `ProjectReference` を引き継いでいる
   ことを実測で確認する**（据え置きは「無視してよい」という意味ではない）。
5. 移送前のテスト件数・`[Fact]`/`[Theory]` 属性数は、旧プロジェクトが消える前に個別 `dotnet test` /
   `grep` で実測しておく（1・4〜8 本目の申し送りを継続して踏襲）。
6. 検証手順そのものの落とし穴（bin/obj 全消去後の restore 未経由・カバレッジ中断の偽陽性・パイプでの
   終了コード隠蔽・空ディレクトリの偽陽性・`internal static` メンバー単位の見落とし）は 4〜8 本目の
   申し送りがすべて有効であり、本 PR でも同じ手順（restore を挟む・`cov/` を作り直す・直接終了コードで
   確認する・`grep -rnE '^\s+internal '` を別走査する）を踏襲する。
7. 🔴 **新規の罠（本 PR で初めて発生）: 同一ファイル内の companion 型（インターフェースと同居する
   record/enum 等）を、ポート名（`I` 始まり）の走査だけでは検出できない。** `LlmUsage`（`ILlmUsageReporter`
   と同居）がこの実例。**移送先フォルダの全公開型名（インターフェースだけでなく record/enum/companion
   class を含む）を列挙してから、Tests 側の `using` 要否を判定すること。**
8. 🔴 **新規の罠: 型エイリアス using（`using X = 旧名前空間[.型];`）は行頭 `using 旧名前空間;` の
   単純一致走査では見つからない。** `using Composable = TradeDecisionService.Infrastructure;`
   （11 ファイルに実在。実害は無かった——`Infrastructure` は新樹形でも実在する親名前空間であるため
   解決自体は成立し、コード本文でも `Composable.X` の形では 1 箇所も使われていなかった。ただし
   `using Orchestrated = ...Application.Services;` / `using AppSvc = ...Application.Services.TradeDecisionAppService;`
   の 2 種は実際に `CS0234` を起こした）。**`grep -n "TradeDecisionService\."`（行頭アンカーなし）で
   using 行・エイリアス行の両方を洗い出してから判定すること。**
9. **移送作業で自作した一括置換スクリプト（sed/perl/node）は、C# の `using` ディレクティブ（ファイル先頭の
   import）と `using var x = ...;` / `using (var x = ...)`（ローカル変数の破棄可能パターン）を
   区別しない実装だと事故る。** 本 PR では「using で始まり `;` で終わる行を全文重複排除する」node
   スクリプトが、複数のテストメソッドに正しく重複して存在する `using var host = await StartHostAsync();`
   等を「重複 import」と誤認し、2 個目以降を削除した（15 ファイル・CS0103 多発）。git の旧パスから
   再構成して復旧した。**重複排除は「ファイル先頭の連続する using ブロック」に限定し、`using var` /
   `using (` の形は対象から除外すること。** 復旧後は `re.findall(r'^\s*using \(?var \w', ...)` で
   移送前後の出現数が完全一致することを機械的に確認した（0 件の不一致）。
