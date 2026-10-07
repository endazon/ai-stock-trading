---
title: チャートが描く ASPNETCORE_ENVIRONMENT を values から与えて既定を Production にし、例外時の応答を全サービスで ProblemDetails に固定する（開発者向けエラーページが Authorization ヘッダーを返していた。#1192）
type: spec
status: accepted
related_ids: [NFR-06, NFR-05, FR-13, FR-01, SC-02, UC-06, IADR-0496, IADR-0052, IADR-0058, IADR-0048, IADR-0011, IADR-0013, IADR-0129, IADR-0434, IADR-0490]
author: claude (Claude Code)
created: 2026-10-07
updated: 2026-10-07
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-06 発注機能へのアクセスは利用者本人のみ・NFR-05 認証情報の秘匿)
---

# ASPNETCORE_ENVIRONMENT を values へ出して既定を Production にし、例外応答を ProblemDetails に固定する（#1192）

## 背景（issue の観測）

- PoC（2026-10-07 20:2x JST・AST 017cbb56）で、オーナーが `POST /monitor/watchlist` に `"market":"UnitedStates"`（文字列）を送った。
  応答は HTTP 400 で、本文は ASP.NET Core の開発者向け例外ページ（`DeveloperExceptionPageMiddlewareImpl`）だった。
  その `HEADERS` 節に、要求の `Authorization: Bearer <アクセストークン全文>` がそのまま出ていた（トークンは失効済み）。
- 原因: `deploy/helm/ai-stock-trading/templates/deployment.yaml` が全サービスの env に `ASPNETCORE_ENVIRONMENT: Development` を固定値で描いていた。
  本番の `values.yaml` でも同じ描画になる（values で変えられない）。

## 起点 ID の選定

- **NFR-06**（発注機能へのアクセスは利用者本人のみ）。漏れたのは所有者・サービスのアクセストークンであり、受け取った者は本人として発注系の API を呼べる。
  関連として NFR-05（認証情報の秘匿）。いずれも計画の ID 列にある番号で、無採番の `NFR` には当たらない。
- 付随の不具合（監視銘柄の要求の `market` が文字列を受けない）は **FR-13**（監視銘柄の変更。SC-02・UC-06）。

## 実測（origin/develop 017cbb56）

### 環境名に依存するもの（全サービス）

| 種別 | 実測 | Production にしたときの影響 |
| --- | --- | --- |
| `IsDevelopment()` / `IsEnvironment()` / `IHostEnvironment` の分岐 | `backend/` 全体を grep して**0 件**（試験を除く） | なし |
| 開発者向け例外ページ | `WebApplication` が Development でだけ自動で挿入する（明示の `UseDeveloperExceptionPage` は 0 件） | **消える**（本件の是正） |
| 最小 API の `RouteHandlerOptions.ThrowOnBadRequest` | Development の既定は true（要求本文の束縛失敗が例外になり、上のページが描く）。Production は false | 束縛失敗は本文なしの 400 になる（ヘッダーもスタックも返さない） |
| DI の検証（`ValidateScopes` / `ValidateOnBuild`） | Development の既定でだけ有効 | 起動時の検証が無くなる（壊れた DI は従来なら起動で落ちた。悪化方向は「黙って動く」側のみ。試験は `Testing` 環境で従来から検証なし） |
| user secrets | `UserSecretsId` は 0 件 | なし |
| JasperFx / Wolverine のプロファイル | 実測（Wolverine 6.24.5 を Development / Production で起動して `JasperFxOptions.ActiveProfile` を表示）: 両者とも `GeneratedCodeMode=Dynamic`・`ResourceAutoCreate=CreateOrUpdate`・`AssertAllPreGeneratedTypesExist=False`・`SourceCodeWritingEnabled=True`、`AutoBuildMessageStorageOnStartup=CreateOrUpdate`。`TypeLoadMode` は `WolverineExtensions` が環境変数・生成コードの有無で明示している（#811） | なし。RabbitMQ の `AutoProvision()` も明示で、環境名に依らない |
| EF の自動マイグレーション | 7 サービスの `MigrateAsync` は無条件（`JasperFxCommandLine.IsHostRun` のみ） | なし |
| `appsettings.Development.json` | 11 サービスにある（opend-auth-gateway には無い）。Production では読まれない | 下表 |

### `appsettings.Development.json` の値のうち、描画される env に無く、コード既定とも違うもの（母集合の取り方は下）

母集合: 11 ファイルを JSONC として平坦化し、`appsettings.json`（base）と同値のキーを除き、`helm template`（values.yaml / values-local.yaml）の
Deployment ごとの env 名（`__` を `:` に戻す）が覆うキーを除いた残りを、1 キーずつコード既定と突き合わせた
（スクリプトは作業用の一時物でコミットしない。結果の全件を下に置く）。

| サービス | キー | Development の値 | Production（env 無し）の実効値 | 扱い |
| --- | --- | --- | --- | --- |
| 11 サービス | `Serilog:MinimumLevel:Default` | `Debug` | `Information`（base） | **経路B は保持**（`global.serilogMinimumLevel: Debug` → env `Serilog__MinimumLevel__Default`）。本番は Information へ下がる（`LogDebug` 24 箇所が出なくなる。`Microsoft` / `Wolverine` の Override は base のまま） |
| information-collection | `Collection:PollIntervalSeconds` | `300` | `1800`（`CollectionOptions` 既定） | **保持**（values.yaml・values-local とも env `Collection__PollIntervalSeconds: "300"`）。これは巡回間隔だけでなく、`InformationCollected.NewsStatusValidFor`（巡回間隔の 2 倍＝600 秒。IADR-0490 の定時サイクルの鮮度の上限）と Finnhub の日次見積り（IADR-0434 の予算表）にも効く |
| trade-decision | `Auth:Authority` | `http://keycloak:8080/realms/ai-stock-trading` | 無し | 変化なし: trade-decision は受信の JWT 認証を持たず（`AddAiStockTradingAuth` を呼ばない）、`ServiceAuth` の token エンドポイントは描画が `ServiceAuth__TokenEndpoint` を明示している（両プロファイル） |
| backtest | `Backtest:BarData:Stooq:BaseUrl` | `""` | Stooq の既定 URL | 変化なし: `Backtest__BarData__Provider` が空（両プロファイル）で no-op |
| 以下はコード既定と同値（または空文字と不在が同じ解釈） | backtest `Stooq:RequestsPerMinute=10`・`Stage0:IntervalSeconds=86400`・`Stage0:LookbackDays=365`／cost-control `Configuration:AssumptionsCacheTtlSeconds=""`（`TryParse` 失敗＝既定）・`Retention:*`（既定と同値）／order-execution `Retention:*`／report `Reports:Bootstrap:AssumptionsVersion=""`（`TryParse`）・`Reports:AutoGeneration:*`（`IntervalSeconds` 300・16:00/16:30/17:00・`AssumptionsVersion` 1・`NotifyOnDraftPresented` true＝既定）・`MarketData:MaxQuoteStalenessSeconds=300`・`Finnhub:RequestsPerMinute=5`／risk `MarketData:RefreshIntervalSeconds=60`・`MaxQuoteStalenessSeconds=300`・`Finnhub:RequestsPerMinute=5`・`Risk:SimulatorProfile:Enabled=false`（本番描画のみ。経路B は env）／trade-decision `MarketData:*`（本番描画のみ）・`Fx:*`（`FXERD04`・`fm08`・30 日・5 日・21600 秒・`DEXJPUS`・1/5 回/分＝`FxOptions` 既定）・`Retrieval:TopK=""`（`ParseTopK` 既定 5）・`Configuration:BaseUrl=""`（`IsNullOrWhiteSpace`）・`Profitability:*`（既定 false・`TryParse`）／report の `LlmGateway:TimeoutSecondsByKind:*=""`（本番描画のみ。空は既定） | — | 同じ | 変化なし |

上記以外の Development のキー（RabbitMQ・OTLP・接続文字列・`Auth:Authority`・各 `BaseUrl`・鍵）は、両プロファイルとも描画される env が上書きしている
（env が優先。値は不変）。opend-auth-gateway は env に `ASPNETCORE_ENVIRONMENT` を持たず（`templates/opend.yaml`）、従来から Production で動いている。

### 例外応答の経路

- 共通のミドルウェアは `AiStockTrading.TestSupport.PlatformShim`（名前は TestSupport だが 11 サービスの Program.cs がすべて参照する稼働の配線）の
  `UseAiStockTradingMiddleware`（8 サービスが呼ぶ）。information-collection・notification・trade-decision は呼ばない（HTTP 面は health・introspection・
  run-once 等で、認証は `WebApplication` の自動挿入）。11 サービスとも終端は `RunAiStockTradingAsync`。
- `UseExceptionHandler` / `AddProblemDetails` は 0 件。Production の未処理例外は本文なしの 500（ヘッダーは返さない）。

### 付随: `WatchlistChangeRequest.Market`

- `System.Text.Json` の web 既定は列挙を数値でしか読まない。市場監視は `ConfigureHttpJsonOptions` を持たない。
- 文字列の列挙を受ける慣例は報告書・費用統制（サービス全体に `JsonStringEnumConverter`）。ただし市場監視の読み取り口
  （`GET /monitor/watchlist`・`open-positions` 系）は**数値の列挙が契約**であり（T-10-931 `ReadContractWireFormatTests`：受け手の判断は数値で読む）、
  サービス全体へ足すと受け手が実行時に壊れる。→ **要求型の 1 プロパティにだけ付ける**（`[JsonConverter(typeof(JsonStringEnumConverter<Market>))]`）。
  数値も従来どおり読む（`AllowIntegerValues` 既定 true）。`WatchlistChangeRequest` は要求専用（直列化して返す口が無い）。
- 入れ替え案の適用（`WatchlistSymbolRef`）は通知サービスの送り手契約（数値）が参照する公開型のため対象外（範囲外として記録）。

## 設計

1. **チャート**（`templates/deployment.yaml`）: `ASPNETCORE_ENVIRONMENT` を `services.<name>.aspnetcoreEnvironment` ＞ `global.aspnetcoreEnvironment` ＞
   `Production` の順で決める。values.yaml の既定は `Production`。**`Development`（大小無視）は描画で止める**（`fail`）——どのプロファイルでも配備では
   開発者向けの挙動を取らない。経路B（values-local）は既定を継ぐ（Production）。
2. **経路B の挙動の保持**: 上表の 2 件を明示の設定にする（`global.serilogMinimumLevel`＝空なら env を描かない／values-local は `Debug`。
   `Collection__PollIntervalSeconds: "300"` は values.yaml と values-local の両方）。
3. **コードの多重防御**（shim）: `UseAiStockTradingExceptionHandler()` を新設し、`UseExceptionHandler` の委譲で ProblemDetails
   （`type`・`title`・`status`・`traceId` のみ。例外の型・メッセージ・スタック・要求ヘッダーは載せない）を書く。状態は `BadHttpRequestException` なら
   その `StatusCode`、それ以外は 500。パイプラインの内側に置くので、Development で自動挿入される開発者向けページ（外側）へ例外が届かない。
   `UseAiStockTradingMiddleware` の先頭で呼び（8 サービス）、呼ばない 3 サービスは `Build()` の直後に呼ぶ。
   **`RunAiStockTradingAsync` は導入されていなければ起動を止める**（`InvalidOperationException`。付け忘れを WebApplicationFactory の試験と起動で捕まえる）。
4. **付随**: `WatchlistChangeRequest.Market` に上記の変換器。

## 受け入れ基準と試験（T-10-2350〜T-10-2357。#1191 の T-10-2310〜2322・#1175 の 2330 台と重ならない）

| 試験 | 内容 | 置き場所 |
| --- | --- | --- |
| T-10-2350 | shim の例外処理: Development／Production の両環境で、Authorization ヘッダー（実行時に組み立てた非秘密の目印）付きの要求が未処理例外になると、500・`application/problem+json`・本文に目印・例外メッセージ・型名・スタック（`   at `）を含まない | `AiStockTrading.TestSupport.PlatformShim.Tests` |
| T-10-2351 | 同: `BadHttpRequestException`（Development の束縛失敗）は 400 の ProblemDetails | 同上 |
| T-10-2352 | 同: `RunAiStockTradingAsync` は例外処理の導入が無ければ止まる（導入があれば止めない） | 同上 |
| T-10-2353 | 代表サービス（市場監視・WebApplicationFactory）: Production／Development で、監視銘柄の読み取りが未処理例外を投げたとき、応答は ProblemDetails で Authorization の値・例外メッセージ・スタックを含まない | `MarketMonitorService.Tests` |
| T-10-2354 | `POST /monitor/watchlist` が `"market":"UnitedStates"`（文字列・大小無視）を受け、数値（1）も従来どおり受ける。`DELETE` も文字列を受ける | 同上 |
| T-10-2355 | 未知の市場名（`"Mars"`）は 400（追加しない） | 同上 |
| T-10-2356 | 描画（helm.yml）: values.yaml・values-local・全フラグ ON の描画で `ASPNETCORE_ENVIRONMENT` が全 Deployment で `Production`、`Development` は 0 件。`--set global.aspnetcoreEnvironment=Development`・サービス単位の `Development` は描画が失敗する（陰性対照） | `.github/workflows/helm.yml` |
| T-10-2357 | 描画: values-local は `Collection__PollIntervalSeconds=300`・`Serilog__MinimumLevel__Default=Debug`（全 Deployment）を描き、本番既定は Serilog の env を描かず巡回間隔 300 を描く | 同上 |

否定形・変異: 例外処理の委譲を外す／`UseAiStockTradingMiddleware` から外す／`RunAiStockTradingAsync` の表明を外す／チャートの `fail` を外す・既定を
`Development` へ戻す／変換器を外す、の各変異で対応する試験が赤になることを確認する（結果は PR に記す）。

## 影響（配備）

- 全 Deployment の env が変わるため、`helm upgrade` で全サービスが再起動する。
- 変わる挙動: 開発者向け例外ページが消える／束縛失敗は Production の既定（本文なしの 400）、未処理例外は ProblemDetails／DI の起動時検証が無くなる／
  本番は Serilog が Information へ下がる。経路B は Debug・巡回 300 秒（鮮度 600 秒）を保持する。

## 範囲外・残余

- `WebApplication` が利用者のミドルウェアより外側に自動挿入するルーティング（`UseRouting`）の例外（あいまいな経路の一致など）は shim の委譲が受けない。
  Production ではページ自体が無いので漏れない（本文なしの 500）。Development（docker-compose）だけの残余。
- docker-compose（ローカル開発）は Development のまま（例外処理の委譲でページへは届かない）。
- 入れ替え案の適用の `WatchlistSymbolRef.Market` は数値のまま。
