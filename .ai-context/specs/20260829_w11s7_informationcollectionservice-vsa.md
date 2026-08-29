---
title: InformationCollectionService を単一プロジェクト＋VSA 樹形へ移送する（W11 段 4-7）
type: spec
status: approved
related_ids: [NFR, IADR-0259, IADR-0263, IADR-0264, IADR-0265]
author: endazon (with Claude Code)
created: 2026-08-29
updated: 2026-08-29
plan_refs: []
---

# 仕様書: InformationCollectionService の単一プロジェクト＋VSA 移送（W11 段 4-7）

> **11 サービス移送波の 7 本目**である。1 本目（AuditService・
> [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)）・
> 2 本目（ConfigurationService・[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md)）・
> 3 本目（CostControlService・[20260829_w11s4c](20260829_w11s4c_costcontrolservice-vsa.md)）・
> 4 本目（BacktestService・[20260829_w11s4d](20260829_w11s4d_backtestservice-vsa.md)）・
> 5 本目（MarketMonitorService・[20260829_w11s5](20260829_w11s5_marketmonitorservice-vsa.md)。develop へマージ済み・#590）・
> 6 本目（NotificationService・隣接作業ツリー `/home/user/wt/w11s6` 読み取り専用。
> `.ai-context/specs/20260829_w11s6_notificationservice-vsa.md`。**着手時点で develop へ未マージ**〔`origin/develop`
> は本ブランチの base〔`35b330a`・#590〕と一致しており、乖離は無かった〕）で確定した判断の型をそのまま適用する。
> **新しい判断軸は生じなかった。** InformationCollectionService は **AuditService（1 本目）と同型
> （`Domain/` を持つ・集約は 1 つ）＋ NotificationService（6 本目）と同型（`Hosted/` の要否判定が
> `BackgroundService` のリテラル継承で決まる）** の組み合わせであり、両者の決定をそのまま適用した。

## 起点

- 起点 ID: **`NFR`（無採番）**。構造移送＝メタ作業であり、`.claude/rules/traceability.md`
  「起点 ID の種別」の無採番許容ケース **2** に当たる（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)
  が確定済みの判断を継承する。環流はしない）。
- 上流: [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)（1 本目の 5 決定。
  **`Domain/` を持つサービスの集約 1 つの型**）・[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md)
  決定3（`Domain/` を持つサービスでの型の振り分け基準）・[IADR-0265](../adr/IADR-0265_domain-project-count-checker-dynamic-lower-bound.md)
  （検査の下限の動的化）・[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)（樹形・写像方針表）

## 着手前に読んだもの

- `CLAUDE.md` / `.claude/rules/traceability.md` / `.claude/rules/traceability.repo.md` /
  `docs/DEFINITION_OF_DONE.md`
- [IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)（決定1 樹形・決定2 共有・決定3 依存規律・
  決定4 Tests 統合・決定7 振る舞い不変）/
  [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)（決定1〜5）/
  [IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md)（決定3: Domain/Features 切り分け基準）/
  [IADR-0265](../adr/IADR-0265_domain-project-count-checker-dynamic-lower-bound.md)
- [20260829_w11s4a](20260829_w11s4a_auditservice-vsa.md)（**Domain 1 集約・最重要**）/
  [20260829_w11s4b](20260829_w11s4b_configurationservice-vsa.md) /
  [20260829_w11s4c](20260829_w11s4c_costcontrolservice-vsa.md) /
  [20260829_w11s4d](20260829_w11s4d_backtestservice-vsa.md) /
  [20260829_w11s5](20260829_w11s5_marketmonitorservice-vsa.md)（Domain＋Hosted の型・空ディレクトリの偽陽性・
  IADR リンク是正・パイプでの終了コード隠蔽の申し送り）
- **隣接作業ツリー `/home/user/wt/w11s6`（読み取り専用・6 本目 NotificationService）**の
  `.ai-context/specs/20260829_w11s6_notificationservice-vsa.md`（**`Hosted/` は `BackgroundService` の
  リテラル継承限定**という最重要申し送り・restore を挟む手順・カバレッジ中断の偽陽性の申し送りはここで確認した）
- 基盤の実物は本 PR では再確認していない（2〜6 本目が Domain/Features の基準を確定済みのため）

## 対象範囲

- 対象: `backend/Services/InformationCollectionService/`（8 csproj → 2 csproj）、`backend/backend.slnx`、
  `docker-compose.yml`、`scripts/k8s-local-images.sh`、
  `backend/Tests/AiStockTrading.Architecture.Tests/RetrievalSourceVocabularyTests.cs`（旧パス参照 1 箇所。後述「想定外」）
- 対象外: 他サービス（次の PR 以降）、`backend/Shared/` `backend/TestSupport/`（据え置き集合）

## 着手前の母集合の引き直し（`.claude/rules/traceability.repo.md` 規則1〜10）

**母集合は記憶で挙げず、誤りになる側の文字列で全追跡ファイルを走査して引いた**（規則1・2・9・10）。
走査した語は `InformationCollectionService\.(Api|Application|Domain|Infrastructure)` /
`InformationCollectionService/(src|tests)` の 2 本。

| 項目 | 実測 |
| --- | --- |
| 移送前の .cs（src + tests） | **69**（src 39・tests 30。タスク文の前提と一致） |
| 移送前の csproj | **8**（src 4・tests 4。タスク文の前提と一致） |
| migration | **0 本**（`find ... -iname "*migration*"` 0 件） |
| `DbContext` | **0 件**（`grep -rl DbContext` 0 件） |
| `BackgroundService`（リテラル継承） | **1 件**（`CollectionPollingService`。タスク文の前提と一致） |
| Wolverine ハンドラ（Consumer/Steps 相当） | **0 件**（`grep -rl "Wolverine\|IMessageBus\|IConsumer"` は Program.cs・Hosted 配下のみで、
  購読ハンドラクラスは無い。本サービスは `InformationCollected` を**発行するだけ**で購読しない。
  `Infrastructure/Steps/` は**作らない**） |
| `InformationCollectionService` を参照する他サービスの `ProjectReference` | **0 件** |
| `InformationCollectionService` を参照する `backend/Tests` 配下の `ProjectReference` / `extern alias` | **0 件**
  （`AiStockTrading.IntegrationTests.csproj` の `extern alias` は `RiskManagementWorker` / `ReportWorker` /
  `CostControlWorker` の 3 つのみ。本サービスは該当しないことを実測で確認済み） |
| `deploy/helm/.../pipeline.json` の InformationCollectionService 関連参照 | 0 件（対象外） |
| `docker-compose.yml` / `scripts/k8s-local-images.sh` の build args | 各 1 箇所（`SERVICE_PROJECT` / `SERVICE_DLL`。
  両方とも本 PR で追随した） |
| `docs/` 配下の InformationCollectionService パス参照 | **0 件** |
| `.ai-context/adr/` 配下の同パターン参照 | **2 件**（`README.md`（IADR-0176 索引要約の引用文）・
  `IADR-0176_run-once-authorization-and-cronjob-token.md` 本文。いずれも `InformationCollectionService.Api` を
  **当時「`AddAiStockTradingAuth` を呼んでいなかった」という凍結された事実の引用として述べる散文**であり、
  ファイルパスへのリンクではない。書き換えると当時の記述と食い違う——[IADR-0261](../adr/IADR-0261_namespace-alignment-to-platform.md)
  が「移設の由来を述べるコメントは書き換えない」とした判断・6 本目の同種判断をそのまま適用し、**据え置いた**） |
| `.ai-context/specs/` 配下の同パターン参照 | 実測 **7 件**（4 ファイル）。**いずれも point-in-time の記録**
  （`.claude/rules/traceability.repo.md` 除外規定）であり未更新。内訳: `20260803_354_wolverine-migration.md`（1 件）・
  `20260718_kb-save-deploy-optin.md`（1 件）・`20260828_w9f1_architecture-tests-dual-inspection.md`（1 件）・
  `20260807_456_run-once-authorization.md`（3 件） |
| **`backend/Tests/` 配下の InformationCollectionService パス参照**（母集合を `backend/` 全体へ拡張して発見。後述「想定外」） | **1 件**
  （`AiStockTrading.Architecture.Tests/RetrievalSourceVocabularyTests.cs` が `Path.Combine` で
  旧パス `src/InformationCollectionService.Domain/SourceAllowlist.cs` を直接組み立てていた） |
| `internal` 型のうち Tests が直接参照するもの | **24 型**（後述「`internal`→`public` の判断」） |
| `list-test-projects.js --count` | 移送前 **37**（クリーンな作業ツリーで実測。**タスク文の前提「35」は前提の
  実測値が古い**——本ブランチの base 時点で既に 5 本〔Audit/Configuration/CostControl/Backtest/MarketMonitor〕が
  移送済みであるため、無採番の初期値からの差分が積み上がっている。6 本目 NotificationService の申し送り1と
  同型の drift であり、`git log HEAD..origin/develop --oneline` は **0 件**（base は最新）であることを
  着手前に確認済み——drift の原因はタスク文の記載時点が古いだけで、base 自体の世代遅れではない） |

### 母集合の走査で見つかった「想定外」

1. 🔴 **`backend/Tests/AiStockTrading.Architecture.Tests/RetrievalSourceVocabularyTests.cs` が
   `Path.Combine(RepositoryLayout.Root, "backend", "Services", "InformationCollectionService", "src",
   "InformationCollectionService.Domain", "SourceAllowlist.cs")` で**旧パスを直接組み立てて `SourceAllowlist.cs`
   を読んでいた。** 先行 6 本の申し送りには「他ユニットテストが移送対象のファイルをパス文字列で直接読む」という
   事例が無かった（IADR リンクのパス是正〔5 本目〕はあったが、C# コードが `File.ReadAllText` で読む形は初めて）。
   本サービス固有の `SourceAllowlist`（許可ソース語彙）を、境界を跨がずに**ソースの静的解析**で突合する
   アーキテクチャテストが存在したため（`RetrievalSourceVocabularyTests` 冒頭コメント: 「判断サービスは収集
   サービスを参照しない〔ユニット境界〕。したがって語彙は 2 か所に書かれる」）。**この母集合は当初の走査語
   （`InformationCollectionService\.(Api|Application|Domain|Infrastructure)` / `InformationCollectionService/(src|tests)`）
   では発見できず**、`backend/` 全体を InformationCollectionService という文字列だけで再走査して見つけた
   （規則5「軸を 1 本で終わらせない」）。**新しいパスへ 1 行修正した**（本文の判定ロジック・語彙そのものは
   不変。`namespace InformationCollectionService.Domain` 自体は移送で変わっていないため、同ファイル内の
   コメント〔`RetrievalSourcePolicy.cs` の `InformationCollectionService.Domain.SourceAllowlist.Default` という
   FQN 引用〕は正しいまま据え置いた）。
2. **`InformationSourceFactory.ApplyDemotions`（`internal static` メンバ）が `InformationSourceFactoryTests.cs`
   から直接呼び出されていた。** 当初の内部可視性走査（`grep -rn '\bnew <Type>\|\bBeOfType<...>\|\btypeof(...)\|\b<Type>\.\w'`）
   では `InformationSourceFactory.ApplyDemotions(` の形が拾えていたはずだが、**クラス自体を public 化した後の
   初回ビルドで CS0117（メンバが見つからない）として現れた**——`ParseProviders`（同クラスの別 internal static
   メンバ）は Tests から直接参照されていないため internal のまま据え置いて正しかったが、`ApplyDemotions` は
   見落としていた。**5 本目の申し送り5「DbContext の CS0053 連鎖は 2 回目のビルドで初めて検出される」と同型の
   罠**——**internal メンバの可視性は、クラスを public にしただけでは終わらない。個々のメンバも独立に判定が要る**
   （後述「7 本目以降への申し送り」）。

## 目標樹形（実施結果）

```
backend/Services/InformationCollectionService/
├── InformationCollectionService.csproj
├── Program.cs
├── appsettings.json / appsettings.Development.json
├── Domain/                          (8 ファイル。エンティティ・値オブジェクト・純粋な評価器)
├── Features/InformationCollection/  (8 ファイル: Ports 4 + Services 1 + State 3)
├── Common/Abstractions/             (2 ファイル: IClock / SystemClock)
├── Infrastructure/ExternalServices/ (17 ファイル: 旧 Composable/Adapters 14 + 旧 Application/Adapters の
│                                       安全既定実装 3〔NoOpInformationSource / NoSourcesFetcher /
│                                       InMemoryKnowledgeBaseSink〕)
├── Hosted/                          (3 ファイル: CollectionOptions / CollectionPollingService /
│                                       DegradationStateTracker。旧 Composable/Polling を丸ごと移送)
└── Tests/
    └── InformationCollectionService.Tests.csproj  (30 ファイル)
```

`Infrastructure/Persistence/` `Infrastructure/Steps/` `Common/Exceptions/` `Common/Behaviors/` は
**実体が無いため作らなかった**（母集合の実測どおり）。

## 設計（判断とその理由）

### 判断1: 集約は 1 つ（`InformationCollection`）とし、`_Shared/` は作らない
（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定1 の適用。新しい判断ではない）

収集ポーリング・情報源選択・KB シンク選択・費用統制ゲート照会はいずれも「1 巡回の収集」という不可分な
概念に属し、操作フォルダの兄弟を作る決定（3 段目のスライス分割）は採らない
（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) 決定1）。したがって集約は 1 つとし、
`Features/InformationCollection/` 直下に平らに置いた。

**集約名は「サービス名から `Service` を落とす」機械的規則をそのまま適用した**（`CostControlService` →
`CostControl`・`BacktestService` → `Backtest`・`MarketMonitorService` → `MarketMonitor` と同型）。
6 本目 NotificationService のように実例（構成キー・基盤の同名参照実装）が機械的規則と食い違う場合は
実例を優先する運用だが、本サービスでは以下を確認し、**機械的規則と実例が一致した**:

- アプリケーションサービスのクラス名が `InformationCollectionAppService` であり、`Service` を落とした
  `InformationCollection` と一致する。
- 基盤（MSP）に同名サービスは存在しない（`/home/user/microservices-platform/src` を
  `find -iname "*Collection*"` で走査し 0 件を確認済み）ため、参照実装による上書きは無い。
- 構成セクション名は `Collection` / `Collection:Source`（`InformationCollection` の短縮形）であり、
  `InformationCollection` と矛盾しない（フォルダ名と構成キーの命名規則は独立という 6 本目の判断を踏襲）。

### 判断2: `Domain/` と `Features/InformationCollection/` の切り分け（IADR-0264 決定3 の適用。新しい判断ではない）

[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定3 の基準
（**Domain ＝フレームワーク・DI・I/O に触れず業務概念そのものを表す型。ポート・アプリケーションサービス・
DTO・ストアは Features/<集約>/**）と、同決定の 🔴 注記（**移送で型の層を変えない**）をそのまま適用した。

| 元のプロジェクト | 型 | 置き場 |
| --- | --- | --- |
| `InformationCollectionService.Domain`（8 ファイル） | `InformationKind`（列挙）/ `SourceOutcome` / `DegradationNotice`（静的クラス）/ `FinnhubQuotaCalculator`（静的クラス）/ `GeneralWebActivationRequest` 他 / `SourceTier`（列挙）/ `PromptSafetySanitizer`（静的クラス）/ `SourceAllowlist`（いずれもエンティティ・値オブジェクト・純粋な評価器・サニタイザ。I/O 皆無） | **`Domain/`**（そのまま） |
| `InformationCollectionService.Application/Ports/`（`IClock` 以外の 4 インターフェース） | `ICostControlGate`（＋ `CostControlGate` レコード）/ `IInformationSource` / `IKnowledgeBaseSink` / `ISourceFetcher` | **`Features/InformationCollection/`**（決定3「ポートは Features」） |
| `InformationCollectionService.Application/Services/` | `InformationCollectionAppService` | **`Features/InformationCollection/`** |
| `InformationCollectionService.Application/State/` | `CollectionResult` / `RawInformationItem` / `SourceFetch.cs`（`NamedInformationSource` / `SourceFetchResult` を含む） | **`Features/InformationCollection/`** |

### 判断3: `IClock` / `SystemClock` は `Common/Abstractions/`
（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定3 のそのままの適用）

集約を跨いで使われ得る、I/O を持たない技術プリミティブであり、1・5 本目と同じ理由づけでそのまま適用した。
新しい判断ではない。

### 判断4: 安全既定（no-op）のポート実装は、本番実装と同じ `Infrastructure/ExternalServices/` に対で置く
（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) の写像方針の既定＋5 本目「判断5」の運用上の
明確化。新しい判断ではない）

旧 `Application/Adapters/` にあった `NoOpInformationSource`（`IInformationSource` の安全既定）・
`NoSourcesFetcher`（`ISourceFetcher` の安全既定）・`InMemoryKnowledgeBaseSink`（`IKnowledgeBaseSink` の
安全既定）の 3 型は、**旧プロジェクトが `Application` であっても**、各ポートの本番実装（`BojInformationSource`
ほか 6 情報源アダプタ・`SourceFetchRunner`・`LoggingKnowledgeBaseSink` / `KnowledgeBaseWriterSink`）と
同じ `Infrastructure/ExternalServices/` へ置いた。5 本目 MarketMonitorService の
`MarketMonitorService.Application.Adapters`（`IClock`/`SystemClock` 以外の InMemory 実装）を
`Infrastructure/Persistence/` または `Infrastructure/ExternalServices/` へ移した判断と同型
（旧プロジェクト境界ではなく、**ポートの実装であるという型の性質**で置き場を決める）。

`InformationSourceFactory`（情報源選択のファクトリ。構成 DTO 群 `CollectionSourceOptions` ほか 6 型を含む）は
旧 `Infrastructure/Composable/Adapters/` にあったとおり `Infrastructure/ExternalServices/` へそのまま移した
（BacktestService「判断3」〔構成 DTO も内容を個別判定せず元のプロジェクトのまま Infrastructure へ〕・
6 本目「判断3」と同型の適用）。

### 判断5: `Infrastructure/Composable/Polling/` は丸ごと `Hosted/` へ
（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定5・
[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) 決定1、5 本目「判断6」の適用。新しい判断ではない）

`CollectionPollingService`（`BackgroundService` を**リテラルに継承**。母集合の実測どおり本サービスで唯一の
該当）を `Hosted/` に置いた。6 本目の最重要申し送り（「`Hosted/` は `BackgroundService` のリテラル継承限定で
読む。`IHostedService` の直接実装は対象外」）どおり、本サービスは対象がリテラル継承であることを実装
（`internal sealed class CollectionPollingService(...) : BackgroundService`）で確認済み。

`CollectionOptions`（ポーリング間隔の構成。`CollectionPollingService` 専用）と `DegradationStateTracker`
（`CollectionPollingService` の private フィールドとしてのみ使われる欠測状態追跡。他から参照されない）は、
旧構成で同じ物理フォルダ `Composable/Polling/` に同居していたとおり、そのまま `Hosted/` へ同居させた
（5 本目「判断6」の `MonitorOptions` と同じ「専用の設定・補助クラスは同じ `Hosted/` へ同居させる」という
実態に沿った配置。新しい判断ではない）。

### 判断6: `internal` → `public` は「Tests が直接参照する型・メンバー」に限る
（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定4 の適用）

移送前から `internal` だった型のうち、Tests から**コンストラクタ呼び出し・`BeOfType<T>()`（型引数としての
使用）・静的メンバー呼び出し**で直接参照されていたものだけを `public` にした（DI 経由のインターフェース
越しの解決は対象外）。**先に `grep` で Tests からの直接参照を型ごとに洗い出してから**可視性を変えた
（1 本目の申し送り2 を踏襲）。

| 型 | 直接参照の根拠 |
| --- | --- |
| `DegradationStateTracker` | `DegradationStateTrackerTests.cs` が `new DegradationStateTracker()` |
| `CollectionOptions` / `CollectionTrigger`（列挙） | `CollectionPollingServiceTests.cs` が `new CollectionOptions { Trigger = CollectionTrigger.External, ... }` |
| `CollectionPollingService` | `CollectionPollingServiceTests.cs` が `new CollectionPollingService(...)` / `CollectionPollingService.EffectiveInterval(...)`（静的メソッド呼び出し） |
| `CollectionSourceOptions` / `FinnhubOptions` / `GoogleNewsOptions` / `SecEdgarOptions` / `EdinetOptions` / `BojOptions` / `FredOptions` | `InformationSourceFactoryTests.cs` が `new CollectionSourceOptions { ... }` とネストした各 `*Options` の初期化子 |
| `BojInformationSource` | `BojInformationSourceTests.cs` が `new BojInformationSource(...)` |
| `HttpCostControlGate` | `CostControlGateSelectionTests.cs` の `BeOfType<HttpCostControlGate>()`・`HttpCostControlGateTests.cs` の `new HttpCostControlGate(...)` |
| `FinnhubCompanyNewsSource` | `InformationSourceFactoryTests.cs` の `BeOfType<...>()`・`NewsInformationSourceTests.cs` の `new FinnhubCompanyNewsSource(...)` |
| `GoogleNewsRssSource` | 同上（`BeOfType` ＋ `new`） |
| `PlaceholderCostControlGate` | `CostControlGateSelectionTests.cs` が `BeOfType<PlaceholderCostControlGate>()` |
| `EdinetInformationSource` | `EdinetInformationSourceTests.cs` が `new EdinetInformationSource(...)` |
| `SourceFetchRunner` | `InformationSourceSelectionTests.cs` が `BeOfType<SourceFetchRunner>()` |
| `LoggingKnowledgeBaseSink` | `KnowledgeBaseSinkSelectionTests.cs` が `BeOfType<LoggingKnowledgeBaseSink>()` |
| `SecEdgarInformationSource` | `SecEdgarInformationSourceTests.cs` が `new SecEdgarInformationSource(...)` |
| `FinnhubInformationSource` | `InformationSourceFactoryTests.cs` の `BeOfType`・`FinnhubInformationSourceTests.cs` の `new FinnhubInformationSource(...)` |
| `FredInformationSource` | `FredInformationSourceTests.cs` が `new FredInformationSource(...)` |
| `KnowledgeBaseWriterSink` | `KnowledgeBaseSinkSelectionTests.cs` の `BeOfType`・`KnowledgeBaseWriterSinkTests.cs` の `new KnowledgeBaseWriterSink(...)` |
| `NoOpInformationSource` | `InformationSourceSelectionTests.cs` が `BeOfType<NoOpInformationSource>()` |
| `InformationSourceFactory`（クラス） | `InformationSourceFactoryTests.cs` が `InformationSourceFactory.Create(...)` を静的呼び出し |
| `InformationSourceFactory.ApplyDemotions`（🔴 メンバ単位。想定外2 参照） | `InformationSourceFactoryTests.cs` が `InformationSourceFactory.ApplyDemotions(...)` を静的呼び出し |
| `NoSourcesFetcher` | `CollectionPollingServiceTests.cs` / `InformationCollectionServiceTests.cs` / `InformationSourceSelectionTests.cs` が `new NoSourcesFetcher()` / `BeOfType` |
| `InMemoryKnowledgeBaseSink` | `CollectionPollingServiceTests.cs` / `InformationCollectionServiceTests.cs` が `new InMemoryKnowledgeBaseSink()` |

`InformationSourceFactory.ParseProviders`（同クラスの別 `internal static` メンバ）・
`GoogleNewsRssSource.Parse`（`internal static`）は Tests から直接参照されないため internal のまま据え置いた
（`InformationSourceFactory` クラス自体・`GoogleNewsRssSource` クラス自体は他の直接参照により public 化済み。
クラスの可視性とメンバーの可視性は独立に判定する——想定外2 参照）。`InternalsVisibleTo` は新設していない
（旧 3 csproj にあった計 3 エントリはすべて削除した）。

## Tests 統合（4 → 1）で変えていないことの証跡

**中身は 1 行も変えていない**（`git mv` のみ・変更は namespace 宣言・using の書き換え、および
「想定外」1・2 で述べた最小限の実務対応〔他ユニットテストのパス修正・internal メンバの public 化〕に
限定。テスト本体のロジック・アサーションは無変更）。

### テストダブルの重複定義（規則2 に基づき確認・重複なし）

旧 4 テストプロジェクトに `class Fake*` / `class Stub*` / `class Test*` の重複名がないか
`grep -rhoE "class \w+"` で全数走査した。`StubHandler`（3 件）・`StubClock`（3 件）・`Factory`（3 件）・
`StubFetcher`（2 件）・`FixedClock`（2 件）が同名で複数回現れたが、**全件が `private sealed class` として
各テストクラス内に**ネストされており（例外: `TestDoubles.cs` の `internal sealed class StubHandler` は
名前空間直下だが、他の `StubHandler` はすべて別テストクラスにネストされた private 型のため名前が衝突しない）、
Tests 統合後も名前空間フラット化による CS0101（型の重複定義）は**発生しなかった**（実測: ビルド 0 error）。
5 本目が踏んだ「別アセンブリだったため衝突しなかった同名型がテスト統合で衝突する」事故は
**本サービスでは起きなかった**（ネスト private のため元々衝突し得ない設計だった）。

### テスト件数の突合（移送前後を実測。削っていないことの証跡）

移送前は各旧テストプロジェクトを個別に `dotnet test` して実測した（本 PR 着手直後・クリーンな
作業ツリーで測定。旧プロジェクトがまだ存在する段階で先に測定したため `git stash` は使っていない
——1・4・5・6 本目の申し送りを踏襲）。

| テストアセンブリ | 移送前 | 移送後 |
| --- | ---: | ---: |
| `InformationCollectionService.Api.Tests` | 31 | — |
| `InformationCollectionService.Application.Tests` | 8 | — |
| `InformationCollectionService.Domain.Tests` | 338 | — |
| `InformationCollectionService.Infrastructure.Tests` | 80 | — |
| **`InformationCollectionService.Tests`** | — | **457** |
| 合計 | **457** | **457** |

31 + 8 + 338 + 80 = 457 = 移送後の合格件数と**完全一致**。減った件・増えた件は 0。

### `[Fact]`/`[Theory]` 属性の総数（裏取り）

`grep -rhoE '^\s*\[(Fact|Theory)' Tests/*.cs` = **154**（`[Fact]` 139・`[Theory]` 15）。
移送前の同じ走査（旧 4 テストプロジェクト合計）も **154**（`[Fact]` 139・`[Theory]` 15）で完全一致。

## `list-test-projects.js --count` の突合

- 移送前: **37**
- 移送後: **34**
- 差分: **-3**（旧 4 テストプロジェクト → 新 1 テストプロジェクトの差分と一致）

## 名前空間の実装解決に関する事故（4・5 本目の申し送りどおり踏んだ／踏まなかった罠）

4・5 本目の最重要申し送り「1 回目のビルドは全容を報告しない」を踏まえ、**最初から**
`dotnet build-server shutdown` → `bin`/`obj` 全消去 → `dotnet restore` → `dotnet build --no-restore` の
手順で検証した（6 本目の申し送り1〔restore を挟む〕も踏襲）。

- **「using 欠落」「完全修飾名の部分参照」は 2 種類発生した**（6 本目とは異なり発生した）:
  1. `HttpCostControlGateTests.cs` が完全修飾名 `InformationCollectionService.Application.Ports.CostControlGate`
     を 2 箇所で使っていた（`Application.Ports` が新樹形で消滅するため要修正。5 本目の「想定外3」と同型）。
  2. `SourceAllowlistTests.cs` / `PromptSafetySanitizerTests.cs`（旧 `InformationCollectionService.Domain.Tests`
     由来）が、旧構成での**暗黙の親名前空間解決**（`InformationCollectionService.Domain.Tests` の祖先
     `InformationCollectionService.Domain` が `using` 無しで見えていた）に依存しており、Tests フラット化
     （`InformationCollectionService.Tests`）で `CS0103` になった。4・5 本目の「using 欠落」と同型だが、
     **今回は Domain.Tests 由来のみで発生**（Api.Tests / Application.Tests / Infrastructure.Tests 由来の
     旧ファイルはすべて明示的な `using` を既に書いていた）。
- **想定外2（`InformationSourceFactory.ApplyDemotions` の internal メンバ）は 1 回目の完全ビルドで
  CS0117 として検出できた**（5 本目の DbContext 連鎖のように 2 回目まで隠れなかった）——理由は
  クラスの public 化とメンバの参照が同一ファイル内で同時に評価されるため。

## `has-pending-model-changes`（対象外の実測証跡）

```
$ dotnet ef migrations has-pending-model-changes \
    --project backend/Services/InformationCollectionService/InformationCollectionService.csproj \
    --startup-project backend/Services/InformationCollectionService/InformationCollectionService.csproj
Build started...
Build succeeded.
Your startup project 'InformationCollectionService' doesn't reference Microsoft.EntityFrameworkCore.Design.
This package is required for the Entity Framework Core Tools to work. Ensure your startup
project is correct, install the package, and try again.
（exit code 1）
```

`InformationCollectionService.csproj` は `Microsoft.EntityFrameworkCore.Design` を参照しない
（`DbContext` 0 件・移送前後とも `grep -c "DbContext\|migration"` = 0）。**本サービスは対象外**であり、
黙って省略せず本節に実測で記録する（DoD の指示どおり。6 本目 NotificationService と同型の実測構造）。

## `DomainLayerDependencyTests` の下限（[IADR-0265](../adr/IADR-0265_domain-project-count-checker-dynamic-lower-bound.md)。
手で触っていない）

`RepositoryLayout.cs` / `DomainLayerDependencyTests.cs` / `DomainSourceDependencyTests.cs` は
**本 PR で 1 行も変更していない**（`git status`/`git diff --stat` で確認済み。無変更）。
`UnmigratedServicesWithDomainProjectCount` は `backend/Services/<Svc>/src/` の実在と `.Domain` 接尾辞
ディレクトリの実在を実ツリーから動的に数えるため、InformationCollectionService の移送
（`src/InformationCollectionService.Domain/` の消滅）により**自動的に** 1 件減る。

実測（`backend/Services/*/src/` を列挙し `.Domain` 接尾辞ディレクトリの有無を確認）:
移送前 **5**（InformationCollectionService・OrderExecutionService・ReportService・RiskManagementService・
TradeDecisionService） → 移送後 **4**（OrderExecutionService・ReportService・RiskManagementService・
TradeDecisionService）。`dotnet test` で `AiStockTrading.Architecture.Tests` **88 件すべて緑**
（`DomainLayerDependencyTests` を含む）であることを実測済み。**手で下限を書き換える操作は行っていない。**

## `internal`→`public` の想定外（想定外2 の補足）

上記「判断6」参照。**クラスを `public` にしても、そのクラスの `internal static` メンバは自動では
`public` にならない**——C# の可視性はメンバ単位で独立に宣言されるため、クラス宣言の `internal → public`
置換（`sed`）はメンバ宣言（`internal static InformationSourceCatalog ApplyDemotions(...)`）を捕捉しない。
**Tests からの直接参照走査は、クラス名だけでなく「クラス名.メンバ名」の呼び出し形も対象に含めること**
（申し送りへ反映）。

## IADR を作らない判断

**本 PR では新しい IADR（`IADR-0266`）を作らない。** [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)・
[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定3・
[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) の写像方針表・6 本目の `Hosted/` 判定基準を
参照するだけで、本 PR の判断1〜6 すべてが機械的に導けたためである。

- 判断1（集約は 1 つ・命名は機械的規則と実例の双方確認で一致）は IADR-0263 決定1 の**そのままの適用**
  （命名の確認手順は 6 本目の運用を踏襲したが、本サービスでは機械的規則と実例が一致したため新しい分岐は
  生じなかった）。
- 判断2（Domain/Features の切り分け）は IADR-0264 決定3 の**そのままの適用**（Domain を持つサービスの
  4 例目。5 本目 MarketMonitorService が 3 例目）。
- 判断3（`IClock`/`SystemClock` → Common/Abstractions）は IADR-0263 決定3 の**そのままの適用**。
- 判断4（安全既定のポート実装を本番実装と同じ Infrastructure 区分に対で置く）は 5 本目「判断5」の
  **運用上の明確化の再適用**であり、新しい設計判断ではない。
- 判断5（`Composable/Polling/` を丸ごと `Hosted/` へ）は IADR-0263 決定5・IADR-0259 決定1・6 本目の
  `Hosted/` 判定基準（`BackgroundService` のリテラル継承限定）の**そのままの適用**。
- 判断6（`internal`→`public`）は IADR-0263 決定4 の**そのままの適用**（メンバ単位の判定漏れ〔想定外2〕は
  移送手順上の実務であり、樹形・可視性の設計判断そのものではない）。
- 母集合の走査で見つけた「想定外」（他ユニットテストの旧パス直接参照・internal メンバの見落とし）は
  いずれも**移送手順上の実務**であり、樹形・可視性・依存規律に関する**設計判断ではない**。

## 受け入れ基準

- [x] `dotnet build backend/backend.slnx` が 0 warning / 0 error で通る
      （`dotnet build-server shutdown` → `bin`/`obj` 全消去 → `dotnet restore` → `dotnet build --no-restore` で確認済み）
- [x] `dotnet test backend/backend.slnx` の失敗が `AiStockTrading.IntegrationTests` の 8 件のみ
      （Docker 不在の環境制約。全体 5120 件中失敗 8 件・成功 5112 件）
- [x] `dotnet format backend/backend.slnx --verify-no-changes` が通る（exit 0）
- [x] `dotnet ef migrations has-pending-model-changes` は対象外（DbContext 0 件。実測証跡を上記に記録）
- [x] `list-test-projects.js --count` が 37 → 34

  ［2026-08-29 追記 / #592］**この 37 → 34 は base `35b330a` に対する値である。**
  その後 develop（`58854e3`＝NotificationService #591 の移送を含む）を取り込んだため、
  **PR の最終状態では 35 → 32 になる**（#591 がテストプロジェクトを 2 本減らしたぶん、
  移送前後とも 2 少ない）。**どちらも正しく、基準が違うだけである。**
  **`--count` のような導出値は develop 取り込みの前後で必ず変わるので、記録するときは
  *どの base に対する値か* を必ず添える**（6 本目 #591 のレビュー指摘を踏まえた先回りの明示）。
- [x] `coverage-floor.json` の床を割らない（実測値は「検証（再測定）」節）
- [x] 検査器一式（`scripts/README.md` 掲載分）が緑（実行コマンドの直接終了コードで確認）
- [x] `DomainLayerDependencyTests` の下限が自動追随し（5 → 4）、`RepositoryLayout.cs` /
      `DomainLayerDependencyTests.cs` / `DomainSourceDependencyTests.cs` を手で編集していないことを確認した
- [x] `node scripts/scripts.test.js` と `node scripts/scripts.repo.test.js` が緑（294 テスト）
- [x] `pgrep -c dotnet` が終了時点で 0 である

## 計画書との差異

- 差異: なし。本件は構造移送のみで振る舞いを変えていない（IADR-0259 決定7）。

## 残り 4 本のサービスへの申し送り（本 PR で踏んだ落とし穴・再利用可能な手順）

1. 🔴 **他ユニットのテスト（`backend/Tests/AiStockTrading.Architecture.Tests/` 等）が、移送対象サービスの
   ソースファイルを `Path.Combine` で直接読んでいないか確認すること。** 通常の走査語
   （`<Svc>\.(Api|Application|Domain|Infrastructure)` / `<Svc>/(src|tests)`）は対象サービス配下の
   ファイルしか拾わないため、**`backend/` 全体をサービス名の単純文字列だけで再走査する**軸を必ず加える
   （規則5「軸を 1 本で終わらせない」の具体例）。本 PR では `RetrievalSourceVocabularyTests.cs` が
   `SourceAllowlist.cs` の絶対パスをハードコードしており、通常の走査では見つからなかった。
2. 🔴 **`internal static` な「クラスのメンバ」は、クラス自体の `internal → public` 置換では
   public にならない。** Tests からの直接参照走査は「`new <Type>`」「`BeOfType<Type>`」だけでなく
   **「`<Type>.<StaticMember>(`」の形も対象に含めること**——本 PR は `InformationSourceFactory.ApplyDemotions`
   （internal static メンバ）を初回走査で見落とし、1 回目のビルドで CS0117 として検出した
   （5 本目の DbContext 連鎖のように 2 回目まで隠れる罠ではなく、**むしろ 1 回目で出る**ため対処は
   速いが、走査基準に加えておけば最初から拾えた）。
3. **`Hosted/` の判定基準（6 本目「`BackgroundService` のリテラル継承限定」）は本 PR でも有効だった。**
   `CollectionPollingService : BackgroundService` のリテラル継承を実装で確認し、機械的に `Hosted/` へ
   置いた。同じ物理フォルダに同居していた設定・補助クラス（`CollectionOptions` / `DegradationStateTracker`）も
   丸ごと `Hosted/` へ移すのが最速（5 本目「判断6」の踏襲）。
4. **安全既定（no-op/placeholder）のポート実装は、旧プロジェクトが `Application` であっても
   `Infrastructure/ExternalServices/` へ本番実装と対で置く。** 「移送で層を変えない」は
   **フォルダ境界（Application/Infrastructure の csproj 分割）ではなく、型の性質（ポートの実装か否か）**
   で判定する——本 PR の `NoOpInformationSource` 等 3 型がこの型の 2 例目（5 本目 MarketMonitorService の
   InMemory 実装群が 1 例目）。
5. 移送前のテスト件数・`[Fact]`/`[Theory]` 属性数は、旧プロジェクトが消える前に個別 `dotnet test` /
   `grep` で実測しておく（1・4・5・6 本目の申し送りを継続して踏襲。本 PR でも両軸とも完全一致で有効だった）。
6. **タスク文に書かれた「移送前の実測値」（テスト件数・`list-test-projects --count` 等）は、並行移送では
   古くなっている可能性がある。** 本 PR ではタスク文の `list-test-projects --count = 35` の前提が実測 37 と
   食い違ったが、`git log HEAD..origin/develop --oneline` で **base が最新であること**を確認した上で
   「タスク文の記載時点が古かっただけ」と判定した（6 本目の申し送り1と同種の drift だが、原因は base の
   世代遅れではなくタスク文自体の作成時点の古さだった——**両者を区別して記録すること**）。
7. 検証手順そのものの落とし穴（bin/obj 全消去後の restore 未経由・カバレッジ中断の偽陽性・パイプでの
   終了コード隠蔽・空ディレクトリの偽陽性）は 4〜6 本目の申し送りがすべて有効であり、**新しい罠は
   検証手順側では発生しなかった**（本 PR は 6 本目の手順〔restore を挟む・`cov/` を作り直す・
   直接終了コードで確認する〕をそのまま踏襲して問題なく通った）。
