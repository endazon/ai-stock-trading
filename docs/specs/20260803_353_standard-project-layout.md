---
title: 作業仕様書 — 全 11 サービスを Api/Application/Domain/Infrastructure/Contracts/SharedKernel/Tests 標準構成へ揃える
type: work
status: review
related_ids: [NFR, IADR-0001, IADR-0046, IADR-0128]
author: endazon (with Claude Code)
created: 2026-08-03
updated: 2026-08-03
plan_refs:
  - ../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md
  - ../../planning/projects/microservices-platform/06_technical/12_backend-application-stack.md
  - ../../planning/projects/microservices-platform/07_adr/ADR-0019_unit-first-repo-structure.md
  - ../../planning/projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md
  - ../../planning/projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
related_specs:
  - ../adr/IADR-0001_repo-structure-and-stack.md
  - ../adr/IADR-0046_unit-repo-layout.md
  - ../adr/IADR-0128_standard-project-layout.md
  - ./20260803_351_awesomeassertions-migration.md
  - ./20260803_352_xunit-v3-migration.md
  - ./20260802_344_reimplementation-preparation.md
  - ../DEFINITION_OF_DONE.md
---

# 作業仕様書: 標準プロジェクト構成への再配置（#353）

> **本書は後続段階の引き継ぎ資料を兼ねる。** 第 2 段階（残り 9 サービス）は本書 §7「移行レシピ」を
> そのまま機械的に適用すれば再現できるように書いてある。第 2 段階の担当者は §5（規則）と §7（手順）と
> §9（検証）だけ読めば作業できる。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR。プロジェクト構成の標準追随）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR（計画）: platform **ADR-0030**（アプリ層ライブラリ標準・§決定の選定基準 3「層の依存規律」）／
  platform [12_backend-application-stack](../../planning/projects/microservices-platform/06_technical/12_backend-application-stack.md)（fixed・§プロジェクト構成が 7 標準の本文）／
  platform ADR-0019（ユニット第一構成）・ADR-0020（.NET 10）・ADR-0027（Wolverine）・ADR-0029（gRPC/REST 基準）／
  AST ADR-0001（platform 再利用）
- 実装 ADR: [IADR-0001](../adr/IADR-0001_repo-structure-and-stack.md)（基盤リポに規約を揃える）・
  [IADR-0046](../adr/IADR-0046_unit-repo-layout.md)（ユニットリポジトリレイアウト）・
  **[IADR-0128](../adr/IADR-0128_standard-project-layout.md)（本作業で確定した対応規則。決定の正本）**
- 起点 issue: [#353](https://github.com/endazon/ai-stock-trading/issues/353)（親 [#345](https://github.com/endazon/ai-stock-trading/issues/345) / [#344](https://github.com/endazon/ai-stock-trading/issues/344)）

## 目的・背景

platform [12_backend-application-stack](../../planning/projects/microservices-platform/06_technical/12_backend-application-stack.md)（fixed）は、サービス単位のプロジェクト構成の標準を

```text
src/
 ├── Api             # エンドポイント定義・DI 構成・ProblemDetails 変換
 ├── Application     # ユースケース（Wolverine ハンドラ）・検証・マッピング
 ├── Domain          # エンティティ・値オブジェクト（外部依存なし）
 ├── Infrastructure  # EF Core・Redis・オブジェクトストレージ等の実装
 ├── Contracts       # 公開契約（proto・イベント・DTO）
 ├── SharedKernel    # Result / Error・共通基底（過度な共通化は避ける）
 └── Tests           # Unit / Integration
```

と定めた。現行は 1 サービスあたり `Domain / Application / Worker`（＋テスト）である。**`Worker` という単位が
標準に無い**ことが本質的な差分で、実体としては「ホスト（Program.cs・エンドポイント・DI）」と
「技術詳細（EF Core・メッセージング consumer・外部 API アダプタ）」が 1 プロジェクトに同居している。
この同居があるかぎり **Domain 外部依存ゼロ以外の層の規律は機械検査できない**。

本作業は #345 の分割 **3/4**。#351（AwesomeAssertions）・#352（xUnit v3）の完了後に着手する。

## 対象範囲

### 対象（本 issue 全体）

- 11 サービス（RiskManagement / OrderExecution / TradeDecision / InformationCollection / MarketMonitor /
  Report / Notification / Audit / Backtest / Configuration / CostControl）の標準構成への**再配置**
- `backend/backend.slnx` の更新
- **Domain 層の外部依存ゼロ**を強制するアーキテクチャテストの新設
- 再配置に伴うリポジトリ横断の追随（`docker-compose.yml` / `scripts/k8s-local-images.sh` /
  `scripts/validate-runtime-scaffold.js` / `docs/tech/`）

### 対象外（本 issue でやらない）

| 項目 | 理由・担当 |
| --- | --- |
| **ライブラリ標準への新規適用**（Riok.Mapperly 導入・FluentValidation 導入・Polly（`Microsoft.Extensions.Http.Resilience`）への置換・ProblemDetails 化） | **本 PR ではやらない**（§12 未決事項 1）。いずれも既存コードの書き換えを伴い「再配置＝振る舞いを変えない」という本 issue の受け入れ条件（合格数 2256 の完全一致）と両立しない。構成移動に**不可避**な参照調整のみ行う |
| MassTransit → Wolverine | #354（#345 分割 4/4） |
| SharedKernel（自前 `Result` / `Error` 型）の導入 | 新規実装であり再配置ではない（§5.2・§12 未決事項 2） |
| 旧実装の物理削除 | IADR-0126 決定 5 により #346 へ集約。本作業は `git mv` による**移動のみ**で削除しない |
| Controller の Minimal API 化 | 既に達成済み（Controller 0 件） |

### 第 1 段階の対象（本セッション）

1. 本作業仕様書と [IADR-0128](../adr/IADR-0128_standard-project-layout.md)
2. アーキテクチャテスト基盤（`backend/Tests/AiStockTrading.Architecture.Tests`）
3. パイロット 2 サービス（**ConfigurationService** / **CostControlService**）の移行

## 設計

### 5.1 7 標準への対応表

**現行 → 新構成**（`<Svc>` = `ConfigurationService` 等のサービス名。`<Short>` = `Configuration` 等の
名前空間用短縮名＝サービス名から接尾辞 `Service` を除いたもの）。

| 標準（ADR-0030） | 本リポジトリでの実体 | 備考 |
| --- | --- | --- |
| **Api** | `backend/Services/<Svc>/src/<Svc>.Api`（新設。`Microsoft.NET.Sdk.Web`） | 旧 `<Svc>.Worker` の **Program.cs・`appsettings*.json`・`Foundation/Endpoints/**`** が移る |
| **Application** | `backend/Services/<Svc>/src/<Svc>.Application`（**現状のまま**） | 移動なし |
| **Domain** | `backend/Services/<Svc>/src/<Svc>.Domain`（**現状のまま**。無いサービスは作らない） | 移動なし。外部依存ゼロをアーキテクチャテストで固定 |
| **Infrastructure** | `backend/Services/<Svc>/src/<Svc>.Infrastructure`（新設。`Microsoft.NET.Sdk`） | 旧 `<Svc>.Worker` の **Program.cs / appsettings / Foundation/Endpoints 以外すべて**が移る |
| **Contracts** | `backend/Shared/AiStockTrading.Shared.Contracts`（**ユニット単位で 1 つ**。サービス個別には作らない） | platform ADR-0019 決定 4「ユニット固有のイベント契約はユニット側の契約プロジェクトに置く」。§5.2 |
| **SharedKernel** | **作らない**（本 issue では該当する実体が無い） | §5.2・§12 未決事項 2 |
| **Tests** | `backend/Services/<Svc>/tests/<Svc>.<Layer>.Tests`（本番プロジェクトと 1:1） | §5.5 |

**標準に無いが残す例外プロジェクト**

| プロジェクト | 扱い | 根拠 |
| --- | --- | --- |
| `ConfigurationService.Client` | **そのまま残す**（`Api` へも `Infrastructure` へも畳まない） | サービスが**他サービスへ公開する**クライアントライブラリであり、7 標準のどの層にも当たらない（`Contracts` は型だけを持つ場所であって HTTP クライアント・キャッシュ・DI 拡張・consumer を置く場所ではない）。詳細と代替案の棄却理由は [IADR-0128](../adr/IADR-0128_standard-project-layout.md) §決定 5 |
| `backend/Bff/*`・`backend/TestSupport/*`・`backend/Shared/*`・`backend/Tests/*` | 対象外（サービス単位の構成ではない） | IADR-0046 / IADR-0063 / IADR-0091 |

### 5.2 「7 プロジェクトを常に作るのか、実体があるものだけ作るのか」

**決定: 実体があるものだけ作る。空プロジェクトは作らない。**（正本は [IADR-0128](../adr/IADR-0128_standard-project-layout.md) §決定 2）

根拠は ADR-0030 の文面そのものに 3 つある。

1. §プロジェクト構成の `SharedKernel` 行に **「過度な共通化は避ける」** と明記されている。中身のない
   SharedKernel を 11 個並べるのは、この但し書きが避けよと言っている当のものである。
2. §決定 の選定基準 2 は **「標準機能優先 = .NET / ASP.NET Core 標準で足りるものは標準を使う」**、
   すなわち依存と構成要素を増やさないことを基準に据えている。空プロジェクトは restore・build・
   slnx 登録・CI 時間を増やすだけで、何も足さない。
3. 同 §決定 の主要決定は **`Result` = 「SharedKernel の自前実装」** と述べており、SharedKernel は
   「Result 型を置くための場所」として定義されている。Result 型を導入していない現時点で
   SharedKernel を作れば、**空の器だけが標準に見える**という最悪の状態（構成は揃っているのに規律は無い）になる。

`Contracts` についても同様に、**ユニット単位で 1 つ**（`AiStockTrading.Shared.Contracts`）を正とする。
platform ADR-0019 決定 4 が「ユニット固有のイベント契約はユニット側の契約プロジェクトに置く」と定めており、
AST はそれ自体が 1 つの可変機能ユニットである。サービスごとに `Contracts` を切ると、**サービス間で共有される
イベント契約が置き場所を失う**（`OrderApproved` は発注執行と リスク統制の双方が使う）。

> **判断が割れる論点として残すもの**: 「標準に列挙された 7 つは常に存在すべき」という読み方も文面上は可能である。
> 本作業は上記 3 点を根拠に「実体があるものだけ」を採ったが、基盤リポ（microservices-platform）の実装が
> 空の SharedKernel / Contracts を各サービスに置いている場合は、揃える先が基盤である（IADR-0001）以上
> 再検討を要する。基盤リポは本セッションの参照範囲外のため未確認である（§12 未決事項 3）。

**結果として 1 サービスあたりの本番プロジェクトは 3 → 4**（Domain を持たない Audit / Notification は 2 → 3）。
issue 本文が見積もった「76 → 約 130」ではなく **76 → 98** になる（内訳は §8）。

### 5.3 Worker → Api / Infrastructure の分割基準

**唯一の規則（例外なし・機械的に適用できる）**:

| 旧 `<Svc>.Worker` 内のパス | 行き先 |
| --- | --- |
| `Program.cs` | **Api** |
| `appsettings.json` / `appsettings.Development.json` | **Api** |
| `Foundation/Endpoints/**` | **Api** |
| `Foundation/Persistence/**` | **Infrastructure** |
| `Foundation/Adapters/**` | **Infrastructure** |
| `Migrations/**` | **Infrastructure** |
| `Composable/**`（`Steps` = consumer・`Adapters`・`Polling`・`Retention`・`Reconciliation`・`StageGate`・`MarketData`） | **Infrastructure** |
| 上記以外のすべて | **Infrastructure** |

言い換えると **「ホストの起動と HTTP の入口だけが Api、残りはすべて Infrastructure」**。判断を要する
灰色地帯を作らないため、`Api` の中身は 3 種類（Program・appsettings・Endpoints）に**限定列挙**する。

- **なぜ consumer（`Composable/Steps`）が Api でなく Infrastructure か**: consumer はメッセージング
  基盤（MassTransit → Wolverine）というインフラ技術に張り付いた入口であり、ADR-0030 の Api の定義
  （「エンドポイント定義・DI 構成・ProblemDetails 変換」）に当たらない。#354 で Wolverine へ移る際、
  ハンドラ本体が Application へ上がる余地も Infrastructure に置いた方が素直である。
- **なぜ `Foundation` / `Composable` のフォルダ階層を残すか**: platform ADR-0018 の固定（Foundation）/
  可変（Composable）区分に対応する既存の意味づけであり、本 issue の目的（層の分離）と直交する。
  同時に変えると差分が「再配置」でなくなる。

### 5.4 命名規則

| 対象 | 規則 | 例 |
| --- | --- | --- |
| プロジェクトフォルダ | `backend/Services/<Svc>/src/<Svc>.<Layer>` | `backend/Services/CostControlService/src/CostControlService.Api` |
| `.csproj` ファイル名 | `<Svc>.<Layer>.csproj`（＝アセンブリ名） | `CostControlService.Infrastructure.csproj` |
| `RootNamespace` / C# 名前空間 | `AiStockTrading.<Short>.<Layer>[.<既存の下位階層>]` | `AiStockTrading.CostControl.Infrastructure.Foundation.Persistence` |
| テストプロジェクト | `backend/Services/<Svc>/tests/<Svc>.<Layer>.Tests` / ns `AiStockTrading.<Short>.<Layer>.Tests` | `CostControlService.Api.Tests` |
| slnx のフォルダ | 既存どおり `/Services/<Svc>/src/` と `/Services/<Svc>/tests/` | — |

**名前空間の変換は「層セグメントの置換 1 回」だけである。**

```text
AiStockTrading.<Short>.Worker            → AiStockTrading.<Short>.Api            （Program/Endpoints 由来）
AiStockTrading.<Short>.Worker.<Rest>     → AiStockTrading.<Short>.Api.<Rest>     （同上）
AiStockTrading.<Short>.Worker[.<Rest>]   → AiStockTrading.<Short>.Infrastructure[.<Rest>]（それ以外）
AiStockTrading.<Short>.Worker.Tests      → AiStockTrading.<Short>.{Api,Infrastructure}.Tests
```

`<Short>` は既存の `RootNamespace` から読む（`CostControlService` → `CostControl`、
`InformationCollectionService` → `InformationCollection`）。**新たに決め直さない。**

### 5.5 テストプロジェクトの分割

`<Svc>.Worker.Tests` を **`<Svc>.Api.Tests` と `<Svc>.Infrastructure.Tests` の 2 つに割る**。

振り分けは §5.3 と同じ規則を「そのテストが検証している本番クラスの行き先」に適用する。ただし
**`WebApplicationFactory<Program>` を使うテストは常に Api.Tests**（`Program` は Api にしかない）。
テスト補助クラス（`<Svc>WorkerWebApplicationFactory` / `TestAuthHandler`）は Api.Tests へ移す。

| 例（CostControlService・40 件） | 行き先 | 件数 |
| --- | --- | --- |
| `CostControlEndpointsTests` / `CostControlWiringTests` / `HealthEndpointTests` | Api.Tests | 5 / 3 / 1 |
| `EfCostLedgerTests` / `EfProcessedMessageStoreTests` / `EfProcessedMessageStorePurgeTests` | Infrastructure.Tests | 3 / 3 / 5 |
| `LlmCostIncurredConsumerTests` / `ProcessedMessageRetentionServiceTests` | Infrastructure.Tests | 5 / 6 |
| `AssumptionsCostLimitsProviderTests` / `VersionedCostLimitsTests` | Infrastructure.Tests | 2 / 7 |

**テストファイルの中身は `namespace` 行・`using` 行以外 1 文字も変えない**（表明の変更禁止）。

WebApplicationFactory 名（`<Svc>WorkerWebApplicationFactory`）は**改名しない**。改名は差分を増やすだけで
本 issue の目的に寄与せず、テストコード本文の変更を誘発する。命名の整理は #354 以降の機会に委ねる。

### 5.6 層の参照規則（移行後に成立していること）

```text
Api ──▶ Application ──▶ Domain ──▶ (Shared.Contracts のみ)
 │           ▲
 └──▶ Infrastructure ─┘
```

- **Domain**: `PackageReference` **ゼロ**。`ProjectReference` は `AiStockTrading.Shared.Contracts` と
  他サービスの `*.Domain` のみ（後者は現行の既知の状態。§5.9・アーキテクチャテストの許可リスト）
- **Application**: Domain と Shared.Contracts を参照する。EF Core・MassTransit・ASP.NET へは依存しない
  （現行で既に成立。本 issue では検査対象にしない＝§12 未決事項 4）
- **Infrastructure**: Application / Domain / Shared.Contracts / TestSupport.PlatformShim を参照し、
  EF Core・Npgsql・MassTransit 等の技術パッケージを持つ
- **Api**: Application / Infrastructure / TestSupport.PlatformShim を参照する。**Api → Infrastructure は
  DI 構成のために必要**（Program.cs が具象実装を登録する）であり、Clean Architecture の
  composition root として許容される。旧 Worker が持っていた他の `ProjectReference`
  （`Shared.Contracts`・`ConfigurationService.Client` 等）のうち **Program.cs が使うものは Api にも残す**
  （推移的に届くが、明示参照＝使用箇所という対応を保つ方がレビューしやすい）

### 5.7 振る舞いを変えないことの担保（移行前に確認済みの事項）

再配置は「名前が変わるだけ」ではない。実際に振る舞いへ波及しうる 4 点を事前に確認した。

| 論点 | 実測・結論 |
| --- | --- |
| **RabbitMQ キュー名**（consumer の名前空間を変える） | **影響なし**。MassTransit の `DefaultEndpointNameFormatter` は**クラス名のみ**からエンドポイント名を導き名前空間を含まない（`scripts/check-consumer-endpoint-names.js` の冒頭コメントが同じ前提を明文化している）。本作業はクラス名を変えないためキュー名は不変 |
| **EF Core の Migration 解決**（DbContext が別アセンブリへ移る） | **影響なし**。既定の migrations assembly は「DbContext を含むアセンブリ」であり、DbContext と `Migrations/` を**同じ Infrastructure へ一緒に移す**ため関係は保たれる。`__EFMigrationsHistory` はマイグレーション ID のみを保持し、アセンブリ名・名前空間を持たないため既存 DB への影響もない |
| **`WebApplicationFactory<Program>`** | Api が `Program` を持ち、Api.Tests が Api を参照する形で従来どおり動く（`MvcTestingAppManifest.json` はビルド時に再生成される） |
| **コンテナのエントリポイント** | `SERVICE_DLL` が `<Svc>.Worker.dll` → `<Svc>.Api.dll` に変わる。`docker-compose.yml` と `scripts/k8s-local-images.sh` を同一コミットで追随させる（§5.8）。Helm chart はイメージ名しか持たず**変更不要**（実測） |
| **型の公開面（`internal`）** | 旧 Worker の永続化・アダプタ実装は `internal` である。分割すると Program.cs から見えなくなる（CS0122）。**`public` へ広げず `InternalsVisibleTo` で解決する**（§7 手順 4 の「必須の落とし穴」）。公開面を分割前と一致させることも「振る舞いを変えない」に含める |

### 5.8 リポジトリ横断の追随箇所（サービス移行と同一コミットで直す）

| ファイル | 追随内容 |
| --- | --- |
| `backend/backend.slnx` | 旧 Worker 系 3 行を削り、Api / Infrastructure / それぞれの Tests を登録する |
| `docker-compose.yml` | `SERVICE_PROJECT`（`…/src/<Svc>.Api/<Svc>.Api.csproj`）・`SERVICE_DLL`（`<Svc>.Api.dll`） |
| `scripts/k8s-local-images.sh` | 同上（`SERVICES` 配列の該当行） |
| `scripts/validate-runtime-scaffold.js` | `appsettings*.json` の探索先。**移行中は新旧が混在する**ため、`<Svc>.Api` を優先し無ければ `<Svc>.Worker` へフォールバックする形にする（第 2 段階の各コミットで CI が落ちないため） |
| `backend/Tests/AiStockTrading.IntegrationTests/*.csproj` | OrderExecution / RiskManagement / Report / CostControl の Worker への `ProjectReference` を Api へ（該当サービス移行時） |
| `docs/tech/tech-requirements.md`・`docs/adr/README.md` | 第 3 段階でまとめて更新 |

`scripts/check-test-traceability.js` と `scripts/check-coverage.js` は**追随不要**（実測）。前者は
`tests/` ディレクトリまたは `*.Tests` で終わるディレクトリを再帰探索する実装、後者は
`coverage.cobertura.xml` をファイル名で再帰探索する実装であり、いずれもプロジェクト名を決め打ちしていない。

### 5.9 現行 Domain の既知の状態（アーキテクチャテストの許可リストの根拠）

実測（全 9 Domain プロジェクト）:

- `PackageReference` は **0 件**（＝ADR-0030 の「Domain は外部ライブラリ依存ゼロ」は既に成立している）
- `ProjectReference` は 2 種類のみ
  - `AiStockTrading.Shared.Contracts`（6 件）— それ自体 `PackageReference` 0 件の純粋な契約プロジェクト
  - 他サービスの `*.Domain`（Backtest → Configuration/RiskManagement、CostControl → Configuration、
    Report → Configuration の 4 件）— サービス境界の観点では議論の余地があるが、**本 issue の対象外**
    （再配置ではなくドメイン境界の再設計になる）。アーキテクチャテストは許可し、§12 未決事項 5 に残す

## 6. アーキテクチャテスト（Domain 外部依存ゼロの機械的強制）

### 方式

`backend/Tests/AiStockTrading.Architecture.Tests`（xUnit v3 ＋ AwesomeAssertions）。
**csproj の静的解析**で検査する（リフレクションではない）。

| 選択 | 理由 |
| --- | --- |
| csproj 解析（採用） | 「依存が**宣言されている**か」を直接見る。ビルド成果物・推移解決に依存せず、失敗メッセージが「どの csproj のどの行が違反か」を直接指せる。**未使用の参照も検出する**（リフレクションでは、参照を足しても型を使わなければ検出できない） |
| リフレクション（不採用） | `Assembly.GetReferencedAssemblies()` はコンパイラが最適化で落とした参照を見逃す。Domain アセンブリを本テストが読み込む＝**アーキテクチャテストが Domain を参照する**という循環じみた構図にもなる |

### 検査項目

| # | 検査 | 意図 |
| --- | --- | --- |
| 1 | Domain プロジェクトの `PackageReference` が 0 件 | ADR-0030 「Domain は外部ライブラリへ依存しない（.NET 標準のみ）」 |
| 2 | Domain の `ProjectReference` が許可リスト内（`*.Domain` / `*.SharedKernel` / `AiStockTrading.Shared.Contracts`）のみ | Application / Infrastructure / Api / Client への逆流を止める |
| 3 | Domain から到達する**推移閉包**上のすべてのプロジェクトも `PackageReference` 0 件 | 「Shared.Contracts に EF Core を足す」形の**迂回**を塞ぐ。1 だけでは守れない |
| 4 | 発見した Domain プロジェクトが 9 件以上 | **検査器の空振り防止**。glob が壊れて 0 件になれば 1〜3 は無条件に成功してしまう |

検査 4 は「テストが何も検査していない状態で緑になる」ことを防ぐためのもので、実質的にはメタテストである。

### 起点 ID コメント

各テストに `// NFR, platform ADR-0030 §決定 選定基準 3（層の依存規律）: …` を付す（`.claude/rules/traceability.md`）。

### 変異確認（テストが「正しく壊れる」ことの確認）

第 1 段階の検証で、Domain の csproj へ故意に禁止参照を一時追加 → 失敗を確認 → 復元する（§9）。

## 7. 移行レシピ（1 サービス分・第 2 段階はこれをそのまま適用する）

`<Svc>` = サービス名（例 `ReportService`）、`<Short>` = 短縮名（例 `Report`）。
**すべて `git mv` で行う**（rename 検出を保ち履歴を保全する。`cp` + `rm` は使わない）。

### 手順 0: 事前確認

```bash
cd backend/Services/<Svc>
find . -type f -not -path '*/bin/*' -not -path '*/obj/*' | sort   # 移動対象の全量を控える
grep -rn 'RootNamespace' src/*/*.csproj                            # <Short> を確認する
```

### 手順 1: Api / Infrastructure のフォルダを作り、Worker の中身を振り分ける

```bash
S=backend/Services/<Svc>
git mv "$S/src/<Svc>.Worker" "$S/src/<Svc>.Infrastructure"
git mv "$S/src/<Svc>.Infrastructure/<Svc>.Worker.csproj" "$S/src/<Svc>.Infrastructure/<Svc>.Infrastructure.csproj"

mkdir -p "$S/src/<Svc>.Api"
# §5.3 の限定列挙（存在するものだけ）
git mv "$S/src/<Svc>.Infrastructure/Program.cs"                "$S/src/<Svc>.Api/"
git mv "$S/src/<Svc>.Infrastructure/appsettings.json"          "$S/src/<Svc>.Api/"
git mv "$S/src/<Svc>.Infrastructure/appsettings.Development.json" "$S/src/<Svc>.Api/"
mkdir -p "$S/src/<Svc>.Api/Foundation"
git mv "$S/src/<Svc>.Infrastructure/Foundation/Endpoints"      "$S/src/<Svc>.Api/Foundation/Endpoints"
```

> **注（Endpoints のフォルダ階層）**: `Foundation/Endpoints` は Api でも `Foundation/Endpoints` のまま
> 置く（名前空間の変換規則 §5.4 が「層セグメントの置換 1 回」で閉じるため。パイロット 2 件はこの形で実施した）。
> `Foundation/Endpoints` を持たないサービス（Backtest / InformationCollection / Notification /
> OrderExecution / TradeDecision）はこの行を飛ばす。エンドポイントが Program.cs に直書きされている場合、
> 本 issue では**切り出さない**（再配置の範囲を超える）。

> **補足（第 2 段階・`Foundation/Endpoints` を持たないサービスの実例 = BacktestService `e929e12`）**:
> この形では **`Api` の中身は `Program.cs` と `appsettings*.json` の 2 種類だけ**になり、`Foundation/`
> フォルダ自体を作らない（`mkdir -p "$S/src/<Svc>.Api/Foundation"` も不要）。公開 HTTP 面はヘルスチェックと
> `/internal/introspection` のみで、いずれも共通拡張（`MapAiStockTradingHealthChecks` /
> `MapAiStockTradingIntrospection`）を Program.cs が呼ぶ形であり、Api に固有のエンドポイント型は存在しない。
> **Api プロジェクトが「Program.cs ほぼ単体」になること自体は正常**であり、それを避けるために
> Infrastructure から何かを引き上げてはならない（§5.3 の限定列挙が唯一の規則）。
> テスト側も同様で、`Api.Tests` は WebApplicationFactory 系（`<Svc>WorkerWebApplicationFactory` と
> 配線テスト）だけになる。

`<Svc>.Api.csproj` を新規作成する（雛形は §7.5）。

> **注（ビルド出力の残骸）**: `git mv` したディレクトリには旧名の `bin/` `obj/` `TestResults/` が
> 残る。`obj/project.assets.json` は旧 csproj 名を指したままだが restore で再生成される。
> リポジトリ外へ `mv` する（**`rm -rf` は `.claude/hooks/guard-bash.js` が禁止**）。
>
> **訂正（第 2 段階で判明・「実害はない」は誤り）**: ビルドと合格数には影響しないが、
> **カバレッジ測定は壊れる**。`bin/Debug/net10.0/` に旧名の `<Svc>.Worker.Tests.dll` と
> `<Svc>.Worker.dll`（＋ `.pdb`）が残ると、coverlet は**それらを「テスト対象アセンブリ」として
> 追加で計装する**（現在のテストアセンブリ以外はすべて計測対象になる）。結果、既に存在しない
> パスのテストソースが 0% 被覆の「本番コード」として合計行数へ加算される。
> 実測: 全 11 サービス移行後に残骸ありで測ると **45.07%（12040 / 26716 行）**、
> `backend` 配下の `bin` `obj` `TestResults` をすべてリポジトリ外へ退避して再測すると
> **64.52%（12061 / 18692 行）**——8024 行が残骸由来の水増しだった。
> **カバレッジを測る前に `bin` / `obj` / `TestResults` を退避して full rebuild すること。**

### 手順 2: テストプロジェクトを割る

```bash
git mv "$S/tests/<Svc>.Worker.Tests" "$S/tests/<Svc>.Infrastructure.Tests"
git mv "$S/tests/<Svc>.Infrastructure.Tests/<Svc>.Worker.Tests.csproj" \
       "$S/tests/<Svc>.Infrastructure.Tests/<Svc>.Infrastructure.Tests.csproj"
mkdir -p "$S/tests/<Svc>.Api.Tests"
# WebApplicationFactory を使うテスト＋Endpoints のテスト＋テスト補助クラスを Api.Tests へ
git mv "$S/tests/<Svc>.Infrastructure.Tests/<Svc>WorkerWebApplicationFactory.cs" "$S/tests/<Svc>.Api.Tests/"
git mv "$S/tests/<Svc>.Infrastructure.Tests/TestAuthHandler.cs"                  "$S/tests/<Svc>.Api.Tests/"
git mv "$S/tests/<Svc>.Infrastructure.Tests/<...>EndpointsTests.cs"              "$S/tests/<Svc>.Api.Tests/"
git mv "$S/tests/<Svc>.Infrastructure.Tests/HealthEndpointTests.cs"              "$S/tests/<Svc>.Api.Tests/"
```

`<Svc>.Api.Tests.csproj` を新規作成する（雛形は §7.5）。

### 手順 3: 名前空間・using の機械置換

```bash
S=backend/Services/<Svc>
# Api 側（Program.cs は file-scoped namespace を持たないが using は書き換わる）
grep -rl 'AiStockTrading.<Short>.Worker' "$S/src/<Svc>.Api" "$S/tests/<Svc>.Api.Tests" \
  | xargs sed -i 's/AiStockTrading\.<Short>\.Worker/AiStockTrading.<Short>.Api/g'
# Infrastructure 側
grep -rl 'AiStockTrading.<Short>.Worker' "$S/src/<Svc>.Infrastructure" "$S/tests/<Svc>.Infrastructure.Tests" \
  | xargs sed -i 's/AiStockTrading\.<Short>\.Worker/AiStockTrading.<Short>.Infrastructure/g'
```

**そのうえで手作業の補正が 2 種類だけ要る**（機械置換では出せない）。

1. **Api → Infrastructure の参照**: Program.cs は Persistence / Composable の型を DI 登録するため、
   `using AiStockTrading.<Short>.Infrastructure.…` が要る（上の置換で `…Api.Foundation.Persistence` /
   `…Api.Composable.…` になってしまう箇所を戻す）。**Program.cs のみで発生する**。
2. **Api.Tests → Infrastructure の参照**: `<Svc>WorkerWebApplicationFactory` は DbContext を差し替えるため
   `using AiStockTrading.<Short>.Infrastructure.Foundation.Persistence;` が要る。DI 配線を検証するテスト
   （CostControl の `CostControlWiringTests` 等）も `…Infrastructure.Composable.Adapters` を参照する。

**判定は「置換後に `AiStockTrading.<Short>.Api.` で始まる `using` が残っていたら疑う」**でよい。Api の
名前空間は `Foundation.Endpoints`（と `Api.Tests`）しか存在しないため、それ以外は戻し忘れである。

> **補正 3（第 2 段階で追加・相対名で型を参照しているファイル。実例 NotificationService `53938ca`）**:
> `using` を一切書かず**相対名**で型を参照しているファイルがある
> （`NotificationWorkerWebApplicationFactory` の `x.AddConsumer<Composable.Steps.OrderExecutedNotificationConsumer>()`）。
> これは名前空間 `AiStockTrading.<Short>.Worker.Tests` の親（`…Worker`）から相対解決していたもので、
> `…Api.Tests` へ移すと壊れる（CS0246）。**`using` の追加だけでは直らない**——C# の
> using ディレクティブは名前空間に含まれる**型**を import するが、**入れ子の名前空間は import しない**ため
> `using AiStockTrading.<Short>.Infrastructure;` を足しても `Composable.Steps.…` は解決しない。
>
> **名前空間別名で解決する**（テスト本文を 1 文字も触らずに済む唯一の手段）:
>
> ```csharp
> using Composable = AiStockTrading.<Short>.Infrastructure.Composable;
> ```
>
> 型を完全修飾名へ書き換える案は、テスト本文の編集になるため採らない（§5.5「本文は 1 文字も変えない」）。

> **補正 4（第 2 段階で追加・暗黙 using の差。実例 ReportService `2e4c6d2`）**:
> 旧 Worker は `Microsoft.NET.Sdk.Web` であり、**Web SDK の暗黙 using**（`Microsoft.Extensions.Hosting` /
> `Microsoft.Extensions.DependencyInjection` / `Microsoft.Extensions.Logging` / `Microsoft.AspNetCore.*` 等）が
> 効いていた。Infrastructure は `Microsoft.NET.Sdk`（暗黙 using は `System` 系のみ）へ変わるため、
> **これらに依存していたファイルが CS0246 で落ちる**（`BackgroundService` / `ILogger<>` /
> `IServiceScopeFactory` が見つからない）。`FrameworkReference` を足しても暗黙 using は復活しない。
>
> **不足している `using` を当該ファイルへ明示的に足して直す**（using 行のみの変更＝振る舞い不変）。
> csproj へ `<Using Include="…" />` を並べて Web SDK の暗黙 using を再現する案は、
> 「どの型がどこから来るか」を隠す方向であり採らない。
> 全サービスで起きるわけではない（多くのファイルは元から using を明示している）。ビルドエラーが出た
> ファイルだけ直せばよい。

> 実務上は、**先に Infrastructure 側を置換 → 次に Api 側を置換 → ビルドエラーが出た箇所だけ直す**のが速い。
> ビルドエラーは「型が見つからない（CS0246）」の形で必ず出るため、見落としが起きない。

### 手順 4: csproj の相互参照を直す

- `<Svc>.Infrastructure.csproj`: `Microsoft.NET.Sdk.Web` → `Microsoft.NET.Sdk` へ変更し
  （`<OutputType>Library</OutputType>` を明示）、`<FrameworkReference Include="Microsoft.AspNetCore.App" />` を足す
  （`BackgroundService` / `IHealthCheck` / `IOptions` 等の共有フレームワーク型を使うため）。
  **`InternalsVisibleTo` は 3 つ**（後述の「必須の落とし穴」）
- `<Svc>.Api.csproj`: §7.5 の雛形
- `<Svc>.Infrastructure.Tests.csproj` / `<Svc>.Api.Tests.csproj`: `ProjectReference` を張り替える。
  `Microsoft.AspNetCore.Mvc.Testing` は **Api.Tests のみ**（Infrastructure.Tests からは外す）
- 他サービス・`backend/Tests/AiStockTrading.IntegrationTests` からの `<Svc>.Worker` 参照を `<Svc>.Api` へ。
  `Aliases="…Worker"`（extern alias）は**名前を変えない**（テスト本文の `extern alias` 行を触らないため）

> **必須の落とし穴 —— `internal` の可視性（パイロットで実際に踏んだ）**
>
> 旧 Worker の永続化・アダプタ実装は `internal` である（`ConfigurationDbContext` / `EfAssumptionsStore` /
> `EfCostLedger` 等）。同一アセンブリだったから Program.cs が DI 登録できていたのであって、
> 分割した瞬間に **CS0122「保護レベルのためアクセスできません」**が出る。
>
> **`public` へ広げてはならない。** 分割前に無かった公開面が生まれ、「再配置＝振る舞いも公開面も変えない」
> という前提が崩れる。代わりに Infrastructure 側へ `InternalsVisibleTo` を 3 つ置く。
>
> ```xml
> <InternalsVisibleTo Include="<Svc>.Api" />
> <InternalsVisibleTo Include="<Svc>.Api.Tests" />
> <InternalsVisibleTo Include="<Svc>.Infrastructure.Tests" />
> ```

### 手順 5: slnx・compose・スクリプトの追随（§5.8）

### 手順 6: 検証

```bash
dotnet build backend/backend.slnx                                  # 0 warning / 0 error
dotnet test  backend/backend.slnx --filter "Category!=Integration"  # 合計 2256・当該サービスの内訳一致
dotnet format backend/backend.slnx --verify-no-changes
node scripts/scripts.test.js && node scripts/check-banned-libraries.js \
  && node scripts/check-test-traceability.js && node scripts/check-consumer-endpoint-names.js \
  && node scripts/validate-runtime-scaffold.js
```

**合格数は「合計」だけでなく「当該サービスのアセンブリ別内訳」を突き合わせる**
（Api.Tests + Infrastructure.Tests = 旧 Worker.Tests。増減の相殺を見逃さないため）。

### 7.5 csproj 雛形

**`<Svc>.Api.csproj`**（旧 Worker の `PackageReference` のうち Program.cs が使うものだけを残す）

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <RootNamespace>AiStockTrading.<Short>.Api</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MassTransit.RabbitMQ" />
    <PackageReference Include="Serilog.AspNetCore" />
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />   <!-- UseNpgsql を Program で呼ぶ場合 -->
    <PackageReference Include="AspNetCore.HealthChecks.NpgSql" />          <!-- 同上 -->
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design">      <!-- dotnet ef の startup project -->
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <ProjectReference Include="..\<Svc>.Application\<Svc>.Application.csproj" />
    <ProjectReference Include="..\<Svc>.Infrastructure\<Svc>.Infrastructure.csproj" />
    <ProjectReference Include="..\..\..\..\TestSupport\AiStockTrading.TestSupport.PlatformShim\AiStockTrading.TestSupport.PlatformShim.csproj" />
    <InternalsVisibleTo Include="<Svc>.Api.Tests" />
  </ItemGroup>
</Project>
```

**`<Svc>.Infrastructure.csproj`**（旧 Worker から Sdk と `InternalsVisibleTo` を替え、`FrameworkReference` を足す）

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <RootNamespace>AiStockTrading.<Short>.Infrastructure</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <!-- 旧 Worker の PackageReference をそのまま引き継ぐ（EF/Npgsql/MassTransit 等） -->
    <ProjectReference Include="..\<Svc>.Application\<Svc>.Application.csproj" />
    <!-- 旧 Worker が持っていた他の ProjectReference をそのまま引き継ぐ -->
    <InternalsVisibleTo Include="<Svc>.Infrastructure.Tests" />
  </ItemGroup>
</Project>
```

## 8. 段階分割とコミット単位

**1 サービス = 1 コミット**（レビュー可能な粒度。IADR-0126 決定 1 の「1 issue = 1 PR」の下位分割）。
PR は #353 に対して 1 本（第 1〜3 段階を通しで積む）。

| 段階 | 内容 | コミット |
| --- | --- | --- |
| **1**（本セッション） | 作業仕様書・IADR-0128 | `docs(NFR,IADR-0128): 標準プロジェクト構成への再配置の作業仕様と実装ADR を定める` |
| | アーキテクチャテスト基盤 | `test(NFR): Domain 層の外部依存ゼロを強制するアーキテクチャテストを追加する` |
| | ConfigurationService | `refactor(NFR): ConfigurationService を標準構成へ再配置する` |
| | CostControlService | `refactor(NFR): CostControlService を標準構成へ再配置する` |
| **2** | 残り 9 サービス（Audit / Backtest / InformationCollection / MarketMonitor / Notification / OrderExecution / Report / RiskManagement / TradeDecision） | 各 1 コミット `refactor(NFR): <Svc> を標準構成へ再配置する` |
| **3** | `docs/tech/tech-requirements.md` の構成節・`docs/adr/README.md`・本仕様書の検証結果更新・総検証 | `docs(NFR): 標準プロジェクト構成を技術要件書へ反映する` |

すべてのコミット footer に `Refs #353` を付す。

**推奨順序（第 2 段階）**: 小さい順に Audit → Backtest → InformationCollection → Notification →
TradeDecision → MarketMonitor → Report → OrderExecution → RiskManagement。
RiskManagement（Worker 51 ファイル・テスト 149 件）を最後に置くのは、そこまでにレシピが枯れているようにするため。

**プロジェクト数の推移**（`backend.slnx` の `<Project Path=` 実測）

| | 移行前（`b4b4096`） | 第 1 段階完了時 | 全段階完了時（見込み） |
| --- | --- | --- | --- |
| サービス本番（`Services/*/src`） | 32（Domain 9・Application 11・Worker 11・Client 1） | 34 | 43（Worker→Infrastructure 11 ＋ Api 11） |
| サービステスト（`Services/*/tests`） | 32 | 34 | 43 |
| その他（Shared 6・TestSupport 2・Bff 2・Tests 2） | 12 | 13（アーキテクチャテスト +1） | 13 |
| **合計** | **76** | **81** | **99** |

> issue 本文の「76 → 約 130」は「7 プロジェクトを常に作る」前提の見積もりである。§5.2 の決定により
> 実数は **76 → 99**（1 サービスあたり +2＝Api とその Tests）となる。

## 9. 受け入れ基準

issue [#353](https://github.com/endazon/ai-stock-trading/issues/353) の受け入れ基準を写し、第 1 段階での状況を併記する。

- [x] 全 11 サービスが標準構成に揃っている（第 1 段階: 2 / 11 → **第 2 段階完了時 11 / 11**）
- [x] **Domain 層の外部依存ゼロ**をアーキテクチャテストが強制する
- [x] 不採用ライブラリ（AutoMapper / Mapster）の混入を CI が検知する
      — **#351 で対応済み**。`scripts/check-banned-libraries.js` の `BANNED` に
      `AutoMapper` / `Mapster`（＋ `MediatR` / `FluentAssertions`）が登録済みで、
      `.csproj` / `Directory.Packages.props` / `*.cs` の `using` を走査する。**本 issue での追加作業は無い**
- [ ] `dotnet build` / `dotnet test`（`Category!=Integration`）が**再配置前と同一の合格数**で green
      （第 1 段階時点で確認済み＝既存 2256 件が不変。全段階完了時に再確認）
- [ ] カバレッジが floor を下回らない（#343）
      （第 1 段階時点で 64.47%・移行前と行数まで一致。全段階完了時に再確認）
- [ ] `docs/tech/`（技術要件書）が新標準を反映している（第 3 段階）

## 10. テスト方針・検証結果（第 1 段階）

本作業はテストの意味を変えないため、**合格数の完全一致**が受け入れの中心である。

### 基準値（移行前・`b4b4096`）

**2256 passed / 0 failed / 39 アセンブリ**（`dotnet test backend/backend.slnx --filter "Category!=Integration"`）。

| 対象 | 方法 | 結果 |
| --- | --- | --- |
| **合格数の一致** | 移行前後の合計と**アセンブリ別内訳の差分** | 合計 **2256 → 2260**。増分 4 は**新設したアーキテクチャテストの 4 件のみ**であり、既存テストは 2256 のまま（Failed=0）。アセンブリ別内訳の diff は次の 5 行だけで、他 37 アセンブリは 1 件も動いていない: `+AiStockTrading.Architecture.Tests 4` / `-ConfigurationService.Worker.Tests 13` → `+Api.Tests 8` `+Infrastructure.Tests 5` / `-CostControlService.Worker.Tests 40` → `+Api.Tests 9` `+Infrastructure.Tests 31`（**13 = 8+5・40 = 9+31 で完全一致**） |
| アセンブリ数 | 同上 | 39 → **42**（Worker.Tests 2 件が割れて +2、アーキテクチャテスト +1） |
| ビルド | `dotnet build backend/backend.slnx` | **0 Warning / 0 Error** |
| アーキテクチャテストの変異確認 | Domain csproj へ故意に禁止参照を追加 → 実行 → 復元 | §10.1（3 変異すべて検出） |
| 整形 | `dotnet format backend/backend.slnx --verify-no-changes` | 差分なし（終了コード 0） |
| リポジトリ検査 | `scripts.test.js`（142 passed）/ `check-banned-libraries.js` / `check-test-traceability.js`（316 ファイル・25 ID）/ `check-consumer-endpoint-names.js`（consumer 47 件・衝突なし）/ `validate-runtime-scaffold.js`（Worker 11）/ `check-action-versions.js` | すべて OK |
| カバレッジ | `--collect:"XPlat Code Coverage"` ＋ `check-coverage.js` | **64.47%（12051 / 18692 行・レポート 42 件）**。移行前の記録（`coverage-floor.json` の `measuredLineRate` 0.6447・12051/18692 行）と**行数・被覆行数とも完全一致**。floor 62.00% を上回る。ソースに実質的な変更が無いことの傍証になる |

> **カバレッジ測定時の注意**: `TestResults/` は過去の実行分が残る。古いレポート（旧パスの `.Worker` を指すもの）が
> 混ざると合計行数が水増しされる（実測で 74 レポート・19173 行・62.91% になった）。測り直す前に
> `backend` 配下の `TestResults/` をリポジトリ外へ退避すること。

**consumer のキュー名が不変であることの確認**: `check-consumer-endpoint-names.js` が 47 件の consumer を
走査して衝突ゼロ。名前空間を変えた `LlmCostIncurredConsumer`（CostControl）もクラス名は不変であり、
`DefaultEndpointNameFormatter` が導くキュー名 `LlmCostIncurred` は変わらない（§5.7）。

### 10.1 アーキテクチャテストの変異確認（実施記録）

`ConfigurationService.Domain.csproj` に一時的に次を追加し、検査が落ちることを確認したうえで復元した。

| 変異 | 期待 | 結果 |
| --- | --- | --- |
| `<PackageReference Include="Microsoft.EntityFrameworkCore" />` を Domain へ追加 | 検査 1 が失敗 | **失敗した**（違反プロジェクト名とパッケージ名がメッセージに出る） |
| `<ProjectReference … ConfigurationService.Application.csproj />` を Domain へ追加 | 検査 2 が失敗 | **失敗した** |
| `AiStockTrading.Shared.Contracts.csproj` へ `<PackageReference Include="Microsoft.EntityFrameworkCore" />` を追加 | 検査 3（推移閉包）が失敗 | **失敗した**（検査 1 では検出できない迂回を捕まえた） |

### 10.2 第 2 段階（残り 9 サービス）の検証結果

移行順: Audit → Backtest → InformationCollection → Notification → TradeDecision → MarketMonitor →
Report → OrderExecution → RiskManagement（§8 の推奨順。1 サービス = 1 コミット）。

| 対象 | 方法 | 結果 |
| --- | --- | --- |
| **合格数の一致** | 第 1 段階完了時（2260 / 42 アセンブリ）との**アセンブリ別内訳の差分** | 合計 **2260 で不変**（Failed=0）。差分は 9 行の `Worker.Tests` が消えて 18 行の `Api.Tests` / `Infrastructure.Tests` が現れただけで、**9 サービスすべてで旧＝新の和が一致**（Audit 19=6+13 / Backtest 51=7+44 / InformationCollection 73=12+61 / MarketMonitor 35=18+17 / Notification 70=1+69 / OrderExecution 124=1+123 / Report 100=35+65 / RiskManagement 149=62+87 / TradeDecision 186=48+138）。他 33 アセンブリは 1 件も動いていない |
| アセンブリ数 | 同上 | 42 → **51**（Worker.Tests 9 件が割れて +9） |
| ビルド | `dotnet build backend/backend.slnx --no-incremental` | **0 Warning / 0 Error** |
| アーキテクチャテスト | 4 件 green（Domain 9 件の探索空振り防止テストを含む） | 合格 |
| カバレッジ | `--collect:"XPlat Code Coverage"` ＋ `check-coverage.js`（**bin/obj/TestResults を退避して full rebuild 後**・§7 手順 1 の訂正） | **64.52%（12061 / 18692 行・レポート 51 件）**。総行数は第 1 段階と**完全一致**（18692）。floor 62.00% を上回る |
| 整形 | `dotnet format backend/backend.slnx --verify-no-changes` | 差分なし |
| リポジトリ検査 | `scripts.test.js`（142 passed）/ `check-banned-libraries.js` / `check-test-traceability.js`（316 ファイル・25 ID）/ `check-consumer-endpoint-names.js`（consumer 47 件・衝突なし）/ `validate-runtime-scaffold.js`（Worker 11）/ `check-commit-messages.js` | すべて OK |

**第 2 段階で追加した手作業補正は 2 種類**（いずれも §7 手順 3 に追記済み）:

1. **相対名（`Composable.Steps.*`）参照** — Notification / TradeDecision（11 ファイル）/ MarketMonitor（2）/
   OrderExecution（1）/ RiskManagement（1）。名前空間別名で解決した。
2. **Web SDK の暗黙 using** — Report（1 ファイル）/ RiskManagement（3 ファイル）の常駐 `BackgroundService`。
   不足 using を明示した。

**サービス固有の追随**:

- `AiStockTrading.IntegrationTests` は OrderExecution（無名参照）・Report（`ReportWorker` 別名）を Api へ。
  RiskManagement は **Api と Infrastructure の双方に同じ別名 `RiskManagementWorker` を与えた**
  （`Program` は Api、`#305`/IADR-0124 の並行トークン E2E が使う `internal` 永続化型は Infrastructure に
  分かれたため。`InternalsVisibleTo Include="AiStockTrading.IntegrationTests"` は Infrastructure が引き継ぐ）。

## 11. 計画書との差異

- **差異: あり（2 点。いずれも ADR-0030 に反しない範囲の実装判断）**

1. **7 プロジェクトのうち `SharedKernel` を作らず、`Contracts` はユニット単位で 1 つとする**（§5.2）。
   ADR-0030 の但し書き「過度な共通化は避ける」・選定基準 2「標準機能優先」・platform ADR-0019 決定 4 を
   根拠とする。決定の正本は [IADR-0128](../adr/IADR-0128_standard-project-layout.md)。
2. **`ConfigurationService.Client` という標準外プロジェクトを残す**（§5.1）。7 標準のどの層にも当たらない
   「他サービスへ公開するクライアントライブラリ」であり、畳むと HTTP・キャッシュ・DI 拡張が
   `Contracts` か `Infrastructure` に紛れ込んで意味が崩れる。

いずれも計画側へ環流すべき論点を含む（ADR-0030 が per-service Contracts / SharedKernel を必須とするのか、
サービス公開クライアントの置き場所をどう定めるのか）。第 3 段階で `/plan-feedback` に出す（§12 未決事項 3・6）。

## 12. 未決事項

1. **ライブラリ標準への追随（Riok.Mapperly・FluentValidation・Polly・ProblemDetails）を本 PR で行わない**。
   issue #353 のスコープ節には列挙されているが、いずれも**新規導入または既存コードの書き換え**であり、
   受け入れ基準「再配置前と同一の合格数で green」と両立しない（マッピングを Mapperly へ替えれば
   生成コードの差でテストが動く可能性があり、ProblemDetails 化は HTTP 応答本文が変わる＝**振る舞いの変更**）。
   CLAUDE.md の「計画外の大規模リファクタ・過剰な抽象化を行わない」にも抵触する。
   **後続 issue の提案**: 「(a) 手書きマッピングの Riok.Mapperly 化」「(b) 入力検証の FluentValidation 統一」
   「(c) `Microsoft.Extensions.Http.Resilience` への集約」「(d) 例外応答の標準 ProblemDetails 化」を
   4 件に分けて起票する（いずれも振る舞いの変更を伴うため、変更点を個別にレビューできる粒度に割る）。
   なお **Polly / ProblemDetails は現状の採否そのものが未調査**であり、起票前に実測が要る。
2. **SharedKernel（自前 `Result` / `Error`）の導入**。ADR-0030 が Result 型の置き場所として定義した
   プロジェクトであり、導入すれば例外ベースの現行コードを広範に書き換えることになる。独立 issue。
3. **基盤リポ（microservices-platform）実装の実構成との突合**。IADR-0001 が「揃える先は基盤実装リポ」と
   定めている以上、基盤が per-service に `Contracts` / `SharedKernel` を置いているかを確認し、
   食い違うなら §5.2 の決定を見直す。本セッションでは基盤リポが参照範囲外のため未確認。
4. **Application 層の依存規律の機械検査**。本 issue のアーキテクチャテストは Domain のみを対象とする。
   「Application が EF Core / ASP.NET へ依存しない」も検査可能だが、現行の成立を実測していないため
   本 issue では入れない（落ちる検査を入れると検査ごと無視される。`check-banned-libraries.js` の
   PENDING 節と同じ考え方）。
5. **Domain → 他サービス Domain の参照 4 件**（Backtest → Configuration / RiskManagement、
   CostControl → Configuration、Report → Configuration）。サービス境界（platform ADR-0002）の観点では
   共有カーネルの明示化が望ましいが、再配置ではなくドメイン設計の変更になるため本 issue では触れない。
6. **サービス公開クライアント（`*.Client`）の標準上の位置づけ**。#354（Wolverine）・ADR-0029（gRPC/REST 基準）で
   サービス間同期呼び出しの形が変わる際に再検討する。
7. **`Foundation` / `Composable` のフォルダ階層の要否**。Infrastructure 配下の `Foundation/Persistence` は
   階層が重複気味だが、platform ADR-0018 の固定/可変区分に対応する既存の意味づけであり本 issue では変えない。
8. **`Worker` を含む名前の残骸**。`<Svc>WorkerWebApplicationFactory`（テスト補助クラス）と
   `AiStockTrading.IntegrationTests` の `extern alias CostControlWorker` / `RiskManagementWorker` /
   `ReportWorker` は、テスト本文を触らないため据え置いた。改名はテストファイルの一括編集になり、
   「テストの表明を変えない」の確認コストに見合わない。#354 以降で名前の整理をまとめて行う。
9. **`dotnet ef` の実走確認が未了**。本セッションの環境に `dotnet-ef` ツールが入っておらず
   （CI・`scripts/` も使用していない＝Migration は開発者が手元で追加している）、
   分割後に `dotnet ef migrations add` が通ることを**実走では確認できていない**。
   根拠づけは「DbContext と `Migrations/` を同じ Infrastructure へ一緒に移したため既定の
   migrations assembly は保たれる」「`Microsoft.EntityFrameworkCore.Design` を Api（startup project）と
   Infrastructure（target project）の両方に置いた」という設計上のものに留まる。
   次に Migration を追加する担当者は `dotnet ef migrations add <名前> -p <Svc>.Infrastructure -s <Svc>.Api` の
   形で実走し、通らなければ本仕様書へ追記すること。
10. **Program.cs 直書きのエンドポイント**。`Foundation/Endpoints/` を持たないサービス
   （Backtest / InformationCollection / Notification / OrderExecution / TradeDecision）は
   エンドポイント定義が Program.cs にある。Vertical Slice としての切り出しは再配置の範囲を超えるため
   本 issue では行わない。

## 変更履歴

| 日付 | 内容 |
| --- | --- |
| 2026-08-03 | 初版作成（#353・第 1 段階着手前） |
| 2026-08-03 | 第 1 段階（アーキテクチャテスト・パイロット 2 サービス）の実施結果を §10 へ反映。パイロットで判明した `internal` の可視性の落とし穴（§5.7・§7 手順 4）とプロジェクト数の実測（§8）を追記 |
| 2026-08-03 | 第 2 段階（残り 9 サービス）完了。§10.2 に検証結果、§7 に補正 3（相対名参照）・補正 4（Web SDK の暗黙 using）・ビルド残骸がカバレッジ測定を壊す件の訂正、Endpoints を持たないサービスの移行形の補足を追記 |
