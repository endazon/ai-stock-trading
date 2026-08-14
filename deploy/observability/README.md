# deploy/observability/ — AST 可観測性資産（opt-in）

> 起点: [ADR-0006](../../planning/projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md)（OTel/Prometheus/Loki 可観測性）/ [IADR-0094](../../docs/adr/IADR-0094_local-infra-observability-gitops.md)（#24）

AST サービス（10 Worker）は OTLP（`Otlp__Endpoint`→otel-collector）でメトリクス・ログ・トレースを送出する
（[IADR-0052](../../docs/adr/IADR-0052_k8s-helm-chart-shared-infra.md)）。本ディレクトリは AST 固有の**可観測性資産**を置く。
バックエンド（Prometheus/Grafana/Loki/Tempo）の**実 stand-up は MSP 側の共有 overlay**（別 PR・MSP/IADR-0077）で行う。

## 構成

| ファイル | 役割 |
| --- | --- |
| `dashboards/ai-stock-trading-overview.json` | AST 10 Worker の RPS/エラー率/P99/CPU/ログを俯瞰する Grafana ダッシュボード |

## 使い方

- **ローカル（経路B）**: MSP の Grafana provisioning（`deploy/grafana/provisioning/dashboards`）が本 JSON を
  マウントするか、Grafana UI から手動 import する（datasource は `Prometheus`/`Loki`）。詳細な経路と opt-in 手順は
  [`../../docs/observability/observability.md`](../../docs/observability/observability.md)。
- **メトリクス名**は otel-collector が Prometheus へ出力する OTel 由来名（`http_server_duration_milliseconds_*`・
  `process_runtime_dotnet_*`）に依存する。exporter 構成が異なる場合はクエリを読み替える。

Tier 3（Hetzner 実デプロイ・実測・稼働率99%）は本 PR の対象外。境界は [`../../docs/infra/infra.md`](../../docs/infra/infra.md) を参照。
