---
title: IADR-0496 配備の ASPNETCORE_ENVIRONMENT を values から与えて既定を Production にし Development を描画で止め、Development でしか読まれなかった設定のうち経路B が要るものを明示の値へ移し、未処理例外の応答を全サービスで ProblemDetails に固定する
type: impl-adr
status: Accepted
related_ids: [NFR-06, NFR-05, FR-13, FR-01, SC-02, UC-06, IADR-0052, IADR-0058, IADR-0048, IADR-0011, IADR-0013, IADR-0129, IADR-0434, IADR-0490, IADR-0100]
author: claude (Claude Code)
created: 2026-10-07
updated: 2026-10-07
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-06 発注機能へのアクセスは利用者本人のみ・NFR-05 認証情報の秘匿・FR-13 監視銘柄の変更)
related_specs:
  - ../specs/20261007_1192_aspnetcore-env-production.md
---

# IADR-0496: 配備の環境名を Production にし、例外応答を ProblemDetails に固定する（#1192）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-07
- 決定者: Claude Code（実装）。起点 [#1192](https://github.com/endazon/ai-stock-trading/issues/1192)（セキュリティ・最優先）

## 起点・関連

- 関連する計画書 ID: NFR-06（発注機能へのアクセスは利用者本人のみ）・NFR-05（認証情報の秘匿）。付随の不具合は FR-13 / SC-02 / UC-06
- 作業仕様書: [`20261007_1192_aspnetcore-env-production`](../specs/20261007_1192_aspnetcore-env-production.md)（実測・棚卸しの全件・試験・変異）
- 改める実装判断: [IADR-0052](./IADR-0052_k8s-helm-chart-shared-infra.md)（chart が全 Worker に `ASPNETCORE_ENVIRONMENT=Development` を固定値で描いていた）

## コンテキスト

- PoC（2026-10-07）で、市場監視の `POST /monitor/watchlist` に文字列の `market` を送ると、ASP.NET Core の開発者向け例外ページが返り、
  その `HEADERS` 節に要求の `Authorization: Bearer <トークン全文>` が載っていた。応答は運用のチャットへ貼られた。
- chart（`templates/deployment.yaml`）は全 Worker に `ASPNETCORE_ENVIRONMENT: Development` を固定値で描いていた（values で変えられず、
  本番の `values.yaml` でも同じ）。`WebApplication` は Development でだけ、利用者のパイプラインの外側に開発者向け例外ページを自動で挿入し、
  最小 API の束縛失敗を例外にする（`ThrowOnBadRequest`）。
- コードに `IsDevelopment()` 等の分岐は 0 件。Development でだけ効くのは、例外ページ・`ThrowOnBadRequest`・DI の起動時検証・
  `appsettings.Development.json`（11 サービス）である。JasperFx / Wolverine の Development / Production プロファイルの既定は同値（実測）。

## 決定

### 決定 1: 環境名は values から与え、既定は Production、Development は描画で止める

- `services.<name>.aspnetcoreEnvironment` ＞ `global.aspnetcoreEnvironment`（values.yaml 既定 `Production`）＞ `Production`。
- **`Development`（大小無視）はどのプロファイルでも `fail` で描画を止める。** 配備で開発者向けの挙動を取る正当な理由が無く、
  Development でしか読まれない設定は values の env で明示すれば足りる（決定 2）。上書き自体（`Staging` 等）は効く。

### 決定 2: Development でしか読まれなかった設定のうち、経路B が要るものを明示の値へ移す

`appsettings.Development.json` 11 件を平坦化し、描画される env に無く、コード既定とも違う値を全件洗い出した（作業仕様書の表）。違ったのは 2 件だけ:

| キー | Development | Production（env 無し） | 扱い |
| --- | --- | --- | --- |
| `Serilog:MinimumLevel:Default`（11 サービス） | `Debug` | `Information` | `global.serilogMinimumLevel`（空なら env を描かない）。values-local は `Debug`（経路B 保持）。本番は `Information` へ下がる |
| `Collection:PollIntervalSeconds`（情報収集） | `300` | `1800` | env `Collection__PollIntervalSeconds: "300"` を values.yaml・values-local の両方に置く（巡回・鮮度の上限 600 秒〔IADR-0490〕・Finnhub の見積り〔IADR-0434〕を保つ） |

それ以外（trade-decision の `Auth:Authority`・backtest の Stooq の `BaseUrl` 等を含む）は、描画される env が上書きしているか、
コード既定と同値か、空文字と不在が同じ解釈になる（根拠は作業仕様書）。

### 決定 3: 未処理例外は全サービスで ProblemDetails に写す（環境名に依らない多重防御）

- shim（`AiStockTrading.TestSupport.PlatformShim`。11 サービスが稼働で使う共通配線）に `UseAiStockTradingExceptionHandler()` を置き、
  `UseExceptionHandler` の委譲で `type`・`title`・`status`・`traceId` だけの ProblemDetails を書く。例外の型・メッセージ・スタック・要求ヘッダーは載せない
  （例外はミドルウェアが Error ログへ出す）。状態は `BadHttpRequestException` ならその `StatusCode`、それ以外は 500。
- パイプラインの**内側**（先頭）に置くので、Development で外側に自動挿入されるページへ例外が届かない（docker-compose の開発でも漏れない）。
- `UseAiStockTradingMiddleware` の先頭で呼ぶ（8 サービス）。呼ばない 3 サービス（information-collection・notification・trade-decision）は `Build()` の直後に呼ぶ。
- **共通の終端 `RunAiStockTradingAsync` は導入の印が無ければ起動を止める。** 付け忘れを稼働でも WebApplicationFactory の試験でも黙らせない。

### 決定 4: 監視銘柄の要求の `market` は列挙名の文字列も受ける（要求型のプロパティにだけ）

- `WatchlistChangeRequest.Market` に `JsonStringEnumConverter<Market>`（大小無視・数値も従来どおり）。
- サービス全体の JSON 設定へは足さない。市場監視の読み取り口は数値の列挙が契約で（T-10-931）、受け手が実行時に壊れる。

## 却下した案

- **values-local だけ Development のまま残す**: 例外ページは決定 3 で塞がるが、`ThrowOnBadRequest`・DI 検証・Development.json の暗黙の値が
  「環境名」という 1 つのつまみに隠れたままになる。経路B と本番で挙動の出所が食い違い、本件と同型の取り違えを再生産する。
- **`AddProblemDetails` ＋ `UseStatusCodePages`**: 本文なしの 400 まで ProblemDetails にできるが、各 Program.cs のサービス登録を 11 か所変える。
  漏えいの是正には不要（本文なしの応答は何も漏らさない）で、範囲を広げない。
- **IStartupFilter で外側に入れる**: Development の例外ページ（さらに内側）が先に例外を処理するので効かない。
- **市場監視の JSON 設定全体に `JsonStringEnumConverter`**: 決定 4 のとおり読み取り口の契約を壊す。

## 結果・影響

- 全 Deployment の env が変わり、`helm upgrade` で全 Worker が再起動する。描画の差分は `ASPNETCORE_ENVIRONMENT` が全件 `Production`、
  情報収集に `Collection__PollIntervalSeconds=300`（両プロファイル）、経路B に `Serilog__MinimumLevel__Default=Debug`（全 Worker）。
- 変わる挙動: 開発者向けページが無い／束縛失敗は本文なしの 400／未処理例外は ProblemDetails／DI の起動時検証が無い／本番のログは Information。
- 試験: T-10-2350〜T-10-2357（shim・市場監視の WebApplicationFactory・helm の描画検査）。

### 残余リスク

- `WebApplication` が利用者のミドルウェアの外側に自動挿入するルーティングの例外（あいまいな経路の一致等）は決定 3 の委譲が受けない。
  配備は Production なのでページ自体が無く漏れない（本文なしの 500）。docker-compose の開発環境だけの残余。
- DI の起動時検証（Development の既定）が無くなるため、スコープの取り違えは起動で落ちず実行時まで残る。試験（`Testing` 環境）は従来から検証なし。
- 入れ替え案の適用の `WatchlistSymbolRef.Market` は数値のまま（通知サービスの送り手契約が参照する公開型）。
- opend-auth-gateway は env に `ASPNETCORE_ENVIRONMENT` を持たず（`templates/opend.yaml`）、従来から Production。shim を使わないため決定 3 の対象外。
