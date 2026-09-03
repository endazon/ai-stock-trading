---
title: IADR-0294 Finnhub の日次総量を運用者申告から見積もり、暫定上限超過を警告する（強制はしない）
type: impl-adr
status: Accepted
related_ids: [FR-01, ADR-0020, IADR-0068, IADR-0224, IADR-0275]
author: claude (Claude Code)
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0031_finnhub-rate-limit-minute-confirmed-daily-open.md
related_specs:
  - ../specs/20260903_667_finnhub-daily-volume-control.md
---

# IADR-0294: Finnhub の日次総量を運用者申告から見積もり、暫定上限超過を警告する（強制はしない）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は planning へ issue で環流する（`feedback.yml` テンプレート）。

- 状態: Accepted
- 日付: 2026-09-03
- 決定者: claude（起票 [#667](https://github.com/endazon/ai-stock-trading/issues/667)。計画 ADR-0031 決定2〜4 への追随）

## 起点・関連

- 関連する計画書 ID: FR-01（情報収集）、ADR-0031（Finnhub の分次上限は確定・日次は未確定。銘柄数を
  日次上限から逆算する統制は撤回しない）決定2〜4
- 対象 Issue: [#667](https://github.com/endazon/ai-stock-trading/issues/667)
- 関連する実装仕様書: [`.ai-context/specs/20260903_667_finnhub-daily-volume-control.md`](../specs/20260903_667_finnhub-daily-volume-control.md)
- 関連 IADR: [IADR-0275](IADR-0275_finnhub-effective-rate-limit-measurement.md)（分次の実測。決定5「監視銘柄数の
  絶対的な上限は新設しない」の論拠は ADR-0031 決定2 により**分次に限定される**と訂正済み）、
  [IADR-0224](IADR-0224_rate-limits-as-settings-and-unmeasured-daily-quota.md)（推測値を実測として焼き込まない原則）、
  [IADR-0068](IADR-0068_live-quote-feed-finnhub-extraction.md)（実市況共有クライアント）

## コンテキストと課題

計画 ADR-0031 は、IADR-0275 決定5 の論拠（トークンバケットは銘柄数に依らず一定の自制レートを保証する）が
**分次の制約についてのみ正しく、日次の総量には当たらない**と訂正した。1 日の要求数は
「監視銘柄数 × 1 巡回あたりの要求数 × 1 日の巡回回数」で決まり、銘柄数に正比例するため、
**「監視銘柄数を日次上限から逆算する」統制は撤回しない**（ADR-0031 決定2）。

決定3 は、日次上限が未実測である間の**暫定手段**として第三者観測「約 300 回/日」を計画上の前提値として
扱うと定め、同時に「現在の実現手段は無い（日次総量を測る仕組みも、上限に対する警告も配備されていない）」
と明記した。本 IADR はこの欠落を埋める実装判断を記録する。

## 検討した選択肢

1. **監視銘柄数（運用者申告）× 巡回頻度から見積もり、警告ログ＋業務メトリクスで可視化する**（採用）
2. `MaxMonitoredSymbols` のような**絶対的な数値上限**を新設し、超過を拒否（起動失敗・機能停止）する
   — **却下**。IADR-0275 決定5 が退けた設計（「銘柄数さえ上限内なら安全という誤った安心感」）を、
   ADR-0031 決定3 は「新設しないこと自体は妨げない」としており、日次上限が未実測のまま数値で強制すると、
   暫定値（300）の保守性の誤差がそのまま機能停止という重い結果に直結する。ADR-0031 決定3 が求めるのは
   統制であって特定の実現手段（強制停止）ではない。
3. 実際の監視銘柄数・保有建玉数を DB・台帳から起動時に読み取って見積もる（申告不要の自動算出）
   — **一部採用（情報収集のみ）**。情報収集（`InformationCollectionService`）は監視銘柄が静的構成
   （`Collection:Source:Finnhub:Symbols`）のため厳密に算出できる。一方、実市況 4 サービス
   （`MarketMonitorService`/`RiskManagementService`/`ReportService`/`TradeDecisionService`）が扱う
   銘柄数・建玉数は DB・台帳の動的な値であり、Options 検証の時点（DI 構築時）でこれらへ安全に
   アクセスする経路が無い（EF DbContext は scoped、Options 解決は起動同期パス）。動的取得の配線を
   新設するのは計画外の大規模な変更になるため採らず、運用者申告（設定値）で代替する。
4. プロセス間の見積りをリアルタイムに自動集約する（他プロセスの introspection を能動的に問い合わせて
   合算する集約サービスを新設する） — **却下（現時点）**。ADR-0031 決定3 は「現在の実現手段が無い」を
   解消する最小の一歩を求めており、集約サービスの新設は計画外の過剰な抽象化に当たる。各プロセスは
   自プロセスぶんの見積りを計算・自己申告するに留め、複数プロセスを跨ぐ合算の**意味論**（同一鍵は
   合算・鍵が別なら合算しない）は純関数として実装しテストで固定するが、実行時の自動集約は呼ばない。

## 決定

### 1. 純関数 `FinnhubDailyVolumeEstimator` を新設する（`AiStockTrading.Shared.Infrastructure`）

`ProcessVolume`（プロセス名・鍵グループ識別子・銘柄数・1巡回1銘柄あたりの要求数・1日の巡回回数）から
日次要求見積りを算出し、暫定日次上限（`FinnhubDailyVolumeGuardOptions.ProvisionalDailyLimit`・既定 300）
と比較して `Within`/`Exceeds` と超過率を返す。複数の `ProcessVolume` を `ApiKeyGroup` でグルーピングして
合算する経路（ADR-0031 決定4 の意味論）も持つ。1 日の巡回回数は `floor(86400 / 巡回間隔秒)` で、
取引時間帯に限る補正はしない——各サービスの休場中スキップの有無は呼び出し側の責務であり、本関数は
「間隔どおりに回り続けた場合の理論上限」という保守的な上振れ見積りを返す。

### 2. 銘柄数は、情報収集は実数、実市況 4 サービスは運用者申告（既定 0）とする

`FinnhubMarketDataOptions.EstimatedSymbolCount`（既定 **0**）を新設し、実市況 4 サービス共通の
`MarketData:Finnhub` 節に置く。**既定 0 は「未申告」であり、日次見積りへ寄与しない**（挙動中立）。
実際の監視銘柄数・保有建玉数に近い値を運用者が明示したときだけ見積りが有効になる。

情報収集は `Collection:Source:Finnhub:Symbols` の実配列長をそのまま使う（申告不要・厳密）。

### 3. 超過は警告ログ＋業務メトリクスに留め、送出を止めない

暫定上限（既定 300）を超えても収集・現在値取得は継続する。業務メトリクス
`ast.finnhub.daily_request_estimate`（Gauge・見積り値）と
`ast.finnhub.daily_request_limit_ratio_percent`（Gauge・上限に対する比率%）を新設し、
Grafana ダッシュボード「統制: Finnhub 日次要求見積り」から読めるようにする。

配線は各サービスが `IMarketDataSource` を組み立てる箇所（`MarketDataSourceFactory.Create` の
呼び出し元。情報収集は `InformationSourceFactory.Create` の呼び出し元）に置く——これは既存の
Finnhub クォータ計算（`FinnhubQuotaCalculator`）・レート予算の合成が行われている場所と同じであり、
新しい Options 検証の場所を増やさない。

### 4. introspection 自己申告へ載せる（`IntrospectionBuilder.AddMetric` を新設）

`ServiceIntrospectionDto` にポート選択とは別枠の `Metrics`（`MetricSelectionDto` の列。既定空）を追加し、
`IntrospectionBuilder.AddMetric(name, value)` で任意の数値等の自己申告を足せるようにした（既存の
`AddPort`/`AddPortFromBaseUrl` と同じ形）。各プロセスは自プロセスぶんの日次見積り値を
`finnhub-daily-request-estimate` として `GET /internal/introspection` へ載せる。**複数プロセスの
値を跨いで合算する仕組みは配備しない**（決定「検討した選択肢」4 を参照）——将来、人手または別ツールが
5 プロセスぶんの introspection を読んで合算する運用は妨げない。

### 5. `MaxMonitoredSymbols` 相当の絶対的な数値上限は新設しない

IADR-0275 決定5 が示した懸念（「銘柄数さえ上限内なら安全という誤った安心感」）は、ADR-0031 決定3 も
明示的に踏襲している——「実装が `MaxMonitoredSymbols` という設定値を新設しないこと自体は妨げない」。
本 IADR は見積り・可視化のみを実装し、数値上限による強制（起動失敗・機能停止）は導入しない。

## 理由

- **統制は「監視銘柄数 × 巡回頻度からの日次要求量が上限を超えないこと」**（ADR-0031 決定3）であり、
  その実現手段は「見積もりを可視化し、超過に気づけるようにすること」で足りる。暫定上限（300）自体が
  実測ではなく第三者観測であるため、これを理由に機能を止めると、保守的な仮定の誤差がそのまま
  可用性の低下に直結し、決定3 が想定する「日次上限の実測を先行条件とする」判断を運用者から奪う。
- 動的な銘柄数（DB・台帳）を Options 検証の時点で厳密に取得する経路は無く、新設するコストは
  ADR-0031 決定3 が求める「暫定手段」の範囲を超える（過剰な抽象化）。運用者申告（既定 0＝中立）で
  代替することで、実装コストを最小に保ちつつ「現在の実現手段が無い」という欠落を埋める。
- プロセス間の自動集約は、同一鍵共有の運用が「鍵を分ける」選択（ADR-0031 決定4 が明示的に許す）で
  そもそも不要になり得るため、常設の集約基盤を先に作る必然性が無い。

## 結果

- 良い影響: ADR-0031 決定3 が指摘した「現在の実現手段が無い」状態が解消される。既定は完全に挙動中立
  （`EstimatedSymbolCount=0`）であり、既存の全テスト・既定描画に影響しない。
- 悪い影響・トレードオフ: 実市況 4 サービスの見積りは運用者申告に依存するため、申告を怠ると
  「日次要求量が実際には暫定上限を超えているのに警告が出ない」偽陰性が起こり得る（既定 0 は安全側に
  倒すため false negative になる。統制の実効性は運用者の申告精度に依存する）。
- 残存リスク: 日次上限の実測は未解消のまま（ADR-0031 決定3 の先行条件。人間・運用側の残件）。
  プロセス間の自動合算は配備しないため、同一鍵を共有する複数プロセスの合計は人手で足し合わせる必要がある。
- フォローアップ:
  1. 日次上限の実測（実環境が要る。ADR-0031 決定3 の先行条件）。
  2. `.env.example` への設定点追記——本 PR の作業セッションはサンドボックスの権限設定により
     `.env.example` の読み書きができなかった（guard-bash / ファイルアクセスの双方でブロック）。
     `deploy/helm/ai-stock-trading/values.yaml` / `values-local.yaml` / chart README には反映済み。

## 関連

- Supersedes: なし（IADR-0275 決定5 の本文は変更しない。ADR-0031 決定2 による射程限定〔分次のみ〕は
  計画側で既に記録済みであり、本 IADR はその上に「日次総量の見積り」という新機能を追加するもの）
- Superseded by: なし
- 計画への環流: なし（本 IADR は計画 ADR-0031 の決定2〜4 に忠実な実装であり、計画側への新たな
  指摘・差異は無い）
