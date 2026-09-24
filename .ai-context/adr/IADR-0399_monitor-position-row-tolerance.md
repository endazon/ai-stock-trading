---
title: IADR-0399 市場監視は保有照会の応答を行ごとに読み、識別できない行は評価せず声に出し、損切りラインの無い行は送り手と同じ近似のラインで評価する。市況の照会は銘柄ごとに閉じ、1 行・1 銘柄の不正で巡回全体を止めない
type: impl-adr
status: Accepted
related_ids: [FR-03, FR-10, UC-02, ADR-0003, ADR-0040, IADR-0390, IADR-0393, IADR-0030, IADR-0344, IADR-0365, IADR-0374, IADR-0380, IADR-0068]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-03 市場監視 / FR-10 損切り)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1 の S1)
---

# IADR-0399: 市場監視の保有照会を行ごとに堅牢化する

- 状態: Accepted
- 日付: 2026-09-25
- 決定者: claude（起票 [#957](https://github.com/endazon/ai-stock-trading/issues/957)。利用者レビューは PR で受ける）

## 起点・関連

- 関連する計画書 ID: FR-03（市場監視）/ FR-10（損切りラインは到達で発動する）/ UC-02、計画 ADR-0040 決定1（S1）
- 対象 Issue: [#957](https://github.com/endazon/ai-stock-trading/issues/957)（A の市場監視の行と、PR #959 監査の前提条件の追記）
- 関連する実装仕様書: [20260925_957_monitor-position-row-tolerance](../specs/20260925_957_monitor-position-row-tolerance.md)
- 関連 IADR: [IADR-0390](IADR-0390_working-entries-in-decision-input.md)（#943 の追記: 契約テスト T-10-803 と「実行時の堅牢化は #957」）、
  [IADR-0393](IADR-0393_most-protective-stop-line-per-entry-lot.md)（送り手の `StopLossUnknown` の近似。**本 IADR の近似はこれと同じ式**）、
  [IADR-0030](IADR-0030_position-store-sync-api.md)（照会失敗は空列。**変えない**）、
  [IADR-0344](IADR-0344_s1-software-stop-loss.md)（決定4: 発注執行は行自身のラインで判定し直す。**依拠する**）、
  [IADR-0365](IADR-0365_s1-stop-evaluation-liveness-summary.md)（生存要約）、[IADR-0380](IADR-0380_market-session-schedule-and-closed-protection-gap.md)（閉場の報告）、
  [IADR-0374](IADR-0374_decision-skip-reasons-and-first-alert-rule.md)（決定4: アラートの規約）、
  [IADR-0068](IADR-0068_live-quote-feed-finnhub-extraction.md)（Finnhub の「取得不可＝null」）

## コンテキストと課題

市場監視 `HttpPositionStore` は `GET /risk-controls/open-positions` の応答を、非 nullable の `HeldPosition` へ直接逆シリアル化していた。
送り手 `OpenPositionView` の項目名が変わる（片方のサービスだけ先に配備する窓。k3d-local はサービスごとに `:latest` を入れ替えるため窓に上限が無い）と、
コードで追った帰結は次のとおりである（PR #959 監査の実測を含む）。

1. **`Symbol` → null**: 巡回は `IMarketDataSource.GetLatestQuoteAsync(null, …)` を呼ぶ。実運用の市況源 `FinnhubQuoteClient` は
   `Uri.EscapeDataString(null)` で `ArgumentNullException` を投げ、`FinnhubMarketDataSource` の catch（`HttpRequestException` /
   `JsonException` / `NotSupportedException`）の外なので巡回（`MarketMonitorAppService.EvaluateRoundAsync`）から抜ける。
   `MonitorPollingService` は `LogError` するだけで、**その巡回の全建玉の損切り判定・変動判定・生存要約が行われない**。毎巡回そうなる。
2. **`StopLossPrice` → 0**: `StopLossEvaluator` はロング `price <= 0`（発火しない）／ショート `price >= 0`（**含み益でも毎巡回発火**）。
3. **`Side` / `Market` / `Quantity` → 0**: 買い・日本・0 株に化ける。
4. 加えて、`FinnhubMarketDataSource` は `OperationCanceledException` を「停止要求」として再送出するため、HttpClient の上限による打ち切りでも
   1 銘柄で巡回全体が落ちる（呼び出し側のトークンかどうかを見ていない）。

契約テスト（T-10-803）は改名のマージを CI で止めるが、配備の窓は閉じない。#957 の前提条件は「損切りを黙って止めない（fail-loud）」である。

## 検討した選択肢（損切りラインが無い／正でない行）

| 案 | 帰結 |
| --- | --- |
| A. その行を評価せず、Critical と計器で出す | 行の建玉は市場監視の S1 の到達検知が無い（アラートが鳴るまで無保護） |
| **B. 送り手と同じ近似（平均取得単価 × (1 ∓ 既定比率 3%)）で評価し、近似と印を付けて Critical と計器で出す（採用）** | 無保護を作らない。近似が実際より保護的なら空振りの到達（下記）、緩いなら実際より遅い到達になる |
| C. ライン 0 のまま評価（従来） | ロングは発火しない・含み益のショートは毎巡回発火する |
| D. 応答全体を不明（空列）にする | 1 行の欠落で全建玉の検知が止まる（#957 の要件 3 に反する） |

B を採る理由は **一貫性**である。送り手のリスク管理はラインの記録を持たないロットを既に同じ式で見積もっている（IADR-0030・IADR-0393 の
`StopLossUnknown`）。「不明」を送り手は近似、受け手は無保護、と別々に扱うと、同じ建玉の保護がどのサービスに欠けたかで変わる。
加えて、**近似のラインで出た到達は、それだけでは建玉を売らない**: 発注執行の S1 は行自身の `TriggerPrice` と検知価格で判定し直す
（`SoftwareStopExecutor.Reached`・IADR-0344 決定4）。近似が実際より保護的でも、自分のラインに達していない S1 の行は決済されない。
平均取得単価も無い行は見積もれないので A に倒す（評価しない・声に出す）。

## 決定

### 決定1: 応答は行ごとに分類する（1 行の不正で応答全体を捨てない）

受け手の DTO を全項目 nullable（`string? Symbol, Market? Market, TradeSide? Side, int? Quantity, decimal? EntryPrice, decimal? StopLossPrice`）にし、
各行を次に分ける。

| 分類 | 条件 | 扱い | 計器 `reason` |
| --- | --- | --- | --- |
| 評価する | 銘柄が空でない・市場と方向が定義済みの列挙値・数量が正・損切りラインが正 | そのまま | — |
| 近似のラインで評価する | 識別できる・ラインが無い／正でない・平均取得単価が正 | `StopLossApproximation.Approximate` のラインで評価し `StopLossApproximated=true` | `stop-line-approximated` |
| 評価できない（識別） | `null` の行・銘柄なし／空白・市場／方向なしか未定義値・数量なしか正でない | 評価に渡さない | `identity-missing` |
| 評価できない（ライン） | 識別できる・ラインも平均取得単価も無い／正でない | 評価に渡さない（**0 では評価しない**） | `stop-line-unknown` |

200 で本文が一覧として読めない（壊れた JSON・`null`・列挙が文字列）ときは従来どおり空列だが、計器 `response-unreadable` と Critical を出す
（契約の食い違いであって、非 2xx・例外・打ち切りの一過性の障害とは違う）。非 2xx・例外・打ち切りは従来どおり Warning＋空列（IADR-0030）。

### 決定2: 声に出す（Critical 1 巡回 1 行＋計器＋アラート）と、近似の印

- 近似・評価できない行が 1 件でもあれば、その巡回で **Critical を 1 行**出す（全件の内訳・「評価しない行の建玉はこの巡回で到達を検知しない」・
  「近似は実際のラインではない」）。行ごとに Critical を重ねない。巡回ごとには出す（契約の食い違いが続く間は続く事象であり、間引くと止んだように見える）。
- 計器 `ast.market_monitor.position_rows_degraded{reason}`（Counter）を行数ぶん足す。0 件は計上しない。業務ダッシュボードにパネル、
  アラート `AstStopLossPositionRowsDegraded`（`sum(increase(...[15m])) > 0`・`for` なし・`severity: warning`）を置く。
  平常時の期待値が 0 件の事象なので閾値に実測は要らない（IADR-0374 決定5 と同じ論拠）。重大度は IADR-0374 決定4 の規約
  （実測なしに `critical` を置かない）に従う。
- 近似のラインは `HeldPosition.StopLossApproximated` → `StopLossEvaluation.StopLossApproximated` と運び、生存要約・閉場の報告のラインに
  「（近似: …）」を付ける。実値のラインと並べて書かない。
- 近似の式と比率は共有カーネルの `StopLossApproximation`（比率 `DefaultRatio = 0.03m`）に置き、リスク管理の
  `TradingDefaults.DefaultStopLossRatio` と `OpenPositionsService` もこれを使う（**式を 2 か所に置かない**。値は変わらない）。

### 決定3: 市況の照会は銘柄ごとに閉じる

- `MarketMonitorAppService` は損切り・変動の両方の照会を銘柄ごとに try で包み、例外（呼び出し側のトークンでない
  `OperationCanceledException` を含む）をその銘柄の「価格が取れない」（`null`）として扱い、Error を出して次の銘柄へ進む。
  損切りの記録は `Price=null` で残り、生存要約の既存の欠落 Warning（IADR-0365 決定3）へ流れる。**呼び出し側の停止要求だけは伝える。**
- `FinnhubMarketDataSource` は銘柄が null／空白なら照会せず `null` を返す（Warning）。レート枠も消費しない。

### 決定4: 不明を 0 と区別する

`HeldPosition.EntryPrice` を `decimal?` にする。市場監視に読み手は無いが、欠けた値を 0 に化けさせない。

## 理由

- 1 行・1 銘柄の不正は、その行・銘柄の保護を欠くことはあっても、**他の建玉の保護を止めてはならない**（S1 は巡回が唯一の発動源）。
- 「不明」は「無い」でも 0 でもない。識別できない行は数え、ラインの無い行は近似と明示して評価し、どちらも声に出す。
- 近似の採用で起き得る誤りは、発注執行が行自身のラインで判定し直すことにより「決済」ではなく「到達の通知」の側に留まる（S1 の行について）。

## 結果・残余リスク

- 良い点: 送り手の改名を片方だけ先に配備しても、健全な行の損切り検知・変動検知・生存要約は止まらない。識別できない行・ラインの無い行は
  Critical・計器・アラートで見える。ラインが 0 に化けて含み益のショートが毎巡回発火することは無くなる。
- 🔴 残余リスク:
  - **評価できない行の建玉は、アラートで人が気付くまで市場監視の S1 の到達検知が無い。** 識別できない行は銘柄・方向が分からず、
    近似もできない。
  - **近似のラインは実際のラインではない。** 実際より緩ければ到達は実際より遅れる（無保護よりは早い）。実際より保護的なら
    `StopLossTriggered` が毎巡回出て、到達の通知（Critical）が重なる（S1 の行は自分のラインに達するまで決済されない）。
    S2（システムもブローカーも決済しない）の建玉では、通知が近似のラインで「手動で決済してください」と言う。
    `StopLossTriggered`（共有契約）とその通知は近似かどうかを運ばない（本件では契約を変えない）。近似であることは市場監視の Critical と
    生存要約にだけ出る。
  - **すべての行が評価できないとき、生存要約は「保有 0 件」と同じく何も出さない**（`StopLossLivenessReporter.Observe([])` は状態を捨てる）。
    その巡回は `HttpPositionStore` の Critical（1 巡回 1 行）と計器・アラートが声に出す。生存要約に「評価できない行」を運ぶには
    `IPositionStore` の形を変える必要があり、本件では行わない。
  - 報告書 `HttpOpenPositionSource` と判断 `HttpSizingContextProvider` の実行時の堅牢化は本件に含まない（#957 に残す）。
- 追随: テスト仕様書 FR-10（T-10-833〜T-10-843）。IADR-0390 に日付つき追記（#943 の追記の「実行時の堅牢化は追随」のうち市場監視は本件で解消）。

## 関連

- 作業仕様書: `20260925_957_monitor-position-row-tolerance`
- テスト: `HttpPositionStoreTests`（T-10-833〜836）・`MarketMonitorServiceTests`（T-10-837）・`FinnhubMarketDataSourceTests`（T-10-838）・
  `StopLossLivenessReporterTests`（T-10-839）・`PositionRowToleranceCompositionTests`（T-10-840・841。本番の Program.cs の組み立て）・
  `BusinessMetricsTests`（T-10-842）
