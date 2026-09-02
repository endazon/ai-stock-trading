---
title: Finnhub Free の実効レート制限の実測と監視銘柄数上限の逆算
type: spec
status: draft
related_ids: [FR-01, ADR-0020, IADR-0064, IADR-0068, IADR-0224]
author: claude (Claude Code)
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0020_datasource-tiering-and-fallback.md
  - planning:projects/ai-stock-trading/06_technical/02_datasource-candidates.md
---

# 仕様書: Finnhub Free の実効レート制限の実測と監視銘柄数上限の逆算

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/ai-stock-trading/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-01（情報収集）
- 関連 ADR: ADR-0020（データソース階層化とフォールバック）§結果 のフォローアップ
- 計画書リンク: `projects/ai-stock-trading/07_adr/ADR-0020_datasource-tiering-and-fallback.md`（隣接クローン参照）
- 起点 issue: [#566](https://github.com/endazon/ai-stock-trading/issues/566)（#336 の受け皿。実 API 疎通が要るため分離）

## 目的・背景

ADR-0020 は「Finnhub Free の実効レート制限を実測し、監視銘柄数の上限を確定する。公称 60 回/分と
日次上限の関係を一次ソースで再確認する」ことをフォローアップとして残した。IADR-0224 は受け皿として
`Collection:Source:Finnhub:DailyRequestLimit`（既定 null＝未実測）と `FinnhubQuotaCalculator.MaxWatchlistSymbols`
を実装したが、**実測は実 API キーでの試行が要るため後日**とし、値は焼き込まなかった。

本作業は、実クラスタ（namespace `ai-stock-trading`）の Secret `ast-secrets`（キー `finnhub-api-key`）を使い、
使い捨て Pod から実際に Finnhub `/quote` を段階的な頻度で呼び、429 が返り始める閾値と応答ヘッダ
（`X-Ratelimit-*`）を実測する。あわせて、1 銘柄・1 巡回あたりの Finnhub 呼び出し回数（情報収集＋実市況の
両方）を数え、監視銘柄数の上限に関する結論を出す。

## 対象範囲

- 対象:
  - 使い捨て Pod による Finnhub `/quote` の段階的頻度実測（429 閾値・応答ヘッダ・秒単位バーストの確認）
  - 情報収集（`InformationCollectionService`）と実市況（共有 `FinnhubQuoteClient`。4 消費サービス）の
    呼び出し回数の実装調査、および両者が**同一 Finnhub 鍵を共有し得る**構成上のリスクの定量化
  - 実測結果に基づく設定既定値の是正（`FinnhubMarketDataOptions.RequestsPerMinute` 等）
  - 実測結果の IADR 記録・planning への環流・`docs/blocked-tasks.md` の追随
- 対象外:
  - **日次上限の確定**（実測セッション内で持続的なブロックを観測できなかったため。継続観察が要る＝残件）
  - `Collection:Source:Finnhub:DailyRequestLimit` への具体値の設定（未確定のまま推測値を焼き込まない。IADR-0224 決定2 を維持）
  - moomoo 市況・日本株の現在値（本件は Finnhub 米国株 `/quote` のみ）

## 設計

### 実測方法

`deploy/opend/k8s/bootstrap-pod.yaml` を雛形に、`curlimages/curl:8.11.1`（本リポ既存の固定版。
`deploy/helm/ai-stock-trading/values.yaml` で使用中）による使い捨て Pod
（`finnhub-ratelimit-probe-566`。**コミットしない一時マニフェスト**）を作成し、以下を実行する。

1. **Phase1（バースト/連続実行）**: 追加の待機を入れず `GET /quote?symbol=AAPL` を最大 150 回連続実行し、
   各回の `http_code`・`X-Ratelimit-Limit`・`X-Ratelimit-Remaining`・`X-Ratelimit-Reset` を記録する。
   429 が 8 回連続したら早期終了する（無駄なクォータ消費を避ける）。
2. **Phase2（ウィンドウ復帰確認）**: 65 秒待機後に 2 回（5 秒間隔）プローブし、ウィンドウが完全リセット
   されるか（`Remaining` が満額へ戻るか）を確認する。
3. **Phase3/4（定常ペーシング確認）**: 65 秒のクールダウンを挟み、30 回/分・60 回/分の 2 ペースで
   各 60 秒間実行し、429 が出ないことを確認する。
4. API キーは Pod の環境変数（Secret `ast-secrets` の `finnhub-api-key`）から `X-Finnhub-Token` ヘッダで
   渡す（URL クエリに載せない・ログへ出さない）。完了後は `kubectl delete pod` で必ず削除する。

### 呼び出し回数の調査

- 情報収集（`InformationCollectionService`）: `Collection:Source:Finnhub:Symbols` の銘柄ごとに、
  `finnhub`（現在値・1 回）＋ `finnhub-news`（企業ニュース・1 回、有効時）＝最大 2 回/銘柄/巡回
  （`InformationSourceFactory.LogFinnhubQuota` が既に同じ前提で実装済み）。巡回間隔は
  `Collection:PollIntervalSeconds`（既定 1800 秒＝30 分、開場中 13 巡回/日の前提と整合）。
- 実市況（共有 `FinnhubQuoteClient`・`MarketData:Finnhub`）: `MarketMonitorService`（保有銘柄＋監視銘柄の
  巡回ごとに 1 回/銘柄）・`RiskManagementService`（`QuoteRefreshService`。保有銘柄の定期補充）・
  `TradeDecisionService`（判断サイクルの価格文脈）・`ReportService`（日報ドラフト生成時）の**4 サービス**が、
  各サービスに配られた `MarketData:Finnhub:RequestsPerMinute`（既定 10）で自制しながら呼ぶ
  （IADR-0068 決定4）。**IADR-0068 決定4 の「3 サービス」という記述は実装（4 サービス）と食い違っている**
  （後述「計画書との差異」）。

### 同一鍵共有リスクの確認

`values-local.yaml`（経路B・ローカル実行環境）が実際に配線している
`Collection:Source:Finnhub:ApiKey`（Secret キー `finnhub-api-key`）と
`MarketData:Finnhub:ApiKey`（Secret キー `marketdata-finnhub-api-key`）の値を、
**値を露出させずに** SHA-256 ハッシュで比較し、同一鍵かどうかを確認する
（`kubectl ... -o jsonpath | base64 -d | sha256sum`。生の値は一切標準出力・ログへ出さない）。

## 受け入れ基準

- [x] Finnhub `/quote` の実効レート制限を実測し、429 が返り始める閾値・応答ヘッダの意味（固定ウィンドウか
      ローリングか、`Reset` の単位）を確認した
- [x] 秒単位のバースト上限の有無を確認した（60 秒ウィンドウとは別の追加制約があるか）
- [x] 情報収集・実市況それぞれの 1 銘柄・1 巡回あたりの Finnhub 呼び出し回数を実装から数えた
- [x] 同一鍵共有時の合計自制レートが実測上限を超えないかを確認し、超える場合は是正した
- [ ] 監視銘柄数の絶対的な上限値を新設した（**実施しない。理由は「設計」および IADR-0275 決定5 を参照**）
- [x] 実測値を `/plan-feedback` で計画リポジトリへ環流した
- [x] `docs/blocked-tasks.md` に実測結果を追記した

## テスト方針

- `FinnhubMarketDataOptions` の既定値変更（`RequestsPerMinute` 10→5）を検証するユニットテストを追加する。
- 情報収集（既定 30/分）＋実市況 4 サービス（既定 × 4）の合計が実測上限（60/分）以下であることを固定する
  退行テストを `AiStockTrading.Shared.Infrastructure.Tests` に追加する（同一鍵共有時の再発防止）。
- 実 API を呼ぶ検証は本仕様書内の実測作業（手動 opt-in・使い捨て Pod）に限り、CI では実行しない
  （IADR-0049 と同じ切り分け）。

## 計画書との差異

- 差異: あり。**IADR-0068 決定4 は「情報収集 30 ＋ 市況 10 × 3 サービス = 60 回/分」としているが、
  実装済みの市況消費サービスは `MarketMonitorService`/`ReportService`/`RiskManagementService`/
  `TradeDecisionService` の**4 サービス**である**（3 ではない）。IADR-0068 は `.ai-context/` の凍結記録の
  ため本文は書き換えず、本作業の IADR-0275 で訂正を記録し、既定値を是正する。
- 実測値（公称 60 回/分・日次上限は第三者観測で約300回/日）を計画（ADR-0020・02_datasource-candidates）へ
  環流する（`/plan-feedback`）。

## 未決事項

- **日次上限は未確定のまま。** 実測セッション（累計約 159 回・約 11 分）ではブロックを観測できなかった。
  第三者観測（約 300 回/日）の裏取りには、より長時間・高頻度の実測か、本番運用ログの継続観察が要る。
  `Collection:Source:Finnhub:DailyRequestLimit` は引き続き未設定（null）のままとする。
