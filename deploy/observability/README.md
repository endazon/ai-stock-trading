# deploy/observability/ — AST 可観測性資産（opt-in）

> 起点: ADR-0006（計画リポ）（OTel/Prometheus/Loki 可観測性）/ [IADR-0094](../../.ai-context/adr/IADR-0094_local-infra-observability-gitops.md)（#24）

AST サービス（10 Worker）は OTLP（`Otlp__Endpoint`→otel-collector）でメトリクス・ログ・トレースを送出する
（[IADR-0052](../../.ai-context/adr/IADR-0052_k8s-helm-chart-shared-infra.md)）。本ディレクトリは AST 固有の**可観測性資産**を置く。
バックエンド（Prometheus/Grafana/Loki/Tempo）の**実 stand-up は MSP 側の共有 overlay**（別 PR・MSP/IADR-0077）で行う。

## 構成

| ファイル | 役割 |
| --- | --- |
| `dashboards/ai-stock-trading-overview.json` | AST 10 Worker の RPS/エラー率/P99/CPU/ログを俯瞰する Grafana ダッシュボード（**技術指標**） |
| `dashboards/ai-stock-trading-business.json` | 取引サイクル・統制・発注・費用を 1 画面で見る Grafana ダッシュボード（**業務指標**。#287） |

### 業務ダッシュボードが引く系列（#287 / IADR-0255）

業務指標は AST が自前で計上する（OTel の `Meter` 名は `AiStockTrading.Business`）。系列名の単一情報源は
コード側の `backend/Shared/AiStockTrading.Shared.Contracts/Observability/BusinessMetricNames.cs` である。

| Prometheus 系列 | 主なタグ | 何が見えるか |
| --- | --- | --- |
| `ast_information_items_collected_total` | — | 収集件数（サイクルの起点が動いているか。**空巡回も 0 として出る**） |
| `ast_trade_cycle_decisions_total` | `action` / `trigger` | 判断回数と buy / sell / 見送りの内訳 |
| `ast_trade_cycle_decision_duration_ms_*` | `trigger` | 判断レイテンシ（ヒストグラム） |
| `ast_risk_screenings_total` | `outcome` | 発注前審査（**承認も拒否も数える**） |
| `ast_risk_rejections_total` | `reason` | 見送り理由の内訳 |
| `ast_order_executions_total` | `status` / `provider` | 発注結果と発注先 |
| `ast_order_dispatch_forgone_total` | `reason` | 発注に**届いていない**見送り（ブローカーの拒否とは別） |
| `ast_llm_cost_jpy_total` | `category` | LLM 費用（上限対象 `Llm` / 対象外 `LlmUncapped`） |
| `ast_llm_cost_limit_ratio_percent` | — | 月次上限に対する比率（80 で間隔延長・100 で停止） |

> **接尾辞は otel-collector の Prometheus 変換に依存する**（`add_metric_suffixes` 既定 true を前提とする）。
> コード側の計器には `unit` を与えていないため、変換は「ドットを `_` へ」＋「Counter は `_total`」＋
> 「Histogram は `_bucket`/`_count`/`_sum`」の 3 規則で閉じる。
>
> 🔴 **系列名がずれたパネルはエラーを出さず、空のグラフを描く。空のグラフは「異常が起きていない」と読める。**
> そのため `node scripts/check-observability-assets.js` が CI で、ダッシュボードが引く系列とコード側の
> レジストリの**双方向の一致**（実在しない系列を引いていないか／誰も引いていない計器が無いか）を検査する。
> **ダッシュボードを編集したら、このコマンドをローカルでも走らせること。**

## 使い方

- **ローカル（経路B）**: MSP の Grafana provisioning（`deploy/grafana/provisioning/dashboards`）が本 JSON を
  マウントするか、Grafana UI から手動 import する（datasource は `Prometheus`/`Loki`）。詳細な経路と opt-in 手順は
  [`../../docs/observability/observability.md`](../../docs/observability/observability.md)。
- **投入手順（手動 import の場合）**:
  1. Grafana へログインし、Dashboards → New → **Import** を開く。
  2. 本ディレクトリの JSON を貼り付ける（またはファイルを選ぶ）。`uid` は JSON が持つ値をそのまま使う
     （`ai-stock-trading-overview` / `ai-stock-trading-business`）。**uid を変えると再投入で別物が増える。**
  3. datasource に `Prometheus`（業務ダッシュボードは Prometheus のみ）を割り当てて Import する。
  4. 更新するときは**同じ uid へ再 import する**（Grafana は uid で同一性を決める）。
     🔴 **Grafana の UI で直接編集しない** —— 本リポジトリの JSON が正であり、UI 側の変更は次の import で消える。
- **技術指標のメトリクス名**は otel-collector が Prometheus へ出力する OTel 由来名
  （`http_server_duration_milliseconds_*`・`process_runtime_dotnet_*`）に依存する。exporter 構成が異なる場合は
  クエリを読み替える。**業務指標**（`ast_*`）は AST が自前で計上するため上表が正である。
- **既定では外部へ送らない。** 計装は常に有効だが、dev の otel-collector は metrics を `debug`
  （標準出力のみ）にしか出さない（IADR-0094 の opt-in の作法）。実バックエンドへ流すのは opt-in の stand-up 後である。

Tier 3（Hetzner 実デプロイ・実測・稼働率99%）は本 PR の対象外。境界は [`../../docs/infra/infra.md`](../../docs/infra/infra.md) を参照。
