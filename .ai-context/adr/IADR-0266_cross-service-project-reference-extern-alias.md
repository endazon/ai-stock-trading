---
title: IADR-0266 サービス本体を他サービスから参照するときは extern alias を与える（単一プロジェクト化の帰結）
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0259, IADR-0260, IADR-0263, IADR-0050]
author: endazon (with Claude Code)
created: 2026-08-29
updated: 2026-08-29
---

# IADR-0266: サービス本体を他サービスから参照するときは extern alias を与える

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: Accepted
- 日付: 2026-08-29
- 決定者: endazon（方針・[IADR-0259](IADR-0259_single-project-vsa-structure.md) の利用者裁定）/
  Claude Code（11 本目の移送で顕在化した問題の起案）

## 起点・関連

- 起点 ID: **`NFR`（無採番）**。構造移送＝メタ作業であり、`.claude/rules/traceability.md`
  「起点 ID の種別」の無採番許容ケース 2 に当たる（[IADR-0259](IADR-0259_single-project-vsa-structure.md)
  が確定済みの判断を継承する。環流はしない）。
- 関連する実装仕様書:
  [20260829_w11s11_riskmanagementservice-vsa](../specs/20260829_w11s11_riskmanagementservice-vsa.md)
- 上流: [IADR-0259](IADR-0259_single-project-vsa-structure.md)（1 サービス = 1 プロジェクト）・
  [IADR-0260](IADR-0260_shared-kernel-for-cross-service-domain-types.md)（共有カーネルの憲章と、
  「残るクロスサービス参照」の記録）・[IADR-0263](IADR-0263_auditservice-vsa-migration-first-of-eleven.md)
  （移送の型）

## コンテキストと課題

11 サービス移送波の **11 本目（RiskManagementService）だけが、他サービスから参照される側**である。
[IADR-0260](IADR-0260_shared-kernel-for-cross-service-domain-types.md) が「残るクロスサービス参照」
として明記した 2 本がそれに当たる（実測。本 PR 着手時点）:

| 参照元 | 参照先 | 用途 |
| --- | --- | --- |
| `backend/Services/TradeDecisionService/TradeDecisionService.csproj` | `RiskManagementService.Domain.csproj` | `PositionSizer` / `RiskLimitSettings` / `TradingDefaults`（サイジングの単一情報源。IADR-0003/0017） |
| `backend/Services/BacktestService/Tests/BacktestService.Tests.csproj` | 同上 | Stage 0 の許容 DD が運用の DD 停止ラインと同値であることの固定（テスト専用） |

**単一プロジェクト化で `RiskManagementService.Domain.csproj` が消える。** 参照先を
`RiskManagementService.csproj` へ張り替えると、参照先は `Microsoft.NET.Sdk.Web` の実行可能
プロジェクトであり、**`Program.cs` 末尾の `public partial class Program { }`（統合テストの
`WebApplicationFactory` のために全サービスが持つ）がグローバル名前空間で衝突する。**

実測（張り替え直後のクリーンビルド）: **`error CS0433: The type 'Program' exists in both
'RiskManagementService' and 'TradeDecisionService'` が 24 件**（`TradeDecisionService.Tests` 11 ファイル・
`BacktestService.Tests` 1 ファイル）。**これは移送前には存在しなかった問題である**
（参照先が `Library` SDK のクラスライブラリだったため `Program` を持たなかった）。

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A: 共有される Domain 型を `AiStockTrading.Shared.Kernel` へ移す | `PositionSizer` / `RiskLimitSettings` / `TradingDefaults` ほかをカーネルへ移設し、クロスサービス参照そのものを消す | ✕ **本波の射程外**（[IADR-0259](IADR-0259_single-project-vsa-structure.md) 決定 7「本波では振る舞いを変えない」・型の所属変更は名前空間の変更を伴う）。✕ [IADR-0260](IADR-0260_shared-kernel-for-cross-service-domain-types.md) の残余リスク（「共有カーネルは吸い込み口になりやすい」）に正面から当たり、**移送のついでに決めてよい判断ではない** |
| B: `RiskManagementService` の `Program` を非公開にする | 衝突源を消す | ✕ 同じ `Program` を `AiStockTrading.IntegrationTests`（`RiskManagementWorker::Program`・4 箇所）と `RiskManagementService.Tests`（`RiskWorkerWebApplicationFactory`）が別アセンブリから使う。閉じるには `InternalsVisibleTo` の再導入が要り、[IADR-0263](IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定 4 に反する |
| **C: `ProjectReference` に `Aliases` を与える（採用）** | 参照元 3 csproj に `Aliases="RiskManagementWorker"` を付け、参照する 11 `.cs` に `extern alias` を書く | ○ **本リポジトリが同じ問題に対して既に採っている機構**（IADR-0050 決定 1。`AiStockTrading.IntegrationTests` が**この同じアセンブリ**を `RiskManagementWorker` として参照している）。○ 型の所属も名前空間も 1 文字も変えない。✕ 参照側 11 ファイルに `extern alias` の 1 行が増える |

## 決定

### 決定 1 — サービス本体プロジェクトを他プロジェクトから参照するときは `Aliases` を付ける

`backend/Services/<Svc>/<Svc>.csproj` への `ProjectReference` は、**参照元が自分の `Program` を
使う（`WebApplicationFactory<Program>` 等）場合、必ず `Aliases` を与える。**
別名はリポジトリ全体で **1 アセンブリ 1 名**とし、`RiskManagementService` は既存の
`RiskManagementWorker` を用いる（`AiStockTrading.IntegrationTests` が使っている名と同じ。
**同じアセンブリを場所によって別名で呼ばない**）。

### 決定 2 — 🔴 `Aliases` は推移参照へ伝播しない。中間プロジェクトを持つテストは自分で明示参照する

**実測**: `TradeDecisionService.csproj` に `Aliases` を付けても、それを参照する
`TradeDecisionService.Tests` では `RiskManagementService` が**グローバル別名のまま入り、CS0433 が残った**
（張り替え後 2 回目のビルドで観測。1 回目のエラー集合には現れない）。
したがって **`TradeDecisionService.Tests.csproj` にも同じプロジェクトを明示的に `ProjectReference` し、
`Aliases` を与える**。明示参照は推移参照より優先される（実測でエラーが消えることを確認）。

### 決定 3 — 実行可能プロジェクトを参照しても振る舞いは変わらないことを、思い込みでなく実測で確かめる

`Microsoft.NET.Sdk.Web` のプロジェクトを参照すると、参照元の出力ディレクトリへ
`RiskManagementService.dll` / `.deps.json` / `.runtimeconfig.json` /
`.staticwebassets.endpoints.json` が置かれる。**危ないのは 2 点で、どちらも実測で否定した。**

1. **`appsettings*.json` の上書き** —— 起きない。`Content` は推移的にコピーされず、
   `TradeDecisionService/bin/.../appsettings.Development.json` は自分の内容のまま（md5 一致）。
2. **Wolverine のハンドラ発見が増える** —— 起きない。本ユニットの配線
   （`UseAiStockTradingRabbitMq`）は `options.Discovery.IncludeAssembly(assembly)` で
   **明示的に渡したアセンブリ**とエントリアセンブリだけを見る。出力ディレクトリの走査はしない。
   `TradeDecisionService` が渡すのは `typeof(PriceMovementDetectedHandler).Assembly`＝自分自身のみ。
   **[IADR-0264](IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定 4 が警告した
   「対称に置くこと自体が購読の追加になる」経路は、ここでは開かない。**

## 理由

- **決定 1 は新しい機構を持ち込んでいない。** 同じ衝突（`Program` の曖昧）に対して、本リポジトリは
  IADR-0050 決定 1 以来 `extern alias` を使っており、**その別名（`RiskManagementWorker`）が
  まさに本アセンブリに付いている**。同じ問題に 2 つ目の答えを作らないことを優先した。
- **決定 2 は実測が設計判断を覆した例である。** 「親に `Aliases` を付ければ子にも効く」は
  自然な期待だが偽であり、**1 回目のビルドでは見えない**（1 回目は別のエラーで止まる）。
  [IADR-0259](IADR-0259_single-project-vsa-structure.md) の移送波が繰り返し記録してきた
  「1 回のビルドで全容は出ない」の同型である。
- **決定 3 は「たぶん大丈夫」を残さないためにある。** 実行可能プロジェクトの参照は
  出力を汚すので、**汚れが振る舞いに届くかどうかを 2 経路とも現物で確かめた**。

## 結果

- 良い影響:
  - **11 サービスすべてが 1 サービス = 1 プロジェクトになった**（本リポジトリのうち 10 本。
    ReportService は別 PR で並行して移送中）。クロスサービス参照は型の所属を変えずに存続した。
  - 参照元の変更は **csproj 3 本 ＋ `.cs` 11 ファイル（`extern alias` 1 行と `using` の別名修飾）** に収まり、
    テスト本文・アサーションは 1 行も変えていない。
- 悪い影響・トレードオフ:
  - **`TradeDecisionService` / `BacktestService.Tests` の出力へ `RiskManagementService.dll` と
    その `deps.json` 等が同居する。** 実行は `dotnet TradeDecisionService.dll` で行うため
    起動経路は変わらないが、**コンテナイメージが（リスク管理サービスの分だけ）大きくなる**。
  - **`extern alias` は読み手のコストである。** `using RiskManagementWorker::RiskManagementService.Domain;`
    は「別サービスの Domain を引いている」ことを可視にする利点と引き換えに、慣れない読み手を止める。
- フォローアップ:
  1. 🔴 **恒久解は案 A（共有される Domain 型を `AiStockTrading.Shared.Kernel` へ移す）である。**
     [IADR-0260](IADR-0260_shared-kernel-for-cross-service-domain-types.md) が「Application 層由来なので
     射程外」として残した 2 本が、**本決定によって extern alias というコストを伴う形で固定された**。
     移設の可否（カーネルの憲章に照らして `PositionSizer` / `RiskLimitSettings` / `TradingDefaults` が
     入ってよいか）は**別 PR の設計判断**とし、本 ADR は結論を出さない。
  2. 本決定は**本リポジトリで唯一「他サービスから参照される」サービス**に適用された。
     同型が 2 例目に現れたら（＝新たにサービス跨ぎ参照が生まれたら）、
     「検査器・規約の追加は同型事故 2 回から」に照らして機械検査の要否を検討する。

## 関連

- 上流: [IADR-0259](IADR-0259_single-project-vsa-structure.md)（1 サービス = 1 プロジェクト）・
  [IADR-0260](IADR-0260_shared-kernel-for-cross-service-domain-types.md)（残るクロスサービス参照の記録）・
  [IADR-0263](IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定 4（`InternalsVisibleTo` を新設しない）
- 作業仕様書: [20260829_w11s11_riskmanagementservice-vsa](../specs/20260829_w11s11_riskmanagementservice-vsa.md)
- Supersedes: なし
- Superseded by: なし
