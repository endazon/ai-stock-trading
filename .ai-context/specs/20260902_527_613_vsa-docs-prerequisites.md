---
title: #527 クローズと #613 文書面の前提整備（CLAUDE.md 訂正・Hosted/ の位置づけ）
type: spec
status: draft
related_ids:
  - NFR
  - IADR-0259
author: endazon (with Claude Code)
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md
  - planning:projects/microservices-platform/07_adr/ADR-0068_three-level-slice-split-rule.md
---

# 仕様書: #527 クローズと #613 文書面の前提整備

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（工程管理・文書統制のメタ作業）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 起点 ID: `NFR`（無採番。`.claude/rules/traceability.md` 無採番許容ケース 2 —— 規約整備・文書統制のメタ作業であり、
  計画の非機能要件表に当たる番号が無い。IADR-0259 と同じ判断）
- 関連 ADR: platform `ADR-0065`（サービスの標準構成を単一プロジェクト＋VSA へ改定）・platform `ADR-0068`（3段目の判定基準）
- 計画書リンク: <https://github.com/endazon/project-planning/blob/main/projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md>
  （読み取り専用。隣接クローン `../project-planning` でも同内容を確認できる。リポジトリ本体は依存しない）

## 目的・背景

- **#527**（Tests がサービスごと 3 プロジェクトに分かれている）は、VSA 移送波（IADR-0259・#583〜#600）の完了により
  実測上すでに解消している。実測を issue へ記録しクローズする。
- **#613**（バックエンド構成を platform ADR-0065 へ追随させる）は本体の是正（`Features/<集約>/<操作>/` の 3 段化・
  `Tests/` の鏡写し・`Domain/` 欠け 3 件）を実装 PR で行う前提として、**文書面の 2 点を先に片づける**。
  1. `CLAUDE.md` の「3 段目のスライス分割は MSP も未実装のため採らない」という記述が、
     platform `ADR-0065` 決定 2（3 段化を規範とする・2026-08-30 裁定）と食い違っている。
  2. `Hosted/`（6 サービスが持つ `BackgroundService` の置き場）は platform `ADR-0065` の樹形
     （`Features/Domain/Infrastructure/Common/Tests`）に無い要素であり、位置づけを IADR で確定する必要がある
     （#613 補足・制約）。
- 本作業は**純粋なドキュメント修正**であり、`backend/Services/**` のフォルダ移動は行わない
  （#613 本体の実装 PR がその後に行う）。

## 対象範囲

- 対象:
  - #527 の実測コメント投稿・クローズ
  - `CLAUDE.md` §技術スタック別ルール › C#/.NET の該当 1 文の訂正
  - `Hosted/` の位置づけを確定する IADR の新規作成
  - `.ai-context/adr/README.md` の索引更新
- 対象外:
  - `Features/<集約>/<操作>/` の 3 段化そのもの（#613 本体・別 PR）
  - `Tests/` の鏡写し（#613 本体・別 PR）
  - `Domain/` 欠け 3 サービスへの対応（#613 本体・別 PR）
  - `Hosted/` の実ファイル移動（本 IADR は位置づけの決定のみ）

## 設計

### 1. #527 の実測

`develop` `5fb778e7`（本ブランチの分岐元）で実測する。

```
$ find backend/Services -maxdepth 3 -iname "*.Tests.csproj" | wc -l
11   # 各サービス 1 本ずつ（AuditService〜TradeDecisionService）
$ find backend -iname "*.Api.Tests.csproj" -o -iname "*.Application.Tests.csproj" -o -iname "*.Infrastructure.Tests.csproj"
（0 件。旧 3 分割の残骸なし）
```

統合テストは元から `backend/Tests/AiStockTrading.IntegrationTests`（横断・Testcontainers）に分かれており、
サービス内で `Unit/`/`Integration/` を分ける必要が実質無かった（#528 完了コメントと同じ実測方針）。
この表を `gh issue comment 527` に投稿し、`gh issue close 527 --reason completed` でクローズする。

### 2. `CLAUDE.md` の訂正

`CLAUDE.md` L121 の但し書き「（3 段目のスライス分割は MSP も未実装のため採らない）」を、
platform `ADR-0065` 決定 2（3 段化を規範とする。フォローアップ 7 が本リポの当該記述の訂正を名指しで求める）
に沿って書き換える。**「採らない」から「採る」への反転**であり、実装（フォルダの 3 段化）そのものは
#613 本体 PR が行うため、CLAUDE.md は「移行中である」ことが分かる書き方にする。

バイト数は `scripts/check-reading-budget.js` の予算（Claude Code 集合 51,200 バイト）を確認し、
増える場合は同じ行内の冗長語を削って収める（現状 41,577 バイト・81.2%。数十バイトの増は予算内）。

### 3. `Hosted/` の位置づけ

6 サービス（`CostControlService` `InformationCollectionService` `MarketMonitorService`
`OrderExecutionService` `ReportService` `RiskManagementService`）の `Hosted/` を実測した。

| サービス | `Hosted/` の中身 |
| --- | --- |
| `CostControlService` | `ProcessedMessageRetentionService.cs` |
| `InformationCollectionService` | `CollectionOptions.cs` `CollectionPollingService.cs` `DegradationStateTracker.cs` |
| `MarketMonitorService` | `MonitorOptions.cs` `MonitorPollingService.cs` |
| `OrderExecutionService` | `BrokerAvailabilityProbeService.cs` `BrokerPositionSnapshotService.cs` `OrderFillPollingService.cs` `OrderReservationReconciliationService.cs` `OrderReservationRetentionService.cs` `ProtectiveStopGuardService.cs` |
| `ReportService` | `ReportAutoGenerationOptions.cs` `ReportAutoGenerationService.cs` |
| `RiskManagementService` | `ObservedDrawdownRefreshOptions.cs` `ObservedDrawdownRefreshService.cs` `QuoteRefreshService.cs` `WithdrawalEvaluationOptions.cs` `WithdrawalEvaluationService.cs` |

いずれも `BackgroundService` 派生 ＋ その `Options` 型である。中身を読むと、各 `BackgroundService` は
自サービスの `Features/<集約>/` の AppService を `using` し（例: `MonitorPollingService` は
`MarketMonitorService.Features.MarketMonitor` を参照）、定時に**アプリケーションサービス（複数操作にまたがる
巡回・評価ロジック）を呼び出す**トリガーである。`Infrastructure/`（Persistence/Authentication/Messaging/
ExternalServices）にも `Features/<集約>/<操作>/`（1 操作専属）にも素直には収まらない。

`microservices-platform` 側は `Hosted/` を 1 件も持たない（`find ... -iname Hosted` 0 件・2026-09-02 実測）。
これは MSP がサービスの実行入口を `Api` / `Worker` の排他（ADR-0065 決定 6）で表現するのに対し、
AST の該当 6 サービスは **同一ホストで HTTP 面と定時巡回を併せ持つハイブリッド構成**であり、
決定 6 が想定する「実行入口の形の違い」に当たらないためである（決定 6 は Program.cs の形の話であり、
このハイブリッド構成そのものには触れていない）。

**採る案**: (a) 現状維持 —— `Hosted/` を AST 固有の第 4 の頂点として `CLAUDE.md` に明記する。
理由・却下案の詳細は IADR-0276 に記す。

## 受け入れ基準

- [ ] `gh issue view 527` がクローズ済みで、実測に基づくコメントが付いている
- [ ] `CLAUDE.md` の「3 段目のスライス分割は MSP も未実装のため採らない」が撤回され、
      platform `ADR-0065` 決定 2 に沿った記述に変わっている
- [ ] `Hosted/` の位置づけを決めた IADR が `.ai-context/adr/` にあり、`.ai-context/adr/README.md` の索引に載っている
- [ ] `backend/Services/**` のフォルダ構成に変更が無い（本作業は文書のみ）
- [ ] `node scripts/check-trace-blocks.js` / `check-cross-repo-refs.js` / `check-doc-links.js` /
      `check-adr-index-sync.js` / `check-reading-budget.js` が緑

## テスト方針

本作業はコード変更を伴わないため、xUnit のテストケース追加は無い。上記の機械検査スクリプトの実行結果を
検証手段とする。

## 計画書との差異

- 差異: なし。platform `ADR-0065` 決定 2・フォローアップ 7 の指示どおりに `CLAUDE.md` を訂正する。
  `Hosted/` の位置づけは ADR-0065 が明示的に触れていない領域についての実装側の判断であり、
  計画への環流は不要（#613 補足が「実装側で判断し IADR に残す」ことを求めている）。

## 未決事項

- なし。`Hosted/` の実ファイル移動は #613 本体の実装 PR のスコープであり、本作業では行わない。
