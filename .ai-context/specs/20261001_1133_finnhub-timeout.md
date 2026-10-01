---
title: Finnhub を呼ぶ HttpClient に有界の打ち切りを置き、打ち切りを呼び出し側の「取得できない」の経路へ写す（#1133）
type: spec
status: accepted
related_ids: [FR-02, FR-01, FR-03, FR-10, FR-16, ADR-0020, ADR-0004, IADR-0469, IADR-0068, IADR-0099, IADR-0399, IADR-0095, IADR-0467]
author: claude (Claude Code)
created: 2026-10-01
updated: 2026-10-01
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0020 (決定 3: 情報源の欠測はソース単位)
---

# Finnhub の HttpClient の打ち切り（#1133）

## 背景

監査 F5（2026-10-01）: 2026-09-30 夜、Finnhub への TLS ハンドシェイクが `unexpected EOF` で 3 回失敗した（情報収集 15:56:08・17:08:18 UTC、
取引判断 15:56:30 UTC に 18.5 秒待って AAPL を見送り、市場監視 17:13:51 UTC に NVDA を 1 回スキップ）。
追加の監査コメント: 情報収集の `CreateClient("collection")` には名前付きの構成が無く、市場監視の `AddHttpClient("marketdata")` も同じで、どちらも既定の 100 秒。

## 範囲

1. Finnhub を呼ぶ名前付き HttpClient すべてに有界の打ち切りを置く（値は 1 か所）。
2. 打ち切りが各呼び出し側の既存の「取得できない」の経路に落ち、例外で判断・巡回を落とさないこと。呼び出し側の停止（呼び出し側のトークンの OCE）は伝わること。
3. IADR-0469・試験・自己変異。

範囲外: TLS の EOF の原因調査（egress 側の観測）、再試行・回路遮断の導入、Finnhub 以外の HTTP クライアント（下の除外表）。

## 母集合（規則 9。origin/develop 23b73f35）

引き方（誤りの側＝「打ち切りの無い登録」「OCE を一律に扱う catch」から引く）:

- `git grep -n -i finnhub -- backend`（拡張子で絞らない）→ Finnhub を呼ぶ型: `FinnhubQuoteClient`（/quote）・`FinnhubCompanyNewsSource`（/company-news）。前者の利用者: `FinnhubMarketDataSource`（共有）・`FinnhubInformationSource`（情報収集）。
- `git grep -n -E "AddHttpClient|CreateClient\(|AddStandardResilience|AddResilienceHandler|Polly|\.Timeout = " -- backend` → 全登録を列挙し、上の型へ渡るクライアントを特定。
- `git grep -n "MarketDataSourceFactory.Create\|InformationSourceFactory.Create" -- backend` → 生成箇所 5 つ。
- 呼び出し側の OCE の扱い: `git grep -n "OperationCanceledException" --` 各呼び出し側のファイル。

### Finnhub を呼ぶクライアント（対象）

| サービス | 名前 | 是正前の打ち切り | 呼び出し側と期限 | 呼び出し側の OCE の扱い（是正前） | 是正後 |
| --- | --- | --- | --- | --- | --- |
| TradeDecision | `"marketdata"`（`Program.cs`） | なし（100 秒） | `MarketDataCurrentPriceProvider` → `TradeDecisionAppService.GetCurrentPriceSafeAsync`。期限の設定なし（1 判断に 1 回、LLM の前） | `ex is not OperationCanceledException` だけ握る＝打ち切りは判断ごと例外 | 5 秒・打ち切りは null → `CurrentPriceUnavailable` |
| MarketMonitor | `"marketdata"` | なし（100 秒） | `MarketMonitorAppService.GetQuoteOrNullAsync`。巡回 `Monitor:PollIntervalSeconds` 60 秒（既定） | 呼び出し側のトークンだけ再送出（IADR-0399）＝正しい | 5 秒 |
| RiskManagement | `"marketdata"` | なし（100 秒） | `QuoteRefreshService`。`MarketData:RefreshIntervalSeconds` 60 秒（既定。`EnableMarkToMarket` が既定 false で既定では走らない） | 巡回の外で OCE を停止として `break`＝**打ち切り 1 回で補充が恒久に止まる** | 5 秒・打ち切りは null（巡回は続く） |
| ReportService | `"marketdata"` | なし（100 秒） | `ReportDraftService`（`LastKnownQuoteSource` 経由）。ドラフト生成ごと | 握らない＝ドラフトが例外 | 5 秒・打ち切りは null → 前回値 |
| InformationCollection | `"collection"`（名前付きの登録なし。無名 `AddHttpClient()` の既定） | なし（100 秒） | `SourceFetchRunner`（直列）。`Collection:PollIntervalSeconds` 1,800 秒（既定） | 呼び出し側のトークンだけ再送出・他はソースの欠測＝正しい（企業ニュース・Google ニュースも同じ） | 15 秒 |

resilience handler（Polly・`AddStandardResilienceHandler`・`AddResilienceHandler`）: **0 件**（`AttemptTimeout` / `TotalRequestTimeout` との整合は不要）。

### 除外したもの（理由つき）

| 対象 | 理由 |
| --- | --- |
| BacktestService `BarDataHttpClientName`（Stooq の日足。打ち切りなし） | Finnhub ではない（`StooqHistoricalBarSource` の Finnhub はコメントの言及のみ）。バックテストはオフラインの一括処理 |
| TradeDecision `"fx"`（日銀・FRED。打ち切りなし） | Finnhub ではない。IADR-0469 の残余に記録 |
| RiskManagement / ReportService `"fx"`（10 秒） | Finnhub ではない・既に有界 |
| `"order-execution"`（判断の日足。8 秒・IADR-0467）ほかサービス間 | Finnhub ではない・既に有界 |
| NotificationService の Finnhub の語（監視銘柄の適用の文言） | HTTP で Finnhub を呼ばない |
| `FinnhubDailyVolumeEstimator` / `FinnhubRateLimitClassifier` / `FinnhubLastRequestTracker` | 純関数・HTTP を持たない |

### 追随（規則 9・10）

`git grep -n -E "100 秒|停止要求は「取得不可」ではない" -- ':!.ai-context/specs' ':!CHANGELOG.md'` と `git grep -n -i -E "finnhub.{0,60}(タイムアウト|打ち切|timeout)"` → 追随が要る記述は無い
（IADR-0399 の索引行「呼び出し側以外の打ち切りを含む」は市場監視の正しい挙動の記述で、本件で誤りにならない）。
自分の記述で新たに誤りになるもの: `FinnhubMarketDataSource` のクラス冒頭の「取得できない事象はすべて null」—— 是正で事実に近づく（変更不要）。`FinnhubQuoteClient` の「通信例外は握りつぶさず送出」—— 不変（翻訳は受け手側）。

## 設計（IADR-0469）

- 値: 共有物 `FinnhubHttpTimeouts`（`Quote` 5 秒・`Collection` 15 秒）。各 `Program.cs` が引く。
- 写し方: `FinnhubMarketDataSource` で `catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }` の後に `catch (OperationCanceledException) → Warning ＋ null`。
- 判断のサービス本体（`TradeDecisionAppService`。#1130 が並行で編集）は触らない。

## 窓の表（規則 11）

プローブ: 増える側＝Finnhub の応答時間が打ち切りを越える（応答が返らない）／減る側＝呼び出し側の期限（停止）が打ち切りより先に来る。

| 形 | 増える側（期待: 打ち切りの時間で「取得できない」） | 減る側（期待: OCE が伝わる） |
| --- | --- | --- |
| 後の端だけ（打ち切り＋ OCE を一律 null） | ✅ | ❌（T-10-1861・T-10-1868 が赤：変異 M2 で実測） |
| 前の端だけ（OCE を一律再送出＝是正前の写し方） | ❌（T-10-1860・T-10-1865 が赤：変異 M1 で実測。打ち切りが無ければ 100 秒） | ✅ |
| **両端（採用）** | ✅ | ✅ |

## 試験（T-10-1860〜T-10-1868）

FR-02 の試験仕様書は `docs/tests/` に無い。現在値・判断の見送りの試験が載る `docs/tests/FR-10_risk-controls-tests.md` の帯（指定の T-10-1860..1879）に登録した。

| ID | ファイル |
| --- | --- |
| T-10-1860〜1862 | `backend/Shared/AiStockTrading.Shared.Infrastructure.Tests/MarketData/FinnhubTimeoutTests.cs` |
| T-10-1863 | `backend/Services/{TradeDecisionService,RiskManagementService,ReportService}/Tests/FinnhubClientTimeoutTests.cs`・`backend/Services/MarketMonitorService/Tests/MarketScheduleWiringTests.cs` |
| T-10-1864 | `backend/Services/InformationCollectionService/Tests/InformationSourceSelectionTests.cs` |
| T-10-1865・1868 | `backend/Services/TradeDecisionService/Tests/Features/TradeDecision/DecideTrade/FinnhubQuoteTimeoutDecisionTests.cs`（本物の受け手 → 現在値の口 → 判断） |
| T-10-1866・1867 | `backend/Services/InformationCollectionService/Tests/Infrastructure/ExternalServices/FinnhubCollectionTimeoutTests.cs` |

## 自己変異の結果

| # | 変異 | 結果 |
| --- | --- | --- |
| M1 | 打ち切りを例外のまま返す（`FinnhubMarketDataSource` の打ち切りの catch を `throw;`） | 赤: T-10-1860・T-10-1865 |
| M2 | 呼び出し側の OCE を握る（再送出の条件を `when (false)`） | 赤: T-10-1861・T-10-1868・既存「キャンセルはそのまま伝播する」 |
| M3 | 打ち切りを外す（5 つの `Program.cs` の登録を `AddHttpClient("…")` へ） | 赤: T-10-1863 × 4（MarketMonitor・RiskManagement・Report・TradeDecision）・T-10-1864 |
| M4 | `Quote` を 100 秒へ | 赤: T-10-1862 |
| M5 | `SourceFetchRunner` で呼び出し側の OCE も欠測にする | 赤: T-10-1867 |

いずれも戻した後に緑。

## 検証

コミット前に実行（ローカルは `~/.dotnet/dotnet`）。

| コマンド | 結果 |
| --- | --- |
| `dotnet build backend/backend.slnx -warnaserror` | 成功・警告 0 |
| `dotnet format backend/backend.slnx --verify-no-changes` | 差分なし |
| `dotnet test`（Shared.Infrastructure / TradeDecision / InformationCollection / MarketMonitor / RiskManagement / Report の全件） | 343 / 1,242 / 632 / 333 / 2,109 / 1,494 件すべて成功 |
| `node scripts/scripts.test.js` | 490 / 490 成功 |
| node の検査器（trace-blocks・knowledge-graph・cross-repo-refs・plan-id-qualification・test-traceability・doc-links・adr-index-sync・adr-index-addendum-loss・reading-budget・observability-assets・commit-messages） | すべて OK |

## 残余リスク

- TLS の EOF の原因と頻度は扱わない（egress 側で観測する。issue の提案 2）。
- 判断の定時サイクルは Finnhub が止まると銘柄数 × 5 秒まで伸びる。
- 情報収集の 1 巡回は最悪で銘柄数 × 要求数 × 15 秒まで伸び得る。
- Stooq の日足・判断の `"fx"` は打ち切りなしのまま（Finnhub ではない）。
