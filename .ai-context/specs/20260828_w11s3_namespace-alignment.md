---
title: 名前空間を基盤（MSP）の規則へ完全整合させる（W11 段 3・機械変換）
type: spec
status: approved
related_ids: [NFR]
author: endazon (with Claude Code)
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/06_technical/12_backend-application-stack.md
---

# 仕様書: 名前空間の基盤整合（`IADR-0259` 決定 5 の実施）

> 本仕様書は実装着手前に作成した。**本 PR は機械変換だけを行い、論理変更を一切含めない。**
> メソッドの中身・振る舞い・テストの意図は変えない。フォルダ・csproj の再配置は次の波で行う。

## 起点

- 起点 ID: **`NFR`（無採番）**。構造整備＝メタ作業であり、`.claude/rules/traceability.md`
  「起点 ID の種別」の無採番許容ケース **2**（ID 列はあるが当たる番号が無い）に当たる。
  計画の非機能要件表（`NFR-01`〜`NFR-17`）を読み直したが、名前空間の命名規則を扱う番号は無い。
  `NFR-16`（拡張性・ポートによる差し替え）が最も近いが当たらない。同ケース 2 は環流しないと定める。
- 直接の上流: `IADR-0259` 決定 5③（撤回・確定「名前空間も MSP へ完全整合する。実施は別 PR＝想定 `IADR-0261`」）。
  土台は `IADR-0256`（Domain 依存規律のソース走査）・`IADR-0257`・`IADR-0258`・`IADR-0260`。
  揃える先は `MSP:IADR-0282` 決定 3（ルート名前空間は `<Name>`。`.Api` を含まない）。

## 変換規則

### 規則 A: サービスの接頭辞を `<Name>Service` へ

`AiStockTrading.<Short>.…` → `<Short>Service.…`（11 サービス）。

### 規則 B: `.Foundation` / `.Composable` の名前空間セグメントを除去

`AiStockTrading.Audit.Infrastructure.Foundation.Persistence` → `AuditService.Infrastructure.Persistence`。
**フォルダ名（`Foundation/` `Composable/` ディレクトリ）は動かさない。** 名前空間だけを変える。

**規則 A と B を同時に適用する理由**: EF の `*ModelSnapshot.cs` はエンティティ型を完全修飾名の
**文字列**で持つ。35/35 が `AiStockTrading.<Short>.Infrastructure.Foundation.Persistence.<X>Row` である。
規則 A だけを当てると `Foundation` 段が残り、次の波で `Foundation/` を廃止した時点で型名文字列を
**もう一度**書き換えることになる。EF の型名書き換えは「一度きり・その場で drift 検証」でなければ危ない。

### 🔴 据え置き（1 文字も変えない）

`AiStockTrading.Shared` / `AiStockTrading.TestSupport` / `AiStockTrading.Architecture` /
`AiStockTrading.IntegrationTests` / `AiStockTrading.Bff`。MSP も `Platform.Shared.*` を据え置いている。
**据え置き集合の中にある `.Foundation` / `.Composable` セグメント（実測 31 宣言）も規則 B の対象外**とする
——「1 文字も変えない」が規則 B に優先する。親の実測 1,289 = サービス 1,079 ＋ 据え置き 210 も同じ分割である。

## 母集合の引き直し（規則 1〜10。着手前に自分で引いた実測）

計測は 2026-08-28・`d5ac5c3`（`origin/develop` 起点）の作業ツリー。**shallow clone のため
`git log` / `git blame` は出典に使っていない**（`git rev-parse --is-shallow-repository` = `true`）。

| # | 対象 | 見つけた数 | 直す数 | 除外と理由 |
| ---: | --- | ---: | ---: | --- |
| 1 | `namespace` 宣言（`backend/**/*.cs`） | **1,292** | **1,081** | 据え置き 5 根で **210**。C# 文字列リテラル中の擬似宣言 **1**（`EventMessageTypeNameTests.cs` の入れ子 `namespace MovedNamespaceProbe`＝据え置き集合内）。`DomainSourceDependencyTests.cs` の**テスト用ソース文字列内 2 件**は「1,081」に含めて別途手当てする（下記 §9） |
| 2 | `using` ディレクティブ | 変換対象と同一の正規表現で一括置換 | — | 据え置き根への `using` は不変 |
| 3 | 本文中の（部分）修飾名 | **32 行**（`namespace`/`using` 行・EF 生成物を除く） | 32 | — |
| 4 | csproj の `<RootNamespace>` | **100** | **86** | 据え置き 14 |
| 5 | EF の CLR 型名文字列 | エンティティ **35 種** / `*ModelSnapshot.cs` **7** / migration 本体 **35** | 35 種すべて | `MigrationId`（クラス名・`[Migration("…")]`・ファイル名）は**不変** |
| 6 | `*.Designer.cs` | **35** | 型名文字列のみ | 同上 |
| 7 | `InternalsVisibleTo` / `[assembly:]` | **アセンブリ名を持つ 47 件**（`MarketMonitorService.Api` 等） | **0** | アセンブリ名・プロジェクト名は本 PR で変えないため対象外 |
| 8 | 検査器（`scripts/*.js`） | 名前空間を**仮定して分岐する**箇所 **0**、名前空間の据え置きを**明記した散文** **1**（`check-consumer-endpoint-names.js`） | 散文 1 | `UseAiStockTradingRabbitMq`（メソッド名）・`AiStockTrading.Business`（Meter 名）・`backend/Tests/AiStockTrading.*`（パス）はいずれも本 PR で変わらない |
| 9 | `AiStockTrading.Architecture.Tests` の許可リスト・判定 | `DomainSourceScan` の 3 判定＋`RepositoryLayout`／`DomainSourceArea`／`DomainSourceDependencyTests` | 5 ファイル | `SharedKernelIsLeafTests` / `SharedProjectDependencyTests` / `DomainLayerDependencyTests` は**プロジェクト名**で判定しており、本 PR ではプロジェクト名を変えないため不変 |
| 10 | `docs/` と `.ai-context/adr/` の live な記述 | `docs/` **2 行**（`tech/tech-requirements.md` / `data/trading-assumptions.md`）、`.ai-context/adr/` **5 ファイル 9 行** | `docs/` 2 行 | `.ai-context/adr/` の 9 行はいずれも**当時の実測・当時の決定の記録**（IADR-0106 の旧名の引用・IADR-0122 の移送前後の記述・IADR-0168 の貼り付けたテスト失敗ログ・IADR-0219 の新設型の決定・IADR-0260 の土台 5 実測表）であり、**書き換えると記録が当時と食い違う**ため触らない。`.ai-context/specs/` `.ai-context/superpowers/` は凍結記録につき対象外 |
| 11 | `pipeline.json` | **5 行**（`consumer` 型名） | 5 | 下記の判断に従う |

### 母集合の引き直しで親の一覧に無かったもの

- **`DomainSourceScan.ExternalNamespaceRootsIn` の機能破壊**（§9）。名前空間の根が `AiStockTrading` でも
  `System` でもなくなると、サービス根が「外部ライブラリの根」として禁止トークンへ混入し、
  **Domain の全ファイルが自分の `namespace` 宣言で検査 (c) に違反する**。親の一覧は
  「許可リスト・判定が名前空間文字列を持つ」までしか書いていない。
- **`ForeignServiceReferencesIn` の誤検出源**。Domain のコメント 3 箇所に `StageGateService.` /
  `RiskSettingsService.` / `ReportService.` が現れる。「末尾が `Service` の根」で判定すると誤検出する。
- **`docs/tech/tech-requirements.md` の live な規範記述**（「名前空間は当面変えない」）。
  親の (4) の走査は `AiStockTrading.<Svc>` の形しか引いていないため、この行（`<Short>` を含む一般形）は出ない。

## 衝突するクラス（AST 固有の障害）

新しいルート名前空間と**同名のクラス**が 6 サービスに実在する（`<Svc>Service.Application.Services.<Svc>Service`）。
`namespace ReportService.Application.Services;` の中に `class ReportService` が居ること自体はビルドが通るが、
**そのクラスが可視な場所から修飾名 `ReportService.Domain.X` を書くと `CS0426` になる**。

**採る対処 (a): 6 クラスを改名する。** 改名規則は **`<Svc>Service` → `<Svc>AppService`** で統一する。
理由と代替案の評価は `IADR-0261` に書く。

| 現行クラス | 新クラス | 参照行（実測） |
| --- | --- | ---: |
| `ReportService` | `ReportAppService` | 19 |
| `TradeDecisionService` | `TradeDecisionAppService` | 22 |
| `OrderExecutionService` | `OrderExecutionAppService` | 16 |
| `InformationCollectionService` | `InformationCollectionAppService` | 8 |
| `MarketMonitorService` | `MarketMonitorAppService` | 13 |
| `CostControlService` | `CostControlAppService` | 11 |

🔴 **実測の参照行には「サービス名の文字列リテラル」（キュー名・テレメトリ・語彙表）が混ざる。**
文字列リテラルは wire/構成の識別子であり**改名しない**。置換は 1 行ずつ判定する。

## 🔴 変えてはならないもの

- **Wolverine の wire 契約**。メッセージ識別子は `messageType.ToMessageTypeName()` ＝名前空間込みの完全名で、
  exchange 名・binding key・封筒の `message-type` ヘッダそのものである（`IADR-0129` 決定 2）。
  `EventMessageTypeNameTests` が固定する 45 件のうち 44 件は `AiStockTrading.Shared.Contracts.Events.*`、
  残り 1 件はテスト内の当て馬 `MovedNamespaceProbe`。ハンドラの引数型 45 種のうち `Shared.Contracts/Events/` に
  無いのはテスト内ローカル型 1 件（`AiStockTrading.TestSupport.PlatformShim.Tests`）だけ。**どちらも据え置き集合**。
  → **`EventMessageTypeNameTests` と `event-schemas.baseline.json` は 1 文字も変えない。
  赤くなったら「変えてはいけない名前空間を変えた」という警報であり、期待値を直して緑にすることを固く禁じる。**
- **`MigrationId`**（migration のクラス名・`[Migration("…")]` 属性・ファイル名）。`__EFMigrationsHistory` が
  これで突合するため、変えると既存 DB との整合が壊れる。
- **アセンブリ名・プロジェクト名・`ServiceName` 定数の文字列リテラル・Meter 名 `AiStockTrading.Business`。**

## `pipeline.json` の判断

`deploy/helm/ai-stock-trading/files/pipeline.json` の 5 つの `consumer` 型名は
`AiStockTrading.<Svc>.Worker.Composable.Steps.<Event>Consumer` を名乗るが、**宣言されている型は 1 つも無い**
（実在は `…<Svc>.Infrastructure.Composable.Steps.<Event>Handler`。`#580` として起票済み）。
`validate-pipeline-config.js` は形式しか見ないため緑のまま通る。

**採る判断: 実体（`<Event>Handler` の新名前空間）へ合わせて是正し、`#580` を `Closes` する。**
機械的に置換すると「死んだ参照を別の死んだ参照へ書き換える」だけになる。どのみちこの 5 行を触る。

## 合否判定（本 PR の中核）

変換後、**7 つの DbContext すべて**で「モデルへの変更なし」を確認する。

```
P=backend/Services/<Svc>Service/src/<Svc>Service.Infrastructure
dotnet ef migrations has-pending-model-changes --project $P --startup-project $P
```

- 対象 7 サービス: `Audit` / `Configuration` / `CostControl` / `MarketMonitor` / `OrderExecution` / `Report` / `RiskManagement`。
- `IADR-0259` 決定 5③3 は `dotnet ef migrations add __VerifyNoDrift` の空差分を受け入れ基準に置いたが、
  **`has-pending-model-changes` へ置き換える**（ファイルを生成しないので後片付けが要らず、判定は同値である）。
  `dotnet ef migrations remove` は DB 接続を試みて落ちるため使わない。
- 1 つでも「変更あり」なら型名文字列の書き換えに漏れがある。**名前空間ではなく snapshot の側を疑う。**

## 受け入れ基準

- [ ] `dotnet build backend/backend.slnx` が 0 warning / 0 error
- [ ] `dotnet test backend/backend.slnx` の失敗が `AiStockTrading.IntegrationTests` の 8 件のみ（Docker 不在の環境制約）
- [ ] `dotnet format backend/backend.slnx --verify-no-changes` が通る
- [ ] 7 サービスすべてで `has-pending-model-changes` が「変更なし」
- [ ] `EventMessageTypeNameTests.cs` と `event-schemas.baseline.json` の diff が 0 行
- [ ] `scripts/` の検査器・`scripts.test.js` / `scripts.repo.test.js` が全て緑
- [ ] カバレッジが床 79.00% を割らない
