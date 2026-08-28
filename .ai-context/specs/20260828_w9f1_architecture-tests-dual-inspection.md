---
title: Domain 層の依存規律を「ソース走査」でも検査する（旧 csproj 方式と二重化する）
type: spec
status: approved
related_ids: [NFR]
author: endazon (with Claude Code)
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# 仕様書: 検査器の二重化（Architecture.Tests に Domain ソース走査方式を追加する）

> 本仕様書は実装着手前に作成した。本件は **本番コードを 1 行も変更しない**。変更は
> `backend/Tests/AiStockTrading.Architecture.Tests/` 配下と `.ai-context/` のみである。

## 起点

- 起点 ID: **`NFR`（無採番）**。本件は**検査器の追加＝メタ作業**であり、
  `.claude/rules/traceability.md`「起点 ID の種別」が定める無採番許容ケース **2**
  （「ID 列はあるが、その作業に当たる番号が無い場合」）に当たる。
  着手前に計画の非機能要件表（`projects/ai-stock-trading/02_requirements/01_requirements.md` の
  `NFR-01`〜`NFR-17`）を実際に読んで確認した結果は次のとおりで、**本作業に当たる番号は無い**。

  | ID | 分類 | 内容 | 本件との関係 |
  | --- | --- | --- | --- |
  | NFR-01 / 02 | 性能 | 発注所要時間・サイクル所要時間 | 無関係 |
  | NFR-03 / 04 | 可用性 | 稼働率・障害時の振る舞い | 無関係 |
  | NFR-05 / 06 | セキュリティ | 認証情報の保管・発注機能へのアクセス | 無関係 |
  | NFR-07〜11 | 運用・保守 | 可観測性・データ保持・パージ | 無関係 |
  | NFR-12〜15 | 費用 | 情報費用・LLM 費用・インフラ費用・総費用 | 無関係 |
  | NFR-16 | 拡張性 | 証券会社・情報源・LLM をポートで抽象化し差し替え可能にする | **近いが当たらない**。NFR-16 が求めるのは「差し替えられること」であり、本件が守るのは「Domain 層が外部ライブラリへ依存しないこと」（platform ADR-0030 §基本方針）である。**無理に近い番号を付けると監査が「NFR-16 の実装」として数えてしまい、無採番より劣化する**ため付けない |
  | NFR-17 | 法規・規約 | 利用規約遵守 | 無関係 |

  同ケース 2 は「**環流しない**」と定めている（工程の規律を製品の品質要件の表へ混ぜないと計画側が裁定済み）。
  したがって計画リポジトリへの issue 起票は行わない。
- 対象となる計画上の制約: platform **ADR-0030**（§基本方針「Domain 層は外部ライブラリへ依存しない（.NET 標準のみ）」）、
  計画 `06_technical/12_backend-application-stack` §基本方針。**この制約自体は本件で一切変えない。**
- 先行する実装ADR: `IADR-0128`（層分割とプロジェクト命名）、`IADR-0127`（検査器が静かに失効しないためのメタ検査）。
- 本件で起案する実装ADR: **`IADR-0256`**。

## 背景

Vertical Slice Architecture への全面移行（1 サービス = 1 プロジェクト化）が行われると、
現在 Domain 層の依存規律を機械的に強制している **`*.Domain.csproj` という識別子が消滅する**。
現行の `DomainLayerDependencyTests` は csproj の静的解析であるため、**検査対象が 0 件になり、
「違反なし」で無条件に緑になる**。これは「気付けない形で壊れる」種類の失効である。

そこで移行に先立ち、**同じ規律をソース走査でも検査する新方式を追加**し、移行期間中は
**新旧の二重化**で規律を保つ。本 PR の存在意義は「新方式が**現行構成をそのまま合格と判定できる**こと」の実証にある。

## 射程（広げない）

- **本番コードを 1 行も変更しない。** プロジェクト構成も変えない。
- **旧方式（csproj 静的解析）を残す。** 既存 12 テストメソッド（19 テストケース）を 1 つも
  壊さない・skip しない・削除しない。
- 変更してよいのは `backend/Tests/AiStockTrading.Architecture.Tests/` 配下と `.ai-context/` のみ。
- 移行期の他検査器（`validate-runtime-scaffold.js` / `check-consumer-endpoint-names.js`）の
  両対応、CI シャーディング、`Shared.Kernel` 新設は**本 PR の射程外**（それぞれ別の土台 PR）。

## 母集合の引き直し（`.claude/rules/traceability.md` 規則 1〜10）

**issue 本文・設計書の記述を転記せず、着手時に自分で引いた。** 走査は生の出力のまま貼る。
走査基準は本ブランチの作業ツリー（base: develop `cf5354e`）。
**shallow clone のため `git log` / `git blame` は出典に使っていない**（`git rev-parse --is-shallow-repository` = `true` を確認済み）。

### 軸 1: Domain の母集合（旧方式・csproj）

```
$ find backend/Services -name "*.Domain.csproj" -not -path "*/bin/*" -not -path "*/obj/*" | sort
backend/Services/BacktestService/src/BacktestService.Domain/BacktestService.Domain.csproj
backend/Services/ConfigurationService/src/ConfigurationService.Domain/ConfigurationService.Domain.csproj
backend/Services/CostControlService/src/CostControlService.Domain/CostControlService.Domain.csproj
backend/Services/InformationCollectionService/src/InformationCollectionService.Domain/InformationCollectionService.Domain.csproj
backend/Services/MarketMonitorService/src/MarketMonitorService.Domain/MarketMonitorService.Domain.csproj
backend/Services/OrderExecutionService/src/OrderExecutionService.Domain/OrderExecutionService.Domain.csproj
backend/Services/ReportService/src/ReportService.Domain/ReportService.Domain.csproj
backend/Services/RiskManagementService/src/RiskManagementService.Domain/RiskManagementService.Domain.csproj
backend/Services/TradeDecisionService/src/TradeDecisionService.Domain/TradeDecisionService.Domain.csproj
$ ... | wc -l
9
```

**9 件**（AuditService / NotificationService は Domain を持たない。11 - 2 = 9）。

### 軸 2: Domain のソースディレクトリ（新方式）

新方式が数えるのは**フォルダ**である。移行の前後で形が違うため、**両方の形を数える和集合**にする。

- 現行（層分割）: `backend/Services/<Svc>Service/src/<Svc>Service.Domain/` — 実測 **9 件**
- 移行後（VSA）: `backend/Services/<Svc>Service/Domain/` — 実測 **0 件**（まだ 1 件も存在しない）

```
$ find backend/Services -maxdepth 2 -type d -name Domain -not -path "*/bin/*" -not -path "*/obj/*" | wc -l
0
```

和集合 **9 件**。下限 9 は現行と移行後の両方で満たされる（移行後は 9 → 9 のまま推移し、
Audit / Notification が Domain フォルダを持たない限り増えない）。

### 軸 3: Domain のソースファイルと `using`

```
$ find backend/Services/*/src/*.Domain -name "*.cs" -not -path "*/bin/*" -not -path "*/obj/*" | wc -l
120
$ find backend/Services/*/src/*.Domain -name "*.cs" ... -exec grep -hE "^\s*(global\s+)?using " {} \; | sed 's/^[ \t]*//' | sort | uniq -c | sort -rn
     47 using AiStockTrading.Shared.Contracts.Trading;
      9 using System.Globalization;
      8 using System.Text;
      5 using AiStockTrading.Shared.Contracts.Events;
      4 using AiStockTrading.Configuration.Domain;
      2 using System.Security.Cryptography;
      1 using System.Text.RegularExpressions;
      1 using System.Text.Json;
      1 using AiStockTrading.Shared.Contracts.Llm;
      1 using AiStockTrading.RiskManagement.Domain;
      1 using AiStockTrading.RiskManagement.Domain.Manipulation;
```

`using` ディレクティブ **80 本**（47+9+8+5+4+2+1+1+1+1+1）。名前空間宣言は 10 種類で、
すべて `AiStockTrading.<Short>.Domain[.Manipulation]` の形であった（**`<Short>` はサービスディレクトリ名から
`Service` 接尾辞を落としたものと一致する**。`InformationCollectionService` → `AiStockTrading.InformationCollection.Domain`）。
この一致が「自サービスかどうか」の機械判定の根拠である。

### 軸 4: 既知の逸脱（他サービス参照）— 🔴 設計書の数と食い違った

**設計書 §1.5 は 4 件と書いているが、これは csproj の `ProjectReference` を数えた値（プロジェクト間の辺 4 本）であり、
ファイル単位では 5 件である。** 自分で引いたので数が変わった。

```
$ for d in backend/Services/*/src/*.Domain; do svc=$(basename "$d" .Domain); short="${svc%Service}"; \
  grep -rnoE "AiStockTrading\.[A-Za-z0-9_]+" "$d" --include="*.cs" | grep -v "/bin/" | grep -v "/obj/" | \
  while IFS= read -r line; do ns=$(echo "$line" | sed 's/.*://'); part="${ns#AiStockTrading.}"; \
  if [ "$part" != "$short" ] && [ "$part" != "Shared" ]; then echo "$line (self=$short)"; fi; done; done | sort -u
backend/Services/BacktestService/src/BacktestService.Domain/BacktestCostModel.cs:1:AiStockTrading.Configuration (self=Backtest)
backend/Services/BacktestService/src/BacktestService.Domain/Stage0Promotion.cs:1:AiStockTrading.RiskManagement (self=Backtest)
backend/Services/CostControlService/src/CostControlService.Domain/CostGovernor.cs:1:AiStockTrading.Configuration (self=CostControl)
backend/Services/ReportService/src/ReportService.Domain/LlmUsageRecord.cs:1:AiStockTrading.Configuration (self=Report)
backend/Services/ReportService/src/ReportService.Domain/PnlAggregator.cs:1:AiStockTrading.Configuration (self=Report)
```

**ファイル 5 件 / プロジェクト間の辺 4 本。** 設計書 §1.5 の表が挙げた代表ファイルには
`ReportService.Domain/LlmUsageRecord.cs` が入っていない（`ReportService.Domain → ConfigurationService.Domain` の
辺を代表するファイルとして `PnlAggregator.cs` だけを挙げていた）。
**検出はファイル単位で行うため、許容一覧もファイル単位の 5 件で持つ。**

軸を変えてもう 1 本引き、`using` 行以外（完全修飾）での他サービス参照が無いことも確認した
（上の走査は `using` 行に限定していない全文走査であり、ヒットは 5 件とも 1 行目の `using` 行であった）。

### 軸 5: 外部ライブラリの母集合（CPM）

```
$ grep -c "PackageVersion Include=" Directory.Packages.props
32
```

**32 パッケージ。** 拒否リストを手で書かず、ここから機械的に導く（次に足されたパッケージが素通りしないため）。

### 除外したものと、その理由

| 除外 | 理由 |
| --- | --- |
| `bin/` `obj/` 配下 | ビルド成果物。既存 `RepositoryLayout.NotUnderBuildOutput` と同じ基準 |
| `backend/Services/*/tests/` | Domain 層の**テスト**であり、Domain 層そのものではない。テストは xUnit・AwesomeAssertions へ依存してよい |
| `AuditService` / `NotificationService` | Domain を持たないサービス（実測で `*.Domain.csproj` が存在しない）。**「引き漏らし」ではなく「存在しない」** |
| `backend/Shared/AiStockTrading.Shared.{Infrastructure,KnowledgeBase}` | Domain ではない。ただし**許可名前空間からも外す**（Domain がここへ依存したら違反である） |
| `.ai-context/` `docs/` の文書 | ソース走査の対象ではない |

## 実装する検査（設計書 §2.3 の 5 検査）

| # | 検査 | 実装 |
| ---: | --- | --- |
| (a) | 探索が空振りしていない | Domain ソースディレクトリ（軸 2 の和集合）が **9 件以上** |
| (b) | `using` 許可リスト | `Domain/**/*.cs` の `using X.Y.Z;` の名前空間が許可集合内（`System[.*]` / `AiStockTrading.<任意>.Domain[.*]` / `AiStockTrading.Shared.Contracts[.*]` / 将来の `AiStockTrading.Shared.Kernel[.*]`） |
| (c) | 完全修飾での迂回を塞ぐ | CPM の `PackageVersion Include=` から母集合を導き、各パッケージ ID の**全ドット接頭辞**と、**リポジトリが実際に import している外部名前空間の根**（`System` / `AiStockTrading` を除く）を禁止トークンとし、`Domain/` のソースに**修飾名の先頭として**現れないこと |
| (d) | 他サービス参照の禁止 | `AiStockTrading.<自分以外の Short>.` が現れない。**既知の逸脱 5 件は明示一覧で許容**し、一覧に無いものが増えたら落ちる |
| (e) | 共有プロジェクトの csproj 静的解析（存続） | `AiStockTrading.Shared.Contracts`（および将来の `Shared.Kernel`）とその推移閉包の `PackageReference` が 0 件 |

### (c) のトークン導出をなぜ 2 系統にするか

CPM のパッケージ ID と名前空間の根は**一致しないことがある**。実測で `WolverineFx`（パッケージ ID）の
名前空間は `Wolverine` である。パッケージ ID だけから導くと、`Wolverine.IMessageBus` の完全修飾参照が
(c) を素通りする。CamelCase 分割で補う案は、`OpenTelemetry` → `Open`、`SSH.NET` → `S` / `SS` といった
**危険なほど短いトークン**を生み、`quote.Open.Value` のような正当な記述を誤検出する（実測で 68 トークン中に
`S` / `Asp` / `Open` / `Rabbit` が現れた）。そこで、**リポジトリ全体の `using` ディレクティブが実際に
import している名前空間の根**を第 2 の母集合とする。これも走査由来であり、手書きの拒否リストではない。

```
$ (backend 配下の全 *.cs の using から根を抽出し System / AiStockTrading を除く)
AwesomeAssertions, Discord, DotNet, Google, JasperFx, Microsoft, Moomoo, Npgsql,
OpenTelemetry, RabbitMQ, Serilog, Testcontainers, Wolverine, Xunit
```

CPM 由来 57 トークン ＋ 上記 14 根 = **重複排除して 63 トークン**。この 63 トークンで Domain の 120 ファイルを
走査した結果は **0 ヒット**（＝現状は合格）。

### 「0 件検査で緑」を構造的に防ぐ

(a) の下限に加え、以下を明示的に表明する（下限値は上の実測から余裕を取った）。

| 表明 | 実測 | 下限 |
| --- | ---: | ---: |
| Domain ソースディレクトリ | 9 | 9 |
| Domain の `.cs` ファイル | 120 | 100 |
| Domain の `using` ディレクティブ | 80 | 60 |
| (c) の禁止トークン | 63 | 30 |
| (e) の被検査共有プロジェクト | 1 | 1 |

さらに **既知の逸脱 5 件が「今も実際に観測できる」ことを対で表明する**。
土台 5（`Shared.Kernel` 抽出）で解消されたとき、一覧を消し忘れると赤くなる。
（`KnownPlanDeviations` と同じ作法。**許容一覧が黙って腐ることを防ぐ。**）

### 否定形（検出器が load-bearing であることの実証）

`McpExposureNotDeclaredTests` の作法（照合器を純関数として切り出し、`[Theory]` で
肯定・否定の両方を固定する）に倣う。実ツリーが 0 件である以上、
**照合器が常に「違反なし」を返すよう壊れても、実ツリー走査のテストは緑のままである。**

- `using` パーサ: `using X.Y;` / `global using X.Y;` / `using static X.Y;` / `using A = X.Y;` を解析でき、
  `using var db = ...;` / `using (var scope = ...)` を**名前空間として解析しない**こと。
- (b) の許可判定: `Microsoft.EntityFrameworkCore` / `AiStockTrading.Shared.Infrastructure` /
  `AiStockTrading.Report.Application` を**拒否**し、`System.Text` / `AiStockTrading.Shared.Contracts.Trading` /
  `AiStockTrading.RiskManagement.Domain.Manipulation` を**許可**すること。
- (c) の照合器: `Microsoft.EntityFrameworkCore.EF.Property(...)` を検出し、
  `quote.Open.Value`（`Open` が `.` の後ろ）を**検出しない**こと。
- (d) の照合器: 自サービス・`Shared` を除外し、他サービスの根だけを返すこと。

加えて、**実ツリーへ一時的に違反を仕込んで実際に赤くなること**を実測し、元へ戻す（PR 本文に手順と出力を残す）。

## 変更するファイル

| ファイル | 変更 |
| --- | --- |
| `backend/Tests/AiStockTrading.Architecture.Tests/RepositoryLayout.cs` | `DomainSourceDirectories` / `SharedProjectFiles` を追加（`DomainProjectFiles` は**変えない**） |
| `backend/Tests/AiStockTrading.Architecture.Tests/DomainSourceArea.cs` | 新規。Domain ソース領域（ディレクトリ ＋ 自サービス短縮名） |
| `backend/Tests/AiStockTrading.Architecture.Tests/DomainSourceScan.cs` | 新規。純関数の照合器群 |
| `backend/Tests/AiStockTrading.Architecture.Tests/DomainSourceDependencyTests.cs` | 新規。(a)〜(d) ＋ 否定形 |
| `backend/Tests/AiStockTrading.Architecture.Tests/SharedProjectDependencyTests.cs` | 新規。(e) |
| `.ai-context/adr/IADR-0256_domain-dependency-inspection-by-source-scan.md` | 新規 |
| `.ai-context/adr/README.md` | 索引へ 1 行追加（昇順末尾） |

**`DomainLayerDependencyTests.cs` / `ProjectFile.cs` / `McpExposureNotDeclaredTests.cs` /
`RetrievalSourceVocabularyTests.cs` は変更しない。**

## 受け入れ基準

1. `dotnet build backend/backend.slnx` が成功する（警告 0）。
2. `dotnet test .../AiStockTrading.Architecture.Tests.csproj` が全緑で、**既存 19 テストケースが 1 件も減らない**。
3. `dotnet test backend/backend.slnx` の失敗が `AiStockTrading.IntegrationTests` の 8 件のみである
   （Docker 不在の環境制約）。
4. `dotnet format backend/backend.slnx --verify-no-changes` が通る。
5. 文書系検査（`check-trace-blocks` / `check-adr-index-sync` / `check-cross-repo-refs` /
   `check-doc-links` / `check-commit-messages` / `gen-knowledge-graph --check`）が通る。
6. 各検査について、違反を仕込むと**実際に赤くなる**ことを実測した証跡がある。

## 残余リスク

- **`using` 走査はコンパイラより弱い。** `global using`（`ImplicitUsings`）とソースジェネレータの
  生成参照は見えない。緩和は (c) のトークン走査であり、それでもなお「CPM に無い推移パッケージを
  完全修飾で使う」経路は塞げない。**現時点では旧方式（(e) の推移閉包）が併走してこれを塞いでいる。**
- **フォルダ名 `Domain` に依存する。** 改名すれば空振りする。(a) の下限がこれを捕まえる。
- **(c) の第 2 母集合はリポジトリの現状に依存する。** 誰も import していない外部名前空間は
  トークンに入らない。ただし新たに import した瞬間に母集合へ入るため、**規律の穴は「使われていないもの」に限られる**。
- 既知の逸脱 5 件は**土台 5 で解消する前提**である。本 PR はそれを許容するだけで、解消しない。
