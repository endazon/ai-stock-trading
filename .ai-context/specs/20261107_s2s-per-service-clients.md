---
title: サービス間の認証の身元をサービスごとに分け、口ごとに呼んでよいサービスを限る（クラウド）
type: spec
status: in-progress
related_ids: [NFR, NFR-05, NFR-06, FR-14, ADR-0047, ADR-0080, IADR-0636, IADR-0611, IADR-0626, IADR-0623, IADR-0051, MSP:IADR-0689]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-05・NFR-06)
---

# 仕様書: サービスごとの機密クライアントと口ごとの呼び手の一覧（NFR）

> 本仕様書は実装着手前に作成する。実装の判断は IADR-0636。基盤（MSP）の側は同名のブランチの作業仕様書
> `MSP:.ai-context/specs/20261107_s2s-per-service-clients.md`（realm・OpenBao・ESO）。

## 起点となる計画書（トレーサビリティ）

- 非機能要求: NFR-05（秘匿は OpenBao）・NFR-06（利用者本人のみ。危険な操作の第 2 要素）。サービス間の身元の分離そのものの番号は計画に無いので裸の `NFR` を併記する。
- 関連 ADR: ADR-0047（ボットのトークンはサービスの身元）・ADR-0080（秘匿の区分）。
- 起点: クラウドへの移行の前の点検（詳細は非公開）。利用者の裁定（2026-11-07）「全部直してから稼働」。
- 前提の IADR: IADR-0051（OwnerOrService）・IADR-0611（所有者のトークンの aud / azp。サービスの分岐は見ない）・IADR-0623（OpenBao と ESO）・IADR-0626（第 2 要素の確かめの口。残る制約 4）。

## 現状（着手前に読んだもの）

- `deploy/helm/ai-stock-trading/values-cloud.yaml`: ServiceAuth を持つ 7 つのサービス（下の表）が、どれも Secret `ast-secrets` のキー
  `service-auth-client-id` / `-secret`（＝基盤の realm の 1 つの機密クライアント `ai-stock-trading-svc`）を読む。cost-control は values.yaml の同じ行を受け継ぐ。
- `backend/TestSupport/AiStockTrading.TestSupport.PlatformShim/Foundation/Extensions/AuthExtensions.cs`: `ValidateAudience = false`。
  `OwnerOrService` と `GrpcOwnerOrService` はサービスのロール（`trading-service`）なら aud・azp を見ずに通す（`OwnerTokenBindingHandler` のサービスの分岐・`GrpcOwnerClientGate.Allows`）。
- `backend/Services/RiskManagementService/Features/RiskManagement/VerifyOwnerSecondFactor/Endpoint.cs`: サービスのロールだけを見る（どのサービスも呼べる。IADR-0626 残る制約 4）。
- 基盤の realm（`MSP:deploy/keycloak/microservices-platform-realm.json`）の `ai-stock-trading-svc` は `roles`・`platform-api-audience` だけを持ち、取引ユニットの宛先
  （`ai-stock-trading-api`）を載せない。宛先のスコープ `ai-stock-trading-api-audience` は bff と owner だけ（基盤の検査 9 の 2）。

## 呼び出しの母集合（規則 9: 記憶で挙げず、呼び手の側の実装から引いた）

引き方:

```sh
# サービスの身元（ServiceAuth）を付ける HttpClient の登録
grep -rn "AddAiStockTradingServiceToken\b\|AddAiStockTradingServiceTokenProvider" --include=*.cs backend/Services | grep -v Tests
# 各 HttpClient の型が叩く経路
grep -rnoE '\$?"/(risk-controls|monitor|costs|reports|audit|order-execution|assumptions)[^"?]*' --include=Http*.cs backend/Services/*/Infrastructure/ExternalServices
# 第 2 要素の確かめ（共有の実装）
grep -rn "Route = " backend/Shared/AiStockTrading.Shared.Infrastructure/Composable/SecondFactor/OwnerSecondFactorVerifier.cs
# east-west gRPC の呼び手（サービスの身元のトークンを付ける輸送）
ls backend/Services/*/Infrastructure/ExternalServices/*GrpcTransport.cs; grep -n "IServiceAccessTokenProvider" backend/Services/*/Infrastructure/ExternalServices/AssumptionsClientExtensions.cs
```

除外: 通知（notification）の `Http*Controller` はボットの所有者のトークン（`ai-stock-trading-owner`。ServiceAuth ではない）なので母集合に入れない。
LLM ゲートウェイ（`ai-stock-trading-llm-caller`）・KB は基盤の口なので対象外。

### REST（クラウドで使う経路。gRPC は既定で結線しない）

| 口（受け側・経路） | 口の名前（`ServiceCallerNames`・`Auth:ServiceCallers:<名前>`） | 呼び手（実装） |
| --- | --- | --- |
| リスク管理 `GET /risk-controls/open-positions` | `risk-controls.open-positions` | information-collection（HttpRiskManagementHeldSymbolsProvider）・market-monitor（HttpPositionStore）・report（HttpOpenPositionSource）・trade-decision（HttpHeldPositionProvider） |
| `GET /risk-controls/trading-halt` | `risk-controls.trading-halt` | order-execution（HttpTradingHaltSource） |
| `GET /risk-controls/entry-blockers` | `risk-controls.entry-blockers` | trade-decision（HttpEntryBlockersProvider） |
| `GET /risk-controls/working-entry-orders` | `risk-controls.working-entry-orders` | trade-decision（HttpHeldPositionProvider） |
| `GET /risk-controls/sizing-context` | `risk-controls.sizing-context` | trade-decision（HttpSizingContextProvider） |
| `GET /risk-controls/buy-in-inferences` | `risk-controls.buy-in-inferences` | report（HttpBuyInInferenceRecordSource） |
| `GET /risk-controls/session-uptime` | `risk-controls.session-uptime` | report（HttpOpenDUptimeSource） |
| `GET /risk-controls/opening-inventory` | `risk-controls.opening-inventory` | report（HttpOpeningInventorySource） |
| `GET /risk-controls/drift-adoptions` | `risk-controls.drift-adoptions` | report（HttpPeriodDriftAdoptionSource） |
| `GET /risk-controls/fills` | `risk-controls.fills` | report（HttpPeriodFillSource） |
| `GET /risk-controls/stage-gate` | `risk-controls.stage-gate` | report（HttpStageProgressSource） |
| **`POST /risk-controls/owner-second-factor/verify`** | `risk-controls.owner-second-factor.verify` | **report だけ**（RemoteOwnerSecondFactorVerifier） |
| 発注執行 `GET /order-execution/daily-bars` | `order-execution.daily-bars` | backtest（HttpOrderExecutionHistoricalBarSource）・trade-decision（HttpDailyBarsSource） |
| `GET /order-execution/in-flight-closes` | `order-execution.in-flight-closes` | trade-decision（HttpInFlightCloseProvider） |
| `GET /order-execution/short-permit` | `order-execution.short-permit` | risk-management（HttpShortSellBorrowSource。**クラウドでは結線しない**: `OrderExecution__BaseUrl` が空） |
| 市場監視 `GET /monitor/watchlist` | `monitor.watchlist` | information-collection（HttpMarketMonitorWatchlistReader）・trade-decision（HttpWatchlistProvider） |
| `GET /monitor/watchlist/as-of` | `monitor.watchlist.as-of` | trade-decision（HttpAsOfWatchlistSource） |
| 費用統制 `GET /costs/state` | `costs.state` | information-collection（HttpCostControlGate）・trade-decision（HttpLlmCostHaltGate） |
| 前提条件 `GET /assumptions` | `assumptions` | cost-control・trade-decision（各 HttpAssumptionsClient） |
| 報告書 `GET /reports/daily-policy` | `reports.daily-policy` | trade-decision（HttpDailyPolicyProvider） |
| `GET /reports/daily-policy/as-of` | `reports.daily-policy.as-of` | trade-decision（HttpAsOfDailyPolicySource） |
| 監査 `GET /audit/events/by-type` | `audit.events.by-type` | report（HttpBorrowFeeRecordSource ほか 8 つの Http*Source） |
| 情報収集 `POST /internal/collection/run-once` | `collection.run-once`（一覧なし） | CronJob（`tradingCycle.cronjob`。**クラウドでは無効**。移行の段は旧の共有クライアントで通り、最終の段では 403。有効にするなら CronJob のクライアントを足す） |

### east-west gRPC（サービスの型ごとの名前。呼び手の和）

| 口 | 名前 | 呼び手 |
| --- | --- | --- |
| RiskControlsRead | `grpc.risk-controls-read` | information-collection・market-monitor・order-execution・report・trade-decision |
| OrderExecutionRead | `grpc.order-execution-read` | backtest・risk-management・trade-decision |
| WatchlistRead | `grpc.watchlist-read` | information-collection・trade-decision |
| CostStateRead | `grpc.cost-state-read` | information-collection・trade-decision |
| AssumptionsRead | `grpc.assumptions-read` | cost-control・trade-decision |
| DailyPolicyRead | `grpc.daily-policy-read` | trade-decision |
| AuditEventsRead | `grpc.audit-events-read` | report |

### クライアント（呼び手 → clientId）

クラウドの values-cloud で ServiceAuth を持つ 7 つ: backtest・cost-control・information-collection・market-monitor・order-execution・report・trade-decision。
clientId は `ai-stock-trading-svc-<サービス>`（基盤の realm-for-cloud.js の `SERVICE_CLIENTS` と同じ集合）。risk-management は口の一覧に載る（short-permit・gRPC）が、
クラウドでは呼ばないので realm にクライアントを作らない（描画の検査の「結線しない表」に理由つきで置く）。

### 開いている PR との関係（push の前に develop を merge して引き直す）

- AST#1588 は market-monitor → report のサービスだけの口（`GET /reports/policy-revisions/watchlist-proposal/binding`）を足す。先に入れば、その口に
  `.RequireServiceCallers(...)` と既定の一覧（`ServiceCallerNames.DefaultCallers`。呼び手 market-monitor）を足す（足さなければ Enforce で 403＝閉じる向き）。

## 設計（IADR-0636）

1. **身元**: values-cloud の 7 サービスはそれぞれ Secret `ast-service-auth-<サービス>`（ExternalSecret。client-id は字面、client-secret は基盤の
   `cloud/shared/ast-client-secrets` の同じ clientId のプロパティだけ）を読む。optional を付けない（無ければ Pod は起動しない＝共有の資格へ戻らない）。
2. **宛先（aud）**: 段 `Auth:ServiceToken:Mode=Enforce` で JwtBearer が宛先を確かめる（`Auth:ServiceToken:Audiences`。既定 `ai-stock-trading-api`）。合わなければ 401（所有者のトークンも）。
3. **口ごとの呼び手**: 口に名前を付け（REST は `.RequireServiceCallers(名前)`、gRPC は `[ServiceCallers(名前)]`）、`Auth:ServiceCallers:<名前>` の配列（既定は
   `ServiceCallerNames.DefaultCallers`。構成で口ごとに置き換える。base appsettings.json は Auth の鍵を持てない＝validate-runtime-scaffold）に在る azp だけを通す（ちょうど 1 つの azp・序数一致）。名前の無い口・一覧に無い呼び手は 403。OwnerOrService・GrpcOwnerOrService・サービスだけの口
   （確かめの口）が同じ要件を持つ。
4. **段**: Off（既定。ローカル・CI。従前どおり）/ Report（記録して通す）/ Enforce。移行の段は `Auth:ServiceToken:LegacyClients`（旧の共有クライアント）を受ける（宛先を問わず、
   サービスに開いた口を通し、受けた回をログに残す）。chart は `global.serviceTokenBinding.{mode, legacyClients}`（空なら描かない）。

## 受け入れ基準

- [ ] AC1: Enforce で一覧に無い azp のサービスのトークンは 403（大小文字・空白の変種・旧の共有クライアントも）。名前の無い口も 403。所有者のトークンは変わらない。
- [ ] AC2: Enforce で宛先が `ai-stock-trading-api` でないトークンは 401（サービス・所有者とも。無い・platform-api・大文字）。
- [ ] AC3: 旧の共有クライアントは移行の段（LegacyClients）だけ通り、外すと 401（宛先が無い）／403（宛先があっても一覧に無い）。
- [ ] AC4: 確かめの口は報告書のクライアントだけ（ほかの 6 つと旧の共有クライアントは 403）。リスク管理の既定の一覧で効く。
- [ ] AC5: Off（既定）・Report は従前どおり通る（陰性対照: 同じトークンが Enforce で落ちる）。構成の誤り（未知の段・1 つの値）は起動で止まる。
- [ ] AC6: helm の描画で、7 サービスがそれぞれ別の Secret・別の clientId を読み、共有の ast-secrets を読まない。受け側は Enforce＋移行の段。口の一覧の呼び手は描画に在る。
  既定・ローカルは変わらない。同じ clientId の 2 重・未知の段は描画で止まる。描画物を崩すと判定が赤（変異）。
- [ ] AC7: 修正を外すと試験が赤になる（下の「陰性対照」）。

## 試験

- `PlatformShim.Tests/ServiceCallerPolicyTests`（本物の JwtBearer と TestServer。AC1〜3・5）。
- `RiskManagementService.Tests/ServiceCallerAllowlistTests`（本物の既定の一覧。AC4 と読み取りの口）。
- `scripts/helm-service-auth-clients.test.js`（AC6。helm.yml）。
- 陰性対照: 要件を OwnerOrService から外す・JwtBearer の宛先の確かめを外す・確かめの口の `.RequireServiceCallers` を外すと、上の試験が赤になることを確かめた（結果は PR）。

## 適用の順序と戻し方

手順書 `docs/operations/cloud-s2s-per-service-clients-runbook.md`。基盤の realm・OpenBao・ESO（MSP）→ 取引ユニットの image と chart（移行の段）→ 確かめ → 旧の共有クライアントを外す。

## 残る制約

1. gRPC の口はサービスの型ごとの一覧（メソッドごとではない）。クラウドは gRPC を結線しない。
2. CronJob（run-once）は最終の段で通らない（クラウドでは無効）。有効にするなら CronJob のクライアントを足す。
3. 口の既定の一覧はコード（イメージの中）にある。一覧を変えるにはイメージを作り直すか env で上書きする（`Auth__ServiceCallers__<名前>__0=...`）。
