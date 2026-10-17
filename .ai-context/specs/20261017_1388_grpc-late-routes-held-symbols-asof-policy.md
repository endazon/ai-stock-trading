---
title: east-west gRPC 段 5 の追補 —— 段 2〜5 の後に REST だけで足された読み取り 2 本（情報収集の保有銘柄・Stage 0 の当時の日報の方針）を gRPC でも呼べるようにする
type: spec
status: done
related_ids: [NFR, FR-01, FR-02, FR-04, FR-07, FR-15, IADR-0284, IADR-0328, IADR-0331, IADR-0427, IADR-0446, IADR-0489, IADR-0528, IADR-0547, IADR-0557, MSP:ADR-0029, MSP:ADR-0075]
author: endazon (with Claude Code)
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
---

# 仕様書: east-west gRPC 段 5 の追補（#1388）

## 起点

- issue #1388（受け皿は #753・`Refs`）。射程と段の切り方は [IADR-0284](../adr/IADR-0284_east-west-grpc-scope-and-order-ruling.md) 決定 5。
  雛形は段 2（[IADR-0427](../adr/IADR-0427_risk-read-grpc-stage2.md)）と段 4（[IADR-0446](../adr/IADR-0446_report-monitor-cost-read-grpc-stage4.md)）。
  本 PR の判断は [IADR-0557](../adr/IADR-0557_grpc-late-routes-held-symbols-asof-policy.md)。
- 計画 ID: NFR（輸送の差し替え。振る舞いは変えない）。経路の起点は FR-01・FR-02（情報収集の対象銘柄・#1363）、FR-04・FR-07・FR-15
  （Stage 0 の記録の当時の方針・#1308）。

## 背景

段 6（REST 撤去）の完了条件は「east-west の呼び出し箇所がすべて gRPC へ移り、呼び出し元の `Http*` アダプタが退役した」ことである
（IADR-0284 の 2026-09-27 追記）。段 2〜5 の後に REST だけで足された east-west の読み取りがあり、数えないまま段 6 に入ると
退役できないアダプタが残る。

## 母集合（規則 9。`origin/develop` `c9a4d2ba`・2026-10-17）

引き方: `backend/Services` と `backend/Shared` の `Http*.cs`（テスト以外）について、同じディレクトリに `Grpc` へ置き換えた名前の
ファイルがあるかを突き合わせた（46 件中 NONE 12 件）。NONE を 1 件ずつ開いて分類した。

| ファイル | 呼び先 | 分類 |
| --- | --- | --- |
| `InformationCollectionService/.../HttpRiskManagementHeldSymbolsProvider.cs` | リスク管理 `GET /risk-controls/open-positions`（#1363） | **本 PR**（提供側の rpc は既存） |
| `TradeDecisionService/.../HttpAsOfDailyPolicySource.cs` | 報告書 `GET /reports/daily-policy/as-of`（#1308） | **本 PR**（提供側の面は既存・rpc を足す） |
| `RiskManagementService/.../HttpShortSellBorrowSource.cs` | 発注執行 `GET /order-execution/short-permit`（#967） | 対象外: 発注執行に gRPC 面が無い（面の新設は別段） |
| `TradeDecisionService/.../HttpDailyBarsSource.cs` | 発注執行 `GET /order-execution/daily-bars`（#1118） | 同上 |
| `TradeDecisionService/.../HttpInFlightCloseProvider.cs` | 発注執行 `GET /order-execution/in-flight-closes`（#1338） | 同上 |
| `BacktestService/.../HttpOrderExecutionHistoricalBarSource.cs` | 発注執行 `GET /order-execution/daily-bars`（#1308） | 同上 |
| `Shared.KnowledgeBase/.../HttpKnowledgeBaseSearch.cs` | MSP Retrieval `POST /search` | 対象外: AST→MSP は段の外（IADR-0284）。MSP の `DocumentSearch` は利用者文脈を必須にし、運べる呼び出し元を許可集合（既定 `aianalysis-service`）に限る。並び順（`sort_by`）と属性の完全一致の絞り込みも面に無い |
| `Shared.KnowledgeBase/.../HttpKnowledgeBaseWriter.cs`・`HttpKnowledgeDocumentCatalog.cs` | MSP Document `/documents` | 対象外: MSP に書き込みの proto が無い |
| `TradeDecisionService/.../HttpLlmCompletionClient.cs`・`ReportService/.../HttpReportNarrativeDrafter.cs` | MSP LlmGateway `/complete` | 偽陽性: 共有の `GrpcLlmCompletionTransport` が対（#746） |
| `ReportService/.../HttpDecisionReferenceSource.cs` | 監査 `GET /audit/events/by-type` | 偽陽性: 同じファイルに `GrpcDecisionReferenceSource` がある |

軸 2（構成の側）: `RiskManagement:BaseUrl` を読む情報収集の `Program.cs` の 1 か所、`Reports:BaseUrl` を読む `Stage0AsOfComposition` の 1 か所。
軸 3（提供側）: リスク管理 `RiskControlsRead/GetOpenPositions`（既存）、報告書 `DailyPolicyRead`（as-of の rpc は無い）。

## 受け入れ基準

| # | 基準 | テスト |
| --- | --- | --- |
| AC1 | 宣言（`RiskManagement:Grpc`・`Reports:Grpc`）が無いとき、両経路は変更前と同じ REST の実装を選ぶ | 情報収集の組み立て試験・`Stage0AsOfComposition` の試験 |
| AC2 | 情報収集は `RiskManagement:Grpc` を宣言すると保有銘柄を `GetOpenPositions` で読み、`RiskManagement:BaseUrl` より優先する。組み立てた実装が実際に提供側を呼ぶ | 情報収集の組み立て試験 |
| AC3 | 保有銘柄の gRPC の解釈は REST と同じ 1 つ（欠けた行・市場や方向の未指定・数量の欠落があれば一覧ごと不明、`null`）。照会の失敗も不明 | 情報収集の gRPC 読み取り試験 |
| AC4 | 報告書は `DailyPolicyRead/GetConfirmedDailyPolicyAsOf` を持ち、REST と同じサービス（`GetConfirmedDailyPolicyAsOf`）・同じ門・同じ入力の検証（オフセット必須・欠落と書式違いは `INVALID_ARGUMENT`）を通る。無かったときは `policy` の無い応答 | 報告書の gRPC 面の試験 |
| AC5 | 取引判断は `Reports:Grpc` を宣言すると Stage 0 の当時の方針を gRPC で読む。「無かった」（`null`）と「取れなかった」（撃ち直しの後に記録を止める）を分け、照会の時刻より後の確定は撃ち直さずに記録を止める | 取引判断の as-of の試験 |
| AC6 | `Reports:Grpc` を宣言し `Reports:BaseUrl` が無くても、発注執行の接続先があれば Stage 0 の基底を組む | `Stage0AsOfComposition` の試験 |
| AC7 | proto の baseline・helm の計測用 overlay・Runbook の経路表・通信仕様書が新しい経路を数える | `check-proto-contracts.js`・helm.yml の overlay 検査・文書系検査 |

## 実装の方針

- 段 1〜5 と同じ切り方: 提供側と消費側を同じ PR に入れ、REST は残す。既定は REST。
- 情報収集: `RiskManagementGrpcTransport`（本サービスの輸送。規則は `InformationCollectionGrpcCalls` を共有）と
  `GrpcRiskManagementHeldSymbolsProvider`（解釈は `HttpRiskManagementHeldSymbolsProvider.Interpret`）。
- 報告書: `daily_policy_read.proto` へ rpc を足す（非破壊）。時刻は `string`（オフセット必須。`WatchlistRead/GetWatchlistAsOf` と同じ）、
  確定の時刻は `optional string`（往復書式）。提供側は REST の `TryParseInstant` を共有する。
- 取引判断: `GrpcAsOfDailyPolicySource`（REST と同じ撃ち直し `AsOfRetry`・同じ未来の確定の拒否）。`Stage0AsOfComposition` は輸送があれば
  gRPC を選ぶ。

## 対象外（後続）

- 発注執行の gRPC 面の新設（4 経路・呼び出し元 3 サービス）。提供側に h2c リスナ・`grpcPort`・門が要り、段の大きさが違う。
- 段 6 そのもの（稼働クラスタでの実測待ち。#753 の 2026-09-28 のコメント）。
- AST→MSP の Retrieval / Document（基盤の面が AST の呼び方に合わない・無い）。

## 検証

- `dotnet build` / `dotnet test`（ReportService・TradeDecisionService・InformationCollectionService の各テストプロジェクト）
- `dotnet format --verify-no-changes`
- `node scripts/check-proto-contracts.js`（`--update` の後）・`helm template` と overlay の検査の手元再現
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・文書系検査（trace ブロック・knowledge graph）

## 検証の結果（2026-10-17）

| コマンド | 結果 |
| --- | --- |
| `dotnet test`（InformationCollectionService.Tests / ReportService.Tests / TradeDecisionService.Tests / Architecture.Tests） | 689 / 2020 / 2021 / 212（skip 1）すべて成功 |
| `dotnet format backend.slnx --verify-no-changes`（3 サービス） | 差分なし |
| `node scripts/check-proto-contracts.js`（`--update` の後） | OK（12 ファイル・非破壊の追加 3 件を baseline へ） |
| helm.yml「Assert gRPC measurement overlay keeps every env」を手元の helm v4.2.1 で再現 | ok（呼び出し側 13〔#6 は別窓〕・提供側 6） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 554 件成功 |
| `check-trace-blocks` / `check-test-traceability` / `gen-knowledge-graph --check` / `check-cross-repo-refs` / `check-plan-id-qualification` | OK |

変異注入（1 本ずつ入れて戻した）: 情報収集の組み立てで gRPC の読み手を選ばない／市場の未指定を日本と読む／確定の時刻の欠落を照会の時刻で埋める／
Stage 0 の組み立てで gRPC を選ばない／報告書の提供側で時刻を REST の検証を通さずに読む —— いずれも赤（テスト仕様書の表）。

