---
title: IADR-0261 サービスの名前空間を基盤（MSP）の規則へ完全整合させる
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0259, IADR-0256, IADR-0258, IADR-0260, IADR-0129, MSP:IADR-0282]
author: endazon (with Claude Code)
created: 2026-08-28
updated: 2026-08-28
---

# IADR-0261: サービスの名前空間を基盤（MSP）の規則へ完全整合させる

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-08-28
- 決定者: endazon（方針・[IADR-0259](IADR-0259_single-project-vsa-structure.md) 決定 5 の利用者裁定）/ Claude Code（実施の設計）

## 起点・関連

- **起点 ID: `NFR`（無採番）。** 構造整備＝メタ作業であり、`.claude/rules/traceability.md`「起点 ID の種別」の
  無採番許容ケース **2** に当たる。計画の非機能要件表を読み直したが名前空間の命名規則に当たる番号は無い。
  **環流はしない**（ケース 2）。
- **上流**: [IADR-0259](IADR-0259_single-project-vsa-structure.md) 決定 5③（「名前空間も MSP へ完全整合する。
  実施は別 PR＝想定 `IADR-0261`」）。揃える先は `MSP:IADR-0282` 決定 3（ルート名前空間は `<Name>`。`.Api` を含まない）。
- **作業仕様書**: [20260828_w11s3_namespace-alignment](../specs/20260828_w11s3_namespace-alignment.md)。
- **`Closes`**: [#580](https://github.com/endazon/ai-stock-trading/issues/580)（`pipeline.json` の死んだ consumer 参照。決定 5）。

## コンテキストと課題

[IADR-0259](IADR-0259_single-project-vsa-structure.md) 決定 5 は「名前空間の完全整合は独立した専用の波で行う」と
確定した。本 ADR はその波（W11 段 3）の実施記録である。**本 PR は機械変換だけを行い、論理変更を含まない。**
フォルダ・csproj の再配置（`Features/` 樹形への移送）は次の波で行う —— C# は名前空間とフォルダが独立なので先行できる。

着手時の実測（2026-08-28・`d5ac5c3` の作業ツリー）:

| 対象 | 実測 |
| --- | ---: |
| `namespace` 宣言（`backend/**/*.cs`） | **1,292**（サービス 1,081 ＋ 据え置き 210 ＋ テスト用ソース文字列内 1） |
| うち `.Foundation` / `.Composable` を含む | **235**（サービス **204** ／ 据え置き **31**） |
| csproj の `<RootNamespace>` | **100**（サービス 86 ／ 据え置き 14） |
| EF エンティティの CLR 型名文字列 | **35 種**（`*ModelSnapshot.cs` 7 個 ＋ `*.Designer.cs` 35 個。**35/35 が `AiStockTrading.<Short>.Infrastructure.Foundation.Persistence.*`**） |

## 決定

### 決定 1 — 規則 A と規則 B を**同時に**適用する

- **規則 A**: `AiStockTrading.<Short>.…` → `<Short>Service.…`（11 サービス）。`MSP:IADR-0282` 決定 3 と同じ規則。
- **規則 B**: `.Foundation` / `.Composable` の**名前空間セグメントを除去**する。
  例: `AiStockTrading.Audit.Infrastructure.Foundation.Persistence` → `AuditService.Infrastructure.Persistence`。
  🔴 **フォルダ名（`Foundation/` `Composable/` ディレクトリ）は動かさない。**

🔴 **同時に当てる理由**: EF の `*ModelSnapshot.cs` はエンティティ型を**完全修飾名の文字列**で持ち、
35/35 が `Foundation` 段を含む。規則 A だけを当てると `Foundation` 段が残り、
**次の波で `Foundation/` を廃止した時点で型名文字列をもう一度書き換えることになる**。
EF の型名書き換えは「一度きり・その場で drift 検証」でなければ危ない。

### 決定 2 — 据え置き集合は 1 文字も変えない（規則 B より優先する）

`AiStockTrading.Shared.*`（156）/ `AiStockTrading.TestSupport.*`（32）/ `AiStockTrading.Architecture.*`（10）/
`AiStockTrading.IntegrationTests.*`（8）/ `AiStockTrading.Bff.*`（4）＝ **210 宣言**は変えない。基盤も
`Platform.Shared.*` を据え置いている。**据え置き集合の中にある `.Foundation` / `.Composable` セグメント
31 件も規則 B の対象外**である（「1 文字も変えない」が優先する。移送波が Shared に及ぶときに一緒に扱う）。

### 決定 3 — 🔴 wire 契約は変えない。本変換でも変わらない

Wolverine のメッセージ識別子は `messageType.ToMessageTypeName()` ＝**名前空間込みの完全名**であり、
exchange 名・binding key・封筒の `message-type` ヘッダそのものである（[IADR-0129](IADR-0129_wolverine-messaging-topology.md) 決定 2）。
`EventMessageTypeNameTests` が固定する 45 件のうち 44 件は `AiStockTrading.Shared.Contracts.Events.*`、
残り 1 件はテスト内の当て馬 `MovedNamespaceProbe` である。ハンドラの引数型 45 種のうち
`Shared.Contracts/Events/` に無いのはテスト内ローカル型 1 件（`AiStockTrading.TestSupport.PlatformShim.Tests`）だけ。
**いずれも据え置き集合**であるため、本変換で wire 契約は 1 件も動かない。

🔴 **`EventMessageTypeNameTests.cs` と `event-schemas.baseline.json` は 1 文字も変えない**（実測: 本 PR の
diff は 0 行）。**これらが赤くなったら「変えてはいけない名前空間を変えた」という警報であり、
期待値の側を直して緑にすることを固く禁じる。**

同じく **`MigrationId`（migration のクラス名・`[Migration("…")]` 属性・ファイル名）は変えない** ——
`__EFMigrationsHistory` は `MigrationId` で突合するため、変えると既存 DB との整合が壊れる。
アセンブリ名・プロジェクト名・`ServiceName` 定数の文字列リテラル・Meter 名 `AiStockTrading.Business` も不変である。

### 決定 4 — 🔴 ルート名前空間と同名のクラス 6 件は `<Svc>AppService` へ改名する

**AST 固有の障害である**（設計書にも MSP の記録にも無い。MSP は移送済み全サービスで同名型 0 件）。
`<Svc>Service.Application.Services.<Svc>Service` という形が 6 サービスに実在した。

`namespace ReportService.Application.Services;` の中に `class ReportService` が居ること**自体はビルドが通る**が、
**そのクラスが可視な場所から修飾名 `ReportService.Domain.X` を書くと `error CS0426` になる**
（最寄りスコープのクラスが先に解決される）。`using ReportService.Domain;` ＋非修飾の使用は通る。

| 案 | 評価 |
| --- | --- |
| **(a) 6 クラスを改名する（採用）** | ○ 衝突源を断ち、将来の修飾名の書き方に制約を残さない ○ **MSP には衝突が 1 件も無く、揃えることが整合である** ○ 次の波で `Application/Services` は Handler へ吸収されて消える予定であり、どのみち触る ○ ビルドが全件を捕まえるので取りこぼしが起きない |
| (b) 据え置いて `CS0426` の箇所だけ非修飾 / `global::` へ直す | × 地雷が残る（次に修飾名を書いた人が踏む）。× `<Svc>Service.Application.Services.<Svc>Service` という読みづらい完全名が恒久化する |

**改名規則は `<Svc>Service` → `<Svc>AppService` で統一する。** 一件ずつ意味を汲んだ名前
（`ReportComposer` 等）を付ける案は退けた —— **本 PR は機械変換であり、意味の再解釈を混ぜない**。
実際 `ReportService` は報告書を「合成」しない（LLM ドラフト生成は `ReportDraftService` の役割である）ため、
意味を汲んだつもりの名前は容易に**意味を変えてしまう**。`AppService` は「アプリケーション層のサービス」という
**そのクラスが実際に置かれている層**を言い直しただけで、意味を一切足さない。

| 現行クラス | 新クラス |
| --- | --- |
| `ReportService` | `ReportAppService` |
| `TradeDecisionService` | `TradeDecisionAppService` |
| `OrderExecutionService` | `OrderExecutionAppService` |
| `InformationCollectionService` | `InformationCollectionAppService` |
| `MarketMonitorService` | `MarketMonitorAppService` |
| `CostControlService` | `CostControlAppService` |

- 🔴 **サービス名の文字列リテラル**（キュー名・構成キー `Services:MarketMonitorService`・BFF の `ClientName`・
  語彙表）は**改名しない**。参照行を 1 行ずつ判定した（実測 89 行のうちコード参照は 20 行で、
  残りは「サービスを指す散文」か文字列リテラルであった）。
- **テストクラス名・テストファイル名は変えない**（`ReportServiceTests` 等）。衝突しておらず、
  改名は純粋な churn である。これらは**サービス**の名前を冠していると読む。

### 決定 5 — `pipeline.json` の死んだ consumer 参照は実体へ是正し、[#580](https://github.com/endazon/ai-stock-trading/issues/580) を閉じる

`deploy/helm/ai-stock-trading/files/pipeline.json` の 5 つの `consumer` 型名は
`AiStockTrading.<Svc>.Worker.Composable.Steps.<Event>Consumer` を名乗っていたが、**宣言されている型は 1 つも無かった**
（実在は `…<Svc>.Infrastructure.Composable.Steps.<Event>Handler`。`validate-pipeline-config.js` は形式しか見ないため緑のまま通っていた）。
**機械的に置換すると「死んだ参照を別の死んだ参照へ書き換える」だけになる**ため、実体へ合わせて是正した。
5 件すべてについて、新名前空間・クラス名の型がソースに実在することを確認済みである。

### 決定 6 — Domain 依存規律の走査器（[IADR-0256](IADR-0256_domain-dependency-inspection-by-source-scan.md)）の名前空間モデルを差し替える

🔴 **これは親の指示一覧にも設計書にも無く、母集合を自分で引き直して見つけた破壊である。**
`DomainSourceScan` は「根が `AiStockTrading` か `System` でなければ**外部ライブラリの根**」という前提で
検査 (c) の禁止トークンを導いていた。名前空間の根が `<Svc>Service` になると、**サービスの根が禁止トークンへ混入し、
Domain の全ファイルが自分自身の `namespace` 宣言で違反になる。**

- `DomainSourceScan` の 3 判定（`IsAllowedDomainNamespace` / `ForeignServiceReferencesIn` /
  `ExternalNamespaceRootsIn`）を、**実ツリーから引いたサービスのルート名前空間の集合**
  （`RepositoryLayout.ServiceNamespaceRoots` ＝ `backend/Services` のディレクトリ名）を受け取る形へ改めた。
  `UnitNamespace` は `SharedNamespace`（据え置き集合の接頭辞）へ改名した。
- 🔴 **「末尾が `Service` の識別子」という形の判定は採らない。** Domain のコメントに
  `StageGateService.EffectivePolicy()` / `RiskSettingsService.UpdateGuard` / `ReportService.GetConfirmedDailyPolicy`
  の 3 件が実在し、**クラス名を他サービスの名前空間の根と誤認する**（実測）。
  一覧を手で書かず実ツリーから引くので、サービスの増減に自動で追随する。
- 許可判定は**厳しくなった**（実在しないサービス名の `PhantomService.Domain` を拒む否定形テストを追加）。
  `KnownForeignReferences` は **空のまま**である（[IADR-0260](IADR-0260_shared-kernel-for-cross-service-domain-types.md) の成果を保った）。
- `SharedKernelIsLeafTests` / `SharedProjectDependencyTests` / `DomainLayerDependencyTests` は
  **プロジェクト名**で判定しており、本 PR はプロジェクト名を変えないため不変である。

### 決定 7 — 合否判定は `has-pending-model-changes` で行う

[IADR-0259](IADR-0259_single-project-vsa-structure.md) 決定 5③3 は受け入れ基準を
`dotnet ef migrations add __VerifyNoDrift` の空差分と定めたが、**`dotnet ef migrations has-pending-model-changes`
へ置き換える**。判定は同値でありながら**ファイルを生成しないので後片付けが要らない**（消し忘れた scaffold が
migration 列へ紛れ込む事故を構造的に無くす）。なお `dotnet ef migrations remove` は DB 接続を試みて
`Failed to connect to 127.0.0.1:5432` で落ちるため使えない。

**実測: 7 つの DbContext すべてで「No changes have been made to the model since the last migration.」**
（`Audit` / `Configuration` / `CostControl` / `MarketMonitor` / `OrderExecution` / `Report` / `RiskManagement`）。

## 実施の結果（実測）

- `namespace` 宣言: サービス **1,081** を変換、据え置き **210** は不変。`.Foundation` / `.Composable` の
  残存は据え置き集合の **31** のみ。`<RootNamespace>` は 86 を変換。EF 型名文字列 35 種を変換し `MigrationId` は不変。
- 🔴 **コンパイラが 18 件の「部分修飾」を捕まえた**（親の一覧にも無かった）。
  `namespace AiStockTrading.RiskManagement.Infrastructure.Tests` の中で `Shared.Contracts.Trading.X` と
  書いた箇所は、**囲む `AiStockTrading` 名前空間を経由して解決していた**。根が変わると解決しないため、
  6 ファイル 18 箇所を `AiStockTrading.` から完全修飾へ直した。**`using` の走査では見つからない種類の依存である。**
- テスト: **失敗は `AiStockTrading.IntegrationTests` の 8 件のみ**（Docker 不在の環境制約）。
  `Architecture.Tests` は 74 件緑（否定形 1 件を追加したため 73 → 74）。

## 影響・残余リスク

- **アセンブリ名・プロジェクト名は変えていない**ため、`InternalsVisibleTo`（47 件）・`.slnx`・
  Dockerfile の `SERVICE_PROJECT` / `SERVICE_DLL`・CI のパスはいずれも不変である。
- **凍結記録は書き換えていない。** `.ai-context/specs/` `.ai-context/superpowers/` は対象外。
  `.ai-context/adr/` に旧名前空間が残る 5 ファイル 9 行（[IADR-0106](IADR-0106_consumer-endpoint-name-uniqueness.md) の旧名の引用・
  [IADR-0122](IADR-0122_per-model-llm-pricing.md) の移送前後の記述・[IADR-0168](IADR-0168_tracked-session-timeout-budget.md) の
  貼り付けたテスト失敗ログ・[IADR-0219](IADR-0219_report-llm-cost-metering-point.md) の新設型の決定・
  [IADR-0260](IADR-0260_shared-kernel-for-cross-service-domain-types.md) の土台 5 実測表）は
  **当時の実測・当時の決定の記録**であり、書き換えると記録が当時と食い違うため触っていない。
- 同じ理由で、コード内の「**元は `InformationCollection.Application.Ports`**」のような**移設の由来を述べる
  コメント 3 件は書き換えていない**（当時の所在を述べており、新名前空間へ直すと事実に反する）。
- `TestAuthHandler.cs` 4 件の `RiskManagement.Worker.Tests 準拠` は**本 PR 以前から死んだ参照**である
  （`Worker` プロジェクトは存在しない）。本 PR で新たに誤りになったものではないため射程外とした。
- **次の波（フォルダ・csproj の再配置）で `Foundation/` `Composable/` ディレクトリを廃止するとき、
  EF の型名文字列を再度書き換える必要は無い**（決定 1 の目的）。
