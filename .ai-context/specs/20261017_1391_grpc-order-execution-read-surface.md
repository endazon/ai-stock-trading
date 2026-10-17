---
title: east-west gRPC 段 5 の追補 2 —— 発注執行に gRPC の読み取り面を新設し、REST だけで呼ばれている読み取り 4 本（借株可否・日足 2・処理中の決済）を gRPC でも呼べるようにする
type: spec
status: done
related_ids: [NFR, FR-04, FR-10, FR-15, UC-06, IADR-0284, IADR-0328, IADR-0331, IADR-0425, IADR-0427, IADR-0446, IADR-0448, IADR-0467, IADR-0489, IADR-0528, IADR-0535, IADR-0557, MSP:ADR-0029, MSP:ADR-0075]
author: endazon (with Claude Code)
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
---

# 仕様書: east-west gRPC 段 5 の追補 2 —— 発注執行の gRPC 面（#1391）

## 起点

- issue #1391（受け皿は #753・`Refs`）。射程と段の切り方は [IADR-0284](../adr/IADR-0284_east-west-grpc-scope-and-order-ruling.md) 決定 5。
  直前の追補（#1388・[IADR-0557](../adr/IADR-0557_grpc-late-routes-held-symbols-asof-policy.md) 決定 1）が「面の新設として別段」に分けた 4 本を扱う。
  本 PR の判断は [IADR-0559](../adr/IADR-0559_grpc-order-execution-read-surface.md)（push 直前に develop の最大 0558〔#1390〕を確かめて＋1）。
- 計画 ID: NFR（輸送の差し替え。振る舞いは変えない）。経路の起点は FR-10・UC-06（借株可否・#967）、FR-04・FR-15（日足・#1118・#1308）、
  FR-04・FR-10（処理中の決済・#1338）。

## 母集合（規則 9。`origin/develop` `07f396f4`・2026-10-17・自分で引き直した）

引き方 1（軸 1・アダプタ）: `backend/Services` の `Http*.cs`（テスト以外）について、同じディレクトリに `Http` を `Grpc` に置き換えた名前の
ファイルがあるかを突き合わせた。対の無いもの 7 件。

| ファイル | 呼び先 | 分類 |
| --- | --- | --- |
| `RiskManagementService/.../HttpShortSellBorrowSource.cs` | 発注執行 `GET /order-execution/short-permit` | **本 PR** |
| `TradeDecisionService/.../HttpDailyBarsSource.cs` | 発注執行 `GET /order-execution/daily-bars` | **本 PR** |
| `TradeDecisionService/.../HttpInFlightCloseProvider.cs` | 発注執行 `GET /order-execution/in-flight-closes` | **本 PR** |
| `BacktestService/.../HttpOrderExecutionHistoricalBarSource.cs` | 発注執行 `GET /order-execution/daily-bars` | **本 PR** |
| `ReportService/.../HttpDecisionReferenceSource.cs` | 監査 | 偽陽性（同じファイルに `GrpcDecisionReferenceSource`。#1388 の表と同じ） |
| `ReportService/.../HttpReportNarrativeDrafter.cs`・`TradeDecisionService/.../HttpLlmCompletionClient.cs` | MSP LlmGateway | 偽陽性（共有の gRPC 輸送が対。#1388 の表と同じ） |

（#1388 の表の 12 件との差は、#1388 で 2 件が対を得たことと、`Shared.KnowledgeBase` の 3 件を今回は軸 1 の走査に入れていないこと。3 件はいずれも MSP 宛で段の外。）

引き方 2（軸 2・呼び出し箇所）: `backend/Services`・`backend/Shared`・`backend/Bff` で、`order-execution/` のルート文字列と `OrderExecution:BaseUrl`
（`Backtest:BarData:OrderExecution:BaseUrl` を含む）を読む本番コードを走査した。発注執行以外のサービスから 4 アダプタを**組み立てる箇所は 5 か所**:

| サービス | 組み立てる箇所 | 経路 |
| --- | --- | --- |
| リスク管理 | `Program.cs` の `IShortSellBorrowSource` | short-permit |
| 取引判断 | `Program.cs` の日足の共有口（`DailyBarsComposition.SharedKey`。出来高・ATR(14)） | daily-bars |
| 取引判断 | `Stage0AsOfComposition.CreateBase`（Stage 0 の記録の当時の価格） | daily-bars（2 か所目） |
| 取引判断 | `Program.cs` の `IInFlightCloseProvider` | in-flight-closes |
| バックテスト | `HistoricalBarSourceFactory.Create`（`Backtest:BarData:Provider=order-execution`） | daily-bars |

BFF・フロントエンドからの呼び出しは 0 件（走査で確認）。

引き方 3（軸 3・提供側）: 発注執行の `Program.cs` に `AddAiStockTradingGrpcListener`・`MapGrpcService` は無い。Helm の `services.order-execution`
に `grpcPort` は無く、`deployment.yaml` の呼び先の表（`$grpcCallees`）にも `OrderExecution` が無い。proto も無い。

## 受け入れ基準

| # | 基準 | テスト |
| --- | --- | --- |
| AC1 | 発注執行は `aistocktrading.orderexecution.v1.OrderExecutionRead`（`GetShortPermit`・`GetDailyBars`・`GetInFlightCloses`）を持ち、REST と同じサービス・同じ入力の検証（欠落・値域外は `INVALID_ARGUMENT`＝REST の 400）を通る。値は REST の本文と同じ | 発注執行の gRPC 面の試験 |
| AC2 | 門は `GrpcOwnerOrService`（deny-by-default）: 資格情報が無ければ `UNAUTHENTICATED`、ロールが無ければ `PERMISSION_DENIED`、所有者はボットのクライアント（`azp`）だけ | 同上 |
| AC3 | `Grpc:Port` が無ければ h2c リスナは立たず、helm の既定描画は変わらない（`grpcPort` は宣言したときだけ） | helm.yml の既存検査 |
| AC4 | 4 本とも `OrderExecution:Grpc` の宣言で gRPC 経路になり、未宣言なら REST のまま（輸送そのものを登録しない）。宣言は `BaseUrl` より優先し、使えない宛先は起動時に落とす | 3 サービスの組み立て試験 |
| AC5 | 失敗の意味（「無かった／答えがある」と「取れなかった」）が REST と一致する: 借株可否は Unknown と NotPermitted、日足は null（未提供）と足の一覧、処理中の決済は null（不明）と NotCovered、バックテストは欠測（その銘柄だけ）と例外（輸送の失敗）。解釈は REST の関数を共有する | 3 サービスの gRPC 読み取り試験 |
| AC6 | 計測用 overlay・`helm.yml` の期待表・Runbook の経路表・通信仕様書が新しい経路を数える。proto の baseline を更新する | `check-proto-contracts.js`・helm.yml の overlay 検査・文書系検査 |

## 実装の方針

- 段 1〜5 と同じ切り方: 提供側と消費側を同じ PR に入れ、REST は 1 バイトも変えない（入力の検証だけを REST と共有する関数へ切り出す）。既定は REST。
- 提供側: `OrderExecutionReadGrpcService`（`[Authorize(GrpcOwnerOrService)]`）と写し `OrderExecutionReadWireMapping`。h2c リスナは共有の
  `AddAiStockTradingGrpcListener`（`Grpc:Port` が無ければ立たない）。
- 線上の表現（原則 A）: 状態の列挙は `*_UNSPECIFIED = 0` を持ち、C# の `Unknown = 0` 等とは名前で写す（UNSPECIFIED は契約の食い違い＝REST の状態欠落と同じ）。
  金額は不変文化の 10 進文字列、日付は `yyyy-MM-dd`。運ぶのは受け手が読む項目だけ（`ObservedAt`・`ClosableQuantity`・`DecisionIds`・`From`/`To` は運ばない）。
- 消費側: 各サービスに `OrderExecutionGrpcTransport`（構成 `OrderExecution:Grpc`・deadline の既定は REST の HttpClient.Timeout と同値＝5 / 8 / 30 秒）。
  受け手は線上の行を REST と同じ nullable の DTO へ写し、REST の `Interpret` を通す。
- バックテストの輸送の失敗: REST は通信例外・タイムアウトを送出する（Stooq・moomoo の源と同じ）。gRPC も `UNAVAILABLE`・`DEADLINE_EXCEEDED`・
  呼び出し元のキャンセルは送出し、それ以外の状態（REST の非 2xx に当たる）はその銘柄の欠測にする。
- 計測用 overlay: 発注執行の `grpcPort` を足し、取引判断・バックテストの `OrderExecution` を true にする。リスク管理は **false**（REST が values・values-local の
  どちらでも未結線で、宣言すると借株可否の照会が新しく始まる＝業務の入力が変わる。#6〔report → Audit〕と同じ扱いで別窓）。

## 対象外（後続）

- 段 6 そのもの（稼働クラスタでの実測待ち。#753）。
- AST→MSP の Retrieval / Document（#1388 の表と同じ理由）。

## 検証

- `dotnet test`（OrderExecutionService・RiskManagementService・TradeDecisionService・BacktestService の各テストプロジェクト。フィルタなし）
- `dotnet format --verify-no-changes`
- `node scripts/check-proto-contracts.js`（`--update` の後）・helm.yml の overlay 検査の手元再現
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・`check-adr-index-sync`・文書系検査（trace ブロック・knowledge graph）

## 検証の結果（2026-10-17・develop `27ccbc0a` を merge した後）

| コマンド | 結果 |
| --- | --- |
| `dotnet test`（OrderExecutionService.Tests / RiskManagementService.Tests / BacktestService.Tests / TradeDecisionService.Tests / Architecture.Tests。フィルタなし） | 1885 / 2382 / 435 / 2081 / 212（skip 1）すべて成功 |
| `dotnet build`（InformationCollection・MarketMonitor・Notification・Report の各テストと IntegrationTests。変更したサービスを参照する残り） | 警告 0・エラー 0 |
| `dotnet format <各 csproj> --verify-no-changes`（変更した 4 サービスの本体とテスト） | 差分なし |
| `node scripts/check-proto-contracts.js`（`--update` の後） | OK（13 ファイル。非破壊の追加 1 ファイル） |
| helm.yml の全ステップを手元の helm v3.16.4 で再現（「Assert gRPC measurement overlay keeps every env」を含む） | すべて成功（呼び出し側 15〔#6・#17 は別窓〕・提供側 7） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 554 件成功 |
| `check-adr-index-sync` / `check-trace-blocks` / `check-test-traceability` / `gen-knowledge-graph --check` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-doc-links` / `check-reading-budget` | OK |

途中で見つけたこと: proto の状態の列挙を当初は送り手の C# と同じ名前（`InFlightClosesStatus` 等）にしたところ、アーキテクチャ試験の
「サービス間の読み取りは送り手の型による契約テストを持つ」が赤になった（受け手の本番コードに送り手の型名が現れ、送り手の型の目印から外れた）。
`*State` へ改名して解消した（IADR-0559 決定 2）。

変異注入（1 本ずつ入れて戻した）: 発注執行の gRPC 面を登録しない／借株可否の「分からない」を線上の未指定で運ぶ／リスク管理の組み立てで gRPC を選ばない／
受け手が状態の未指定を「分からない」（送り手の答え）と読む／取引判断の日足の選択で gRPC を選ばない／バックテストが届かない失敗も欠測へ畳む —— いずれも赤（テスト仕様書の表）。
