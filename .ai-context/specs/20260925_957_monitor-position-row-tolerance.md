---
title: 市場監視が読む保有（/open-positions）の識別できない行・損切りラインの無い行を行ごとに扱い、1 行の不正で巡回全体の損切り検知・生存要約を止めない
type: spec
status: accepted
related_ids: [FR-03, FR-10, UC-02, ADR-0003, ADR-0040, IADR-0399, IADR-0390, IADR-0393, IADR-0030, IADR-0344, IADR-0365, IADR-0374, IADR-0380, IADR-0068]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-03 市場監視 / FR-10 損切り)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1 の S1)
---

# 仕様書: 市場監視の保有照会を行ごとに堅牢化する（#957 の市場監視の部分）

## 起点

- #957「A. 契約テストは足したが、実行時は既定値で読むアダプタ」の 1 行目（市場監視 `HttpPositionStore`）と、同 issue の
  2026-09-25 のコメント（PR #959 監査）「市場監視の実行時の堅牢化は、`OpenPositionView` と `HeldPosition` の契約を変える変更より先にマージすること」。
- 本 PR の射程は**市場監視だけ**である。報告書 `HttpOpenPositionSource` と判断 `HttpSizingContextProvider` の堅牢化（#957 の A の 2・3 行目）と
  B・C の契約テストは**別 PR**（#957 に残す）。市場監視だけで IADR・計器・アラート・結線テストまで揃い、差分がレビューできる大きさを超えるため。

## 🔴 実測（コードで確認・`origin/develop` = `f44bb8e2`）

| 事実 | 出典 |
| --- | --- |
| `HttpPositionStore` は応答を**非 nullable の** `HeldPosition(string Symbol, Market, TradeSide, int Quantity, decimal EntryPrice, decimal StopLossPrice)` へ直接逆シリアル化する | `MarketMonitorService/Infrastructure/ExternalServices/HttpPositionStore.cs` |
| 巡回は保有 1 件ずつ `IMarketDataSource.GetLatestQuoteAsync(position.Symbol, …)` を呼び、**例外を捕まえない** | `MarketMonitorService/Features/MarketMonitor/MarketMonitorAppService.cs` |
| 実運用の市況源 `FinnhubQuoteClient.GetQuoteAsync` は `Uri.EscapeDataString(symbol)` を呼ぶ（null で `ArgumentNullException`）。`FinnhubMarketDataSource` が捕まえるのは `HttpRequestException` / `JsonException` / `NotSupportedException` だけで、`OperationCanceledException` は再送出する | `Shared.Infrastructure/Composable/Adapters/MarketData/Finnhub{QuoteClient,MarketDataSource}.cs` |
| 巡回の例外は `MonitorPollingService.ExecuteAsync` の catch で `LogError` されるだけで、その巡回の損切り判定・変動判定・生存要約（`liveness.Observe`）はすべて行われない | `MarketMonitorService/Hosted/MonitorPollingService.cs` |
| 損切り価格 0 は `StopLossEvaluator.IsTriggered` でロング `price <= 0`（発火しない）／ショート `price >= 0`（毎巡回発火） | `MarketMonitorService/Domain/StopLossEvaluator.cs` |
| 到達を受けた発注執行の S1 は、**行自身の `TriggerPrice` と検知価格**で判定し直す（`Reached`）。到達のラインは使わない | `OrderExecutionService/Features/OrderExecution/ExecuteSoftwareStops/SoftwareStopExecutor.cs`（IADR-0344 決定4） |
| 到達の通知は `StopLossTriggered` 1 件につき 1 通（Critical）。S2 は「手動で決済してください」 | `NotificationService/Features/Notifications/NotificationFormatter.cs` |
| 送り手のリスク管理は、ラインの記録を持たないロットを `平均取得単価 × (1 ∓ TradingDefaults.DefaultStopLossRatio)` で見積もる（IADR-0030・IADR-0393 の `StopLossUnknown`） | `RiskManagementService/Features/RiskManagement/GetOpenPositions/OpenPositionsService.cs` |
| `HeldPosition.EntryPrice` を読むコードは市場監視に無い（宣言のみ） | `grep -rn EntryPrice backend/Services/MarketMonitorService` |
| 市場監視の DI は `BusinessMetrics` を解決できる（Program.cs が `MarketDataSourceFactory.EvaluateDailyVolume` へ渡している） | `MarketMonitorService/Program.cs` |
| 組み立てガード（PR #953）は未マージ（OPEN）。本 PR は既存の `WebApplicationFactory<Program>` の作法（T-10-800 と同じく "risk" HttpClient の一次ハンドラだけを差し替える）で結線を試す | `gh pr view 953` |

## 決定（記録は IADR-0399）

1. **行ごとに分類する。** 受け手の DTO を全項目 nullable にし、各行を次のどれかに分ける。1 行の不正で応答全体を捨てない。
   - **評価する**: 銘柄（空でない）・市場・方向（定義済みの列挙値）・数量（正）があり、損切りラインが正。
   - **近似のラインで評価する**: 識別できるが損切りラインが無い／正でない。平均取得単価が正なら、送り手と同じ式
     （`StopLossApproximation`＝既定比率 3%。リスク管理の `OpenPositionsService` も同じ関数を使うよう寄せる）で見積もる。
     `HeldPosition.StopLossApproximated = true` を立て、生存要約・閉場の報告のラインに「（近似）」を付ける。**0 では評価しない。**
   - **評価できない**: 識別できない（`null` の行・銘柄なし／空・市場なし・方向なし・数量なし／正でない・未定義の列挙値）、
     または損切りラインも平均取得単価も無い。その行は評価に渡さない。
2. **声に出す。** 近似・評価できない行が 1 件でもあれば、その巡回で **Critical を 1 行**（全件の内訳つき）出し、行ごとに計器
   `ast.market_monitor.position_rows_degraded{reason}`（`identity-missing` / `stop-line-approximated` / `stop-line-unknown`）を 1 増やす。
   200 で本文が JSON として読めない・`null` は `response-unreadable` で 1 増やし Critical（従来の空列は保つ）。
   アラート `AstStopLossPositionRowsDegraded`（平常時 0 件の事象。IADR-0374 の規約で `warning`）。
   非 2xx・例外・打ち切りは従来どおり Warning＋空列（IADR-0030。本件で変えない）。
3. **市況の照会は銘柄ごとに閉じる。** 巡回（損切り・変動の両方）で 1 銘柄の照会の例外（呼び出し側の停止要求以外の打ち切りを含む）を
   その銘柄の「価格が取れない」として扱い、Error を出して次の銘柄へ進む（損切りの記録は `Price=null` で残り、既存の欠落 Warning に流れる）。
   `FinnhubMarketDataSource` は銘柄が null／空白なら照会せず null を返す（Warning）。
4. **不明は 0 と区別する。** `HeldPosition.EntryPrice` を `decimal?` にする（読み手は無い。欠けた値を 0 に化けさせない）。

## 受け入れ基準

1. （T-10-833）送り手の本物の型 `OpenPositionView` を web 既定で直列化し、1 行から識別項目（`symbol`／`market`／`side`／`quantity`）を
   消す・`symbol` を空にする・`quantity` を 0 にする・`market` を未定義値にする・`null` の行を混ぜる。**健全な行はそのまま返り**、
   不正な行は返らず、`identity-missing` が行数ぶん計上され、Critical が 1 行出る。
2. （T-10-834）1 行の `stopLossPrice` を消す／0／負にする。平均取得単価があれば**近似のライン**（ロング: ×0.97・ショート: ×1.03。
   送り手 `TradingDefaults.DefaultStopLossRatio` と同じ比率）で `StopLossApproximated=true` として返り、ライン 0 では返らない。
   `stop-line-approximated` と Critical。
3. （T-10-835）ラインも平均取得単価も無い行は返らず、`stop-line-unknown` と Critical。平均取得単価だけが無い行はそのまま評価され、
   `EntryPrice` は null（0 ではない）。
4. （T-10-836）200 で本文が壊れている／`null`／列挙が文字列の応答は空列・`response-unreadable`・Critical。健全な応答では計器も Critical も出ない（否定形・隔離した Meter 名）。
5. （T-10-837）巡回: 1 銘柄の照会が例外（`ArgumentNullException`・呼び出し側以外の打ち切り）でも、他の保有の到達は出て、
   その銘柄は `Price=null` の評価として残り、変動判定も続く。呼び出し側の停止要求は従来どおり伝わる。
6. （T-10-838）`FinnhubMarketDataSource` は銘柄が null／空／空白なら HTTP を出さずに null を返し、例外を投げない。
7. （T-10-839）生存要約・閉場の報告は近似のラインに「（近似）」を付ける。
8. （T-10-840）**本番の Program.cs の組み立て**（`RiskManagement:BaseUrl`＋`MarketData:Provider=finnhub`、"risk" と "marketdata" の一次ハンドラだけを差し替え）で、
   送り手の型から 1 行の銘柄を消した応答と 1 行の損切りラインを消した応答を返す。巡回は例外なく終わり、健全な行の到達が出て、
   近似の行も評価され、Finnhub へは健全な銘柄しか照会されず、計器が計上される。
9. （T-10-841）同じ組み立てから `MonitorPollingService.RunOnceAsync` を回すと、Critical が出たうえで、**健全な行の生存要約が出る**。
10. （T-10-842）計器・ダッシュボード・アラートの名前が一致する（`node scripts/check-observability-assets.js`・`BusinessMetricsTests`）。
11. （T-10-843）変異注入: 行ごとの分類・近似・計上・銘柄ごとの catch・Finnhub の門を 1 つずつ外すと、対応するテストが赤になる（実測をテスト仕様書へ）。

## 変更しないもの

- 送り手（リスク管理）の応答型・エンドポイント・JSON 設定。`OpenPositionsService` の値（式を共有関数へ寄せるだけ。結果は同じ）。
- 非 2xx・例外・打ち切りの空列（IADR-0030）。`IPositionStore` の形。`StopLossTriggered`（共有契約）の形。
- 発注執行の判定・決済。報告書・判断のアダプタ（#957 に残す）。

## 🔴 母集合（この変更で追随する記述）

走査語: `HttpPositionStore` / `HeldPosition` / `実行時の堅牢化` / `DefaultStopLossRatio`（`*.cs`・`*.md`・`*.yaml`・`*.json`。確定済みの `.ai-context/specs`・`superpowers` を除く）。

| 箇所 | 扱い |
| --- | --- |
| `HttpPositionStore.cs` / `HeldPosition.cs` / `MarketMonitorAppService.cs` / `StopLossEvaluation.cs` / `StopLossLivenessReporter.cs` / `Program.cs` | 直す |
| `FinnhubMarketDataSource.cs`（共有） | 直す（銘柄の門） |
| `OpenPositionsService.cs` / `TradingDefaults.cs`（リスク管理） | 式・比率を共有の `StopLossApproximation` へ寄せる（値は不変） |
| `BusinessMetricNames.cs` / `BusinessMetrics.cs` / `BusinessMetricsTests.cs` | 計器を足す |
| `deploy/observability/dashboards/ai-stock-trading-business.json` / `alerts/ai-stock-trading-alerts.yaml` / `deploy/observability/README.md` | パネル・ルール・表の行を足す |
| `IADR-0390` 本文末（PR #959 監査の追記「#957 がマージされるまで…入れてはならない」「両アダプタの実行時の挙動は変えていない」） | 日付つき追記（市場監視は本件で解消・報告書とサイジングは残る。規則 10） |
| `docs/tests/FR-10_risk-controls-tests.md` の #943 節「本書が固定していない残余リスク」 | 市場監視を外す文へ直し、#957 節（T-10-833〜843）を足す。trace ブロックへ本仕様書・IADR-0399・#957 |
| `IADR-0030` / `IADR-0051` / `IADR-0066` / `IADR-0068` / `IADR-0119` の `HttpPositionStore` への言及 | **変えない**（失敗＝空列は今も正しい。IADR-0066/0068 の「HttpPositionStore の 3% 近似」は当時の記述で、凍結記録） |
| `TradeDecisionService/.../HttpHeldPositionProvider.cs` の「市場監視は失敗を空列へ倒す」 | **変えない**（今も正しい） |
| `.ai-context/specs/20260925_943_cross-service-read-contracts.md` | **変えない**（確定済み。IADR-0390 の追記で足りる） |

**テスト ID**: 予約ブロック **T-10-833〜T-10-845** のうち 833〜843 を使う（FR-10 の損切り保護の経路のため同じ系列）。844・845 は未使用。
