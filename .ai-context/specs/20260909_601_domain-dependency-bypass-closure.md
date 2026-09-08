---
title: Domain 依存規律の迂回路（global using・自サービス他層の完全修飾参照）を塞ぐ
issue: "#601"
type: spec
status: done
plan_refs:
  - NFR
adr_refs:
  - IADR-0312
related_ids: [NFR, IADR-0256, IADR-0259, IADR-0261, IADR-0265, IADR-0312]
author: claude (Claude Code)
created: 2026-09-09
updated: 2026-09-09
---

# 仕様書: Domain 依存規律の 2 つの迂回路を既存検査器の拡張で塞ぐ（#601）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/ai-stock-trading/`）を
> 一次情報とし、本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（**`NFR` 無採番**。工程・検査器のメタ作業であり、計画側の非機能要件表に
  当たる番号が無い＝配布物 `traceability.md` §起点 ID の種別 の場合 2。環流しない）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: platform ADR-0030 §基本方針（Domain 層は外部ライブラリへ依存しない＝.NET 標準のみ）
- 起点 issue: [#601](https://github.com/endazon/ai-stock-trading/issues/601)
- 関連 IADR: [IADR-0256](../adr/IADR-0256_domain-dependency-inspection-by-source-scan.md)（ソース走査版の新設）、
  [IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)（単一プロジェクト＋VSA。NsDepCop 撤回）、
  [IADR-0261](../adr/IADR-0261_namespace-alignment-to-platform.md)（ルート名前空間 `<Svc>Service`）、
  [IADR-0265](../adr/IADR-0265_domain-project-count-checker-dynamic-lower-bound.md)（csproj 版の退役宣言）、
  **本作業の実装ADR: [IADR-0312](../adr/IADR-0312_domain-dependency-bypass-closure.md)**
- 計画書リンク: `https://github.com/endazon/project-planning/tree/main/projects/ai-stock-trading/`

## 目的・背景

VSA 移送（IADR-0259）で層が**別プロジェクトからフォルダ**へ変わり、`*.Domain.csproj` が 0 本に
なったため csproj 静的解析版の検査は退役した（#600 / IADR-0265）。規律の強制は
`DomainSourceDependencyTests`（ソース走査）へ一本化されたが、**コンパイラが構造的に防いでいた
2 経路が素通りする**（#601。フェーズ末監査が違反を注入して実測。注入しても 78/78 緑のまま）。

- **(i) `global using` 迂回**: Domain **外**のファイル（同一プロジェクト＝同一コンパイル単位）に
  `global using Wolverine;` を置くと、Domain のソースは**非修飾で**外部型を使える。
  検査 (b) は `Domain/` 配下しか走査せず、検査 (c) は本文に `Wolverine` の文字列が現れないと当たらない。
- **(ii) 自サービス他層への完全修飾参照**: Domain から
  `RiskManagementService.Infrastructure.Persistence.RiskManagementDbContext` と完全修飾で書くと
  止まらない（`ForeignServiceReferencesIn` が自サービスのルートを設計上除外しているため）。
  🔴 `using` 形なら検査 (b) が止める＝**書き方によって止まったり止まらなかったりする**。

## 対象範囲

- 対象: `backend/Tests/AiStockTrading.Architecture.Tests/` の
  `DomainSourceScan.cs` / `RepositoryLayout.cs` / `DomainSourceDependencyTests.cs` と、
  新設する `ServiceCompilationArea.cs`。
- 対象外:
  - **NsDepCop の導入**（#601 案 3）。IADR-0259 で撤回済みであり、覆すには改定 IADR が要る。
    本作業は既存検査器の穴を塞ぐ範囲に閉じる（IADR-0312 決定 3）。
  - 検査 (b)(c)(d) の既存挙動の変更（**弱めない**。新設は (e)(f) として足す）。
  - 実装コード（`backend/Services/**`）の変更。実ツリーの違反は 0 件であり是正対象が無い。

## 設計

### 検査 (f)（案 1）: `global using` を Domain 外でも許可リストに掛ける

- 走査母集合を **サービス全体**（`backend/Services/<Svc>/` 配下の `.cs`）へ広げる。
  除外は `Tests/`（**別プロジェクト＝別コンパイル単位**であり Domain の解決に影響しない）・
  `bin/` `obj/`（ビルド成果物。SDK 生成の `GlobalUsings.g.cs` はここに居る）・`*.g.cs`（生成物）。
- 拾うのは **`global using` 行だけ**である。Domain 外の**通常の** `using` は当然許され、
  Domain のコンパイルにも影響しないため対象にしない。
- 判定は既存の許可リスト `IsAllowedDomainNamespace` を**そのまま**使う（規約の単一情報源）。
- 適用するのは **Domain ソース領域を持つサービスだけ**である。Domain を持たないサービス
  （実測: `ConfigurationService`。IADR-0264 で Domain が空になった）には守るべき Domain が
  同一コンパイル内に無く、`global using` を禁じる根拠が無い。

### 検査 (e)（案 2）: 自サービス他層への完全修飾参照を禁止トークンにする

- 禁止トークンは `<自サービスのルート>.<セグメント>` の形で、**セグメントは実ツリーの
  フォルダ名から導く**（`Common` / `Features` / `Hosted` / `Infrastructure` / `Tests`。`Domain` は除く）。
  手書きの拒否リストにしない —— 新しい層フォルダが増えても母集合が自動で追随する。
- 照合は既存の `ContainsQualifiedNameRoot`（直前が `.`／識別子文字でない・直後が `.`）を再利用する。
- 🔴 **コメント・文字列リテラルを除いた本文に対して照合する**（`StripCommentsAndStringLiterals`）。
  Domain のコメントには層の名前が地の文で現れ得るためである（検査 (c) はコメントも見る
  既存挙動のままにする＝**弱めない**。新設の (e) だけが除去する）。
- `using` 形は既に検査 (b) が止める。(e) は完全修飾形を止める（両方止まる＝書き方で結果が変わらない）。

### 下限検査（fail-loud。黙って 0 件へ落ちない）

| 定数 | 値 | 実測 | 何が壊れたら落ちるか |
| --- | --- | --- | --- |
| `MinimumDomainSourceFiles`（既存） | 100 | 138 | Domain 走査の痩せ |
| `MinimumScannedUsings`（既存） | 60 | — | using 解析器の失効 |
| `MinimumForbiddenTokens`（既存） | 30 | — | CPM 由来トークンの導出失敗 |
| `MinimumServiceCompilationSourceFiles`（新設） | 700 | 768 | サービス全体走査（検査 (f)）の痩せ |
| `MinimumCrossLayerTokens`（新設） | 30 | 44 | 層セグメント導出（検査 (e)）の失効 |

🔴 **`global using` の「件数」には下限を置けない**（実ツリーは 0 本。実測: `backend` 配下の
`bin/` `obj/` を除いた `.cs` に `global using` は 1 本も無い）。**下限は走査ファイル数に置き**、
`global using` 解析器が load-bearing であることは陽性対照のユニットテストで固定する。

### 陽性対照（照合器が壊れたら赤くなること）

実ツリーの違反が 0 件である以上、実ツリー走査のテストは「照合器が常に空を返す」壊れ方では
緑のままである。**照合ロジックは純関数に切り出し**、注入サンプル（文字列フィクスチャ）で固定する。

- (i) の注入: Domain 外のファイル本文 `global using Wolverine;` → 許可リストが**拒む**。
- (ii) の注入: `RiskManagementService.Infrastructure.Persistence.RiskManagementDbContext` →
  (e) が**検出する**。
- 正当な書き方（自サービス `.Domain` 参照・`AiStockTrading.Shared.Kernel` 参照・
  **コメント内**および**文字列リテラル内**の層名）→ **検出しない**。

加えて**実ツリーへの注入実験**を行い、赤くなることを実測する（実験後は必ず戻す）。

## 受け入れ基準

- [x] 検査 (f) が Domain 外の `global using Wolverine;`（実ツリー注入）で赤くなる
- [x] 検査 (e) が Domain 内の自サービス他層への完全修飾参照（実ツリー注入）で赤くなる
- [x] 実ツリーの現状で (e)(f) とも違反 0 件（＝両検査を足しても緑）
- [x] 既存の下限検査を割らない。新設の下限（2 本）も実測値より下に置き、根拠を記す
- [x] 純関数の陽性・否定の対をユニットテストで固定する
- [x] `DomainSourceDependencyTests` の doc コメントを「塞いだ」記述へ更新する
- [x] `dotnet test backend/Tests/AiStockTrading.Architecture.Tests/…` が緑
- [x] `dotnet format --verify-no-changes` 差分ゼロ・`node scripts/check-trace-blocks.js` 緑

## テスト方針

- 実ツリー走査（(e)(f) と各下限）＝ `[Fact]`。
- 照合器の陽性・否定 ＝ `[Theory]`（既存テストの作法に合わせ、肯定・否定を対で置く）。
- 変異試験（注入実験）は手元で実測し、件数を作業報告と IADR へ残す。

## 母集合の取り方（是正・追随の規則に従う）

| 軸 | 引き方 | 結果 |
| --- | --- | --- |
| `global using` の実在 | `grep -rn "^\s*global using" backend --include=*.cs`（`bin/` `obj/` 除外。**パスで除外し拡張子で絞らない**） | 0 件 |
| 自サービス他層の完全修飾参照 | 各サービスの `Domain/` に対し `<Svc>\.(Infrastructure\|Features\|Hosted\|Common\|Tests\|Program)` | 0 件 |
| 走査母集合の大きさ | `find backend/Services -name '*.cs'`（`obj/` `bin/` `Tests/` 除外） | 791 件（うち Domain を持つ 10 サービス分が 768 件） |
| 層セグメント | Domain を持つ 10 サービスの直下フォルダ名から `Domain` を除いた数 | 44 件 |

**除外したものと理由**:

| 除外 | 理由 |
| --- | --- |
| `Tests/` 配下 | **別プロジェクト（別コンパイル単位）**であり、そこの `global using` は Domain の名前解決に影響しない |
| `bin/` `obj/`・`*.g.cs` | ビルド成果物・生成物。SDK の `ImplicitUsings` が吐く `GlobalUsings.g.cs` を拾うと `global using System;` 等で無意味に赤くなる |
| Domain を持たないサービス（実測 `ConfigurationService`） | 同一コンパイル内に守るべき Domain が無い。検査 (f) の対象外（Domain が生えたら自動で対象に戻る） |
| 検査 (c)(d) の挙動 | 本作業では**触らない**。コメント除去を (c) へ広げると既存の検出が弱くなる |
| `backend/Shared/**` | 共有物の依存規律は `SharedProjectDependencyTests` / `SharedKernelIsLeafTests` が csproj で検査しており（#601 の表のとおり現存）、本作業の対象外 |

## 実測（注入実験・変異試験）

いずれも `dotnet test backend/Tests/AiStockTrading.Architecture.Tests/AiStockTrading.Architecture.Tests.csproj`
を実走した。**実験後は元へ戻し**、作業ツリーに `backend/Services` の差分が残らないことを確認したうえで
緑を再確認している。

| # | 注入した内容 | 注入先 | 結果 |
| --- | --- | --- | --- |
| 1 | `global using Wolverine;` を先頭へ | `Services/RiskManagementService/Features/RiskManagement/ClosePosition/Endpoint.cs` | **失敗 1 / 合格 109**（`サービス全体の_global_using_は_Domain_の許可リストを破らない`） |
| 2 | `default(RiskManagementService.Infrastructure.Persistence.RiskManagementDbContext)` を返す型を追記 | `Services/RiskManagementService/Domain/AccountTypePolicy.cs` | **失敗 1 / 合格 109**（`Domain_は自サービスの他層を完全修飾でも参照しない`） |
| 3 | 同じ完全修飾参照を**コメント行として**追記 | 同上 | **合格 110**（コメントは違反にしない＝誤検出しないことの実測） |
| 4 | `global using Wolverine;` を **`Tests/` 配下**へ | `Services/RiskManagementService/Tests/MarketDataWiringTests.cs` | **合格 110**（別コンパイル単位＝除外どおり） |

テスト件数: **合格 86 → 110**（総数 87 → 111。skip 1 は既存の環境依存テストで不変）。

## 計画書との差異

- 差異: なし（計画 ADR の制約を強める方向の実装であり、逸脱は無い）

## 未決事項

- 文字列リテラルを除去することで、**リフレクションで層をまたぐ**書き方（型名を文字列で持つ）は
  検出できない。Domain に実例は無く、検出したい対象（コンパイル時の依存）とも異なるため許容する
  （IADR-0312 の残余リスクへ記載）。
