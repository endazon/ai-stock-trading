---
title: AiStockTrading.Shared.Kernel を新設し、サービスを跨ぐ Domain 参照を解消する
type: spec
status: approved
related_ids: [NFR]
author: endazon (with Claude Code)
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/12_backend-application-stack.md
---

# 仕様書: 共有カーネル（`AiStockTrading.Shared.Kernel`）の新設と Domain 跨ぎ参照の解消

> 本仕様書は実装着手前に作成した。VSA/DDD 全面移行の**土台 5 本目**である。
> 変更は `backend/` 配下（共有プロジェクトの新設・型の移送・参照の追随）と `.ai-context/`、
> および移送で記述が誤りになる `docs/` の 2 ファイルに限る。

## 起点

- 起点 ID: **`NFR`（無採番）**。本件は**構造整備＝メタ作業**であり、
  `.claude/rules/traceability.md`「起点 ID の種別」の無採番許容ケース **2**
  （「ID 列はあるが、その作業に当たる番号が無い場合」）に当たる。計画の非機能要件表
  （`NFR-01`〜`NFR-17`）を読み直したが、性能・可用性・セキュリティ・運用保守・費用・拡張性・法規の
  いずれも**稼働する製品**の要件であり、「サービス間の依存の向き」を扱う番号は無い。
  **`NFR-16`（ポートによる差し替え可能性）は近いが当たらない** —— 求めているのは外部連携先の
  差し替えであって、内部プロジェクトの依存規律ではない。無理に付けると監査が「`NFR-16` の実装」として
  数えてしまい、無採番より劣化する。同ケース 2 は「環流しない」と定めるため計画への起票は行わない。
- 直接の上流: `IADR-0256`（Domain 依存規律のソース走査。**既知の逸脱 5 件の一覧を残し、解消を本件に委ねた**）、
  `IADR-0128`（層分割とプロジェクト命名）。基盤側の先行事例は `MSP:IADR-0229`（`Platform.Shared.Kernel`）。

## 目的

`backend/Tests/AiStockTrading.Architecture.Tests/DomainSourceDependencyTests.cs` の
`KnownForeignReferences`（既知の逸脱 5 件）を**実際に解消し、一覧を空にする**。
サービスの Domain が他サービスの Domain を直接引く状態は、1 サービス = 1 プロジェクト化した瞬間に
相手サービスの永続化・エンドポイント・メッセージング配線までビルドへ引き込む。
共有が要る型は共有カーネルへ抜く。

## 母集合の引き直し（`.claude/rules/traceability.md`「是正・追随の母集合の取り方」規則 1〜10）

**親が渡した表（軸 1・型名）は 1 軸にすぎない。** 規則 5（軸を 1 本で終わらせない）に従い 7 軸で引いた。
走査はすべて `bin/` `obj/` を除外し（規則 3・4: 拡張子ではなくパスで除外）、**生の出力に対して判断した**（規則 7）。

| 軸 | 引き方（実際のコマンド） | 実測 | 新規に見つかったもの |
| --- | --- | --- | --- |
| 1. 型名 | `grep -rlnE '(^\|[^A-Za-z0-9_])(TradingAssumptions\|TradingAssumptionsDefaults\|CommissionSchedule\|MonthlyCostLimits\|CostCalculator\|TradingStage)([^A-Za-z0-9_]\|$)' --include='*.cs' backend` | **97 ファイル** | `CommissionSchedule`（`TradingAssumptions` の構成型）を親の表は挙げていない |
| 2. コメント/文字列を除いた実コード | 軸 1 の 97 件を Node でコメント・文字列除去してから再判定 | **85 ファイルが実使用・12 ファイルはコメントのみ** | コメントのみ 12 件は**変更不要**（型名を語っているだけで名前空間に依存しない） |
| 3. 名前空間の側から | `grep -rl "using AiStockTrading\.Configuration\.Domain"` / 同 `RiskManagement` | **42 / 127 ファイル** | 大半は同一サービス内の正当な参照。**軸 1 と交差する集合だけが追随対象**である |
| 4. 実際に呼ばれているメンバ（型宣言ではなく静的呼び出し） | `grep -rn "CostCalculator\|VersionedAssumptions" --include='*.cs' backend \| grep -v ConfigurationService/` | 2 ファイル | 🔴 **`CostCalculator` を新規発見。** 親の表は `BacktestCostModel.cs` / `PnlAggregator.cs` の外部型を `TradingAssumptions` のみとしていたが、両ファイルは `CostCalculator.EstimateOneWayCost(...)` を**実際に呼んでいる**。**これを移送しないと逸脱は解消しない** |
| 5. 部分修飾・完全修飾（`using` 走査では捕まらない） | `grep -rnE "AiStockTrading\.(Configuration\|RiskManagement)\.Domain\.[A-Za-z]"` ＋ `(^\|[^A-Za-z0-9_.])(Configuration\|RiskManagement)\.Domain\.` | 2 ファイル | 🔴 **`RiskManagementService.Infrastructure/Foundation/Persistence/PersistenceRows.cs`**（`AiStockTrading.RiskManagement.Domain.TradingStage` を完全修飾）と **`BacktestService.Application.Tests/Stage0GateServiceTests.cs`**（`RiskManagement.Domain.TradingStage` を部分修飾）。**どちらも `using` の走査には現れない** |
| 6. csproj の辺 | `grep -rl "ConfigurationService.Domain.csproj\|RiskManagementService.Domain.csproj" --include='*.csproj' backend` | 10 件 | サービスを跨ぐ Domain → Domain の辺は **4 本**（Backtest→Configuration / Backtest→RiskManagement / CostControl→Configuration / Report→Configuration）。加えて **TradeDecision.Application → RiskManagement.Domain**（Application 層のため Domain 走査には現れないが、`TradingStage` を運ぶ辺である） |
| 7. 検査器・規約の側 | `grep -rn "Shared\.Kernel"` 全ツリー | 8 箇所 | 🔴 **`DomainLayerDependencyTests.IsAllowedDomainDependency` が `AiStockTrading.Shared.Kernel` を許可していない**（許すのは `*.Domain` / `*.SharedKernel` / `AiStockTrading.Shared.Contracts`）。**新設した瞬間に csproj 側の検査が赤くなる。** 親の指示にも無い |
| 8. 文書 | `grep -rn "TradingAssumptions\|MonthlyCostLimits\|TradingStage\|CostCalculator\|ConfigurationService.Domain\|RiskManagementService.Domain" docs/` | 20 行 | `docs/data/risk-management-aggregates.md`（`TradingStage` を「`RiskManagementService.Domain` の実装済みドメイン型」と書く）と `docs/data/trading-assumptions.md`（型の所有を設定サービスに帰す）が**移送で誤りになる**（規則 10） |

### 除外したものと、その理由（規則 6）

| 除外 | 件数 | 理由 |
| --- | --- | --- |
| コメント・XML doc・文字列リテラル内の型名 | 12 ファイル | **型名を語っているだけで名前空間に依存しない。** 移送後も文の意味は正しい（例: `Shared.Contracts/Trading/MinimumExpectedProfit.cs` の「式の単一情報源は `CostCalculator` と共有する」）。書き換えると差分が膨らむだけで、誤りを 1 件も直さない |
| `.ai-context/adr/` `.ai-context/specs/` の既存記録 | 全件 | **凍結記録**（`traceability.repo.md`）。`IADR-0256` は「解消は `Shared.Kernel` 新設の PR が担う」と書いており、**当時の記述として正しい**。後から書き換えない |
| `CHANGELOG.md` | 全件 | 生成物。コミット件名から再生成される |
| `VersionedAssumptions` | 1 型 | 移送しない（後述の判断 2） |
| 同一サービス内の `using AiStockTrading.RiskManagement.Domain`（軸 3 の 127 件の大半） | 約 90 ファイル | 軸 1 と交差しない。**移送する型を使っていないため追随が要らない**（軸 1 ∩ 軸 3 だけが対象） |
| `docs/data/risk-management-aggregates.md` の `Stage1Paper` / `TradeMode` の古い記載 | 2 行 | **本件で新たに誤りになったものではない**（`#333` / `#334` で既に古い）。**本 PR の射程外**。ここで直すと「何をこの PR が変えたか」が読めなくなる |

> **規則 8（自己参照）**: 本仕様書自身が検索語（`TradingAssumptions` 等）を含む。上表の件数は
> **本仕様書をコミットする前**の走査値である（`.ai-context/specs/` は軸 1 の `--include='*.cs'` に
> 掛からないため、`.cs` 走査の 97 / 85 / 12 はコミット後も不変。文書走査の軸 8 は `docs/` 限定のため同じく不変）。

## 判断（`IADR-0260` に記録する）

### 判断 1: 何を `Shared.Kernel` へ入れるか

入れるのは **5 型のみ**である。`Result` / `Error` / DDD 基底型は **AST に実在しないので新設しない**
（基盤の `Platform.Shared.Kernel` には在るが、無い物を先回りで作らない）。

| 型 | 移送 | 理由 |
| --- | --- | --- |
| `TradingAssumptions` | ○ | Backtest / Report の Domain が引いている |
| `CommissionSchedule` | ○ | `TradingAssumptions` の構成型。**残すと `Shared.Kernel` → `Configuration.Domain` の逆流になる**（随伴物） |
| `MonthlyCostLimits` | ○ | CostControl の Domain が引いている。かつ `TradingAssumptions.CostLimits` の型（随伴物でもある） |
| `TradingAssumptionsDefaults` | ○ | Report の Domain が引いている。`TradingAssumptions` を返すため単独では残せない |
| `CostCalculator` | ○ | **軸 4 で発見。** Backtest / Report の Domain が実際に呼んでいる。移送しないと `using` が残り逸脱も残る |
| `VersionedAssumptions` | ✕ | **設定サービス固有の概念**（設定ストアの版・楽観排他）であり、他サービスの Domain は引いていない（消費側は認可された経路 `ConfigurationService.Client` 越しに使う）。移送は最小に留める |
| `StageSettings` | ✕ | `TradingStage` と同居しているが、`BrokerProvider` と資金上限比を持つ**リスク管理固有の設定**。他サービスは引いていない。**ファイルを分割し、enum だけを抜く** |

**「移送を最小にする案」と「意味的なまとまりを保つ案」の比較**:
`TradingAssumptions.cs` は 3 型を 1 ファイルに持ち、`StageSettings.cs` は 2 型を 1 ファイルに持つ。
- 最小案（`MonthlyCostLimits` だけ・`TradingStage` だけを抜く）は、**`TradingAssumptions` が
  `Configuration.Domain` に残ったまま `Shared.Kernel` の `MonthlyCostLimits` を参照する**形になり、
  共有カーネルが**葉でなくなる**（`Shared.Kernel` → `Configuration.Domain` の逆流）。**採らない。**
- まとまり案（ファイルごと全部移す）は `VersionedAssumptions` と `StageSettings` まで動かし、
  設定サービス・リスク管理サービス固有の概念を共有カーネルへ流出させる。**採らない。**
- **採用**: 「**他サービスから引かれている型と、その型が必要とする随伴物だけ**」を境界にする。
  結果として `TradingAssumptions.cs`（3 型）と `TradingAssumptionsDefaults.cs` と `CostCalculator.cs` は
  ファイルごと移り、`StageSettings.cs` は `TradingStage` だけを新ファイルへ分割して抜く。

### 判断 2: `ConfigurationService.Domain` を空にしない

移送後、`ConfigurationService.Domain` に残るのは `VersionedAssumptions.cs` の 1 ファイルである。
これは判断 1 の帰結だが、**副次的に重要な性質**がある ——
`DomainSourceDependencyTests.Domain_ソース領域の探索が空振りしていない` は Domain 領域が
**9 件以上**あることを要求しており、現状ちょうど 9 件である。`Configuration.Domain` を空にすると
**8 件になり検査が落ちる**（領域は `.cs` を 1 つも持たない枠を数えない）。
`VersionedAssumptions` を残す判断は、この下限とも整合する。

### 判断 3: 名前空間は `AiStockTrading.Shared.Kernel.Trading`

`Shared.Contracts` が `Trading` / `Events` / `Llm` … とフォルダ＝サブ名前空間で切っている粒度に倣う。
`Shared.Kernel` 直下に平置きしない（次に別領域の共有型が来たとき切り直しになる）。
検査器の許可判定は接頭辞一致（`AiStockTrading.Shared.Kernel[.*]`）であり、サブ名前空間でも通る。

### 判断 4: `Shared.Kernel` は葉である（テストで固定する）

`Shared.Kernel` はどのサービスも参照してはならない。既存の `SharedProjectDependencyTests` は
**外部ライブラリ依存ゼロ**しか見ておらず、`Shared.Kernel` → `SomeService.Domain` の
`ProjectReference` を止められない。**`Architecture.Tests` に葉であることの検査を足す。**

## 変更点

1. `backend/Shared/AiStockTrading.Shared.Kernel/`（＋ `.Tests`）を新設し `backend/backend.slnx` へ登録する。
   csproj は既存の共有プロジェクト（`Shared.Contracts`）の書式に厳密に倣う（`OutputType=Library` のみ。
   `TargetFramework` も `PackageReference` のバージョンも書かない ——
   単一情報源は `Directory.Build.props` / `Directory.Packages.props`）。
2. 5 型を移送し、名前空間を `AiStockTrading.Shared.Kernel.Trading` にする。
3. 参照元 85 ファイル（＋完全修飾 1・部分修飾 1）の `using` を実際に直す。
   **型の再エクスポート・`global using`・型エイリアスによる互換層は作らない。**
4. `KnownForeignReferences` の 5 エントリを削除する。
5. `DomainLayerDependencyTests.IsAllowedDomainDependency` に `AiStockTrading.Shared.Kernel` を足す（軸 7）。
6. サービスを跨ぐ Domain → Domain の `ProjectReference` 4 本を削除する。
7. `ConfigurationService.Domain.Tests` の 2 ファイル（`TradingAssumptionsDefaultsTests` / `CostCalculatorTests`）を
   `Shared.Kernel.Tests` へ移す。**テストは 1 件も削除・skip・無効化しない。**
   移送後に空になる `ConfigurationService.Domain.Tests` には、残る `VersionedAssumptions` の
   テストを新規に足す（プロジェクトは残す。削除は「テストを消した」と区別が付かない）。
8. `docs/data/risk-management-aggregates.md` / `docs/data/trading-assumptions.md` の型の所在を直す（軸 8）。

## 受け入れ基準

- [ ] `KnownForeignReferences` が空で、`既知の逸脱は今も実際に観測できる` と
      `Domain_は既知の逸脱を除いて他サービスを参照しない` の両方が緑
- [ ] 下限検査 3 種が割れていない: Domain 領域 **9 件以上** / 走査ファイル **100 件超** / 禁止トークン **30 件超**
- [ ] `Shared.Kernel` が葉である（`ProjectReference` を 1 本も持たない）ことをテストが固定している
- [ ] `dotnet build` / `dotnet test`（`AiStockTrading.IntegrationTests` の Docker 依存 8 件を除く）/
      `dotnet format --verify-no-changes` が通る
- [ ] カバレッジが床 **79.00%** を割らない
- [ ] 文書系検査（`check-*` 一式・`gen-knowledge-graph --check`・`scripts.test.js` / `scripts.repo.test.js`）が緑

## テスト方針

- 移送した型のテストは**内容を変えずに**移す（`using` と名前空間宣言のみ変更）。
  値・境界・否定形の主張を 1 つも落とさない。
- 葉であることの検査は `Architecture.Tests` に置く（肯定形＝探索が空振りしていないことも対で押さえる）。

## 計画書との差異

- 差異: なし。本件は実装内部の構造整備であり、計画書の要求・受け入れ基準を変えない。

## 未決事項

- なし（`IADR-0259` は別 PR で審査中だが、本件は `IADR-0256` が明示した「土台 5 で解消する」に従うため待たない）。
