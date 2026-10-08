---
title: Finnhub の限流器を容量 1（等間隔）にし、どの 60 秒の固定窓でも 1 プロセスの送出が自制レート以下、同じ鍵の全プロセスの合計が 60 回以下になることを模擬時計の試験で固定する（#1247）
type: spec
status: accepted
related_ids: [FR-01, FR-03, FR-04, FR-10, NFR, ADR-0043, IADR-0513, IADR-0512, IADR-0275, IADR-0068, IADR-0434, IADR-0435, IADR-0469, IADR-0494]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0043_finnhub-daily-premise-withdrawn-and-cycle-fit-control.md (決定 2: (a) 同一鍵の合計 ≤ 60・(b) 1 巡回が巡回間隔に収まる)
---

# Finnhub の限流器のバーストを固定窓の内側へ収める（#1247）

> 本仕様書は実装着手前に作成する。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-01 / FR-04（情報収集）・FR-03（市場監視の巡回）・FR-10（損切りの評価に使う価格の欠け）・NFR
- 計画 ADR: ADR-0043 決定 2（(a) 同じ鍵の自制レートの合計 ≤ 60 回/分・(b) 1 巡回が巡回間隔に収まる）
- 関連 IADR: IADR-0275（60 回/60 秒の固定窓の実測。逐次の要求しか測っていない）・IADR-0068 決定 4（プロセス間で協調しない）・
  IADR-0512（構成の合計を描画で検査する。残余リスクに本件）・IADR-0434（市場監視 12 回/分・1 巡回 12 要求）・IADR-0435（情報収集の Finnhub 系は 1 つの限流器を共有）・
  IADR-0469（`/quote` の打ち切り 5 秒）
- 新規 IADR: IADR-0513
- 起票: [#1247](https://github.com/endazon/ai-stock-trading/issues/1247)（起票元 PR #1245 の独立監査 🟡-2）

## 現況（`origin/develop` `21df0170`）

- `TokenBucket(capacity, refillInterval)` は満杯（`_tokens = capacity`）で始まり、`refillInterval` あたり `capacity` を連続で補充する。
- Finnhub の 2 か所はどちらも `new TokenBucket(Math.Max(1, r), 1 分)`＝**容量 ＝ 自制レート r**。
- 長さ 60 秒の半開区間 `[t, t+60)` で 1 プロセスが送れる最大は「t 時点の残量（≤ 容量 C）＋ 区間内の補充（r 未満）」の整数部 ＝ **C + r − 1**。
  C ＝ r では **2r − 1**（30 → 59・12 → 23・5 → 9）。5 プロセス（values-local の 30・12・5・5・5）が同じ窓に重なると **2·57 − 5 ＝ 109**。
- 起動直後だけではない。情報収集は巡回間隔（300 秒）の間に満杯へ戻るため、**毎巡回の頭で 30 回を一気に送る**（issue の例の 59 回/窓はこの形）。
  したがって「空で始める」だけでは足りない（巡回の間の休止で満杯へ戻る）。

### 母集合（`TokenBucket` の生成箇所。`git grep -n "new TokenBucket" backend`）

| 生成箇所 | 送り先 | 本件の対象か |
| --- | --- | --- |
| `Shared.Infrastructure/.../MarketData/MarketDataSourceFactory.cs` `Limiter` | Finnhub `/quote`（市場監視・リスク管理・報告書・取引判断の 4 サービス） | **対象** |
| `InformationCollectionService/.../InformationSourceFactory.cs` の `FinnhubFamily`（`finnhub` と `finnhub-news` が共有） | Finnhub `/quote`・企業ニュース | **対象** |
| 同 `Limiter`（GoogleNews・SEC EDGAR・EDINET・BOJ・FRED・FINRA） | それぞれ別の提供元 | 対象外（固定窓の実測が無く、同じ鍵を共有しない） |
| `Shared.Infrastructure/.../Fx/FxRateSourceFactory.cs` | FRED（公表 120 回/分に対し 5 回/分） | 対象外 |
| `BacktestService/.../HistoricalBarSourceFactory.cs` | Stooq | 対象外 |
| テスト（`TokenBucketTests`・`DelayingRateLimiterTests`） | — | 変えない |

**共有の `TokenBucket` は変えない。** 容量 1・補充間隔 60/r 秒の生成は既存の公開の構築子で表せる（容量 1 は `ThrowIfLessThan(capacity, 1)` を満たす）。
Finnhub 以外の提供元の挙動（バーストの許容）は変えない。

## 設計

対策は issue の候補 (a)〜(c) のうち **(a) の変形「容量を 1 にする（等間隔送出）」** を採る（IADR-0513）。

- 新設 `FinnhubRateLimiter`（`Shared.Infrastructure/.../MarketData/`）が Finnhub 用の唯一の生成点になる:
  `CreateBucket(r)` ＝ `new TokenBucket(1, ⌈1 分 ÷ max(1, r)⌉)`（ティック単位で切り上げる。切り捨てると間隔が 60/r 未満になり、r 回の間隔の合計が 60 秒を割って r + 1 回目が窓に入る）、
  `Create(r, timeProvider)` ＝ `DelayingRateLimiter` で包む。
- 2 か所の生成をこれへ置き換える（情報収集の Finnhub 系の共有は変えない＝1 プロセス 1 限流器のまま）。
- 窓の計算: C ＝ 1 なら `[t, t+60)` の最大は 1 + r − 1 ＝ **r**。全プロセスで Σr ≤ 60（(a)・現在 57）が、そのまま**任意の固定窓の合計 ≤ 60** になる。
  Finnhub の窓は最初の要求の時刻から 60 秒（IADR-0275 決定 1）で、こちらの時計と揃わないが、上界は窓の位置に依らない。
- 浮動小数の許容差（`TokenBucket.Epsilon`）が「ちょうど補充の瞬間」を数ナノ秒早めて r + 1 回目を窓の端へ入れないかは、模擬時計の試験で確かめる（入るなら間隔へ余裕を足す）。

### (b) 市場監視の 1 巡回の収まり（検査器・ピンは変えない）

- 12 回/分 → 5 秒に 1 回。12 要求の送出時刻は 0・5・…・55 秒。最後の要求は 55 秒に出て、打ち切り 5 秒（IADR-0469）で 60 秒までに終わる。
- 巡回は `PeriodicTimer`（60 秒）。次の刻みの 60 秒には最後の要求（55 秒）から 5 秒経っており、1 トークンが戻っている。定常で 60 秒ごとに 12 要求。
- (b) の式 `n × 60 ≤ r × 間隔`（n ≤ 12）は、等間隔では「最後の要求の開始 (n−1)·60/r ≤ 間隔 − 60/r」と同値であり、
  最後の要求の往復に 60/r 秒（＝5 秒＝打ち切りと同じ）を残す。**式は等間隔の送出に対して正しいまま**で、`check-finnhub-key-budget.js` と JSON・ピンの試験は変えない（検査器の見出しの注記だけ足す）。
- 待ち時間への影響（IADR-0513 に記録）: 1 銘柄あたりの価格の更新周期は 60 秒のまま。巡回の中で k 番目の銘柄は巡回の頭から 5(k−1) 秒後に照会される。
  損切りの発行は巡回の評価の後（`MonitorPollingService.RunOnceAsync`）なので、**到達の発行は最大で約 55 秒遅れ得る**（1 巡回 12 要求のとき。現況の 6 要求なら約 25 秒）。
  保有は監視銘柄より先に照会する（IADR-0494）ので、保有の価格そのものは巡回の頭で取れる。

## 受け入れ基準（試験）

| # | 内容 | 試験 |
| --- | --- | --- |
| 1 | 全プロセスが同時に起動し自制レートいっぱいまで要求する。どの 60 秒の窓でも合計 ≤ 60、各プロセス ≤ r（values-local の 30・12・5・5・5） | `FinnhubRateLimiterWindowTests`（T-10-2429） |
| 2 | 1 プロセスの r ＝ 1〜60 のどれでも、どの 60 秒の窓でも ≤ r（起動直後・休止の後） | 同（T-10-2430） |
| 3 | 休止（巡回の間）の後に一斉に要求しても、どの窓でも合計 ≤ 60 | 同（T-10-2431） |
| 4 | **陰性対照**: 是正前の形（容量 ＝ r・満杯で起動）で同じ模擬をすると合計 109（> 60）・1 プロセス 2r − 1 | 同（T-10-2432） |
| 5 | 市場監視 12 回/分で 12 要求は 55 秒で出し終わり、次の巡回の頭（60 秒）で待たずに送れる | 同（T-10-2433） |
| 6 | 2 つの生成箇所が `FinnhubRateLimiter` を使う（容量 1） | 同（T-10-2434。ソースの読み取り） |
| 7 | IADR-0512 の残余リスクへ日付つき追記・索引の行も更新 | 文書 |

窓の数え方: 送出時刻の列を全プロセスで合わせ、**各送出時刻を窓の始点**とする `[s, s+60)` の件数の最大を取る（最大の窓は必ずある送出時刻から始まる形へずらせる）。

## 検証

- `dotnet build backend/backend.slnx`・Shared.Infrastructure.Tests・InformationCollectionService.Tests・MarketMonitorService.Tests（ほかに Finnhub の限流器を使う
  RiskManagement・Report・TradeDecision は生成箇所が共有の `MarketDataSourceFactory` なので Shared.Infrastructure.Tests で覆う）
- `dotnet format backend/backend.slnx --verify-no-changes`
- `node scripts/scripts.test.js`・`check-trace-blocks`・`gen-knowledge-graph --check`・`check-test-traceability`・`check-adr-index-sync`・`check-adr-index-addendum-loss`・`check-commit-messages`

## 是正の母集合（規則 9・10）

- 「バースト」「満杯」「トークンバケット」で `docs/`・`deploy/`・`scripts/` を走査した（`.ai-context/` と CHANGELOG を除く）。本件の挙動を述べる生きた文書の記述は無かった
  （`values.yaml:567`・`operations.md:416` は発注経路の別の話）。
- `.ai-context/` の凍結記録（IADR-0275 決定 5「トークンバケットが構造的に保証する」等）は書き換えない。IADR-0512 の残余リスクだけ日付つきで追記する。
