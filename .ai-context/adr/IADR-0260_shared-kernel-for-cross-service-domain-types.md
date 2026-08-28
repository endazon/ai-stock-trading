---
title: IADR-0260 サービスを跨いで共有する Domain 型は AiStockTrading.Shared.Kernel に置き、共有カーネルは葉に保つ
type: impl-adr
status: Accepted
related_ids: [NFR]
author: endazon (with Claude Code)
created: 2026-08-28
updated: 2026-08-29
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-library-selection.md
---

# IADR-0260: サービスを跨いで共有する Domain 型は `AiStockTrading.Shared.Kernel` に置き、共有カーネルは葉に保つ

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-08-28
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 起点 ID: **`NFR`（無採番）**。構造整備＝メタ作業であり、`.claude/rules/traceability.md` の
  無採番許容ケース 2（「ID 列はあるが、その作業に当たる番号が無い」）に当たる。計画の非機能要件表
  （`NFR-01`〜`NFR-17`）を読み、性能・可用性・セキュリティ・運用保守・費用・拡張性・法規のいずれも
  **稼働する製品**の要件であって「サービス間の依存の向き」を扱う番号が無いことを確認した。
  `NFR-16`（ポートによる差し替え可能性）は近いが、求めているのは**外部連携先**の差し替えであり当たらない。
  同ケース 2 は「環流しない」と定めるため、計画リポジトリへの起票は行わない。
- 守る制約: **platform ADR-0030 §基本方針**（Domain 層は .NET 標準のみに依存する）、
  **platform ADR-0019 決定 4**（ユニット単位の契約プロジェクト）。**制約そのものは本 ADR で変えない。**
- 関連する作業仕様書: [20260828_w9f5_shared-kernel](../specs/20260828_w9f5_shared-kernel.md)
- 関連 IADR: [IADR-0256](IADR-0256_domain-dependency-inspection-by-source-scan.md)（既知の逸脱 5 件を
  一覧に載せ、**解消は「土台 5」に委ねる**と明記した。本 ADR がその土台 5 である）、
  [IADR-0128](IADR-0128_standard-project-layout.md)（層分割とプロジェクト命名。決定 2 が
  「SharedKernel は現時点で実体が無い」と書いていた状態を、本 ADR が更新する）、
  [IADR-0043](IADR-0043_backtest-foundation.md) / [IADR-0025](IADR-0025_pnl-aggregation.md) /
  [IADR-0027](IADR-0027_cost-control.md) / [IADR-0045](IADR-0045_stage0-gate.md)
  （いずれも「既存の型を再利用し新設しない」と決めた結果、Domain 跨ぎ参照が生まれた側）
- 基盤の先行事例: **microservices-platform IADR-0229**（`Platform.Shared.Kernel` が Result / Error を公開する）。
  **樹形だけを揃え、中身は揃えない**（次節の決定 2）。

## 背景・課題

サービスの Domain が**他サービスの Domain を直接参照**している箇所が 5 ファイル・プロジェクト間の辺 4 本あった。

| 参照する側 | 参照先の名前空間 | 実際に使っていた型 |
| --- | --- | --- |
| `BacktestService.Domain/BacktestCostModel.cs` | `AiStockTrading.Configuration.Domain` | `TradingAssumptions` / **`CostCalculator`** |
| `BacktestService.Domain/Stage0Promotion.cs` | `AiStockTrading.RiskManagement.Domain` | `TradingStage` |
| `CostControlService.Domain/CostGovernor.cs` | `AiStockTrading.Configuration.Domain` | `MonthlyCostLimits` |
| `ReportService.Domain/LlmUsageRecord.cs` | `AiStockTrading.Configuration.Domain` | `TradingAssumptionsDefaults` |
| `ReportService.Domain/PnlAggregator.cs` | `AiStockTrading.Configuration.Domain` | `TradingAssumptions` / **`CostCalculator`** |

これは「1 サービス = 1 プロジェクト」にした瞬間に、**相手サービスの永続化・エンドポイント・
メッセージング配線までビルドへ引き込む**。個々の再利用判断（費用式を 2 か所に書かない・段階 enum を
新設しない）はいずれも正しく、**間違っていたのは置き場所だけ**である。

> 🔴 **`CostCalculator` は着手時の引き継ぎ表に無かった。** 引き継ぎは各ファイルの「外部型」を
> `TradingAssumptions` のみと記していたが、両ファイルは `CostCalculator.EstimateOneWayCost(...)` を
> **実際に呼んでいた**。**型宣言の軸だけで引くと、静的メソッド呼び出しは落ちる。**
> 移送しなければ `using` が残り、逸脱は解消しなかった。母集合は軸を変えて引き直す
> （`.claude/rules/traceability.md` 規則 5）。

## 検討した選択肢

| 案 | 評価 |
| --- | --- |
| **A: 共有カーネルを新設して型を移す（採用）** | 依存の向きが正しくなる。共有物は 1 か所に集まり、再利用の判断（式を 2 か所に書かない）はそのまま保てる |
| B: 型を各サービスへ複製する | 費用式・段階 enum の複製は「片方だけ直したときに気付けない」を招く。IADR-0021 / IADR-0045 が明示的に避けた道であり、覆す理由が無い |
| C: 共有契約（`Shared.Contracts`）へ入れる | **採らない。** 契約はイベント・DTO の互換境界であり、`TradingStage` を載せると**契約が enum の序数に縛られる**。契約側は「Risk の enum に依存しないよう primitive で表現する」と明示的に決めてある（`StageTransitioned` 等）。その設計を崩さない |
| D: `using` を型エイリアス・`global using` で吸収する | 参照元を直さずに緑にするだけで、依存は残る。**検査を欺く形**であり採らない |
| E: 現状維持（既知の逸脱一覧を持ち続ける） | 許容一覧は「増やすと検査が弱くなる」もの。IADR-0256 が期限として本作業を指名している |

## 決定

### 決定 1: `backend/Shared/AiStockTrading.Shared.Kernel/` を新設し、名前空間は `AiStockTrading.Shared.Kernel.Trading` にする

`Shared.Contracts` が `Trading` / `Events` / `Llm` … とフォルダ＝サブ名前空間で切っている粒度に倣う。
`Shared.Kernel` 直下に平置きしない（次に別領域の共有型が来たとき切り直しになる）。
検査器の許可判定は接頭辞一致（`AiStockTrading.Shared.Kernel[.*]`）であり、サブ名前空間でも通る。

csproj は既存の共有プロジェクトの書式に厳密に倣い、`TargetFramework` も `PackageReference` の
バージョンも書かない（単一情報源は `Directory.Build.props` / `Directory.Packages.props`）。

### 決定 2: 入れるのは「他サービスから引かれている型と、その随伴物」だけである

**`Result` / `Error` / DDD 基底型は新設しない。** 基盤の `Platform.Shared.Kernel` には在るが、
**AST に実在しない物を先回りで作らない**（樹形を揃えることと、中身を揃えることは別である）。

| 型 | 移送 | 理由 |
| --- | --- | --- |
| `TradingAssumptions` | ○ | Backtest / Report の Domain が引いている |
| `CommissionSchedule` | ○ | `TradingAssumptions` の構成型。**残すと共有カーネル → 設定サービスの逆流になる** |
| `MonthlyCostLimits` | ○ | CostControl の Domain が引いている。かつ `TradingAssumptions.CostLimits` の型 |
| `TradingAssumptionsDefaults` | ○ | Report の Domain が引いている。`TradingAssumptions` を返すため単独では残せない |
| `CostCalculator` | ○ | Backtest / Report の Domain が**実際に呼んでいる**（上記の発見） |
| `TradingStage` | ○ | Backtest の Domain が引いている。通知・報告もこの段階を運ぶ |
| `VersionedAssumptions` | ✕ →〔2026-08-29 / #526〕**○** | **設定サービス固有**（設定ストアの版・楽観排他）。他サービスの Domain は引いておらず、消費側は認可された経路（`ConfigurationService.Client`）越しに使う。🔴 ［2026-08-29 追記 / #526］**[IADR-0264](IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定 2 で本行の判定を引き直し、共有カーネルへ移送した。**除外の理由（「認可された経路越しに使う」）は、同 ADR 決定 1 が `ConfigurationService.Client` を廃止したことで成立しなくなったためである。**他の行の判定は不変。** |
| `StageSettings` | ✕ | **リスク管理固有**（発注先と資金上限比）。`TradingStage` と同一ファイルにあったが、**ファイルを分割し enum だけを抜く** |

**「移送を最小にする案」と「意味的なまとまりを保つ案」の両方を検討した。**
最小案（`MonthlyCostLimits` だけ・`TradingStage` だけを抜く）は、`TradingAssumptions` が
設定サービスに残ったまま共有カーネルの型を参照する形になり、**共有カーネルが葉でなくなる**。
まとまり案（ファイルごと全部移す）は `VersionedAssumptions` と `StageSettings` まで動かし、
各サービス固有の概念を共有カーネルへ流出させる。**採ったのはその中間で、境界は
「他サービスから引かれているか」＋「その型が成立するのに要るか」の 2 条件である。**

### 決定 3: 参照元は実際に直す。互換層を作らない

型の再エクスポート・`global using`・型エイリアスによる吸収は行わない。
`using` を書き換えた実コードは 85 ファイル、加えて**完全修飾 1 箇所**
（`RiskManagementService.Infrastructure/Foundation/Persistence/PersistenceRows.cs`）と
**部分修飾 1 箇所**（`BacktestService.Application.Tests/Stage0GateServiceTests.cs`）を直した。
**後者 2 つは `using` の走査には現れない** —— 名前空間の軸だけで引くと落ちる。

### 決定 4: 共有カーネルは葉である。テストで固定する

`AiStockTrading.Shared.Kernel` はどのサービスも参照しない（参照してよいのは
`AiStockTrading.Shared.Contracts` だけ）。**これを守らないと、解消したはずの Domain 跨ぎ参照が
共有カーネル経由で復活する。しかもそれはソース走査には現れない**——各サービスの Domain は
`AiStockTrading.Shared.Kernel` としか書かないためである。

既存の `SharedProjectDependencyTests` は**外部ライブラリ依存ゼロ**しか見ておらず、
`Shared.Kernel` → `SomeService.Domain` を止められない（参照先の Domain も `PackageReference` を
持たないため推移閉包の検査は緑のまま通る）。**依存の向きは、外部ライブラリの有無とは別の関心事である。**
`AiStockTrading.Architecture.Tests/SharedKernelIsLeafTests` を新設し、
肯定形（対象が実在する）・否定形（許可判定が load-bearing である）と対で固定した。

### 決定 5: 二重化した検査は、両方に同じ許可を書く

ソース走査側（`DomainSourceScan.IsAllowedDomainNamespace`）は先に
`AiStockTrading.Shared.Kernel[.*]` を許していたが、**csproj 側
（`DomainLayerDependencyTests.IsAllowedDomainDependency`）は許していなかった**
（許すのは `*.Domain` / `*.SharedKernel` / `AiStockTrading.Shared.Contracts`）。
新設した瞬間に csproj 側が赤くなる状態だった。**二重化は「両方に同じ規約を書く」ことまで含む。**

### 決定 6: `KnownForeignReferences` は空にし、空のまま保つ

既知の逸脱 5 件を削除した（残すと `既知の逸脱は今も実際に観測できる` が赤くなる設計である）。
**ここへ行を足すのは「Domain がサービス境界を跨いだ」ことの追認であり、許容範囲が広がるぶんだけ
新しい違反を見逃す。** 足す前に共有カーネルへ抜けないかを検討する。

### 決定 7: 移送した型のテストは内容を変えずに移し、残った型のテストを足す

`ConfigurationService.Domain.Tests` の 2 ファイル（`TradingAssumptionsDefaultsTests` /
`CostCalculatorTests`）を `AiStockTrading.Shared.Kernel.Tests` へ移した。**`using` と名前空間宣言
以外は 1 文字も変えていない**（主張・境界値・否定形を落とさないため）。
移送後に設定サービスの Domain に残るのは `VersionedAssumptions` だけになるため、
**その判定のテストを新設した**（`VersionedAssumptionsTests`）——テストが 1 件も無いテストプロジェクトは
「テストを消した」と区別が付かない。**プロジェクトは削除しない。**

## 影響・帰結

- **サービスを跨ぐ Domain → Domain の `ProjectReference` は 0 本になった**（着手時 4 本）。
  残るクロスサービス参照は `TradeDecisionService.Application` → `RiskManagementService.Domain`
  （Application 層・本 ADR の射程外）と、`BacktestService.Domain.Tests` →
  `RiskManagementService.Domain`（**テスト専用**。Stage 0 の許容 DD が運用の DD 停止ラインと
  同値であることを固定するために必要で、土台 5 まではバックテストの Domain 経由で推移的に入っていた。
  Domain 層の依存規律はテストプロジェクトへは及ばないため、**明示的な参照として残す**）。
- 下限検査の実測は Domain 領域 **9 件**（下限 9）・走査ファイル **117 件**（下限 100 超）・
  `using` **87 本**（下限 60 超）・禁止トークン **63 件**（下限 30 超）。
  **ファイル数は 120 → 117 へ減ったが下限には触れない。下限は下げていない**（doc コメントに
  新旧の実測値を併記した）。
- **`ConfigurationService.Domain` は 1 ファイル（`VersionedAssumptions.cs`）になった。**
  空にすると Domain ソース領域が 8 件になり `Domain_ソース領域の探索が空振りしていない`（下限 9）が
  落ちる。決定 2 の線引きはこの下限とも整合する。
- 契約プロジェクトは**一切変えていない**。イベントは引き続き段階を primitive で運ぶ。

## 残余リスク

- **共有カーネルは「共有したい物の吸い込み口」になりやすい。** 決定 2 の 2 条件を満たさない型が
  「ついでに」入ると、全サービスが不要な型を引く。**入れる前に、引いているのが本当に他サービスの
  Domain かを確認する**（Application / Infrastructure から引かれているだけなら、公開クライアント
  ライブラリ〔IADR-0063 決定 3〕の経路で足りる）。機械検査は無い。
- `TradingStage` の序数は永続化された遷移履歴と設定 JSON の意味を担う。**移送で序数は変えていない**が、
  共有カーネルへ移ったことで「リスク管理サービスの都合で足す」判断がしにくくなった側面はある
  （末尾追加という規律は enum のコメントに残してある）。
