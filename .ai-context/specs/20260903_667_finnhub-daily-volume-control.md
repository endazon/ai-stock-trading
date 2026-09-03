---
title: Finnhub 日次総量の見積りと暫定上限超過の警告
type: spec
status: draft
related_ids: [FR-01, ADR-0020, IADR-0068, IADR-0224, IADR-0275]
author: claude (Claude Code)
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0031_finnhub-rate-limit-minute-confirmed-daily-open.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0020_datasource-tiering-and-fallback.md
---

# 仕様書: Finnhub 日次総量の見積りと暫定上限超過の警告

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/ai-stock-trading/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-01（情報収集）
- 関連計画 ADR: ADR-0031（Finnhub の分次上限は確定・日次は未確定。銘柄数を日次上限から逆算する統制は撤回しない）
  決定2〜4。§関連 ADR-0020（データソース階層化とフォールバック）
- 計画書リンク: `projects/ai-stock-trading/07_adr/ADR-0031_finnhub-rate-limit-minute-confirmed-daily-open.md`
  （隣接クローン参照）
- 起点 issue: [#667](https://github.com/endazon/ai-stock-trading/issues/667)

## 目的・背景

ADR-0031 決定2 は、IADR-0275 決定5（「監視銘柄数の絶対的な上限は新設しない」）の論拠が**分次の制約についてのみ
正しい**と訂正した。分次の自制レート（トークンバケット）は瞬間的な要求レートしか保証せず、1 日の総量
（監視銘柄数 × 1 巡回あたりの要求数 × 1 日の巡回回数）は別の制約であり、**銘柄数に正比例する**。

決定3 は、日次上限が未実測である間の**暫定手段**として、第三者観測「約 300 回/日」を計画上の前提値として
扱うと定めた。しかし決定3 が明記するとおり「**現在の実現手段は無い**」——日次総量を測る仕組みも、上限に
対する警告も配備されていない。本作業はこの欠落を埋める。

決定4 は、同一鍵を共有する全プロセスの自制レート・日次見積りを合算すると定めた。

## 対象範囲

- 対象:
  - 純関数 `FinnhubDailyVolumeEstimator`（`AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData`）:
    監視銘柄数 × 1 巡回あたりの要求数 × 1 日の巡回回数から日次要求見積りを算出し、暫定日次上限
    （`FinnhubDailyVolumeGuardOptions.ProvisionalDailyLimit`・既定 300）と比較する。複数プロセスの見積りを
    `ApiKeyGroup`（同一鍵の識別子）で束ねて合算する経路も持つ（ADR-0031 決定4 の意味論を固定するテスト用）。
  - 配線: 情報収集（`InformationSourceFactory`）と実市況 4 サービス（`MarketDataSourceFactory` 経由。
    `MarketMonitorService` / `RiskManagementService` / `ReportService` / `TradeDecisionService`）の各プロセス
    起動時（Options を消費する箇所）に自プロセスぶんの見積りを計算し、暫定上限超過なら警告ログ＋業務メトリクス
    （`ast.finnhub.daily_request_estimate` / `ast.finnhub.daily_request_limit_ratio_percent`）を出す。
    **送出は止めない**（統制は現時点では可視化のみ。ADR-0031 決定3 は強制ではなく暫定手段としての可視化を求める）。
  - introspection 自己申告: `GET /internal/introspection` の `Metrics`（新設の任意申告枠）へ
    `finnhub-daily-request-estimate` を載せる。
  - 設定点: `MarketData:Finnhub:EstimatedSymbolCount`（4 消費サービス共通。既定 0＝未申告＝挙動中立。
    実際の銘柄数・建玉数は DB・台帳の動的な値であり起動時に確定しないため運用者申告とする）、
    `Finnhub:ProvisionalDailyLimit`（情報収集・4 消費サービス共通。既定 300）。
  - `values.yaml`（既定は空＝挙動中立）・`values-local.yaml`（経路B デモ。AAPL 1 銘柄に合わせ
    `EstimatedSymbolCount=1`）・chart README・Grafana ダッシュボードへの反映。
- 対象外:
  - 日次上限の実測（未解消のまま。人間・運用側の残件。ADR-0031 決定3 の先行条件）。
  - `MaxMonitoredSymbols` 相当の絶対的な数値上限の新設（IADR-0275 決定5 が退けた設計を再導入しない。
    ADR-0031 決定3 は「妨げない」としているだけで「求める」わけではない）。
  - プロセス間のリアルタイム自動合算（同一鍵を共有する複数プロセスが稼働時に互いの見積りを突き合わせる
    仕組み）。各プロセスは自プロセスぶんの見積りのみを計算・自己申告する。複数プロセスを合算する意味論は
    純関数のテストで固定するに留め、実行時の自動集約（他プロセスの introspection を能動的に問い合わせて
    合算する等）は本作業の範囲外とする——過剰な抽象化を避け、ADR-0031 決定3 の「暫定手段」という位置づけに
    見合う最小の実装とする。
  - `.env.example` の追記: **本セッションはサンドボックスの権限設定により `.env.example` を読み書きできない**
    （guard-bash / ファイルアクセス双方でブロックされる）。代わりに `deploy/helm/ai-stock-trading/values.yaml` /
    `values-local.yaml` / chart README に設定点を明記した。`.env.example` への反映は別途人手または別セッションで
    行う必要がある（残件として issue #667 のフォローアップに残す）。

## 設計

### 見積り式

```
1 プロセスの日次要求見積り = 銘柄数（申告 or 実数） × 1 巡回・1 銘柄あたりの要求数 × 1 日の巡回回数
1 日の巡回回数 = floor(86400 / 巡回間隔秒)
```

- 情報収集: 銘柄数 = `Collection:Source:Finnhub:Symbols` の実数（厳密）。1 巡回あたりの要求数 = 有効化中の
  `finnhub` / `finnhub-news` の数（1 または 2。既存の `InformationSourceFactory.LogFinnhubQuota` と同じ数え方）。
  巡回間隔 = `Collection:PollIntervalSeconds`（既定 1800 秒）。
- 実市況 4 サービス: 銘柄数 = `MarketData:Finnhub:EstimatedSymbolCount`（運用者申告・既定 0）。
  1 巡回あたりの要求数 = 1（現在値取得のみ）。巡回間隔は各サービスの実際の巡回実装に合わせる
  （`MarketMonitorService`=`Monitor:PollIntervalSeconds` 既定 60 秒、他 3 サービスは
  `MarketData:RefreshIntervalSeconds` 既定 60 秒を保守的な仮定として使う——`ReportService`/`TradeDecisionService`
  は固定間隔ではなくイベント駆動〔報告書ドラフト生成時・判断サイクル起動時〕のため、巡回間隔は「その頻度で
  回り続けた場合の理論上限」という保守的な上振れ見積りである）。
- 暫定日次上限との比較: 見積り > 上限 なら `Verdict.Exceeds`。ちょうど上限なら `Verdict.Within`（超過ではない）。

### 合算（ADR-0031 決定4）

`FinnhubDailyVolumeEstimator.Evaluate(IReadOnlyCollection<ProcessVolume>, int)` は `ApiKeyGroup`
（同一鍵の識別子。生の鍵値は持たせない）でグルーピングし、グループごとに合算・判定する。鍵が別のグループは
合算しない。本作業の配線は各プロセスが自プロセスぶんのみを計算するため、この合算経路は現時点では
「将来の外部集約（例: 複数プロセスの introspection 値を人手または別ツールで束ねて呼ぶ）」に備えた
意味論の固定であり、実行時に自動では呼ばれない。

### 停止しない設計

ADR-0031 決定3 は統制を「監視銘柄数 × 巡回頻度からの日次要求量が上限を超えないこと」と定めるが、
現在の実現手段が無い状態から「警告ログ＋業務メトリクスによる可視化」へ進める段階であり、**確定した
数値上限による強制（送出停止）ではない**。超過を検知しても収集・現在値取得は継続する。

## 受け入れ基準

- [x] 純関数 `FinnhubDailyVolumeEstimator` が「ちょうど暫定上限」「超過」「複数プロセス合算（同一鍵）」
      「鍵が別なら合算しない」の境界を持つ
- [x] 情報収集・実市況 4 サービスの各プロセス起動時に日次要求見積りを計算する配線がある
- [x] 見積りが暫定上限（既定300）を超えると警告ログ＋業務メトリクスを出す。超えなければ出さない
- [x] 既定（`EstimatedSymbolCount=0`）は挙動中立（警告・メトリクスとも出ない）
- [x] 超過時も送出（収集・現在値取得）を止めない
- [x] `GET /internal/introspection` の自己申告に見積り値を載せる
- [x] `values.yaml`（既定は空＝挙動中立）・`values-local.yaml`（経路Bデモ）・chart README・Grafana ダッシュボード
      へ設定点・可視化を反映した
- [ ] `.env.example` への反映（サンドボックス制約により未実施。残件）
- [x] `MaxMonitoredSymbols` 相当の絶対的な数値上限は新設しない
- [x] 日次上限の実測は未解消のまま issue・IADR に残す

## テスト方針

- `FinnhubDailyVolumeEstimatorTests`（`AiStockTrading.Shared.Infrastructure.Tests`）: 純関数の境界
  （ちょうど暫定上限・超過・複数プロセス合算・鍵が別なら合算しない・負値の拒否・0 件合算）。
- `MarketDataSourceFactoryDailyVolumeTests`: 実市況側の配線（銘柄数未申告は警告・メトリクスとも無し／
  上限内は警告無しでメトリクスのみ／超過は警告＋メトリクス／例外を投げず送出を止めない）。
- `InformationSourceFactoryDailyVolumeTests`: 情報収集側の配線（銘柄未設定・finnhub 系ソース未有効は
  見積らない／finnhub のみ・finnhub+news の要求数の違い／超過時の警告）。

## 計画書との差異

- 差異: なし。ADR-0031 決定2〜4 に忠実に実装した。決定3 が明示的に許容する
  「`MaxMonitoredSymbols` を新設しないこと自体は妨げない」を踏襲し、運用者申告の構成値
  （`EstimatedSymbolCount`）による見積りに留めた。

## 未決事項

- 日次上限は未確定のまま（ADR-0031 決定3 の先行条件）。本作業の暫定上限（300）は第三者観測であり、
  実測ではない。実測後は `Finnhub:ProvisionalDailyLimit` を実測値で上書きする運用とする。
- `.env.example` への反映は本セッションのサンドボックス制約により未実施（上記「対象外」参照）。
