---
title: ログ・可観測性仕様書（AST）
type: observability-spec
status: draft
related_ids:
  - ADR-0006
  - NFR
  - IADR-0052
  - IADR-0094
author: endazon (with Claude Code)
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md"
---

# ログ・可観測性仕様書（AST）

> ログ・メトリクス・トレースの経路と、ローカル（経路B）での可観測性バックエンドの opt-in stand-up を定める。
> 起点: [ADR-0006](../../planning/projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md)（OTel/Prometheus/Loki）/
> [IADR-0094](../adr/IADR-0094_local-infra-observability-gitops.md)（#24）。

## 起点となる計画書（トレーサビリティ）

- 非機能要件（**NFR**）: 可観測性（メトリクス・ログ・トレース）・稼働率 99%（開場時間帯）
- 関連 ADR: [ADR-0006](../../planning/projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md)、実装は [IADR-0052](../adr/IADR-0052_k8s-helm-chart-shared-infra.md)（OTLP 送出）

## 送出経路（テレメトリの流れ）

```
AST 10 Worker  --OTLP(gRPC :4317)-->  otel-collector  --export-->  Prometheus (metrics)
(Otlp__Endpoint)                       (共有 platform-infra)          Loki       (logs)
                                                                      Tempo      (traces)
                                                          --> Grafana（可視化・datasource: Prometheus/Loki/Tempo）
```

- AST サービスは OTLP エンドポイント（`Otlp__Endpoint`＝`http://otel-collector:4317`・[IADR-0052](../adr/IADR-0052_k8s-helm-chart-shared-infra.md)）
  へメトリクス・ログ・トレースを push する。**AST 側に Prometheus scrape の口は持たない**（push モデル）。
- otel-collector（MSP `platform-infra` の共有 infra）が受けて各バックエンドへ export する。
  dev の既定は debug exporter（標準出力のみ・外部送信なし）。実バックエンド連携は**下記 opt-in**。

## メトリクス（主なもの・OTel 由来名）

| 指標 | メトリクス（Prometheus 出力名） | 用途 |
| --- | --- | --- |
| HTTP リクエストレート | `http_server_duration_milliseconds_count` | 各 Worker の負荷・可用性 |
| HTTP エラー率 | 同上（`http_status_code=~"5.."`） | 障害検知 |
| HTTP レイテンシ | `http_server_duration_milliseconds_bucket`（P99） | 応答性 |
| .NET ランタイム | `process_runtime_dotnet_*` | CPU・GC・メモリ |

> 実際の出力名は otel-collector の Prometheus exporter 構成に依存する。ダッシュボードのクエリは
> [`deploy/observability/dashboards/ai-stock-trading-overview.json`](../../deploy/observability/dashboards/ai-stock-trading-overview.json) を参照。

## ログ・トレース

- **ログ**: 構造化ログを OTLP で送出。Loki の `{namespace="ai-stock-trading"}` で参照する。個人情報・秘匿値は
  ログへ流さない（LLM プロンプトの全量ログは既定オフ・[IADR-0061](../adr/IADR-0061_llm-production-wiring.md)）。
- **トレース**: サービス間（s2s）呼び出しは Tempo で追跡する。Grafana の Trace→Logs 相関を有効化済み（MSP datasource）。

## ローカル（経路B）での可観測性バックエンド stand-up（opt-in）

- Prometheus/Grafana/Loki/Tempo の **k8s manifest と `k8s-local-up.sh` の env ゲートは MSP 側の共有 overlay**
  （別 PR・MSP/IADR-0077）で追加する。既定オフ＝現行の debug exporter のまま（外部送信なし）。
- 有効化後、Grafana へ AST ダッシュボード（[`deploy/observability/dashboards/ai-stock-trading-overview.json`](../../deploy/observability/dashboards/ai-stock-trading-overview.json)）を
  provisioning または手動 import する（datasource: `Prometheus`/`Loki`/`Tempo`）。

## Tier 3（対象外）

- 稼働率 99%（開場時間帯）の**実測**・アラート閾値の実運用調整は Hetzner 実環境依存（[`docs/infra/infra.md`](../infra/infra.md) の Tier 境界）。
