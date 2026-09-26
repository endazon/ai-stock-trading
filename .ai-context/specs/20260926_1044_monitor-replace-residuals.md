---
title: 監視銘柄の全置換で symbol が null の要素を 400 で弾き、米国株だけを数える検査を試験で固定し、情報収集の企業ニュースの送出を 429 の分類へ数える（#1044＝#1037 の残り）
type: spec
status: accepted
related_ids: [FR-13, FR-03, FR-01, SC-02, ADR-0043, ADR-0031, IADR-0437, IADR-0435, IADR-0164]
author: claude (Claude Code)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0043_finnhub-daily-premise-withdrawn-and-cycle-fit-control.md (決定 1・2・4)
related_specs:
  - 20260926_1030_finnhub-cycle-fit-control.md
---

# 仕様書: 監視銘柄の全置換と 429 の分類の残り（#1044）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-13（監視銘柄の変更）、FR-03（市場監視）、FR-01（情報収集）
- 画面: SC-02（監視銘柄。全置換 `PUT /monitor/settings` は画面から使わない口）
- 計画 ADR: ADR-0043 決定 1（日次は実際の 429 で見張る）・決定 2 (b)・決定 4（収まらない追加を適用しない）
- 関連 IADR: IADR-0437（本 PR が決定 7 を追記する。新しい IADR は起こさない）、IADR-0435（情報収集の Finnhub は現在値と企業ニュースで 1 つのバケットを共有）、IADR-0164 決定 3（全置換も部分更新と同じ規律）
- 起点 Issue: #1044（#1037 の差分監査のブロックしない指摘 4 件）

## 事象と対応

| # | 事象 | 対応 | 分類 |
| ---: | --- | --- | --- |
| 1 | `MonitorSettingsService.Replace` の重複検査が `s.Symbol.Trim()` を呼ぶため、`{"symbol":null}`（要素そのものが null も同じ）を含む全置換が NullReferenceException → 500 | 重複検査の前に `null` 要素と `string.IsNullOrWhiteSpace(Symbol)` を `ArgumentException`（→ 400）で弾く。保存も履歴も無い | コード＋試験 |
| 2 | 変異 M7（全置換の「費用のかかる追加」から米国の条件を外す）が生き残った | 予算を既に超えている監視銘柄へ東証の銘柄だけを足す置換が通ること、米国と東証を同時に足す置換の拒否文言が米国の銘柄だけを挙げることを固定する | 試験 |
| 3 | 情報収集の `FinnhubCompanyNewsSource` は現在値と同じバケットで送るが `FinnhubQuoteClient` を通らないため、直前の企業ニュースの要求による秒次の 429 を、現在値のクライアントは「直前の要求から 1 秒以上」と読み 4301 に記録し得る | **共有の直前の要求の時刻（`FinnhubLastRequestTracker`）を Finnhub 系（同じ鍵・同じバケット）で 1 つ持つ**。現在値のクライアントと企業ニュースの両方が送出時に刻み、429 の分類は同じ時刻から間隔を読む | コード＋試験 |
| 4 | #1037 より前に保存された重複（旧い全置換・初回シード `Monitor:SeedSymbols` は重複を除かない）が残っていると、それを保ったままの全置換が 400 になる | **運用者向けの注記**（chart README・IADR-0437 決定 7）と、拒否の文言に「保存済みの重複も 1 件ずつに減らして送れば適用される」を足す。読み取り時の正規化はしない | 文書＋文言 |

### 項目 3 の選択: 共有の追跡器を入れる（文書化だけに留めない）

- 変更は小さい: 追跡器は `Interlocked` の 1 フィールドを持つ型で、`FinnhubQuoteClient` の既存の `_lastRequestTicks` をそのまま外へ出すだけ。
  `FinnhubQuoteClient` と `FinnhubCompanyNewsSource` は省略可能な引数で受け、省略すれば従来どおり自分だけの時刻を持つ
  （市場監視・リスク管理・報告書・取引判断の `MarketDataSourceFactory` の経路は変わらない。これらのプロセスは Finnhub の送り手を 1 つしか持たない）。
- 結線は `InformationSourceFactory` の `FinnhubFamily`（バケットを 1 つだけ作る既存の場所）に並べる。同じ鍵・同じバケットの単位で共有するため
  「鍵ごと」の追跡になる。
- 企業ニュースの 429 そのものは分類しない（従来どおり例外→ソース単位の欠測。4301 は現在値のクライアントだけが出す）。必要なのは、企業ニュースの送出を
  現在値の 429 の「直前の要求」に数えることだけである。
- 文書化だけに留める案は不採用: 同じプロセスの中の取り違えは直せる（他のプロセスの送出は見えないので残余リスクとして残る）。

### 項目 4 の選択: 運用者向けの注記（読み取り時の正規化はしない）

- 読み取り時の一回きりの正規化は不採用: 保存済みの台帳を利用者の操作なしに書き換えることになり、変更履歴（理由必須・操作者）の規律
  （IADR-0164 決定 3）の外で監視銘柄が変わる。巡回（`MarketMonitorAppService`）が照会する銘柄の数も黙って変わる。
- 全置換だけが止まる（部分更新・SC-02 の追加／除外は重複検査を通らない）ため、影響は画面から使わない口に限られる。
  直し方は「重複を 1 件ずつに減らして送る」で、これは除外なので巡回の検査にも掛からず必ず通る。拒否の文言にこの直し方を書く。
- 🔴 残余: 重複を減らす置換は、`RecordWatchlistDelta` が (Symbol, Market) の集合で増減を見るため「除外」の履歴を残さない（既存の挙動。変えない）。

## 母集合（規則 1〜6・9）

- 項目 1: `git grep -n "Symbol.Trim()\|\.Symbol\.Trim" backend/Services/MarketMonitorService` → 全置換の重複検査の 1 箇所。
  他の口（SC-02 の追加 `MonitorWatchlistService.Add`、入れ替え案 `WatchlistProposalPlan`）は引数・案の要素を別に検証している
  （`MonitorWatchlistService` は `ThrowIfNullOrWhiteSpace`）。初回シードは空白の要素を捨てる（`MonitorSeedOptions`）。対象は全置換だけ。
- 項目 3: `git grep -n "new FinnhubQuoteClient"`（非テスト）→ `InformationSourceFactory.cs` と `MarketDataSourceFactory.cs` の 2 箇所。
  `git grep -n "finnhub.io"`（非テスト）→ 企業ニュース・現在値の 2 つ。同じプロセスで同じ鍵を使う Finnhub の送り手が 2 つあるのは情報収集だけ。
- 項目 3 の文書: `git grep -n "4301\|直前の要求から 1 秒"` → chart README（「同じプロセスの直前の要求から 1 秒以内」）、IADR-0437 決定 6、
  `FinnhubRateLimitClassifier` のコメント。README の「同じプロセス」は是正前は実装と食い違っていた（実際は同じクライアント）ため、本 PR で実装が README に追いつく。
  README と IADR-0437（追記）を更新する。分類器のコメントは「このクライアント」→「同じ鍵の送り手」に改める。
- 項目 4: `git grep -n "重複"` の全置換関連 → `MonitorSettingsService.cs`・chart README・IADR-0437 決定 6・`docs/tests/FR-10_risk-controls-tests.md`（T-10-1458）。

## 受け入れ基準 → テスト（T-10-1570〜T-10-1574）

| # | 基準 | テスト |
| --- | --- | --- |
| 1 | 全置換に `symbol` が null・空文字・空白・要素そのものが null を含むと `ArgumentException`（保存も履歴も無い） | `WatchlistCycleFitTests`（T-10-1570） |
| 1 | 本番の組み立てで `{"symbol":null}` を含む `PUT /monitor/settings` は 400（500 ではない）で、`error` に理由。監視銘柄は変わらない | 同（T-10-1571） |
| 2 | 予算を既に超えた監視銘柄へ東証だけを足す置換は通る。米国と東証を足す置換は拒否し、文言の「収まらない追加」は米国の銘柄だけ | 同（T-10-1572） |
| 3 | 共有の追跡器を渡したクライアントは、他の送り手（企業ニュース）が直前に送った要求から 1 秒以内の「残りがあるのに拒否」を 4301 にしない。追跡器を共有しなければ従来どおり 4301 | `FinnhubDailyPremiseWithdrawnTests`（T-10-1573） |
| 3 | 情報収集の本番の組み立て（`InformationSourceFactory`）で、企業ニュースの要求の直後の現在値の 429（残りあり）は 4301 にならない | `FinnhubDailyEstimateFollowsWatchlistTests` または新規（T-10-1574） |
| 4 | 重複の拒否の文言に「保存済みの重複も 1 件ずつに減らして送れば適用される」 | `WatchlistCycleFitTests`（T-10-1458 の期待に追加） |

T-10-1575 以降は同時に進める #826 の PR（損切り手法の選択の監査の残り）が使う。

## 変異注入（予定）

- 項目 1 の検証を外す／空白を通す（`IsNullOrEmpty`）
- M7: 全置換の費用のかかる追加から米国の条件を外す
- 項目 3: クライアントが共有の追跡器を使わない／企業ニュースが刻まない／工場が別々の追跡器を渡す

## 完了の定義

- `dotnet test`（市場監視・共有・情報収集の該当テスト）と `dotnet format --verify-no-changes` が通る。
- 静的検査（trace ブロック・試験のトレーサビリティ・コミット規約）が通る。
