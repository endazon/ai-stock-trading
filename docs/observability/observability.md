---
title: ログ・可観測性仕様書（AST）
type: observability-spec
status: draft
created: 2026-07-19
updated: 2026-09-04
author: endazon (with Claude Code)
---
<!-- trace:
ids: [NFR-01, NFR-02, NFR-03, NFR-07]
adrs: [ADR-0006]
iadrs: [IADR-0052, IADR-0061, IADR-0094, IADR-0255, IADR-0307, MSP:IADR-0077]
specs: [20260828_287_business-metrics-and-dashboards, 20260904_689_nfr-01-02-end-to-end-latency-metrics]
issues: [#24, #287, #689]
-->


# ログ・可観測性仕様書（AST）

> ログ・メトリクス・トレースの経路と、ローカル（経路B）での可観測性バックエンドの opt-in stand-up を定める。
> 起点は稼働環境として Hetzner を採る計画 ADR（OTel/Prometheus/Loki）である。
> ローカル（経路B）の Vault 秘匿参照・可観測性・GitOps は AST リポ内の opt-in manifest／docs として整備し、
> 共有スタックの stand-up は MSP 側へ分離する。

## 本書が受け持つ範囲

- 非機能要件: 可観測性（メトリクス・ログ・トレース）・稼働率 99%（開場時間帯）
- 関連する計画 ADR: 稼働環境として Hetzner を採用する決定。実装側は「AST の k8s デプロイは Helm chart とし、共有インフラは MSP の platform-infra を参照する」（OTLP 送出）に従う

## 送出経路（テレメトリの流れ）

```
AST 10 Worker  --OTLP(gRPC :4317)-->  otel-collector  --export-->  Prometheus (metrics)
(Otlp__Endpoint)                       (共有 platform-infra)          Loki       (logs)
                                                                      Tempo      (traces)
                                                          --> Grafana（可視化・datasource: Prometheus/Loki/Tempo）
```

- AST サービスは OTLP エンドポイント（`Otlp__Endpoint`＝`http://otel-collector:4317`。共有インフラは MSP の platform-infra を参照する）
  へメトリクス・ログ・トレースを push する。**AST 側に Prometheus scrape の口は持たない**（push モデル）。
- otel-collector（MSP `platform-infra` の共有 infra）が受けて各バックエンドへ export する。
  dev の既定は debug exporter（標準出力のみ・外部送信なし）。実バックエンド連携は**下記 opt-in**。

## メトリクス（技術指標・OTel 由来名）

| 指標 | メトリクス（Prometheus 出力名） | 用途 |
| --- | --- | --- |
| HTTP リクエストレート | `http_server_duration_milliseconds_count` | 各 Worker の負荷・可用性 |
| HTTP エラー率 | 同上（`http_status_code=~"5.."`） | 障害検知 |
| HTTP レイテンシ | `http_server_duration_milliseconds_bucket`（P99） | 応答性 |
| .NET ランタイム | `process_runtime_dotnet_*` | CPU・GC・メモリ |

> 実際の出力名は otel-collector の Prometheus exporter 構成に依存する。ダッシュボードのクエリは
> [`deploy/observability/dashboards/ai-stock-trading-overview.json`](../../deploy/observability/dashboards/ai-stock-trading-overview.json) を参照。

## メトリクス（業務指標・本システムが自前で計上する）

技術指標だけでは「**事後に追える**」（ログ・トレース）状態にしかならず、「**異常に気づける**」状態にならない。
取引サイクルが止まっても、統制が空回りしていても、費用が上限に迫っても、技術指標の側には何も現れないためである。
業務指標は Meter `AiStockTrading.Business` から出す。

| 区分 | Prometheus 系列 | 主なタグ | 何が見えるか |
| --- | --- | --- | --- |
| 取引サイクル | `ast_information_items_collected_total` | — | 収集件数。**空巡回も 0 として出す**（「回って 0 件」と「止まっている」を区別するため） |
| 取引サイクル | `ast_trade_cycle_decisions_total` | `action` / `trigger` | 判断回数と buy / sell / 見送りの内訳 |
| 取引サイクル | `ast_trade_cycle_decision_duration_ms_*` | `trigger` | 判断レイテンシ（ヒストグラム）。**1 サービス内の判断 1 回**であり、端点間ではない |
| 取引サイクル | `ast_trade_cycle_order_completion_latency_ms_*` | `trigger` | **起点イベント → 発注完了**の端点間所要（ヒストグラム）。価格変動検知起点は `trigger=price-movement` の系列で読む（目標 5 分＝300,000 ms） |
| 取引サイクル | `ast_trade_cycle_record_completion_latency_ms_*` | `trigger` | **起点イベント → 記録完了**（監査台帳へ記録した時点）の端点間所要。定時サイクルは `trigger=scheduled` の系列で読む（目標 10 分＝600,000 ms） |
| 取引サイクル | `ast_trade_cycle_latency_unobserved_total` | `stage` / `reason` | 🔴 **端点間の所要を確定できなかった件数。**起点を持たない注文（利用者の手仕舞い・維持証拠金の自動縮小・約定追跡の後追い）や時計ずれで負になった区間はここへ出し、**ヒストグラムには 1 件も入れない**（0 ms を入れると目標を満たしているように見えるため） |
| 統制 | `ast_risk_screenings_total` | `outcome` | 発注前審査。**承認も拒否も数える**（拒否だけを数えると「違反 0 件」と「審査が動いていない」を区別できない） |
| 統制 | `ast_risk_rejections_total` | `reason` | 見送り理由の内訳（上限超過・緊急停止・一時停止・禁止銘柄ほか） |
| 発注 | `ast_order_executions_total` | `status` / `provider` | 発注結果と発注先 |
| 発注 | `ast_order_dispatch_forgone_total` | `reason` | 発注に**届いていない**見送り。ブローカーの拒否（`status=Rejected`）と混ぜない |
| 費用 | `ast_llm_cost_jpy_total` | `category` | LLM 費用（上限対象 `Llm` / 対象外 `LlmUncapped`） |
| 費用 | `ast_llm_cost_limit_ratio_percent` | — | 月次上限に対する比率。80 で間隔延長・100 で停止 |

### 系列名の規約と乖離の防止

- **系列名の単一情報源はコード側**（`backend/Shared/AiStockTrading.Shared.Contracts/Observability/BusinessMetricNames.cs`）である。
- 計器には `unit` を与えない。OTel の Prometheus 変換は `unit` を名前へ接尾するため、与えると変換規則が単位表に
  依存して増える。単位は名前へ埋めてある（`_ms` / `_jpy` / `_percent`）。結果として変換規則は
  「ドットを `_` へ」＋「Counter は `_total`」＋「Histogram は `_bucket`/`_count`/`_sum`」の 3 つで閉じる。
- 🔴 **系列名がずれたパネルはエラーを出さず、空のグラフを描く。空のグラフは「異常が起きていない」と読める。**
  そのため `node scripts/check-observability-assets.js` が CI で、ダッシュボードとコードの**双方向の一致**
  （実在しない系列を引いていないか／誰も引いていない計器が無いか）を検査する。
- **タグの基数を業務量に比例させない。** 銘柄・注文 ID・判断 ID はタグにしない。銘柄単位の追跡はログとトレースが担う。
- **端点間レイテンシのバケット境界は明示する。** OTel の既定境界は上限 10,000 ms であり、5 分・10 分の
  目標値はすべて `+Inf` バケットへ落ちて分位点も超過件数も読めない。境界は
  `AddAiStockTradingObservability` の View で与え、**目標値そのもの（300,000 / 600,000）を境界に置く**
  ——超過件数が隣り合うバケットの引き算で読め、分位点の補間に頼らずに済む。
- 🔴 **「測れなかった」を 0 として記録しない。** 端点間の計測は、起点（どの契機で・いつ始まったか）を
  イベントが運んでこなければ確定できない。確定できない件は専用のカウンタへ理由つきで出し、
  ヒストグラムへは入れない。**未観測を 0 ms として混ぜると、目標を満たしているように見える。**

### 既定では外部へ送らない

計装は常に有効である（計器は in-process の Meter へ記録する）。**外部へ出るかどうかは otel-collector の
exporter 構成が決める**。dev の既定は `debug`（標準出力のみ・外部送信なし）であり、本システム側に
新しい送出先は無い。実バックエンドへ流すのは下記の opt-in stand-up 後である。

## ログ・トレース

- **ログ**: 構造化ログを OTLP で送出。Loki の `{namespace="ai-stock-trading"}` で参照する。個人情報・秘匿値は
  ログへ流さない（実 LLM 接続の安全既定により、LLM プロンプトの全量ログは既定オフ）。
- **トレース**: サービス間（s2s）呼び出しは Tempo で追跡する。Grafana の Trace→Logs 相関を有効化済み（MSP datasource）。

## ローカル（経路B）での可観測性バックエンド stand-up（opt-in）

- Prometheus/Grafana/Loki/Tempo の **k8s manifest と `k8s-local-up.sh` の env ゲートは MSP 側の共有 overlay**
  （別 PR。基盤側の共有 overlay の決定に従う）で追加する。既定オフ＝現行の debug exporter のまま（外部送信なし）。
- 有効化後、Grafana へ AST ダッシュボード（技術指標
  [`deploy/observability/dashboards/ai-stock-trading-overview.json`](../../deploy/observability/dashboards/ai-stock-trading-overview.json) と
  業務指標 [`deploy/observability/dashboards/ai-stock-trading-business.json`](../../deploy/observability/dashboards/ai-stock-trading-business.json)）を
  provisioning または手動 import する（datasource: `Prometheus`/`Loki`/`Tempo`）。投入手順は
  [`deploy/observability/README.md`](../../deploy/observability/README.md)。**Grafana の UI で直接編集しない**
  —— リポジトリの JSON が正であり、UI 側の変更は次の import で消える。

## 実環境でしか確認できない残件

本書が定める系列は、コード側で**実際に値が刻まれること**まではテストで固定してある
（計器の発火を `MeterListener` で観測している）。次の 3 点は**実バックエンドが要るため未確認**である。
達成済みとして読まないこと。

| 残件 | 内容 |
| --- | --- |
| Prometheus 疎通 | 業務指標が実際に Prometheus へ現れ、取引サイクル 1 巡回で値が動くこと |
| 基盤側の追随 | 基盤リポジトリを develop へ追随させ、LLM 拒否率の計上を消費すること（本リポジトリ外の作業） |
| scrape target の切り分け | scrape target `otel-collector:8888` が down している件。collector は `prometheusremotewrite` で送るためランタイム系は到達しており**実害は無い**と整理済みだが、実機が無いため切り分けは未実施 |

> **閾値（「判断が N 分間 0 件なら異常」等）は実測してから決める。** 実測が無いまま閾値を置くと、
> 最初のアラートで狼少年になり、以後の本物も無視される。

## Tier 3（対象外）

- 稼働率 99%（開場時間帯）の**実測**・アラート閾値の実運用調整は Hetzner 実環境依存（[`docs/infra/infra.md`](../infra/infra.md) の Tier 境界）。
