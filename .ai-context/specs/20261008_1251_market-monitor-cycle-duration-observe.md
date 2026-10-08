---
title: 市場監視の 1 巡回の所要を計量（秒）と Warning ログで観測し、限流器の外の所要の余裕を (b) の式へ入れない理由と巡回の途中の発行を範囲外とする理由を IADR-0513 へ追記する（#1251）
type: spec
status: accepted
related_ids: [FR-03, FR-04, FR-10, NFR-01, NFR, ADR-0043, IADR-0513, IADR-0469, IADR-0494, IADR-0434, IADR-0437, IADR-0255, IADR-0307, IADR-0064, IADR-0068]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0043_finnhub-daily-premise-withdrawn-and-cycle-fit-control.md (決定 2 (b): 1 巡回が巡回間隔に収まる)
---

# 市場監視の 1 巡回の所要を観測する（#1251）

> 本仕様書は実装着手前に作成する。

## 起点となる計画書（トレーサビリティ）

- 機能要求: FR-04（起票の起点）・FR-03（市場監視の巡回）・FR-10（損切りの評価）・NFR-01（価格変動検知から発注完了まで 5 分以内）
- 計画 ADR: ADR-0043 決定 2 (b)（1 巡回が巡回間隔に収まる。「遅れは統制の抜けになる」）
- 関連 IADR: IADR-0513 決定 3・4・残余リスク（本件の背景）・IADR-0469（`/quote` の打ち切り 5 秒）・IADR-0494（保有を先に照会する）・
  IADR-0434 / IADR-0437（(b) の式と検査）・IADR-0255 / IADR-0307（業務メトリクスの名前レジストリ・ヒストグラムの境界を View で明示する慣行）・
  IADR-0064 / IADR-0068（`DelayingRateLimiter`）
- 新規 IADR: なし（IADR-0513 への日付つき追記で記録する）
- 起票: [#1251](https://github.com/endazon/ai-stock-trading/issues/1251)（起票元 #1247 / PR #1250 の独立監査 🟡-1・🟢-3）

## 現況（`origin/develop` `275df0d4`）

- `MonitorPollingService.ExecuteAsync` は `PeriodicTimer(max(1, PollIntervalSeconds))` の刻みごとに `RunOnceAsync` を呼ぶ。
  `RunOnceAsync` は全市場が閉場なら何もせず返り、開場していれば「保有の照会 → 照会（限流器 12 回/分＝5 秒間隔）→ 評価 → 損切り・変動の発行 → 日次見積り → 生存の報告」を行う。
- 1 巡回の所要を測る計器・警告は無い（IADR-0513 残余リスク）。
- 計量の慣行: 名前は `BusinessMetricNames` だけが持ち（単位は名前へ埋める。`unit` は与えない）、`BusinessMetrics` が計器を作り、
  ヒストグラムの境界は `ObservabilityExtensions` の `AddView` で明示する（既定の境界では目標値が読めない。IADR-0307）。
  `check-observability-assets.js` R2 は「レジストリの各計器が少なくとも 1 つのパネルから引かれている」ことを要求する。
- `DelayingRateLimiter` の既定の待機は `Task.Delay`（`TimeProvider` を経由しない）。

## 設計

### AC1 — 巡回の所要の計量と Warning（必須）

- 計器 `ast.market_monitor.cycle_duration_seconds`（`Histogram<double>`・タグなし）を `BusinessMetricNames` / `BusinessMetrics.RecordMarketMonitorCycleDuration(double seconds)` に足す。
- 境界は `ObservabilityExtensions.MarketMonitorCycleDurationBucketsSeconds` を `AddView` で明示する。**既定の巡回間隔 60 秒と、その 1 要求ぶん手前（55 秒）を境界そのものに置く**
  （既定の境界 0,5,10,25,50,75,… では 60 秒が 50〜75 のバケットに埋もれ、「間隔に達した巡回」の件数が読めない）。
- `MonitorPollingService` に任意の依存 `TimeProvider? timeProvider = null`（既定 `TimeProvider.System`）と `BusinessMetrics? metrics = null` を足す（既存の任意引数 `liveness` / `dailyVolume` の後ろ）。
  DI は両方とも登録済み（`AddSingleton(TimeProvider.System)`・`AddAiStockTradingObservability` の `TryAddSingleton<BusinessMetrics>`）。
- `RunOnceAsync` の頭で `GetTimestamp()`、**開場して評価に進んだ巡回だけ**、終わり（例外で抜けた場合も含む。`finally`）で経過を測る。
  - 全市場が閉場の巡回は記録しない（評価をしない巡回の 0 秒でヒストグラムを薄めない。日次見積りと同じ扱い）。
  - 停止要求（`cancellationToken` が取り消し済み）で抜けた巡回は記録しない。
  - 所要 ≥ 巡回間隔（`max(1, PollIntervalSeconds)` 秒）なら Warning を 1 行出す（所要・巡回間隔・`PeriodicTimer` が逃した刻みを畳むので価格の確認の周期が延びること・見直しの手がかり）。
  - 観測の失敗は巡回を失敗させない（`try/catch` で Warning。既存の生存の報告と同じ作法）。
- ダッシュボード `ai-stock-trading-business.json` にパネル 21（P95 と 60 秒超の件数）を足し、`deploy/observability/README.md` の計器表へ 1 行足す。
- アラートは置かない（Warning ログで足りるかを運用で見る。残余）。

### AC2 — 限流器の外の所要の余裕を (b) の式へ入れるか（必須・決定）

**入れない。IADR-0513 へ日付つき追記で理由を残す。**

- 等間隔の送出では `n × 60 ≤ r × 間隔` は「最後の要求の開始 ≤ 間隔 − 60/r」と同値で、既に 1 要求ぶん（5 秒）の余裕を残している（IADR-0513 決定 3）。
- 例示の `n ≤ r × 間隔 / 60 − 1` は 12 ≤ 11 となり、**現在の構成（市場監視 12 回/分・60 秒・最低 12 要求/巡回。`finnhub-key-budget.json` の `minRequestsPerCycle`）を赤にし、
  監視銘柄の追加の上限を 12 → 11 へ黙って下げる**。容量を下げる判断は観測の前に取らない。
- 限流器の外の所要（gRPC の保有照会・最後の往復・発行・生存の報告）は静的な量ではなく、構成から計算できない。式へ定数として入れても根拠のない値になる。
  AC1 の計量で実測し、所要が間隔に達する巡回が観測されたら、そのときに式（または自制レート・巡回間隔・監視銘柄の数）を見直す。

### AC3 — 損切りの到達を巡回の途中で発行するか（任意・決定）

**範囲外（理由を IADR-0513 へ追記する）。**

- 発行は巡回ごとのスコープの `IMessageBus` で評価の後にまとめて行い、損切りを先に出す（IADR-0014）順序を持つ。巡回の途中の発行は `EvaluateRoundAsync` の分割（保有の評価と監視銘柄の評価の間で返す）を要し、
  本件（観測の追加）の範囲を超える。
- 遅れ（最大約 55 秒）は NFR-01 の 5 分の内側であり、損切りそのものは証券会社側の逆指値（FR-10）で、発行の遅れは決済の遅れではない（IADR-0513 決定 4）。
- 見直しの条件: NFR-01 の計器（`order_completion_latency_ms{trigger=price-movement}`）の P95 が 5 分へ近づく、または証券会社側の逆指値に頼れない運用へ変わったとき。

### 補足 — `DelayingRateLimiter` の既定の待機（PR #1250 の監査 🟢-3）

**対応する（自明な変更）。** 既定を `(d, ct) => Task.Delay(d, timeProvider, ct)` にする。

- 母集合（既定の待機に依る生成）: `git grep -n "new DelayingRateLimiter" backend` ＝ `FinnhubRateLimiter.Create`・`InformationSourceFactory`・`FxRateSourceFactory`・`HistoricalBarSourceFactory` の 4 か所（どれも待機を注入しない）。
  試験の `DelayingRateLimiterTests` は待機を注入するので影響しない。
- `TimeProvider.System` では `Task.Delay(d, TimeProvider.System, ct)` は `Task.Delay(d, ct)` と同じ（実行時の挙動は変わらない）。
- 偽の `TimeProvider` で `CreateTimer` を上書きしているのは `OrderExecutionService.Tests/Hosted/ManualTimerTimeProvider` だけで、限流器を通る経路では使っていない（`git grep -n "CreateTimer" backend`）。
  他の手製の偽の時計は `GetUtcNow` だけを上書きし、基底の `CreateTimer` は実の時計のタイマーを作るので、既存の試験の待ち方は変わらない。

## 受け入れ基準 → 試験

| # | 受け入れ基準 | 試験（T-10 帯。develop の最大 T-10-2455 の次から採番） |
| --- | --- | --- |
| AC1-a | 開場した巡回の所要が巡回間隔に達する（60 秒ちょうど・超える）と、計量に所要の秒数が 1 件入り、Warning が 1 行出る | `MonitorPollingServiceTests`（T-10-2456。偽の `TimeProvider` を照会ごとに進める） |
| AC1-b | 所要が巡回間隔未満なら計量は入るが Warning は出ない（否定形） | 同（T-10-2457） |
| AC1-c | 巡回間隔は構成の値で判定する（120 秒の構成で 90 秒の巡回は Warning を出さない） | 同（T-10-2457 の `Theory` の行） |
| AC1-d | 全市場が閉場の巡回は記録しない | 同（T-10-2458） |
| AC1-e | 計器名がレジストリと一致し、ヒストグラムは既定ではなく明示した境界（55・60 を含む）で出ていく | `BusinessMetricsTests`（既存の一致検査へ足す）・`BusinessMetricsWiringTests`（T-10-2459） |
| AC2 | (b) の式は変えない理由を IADR-0513 へ追記・索引の行も更新 | 文書（`check-adr-index-sync`・`check-adr-index-addendum-loss`） |
| AC3 | 巡回の途中の発行は範囲外とする理由を IADR-0513 へ追記 | 文書 |
| 補足 | 既定の待機は `TimeProvider` のタイマーで待つ（偽の時計のタイマーを発火させるまで通らず、発火させると通る） | `DelayingRateLimiterTests`（T-10-2460） |

実時間の待機（`Task.Delay` / `Thread.Sleep`）は試験に使わない。経過は偽の `TimeProvider`（`GetTimestamp` を手で進める）で作る。

## 検証

- `dotnet build backend/backend.slnx`（警告 0）
- `dotnet format --verify-no-changes`（Shared.Contracts・Shared.Infrastructure・TestSupport.PlatformShim・MarketMonitorService と各試験）
- `dotnet test`: MarketMonitorService.Tests・Shared.Contracts.Tests・Shared.Infrastructure.Tests・TestSupport.PlatformShim.Tests・Architecture.Tests
- `node scripts/scripts.test.js`・`check-observability-assets`・`check-trace-blocks`・`check-adr-index-sync`・`check-adr-index-addendum-loss`・`check-cross-repo-refs`・
  `check-commit-messages`・`check-test-traceability`・`check-plan-id-qualification`・`gen-knowledge-graph --check`・`check-reading-budget`

## 是正の母集合（規則 9・10）

- 規則 9（誤りの側の文字列で走査）: 「計量・警告は置いていない」「後続で扱う」「巡回の所要」「巡回の途中の発行」「5 秒 − 限流器」で全文書を走査した（`git grep`。CHANGELOG を除く）。
  該当は IADR-0513 の決定 3・残余リスク（凍結記録。本文は書き換えず日付つき追記）と IADR-0275 の別件（情報収集の巡回）・IADR-0494 / 仕様書 1189 の「1 巡回の所要時間の内側」（観測の有無を述べていない）だけだった。
  生きた文書（`docs/`・`deploy/`）に「巡回の所要を観測していない」と述べる記述は無い。
- 規則 10（この変更で新たに誤りになる自分の記述）: 計器を足すと、`deploy/observability/README.md` の計器表・ダッシュボード（R2）・`BusinessMetricsTests.RecordEveryInstrument`・
  `check-observability-assets.js` の下限（増える側なので不変）が追随先になる。`DelayingRateLimiter.cs` 冒頭の「既定は Task.Delay」の注記も直す。
  IADR-0513 の索引行の「残余: … 損切りの発行の遅れ」は追記で解消を示す。
- 除外: `.ai-context/specs/`（凍結）・CHANGELOG（生成物）。
