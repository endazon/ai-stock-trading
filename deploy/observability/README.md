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
| `alerts/ai-stock-trading-alerts.yaml` | Prometheus のアラートルール（`PrometheusRule`。**人が見ていなくても働く側**。#891 / #942） |

### 業務ダッシュボードが引く系列（#287 / IADR-0255）

業務指標は AST が自前で計上する（OTel の `Meter` 名は `AiStockTrading.Business`）。系列名の単一情報源は
コード側の `backend/Shared/AiStockTrading.Shared.Contracts/Observability/BusinessMetricNames.cs` である。

| Prometheus 系列 | 主なタグ | 何が見えるか |
| --- | --- | --- |
| `ast_information_items_collected_total` | — | 収集件数（サイクルの起点が動いているか。**空巡回も 0 として出る**） |
| `ast_trade_cycle_decisions_total` | `action` / `trigger` | 判断回数と buy / sell / 見送りの内訳 |
| `ast_trade_cycle_decision_skips_total` | `reason` / `trigger` | **見送りの理由**の内訳（#891）。上の `action=no-trade` を**置き換えない**——1 回の見送りで両方が 1 ずつ増える |
| `ast_trade_cycle_decision_duration_ms_*` | `trigger` | 判断レイテンシ（ヒストグラム） |
| `ast_risk_screenings_total` | `outcome` | 発注前審査（**承認も拒否も数える**） |
| `ast_risk_rejections_total` | `reason` | 見送り理由の内訳 |
| `ast_risk_capital_baseline_reads_total` | `outcome` | 統制上限の**基準資金を読んだ帰結**（#889）。🔴 `SuppliedWithGap` は「値は返っているが直前の取引日の観測が届いていない」＝**古い分母で統制が回っている**印である |
| `ast_order_executions_total` | `status` / `provider` | 発注結果と発注先 |
| `ast_order_dispatch_forgone_total` | `reason` | 発注に**届いていない**見送り（ブローカーの拒否とは別） |
| `ast_order_drift_adoption_followup_abandoned_total` | `reason` | 乖離の取り込みの追随を、建玉照会の不明（`positions-unknown`）・失敗（`positions-query-failed`）のまま**再試行を使い切って**打ち切った件数（#942）。🔴 発注執行の起動完了時に 0 で作られる（最初の打ち切りを `increase()` が取りこぼさないため）。ダッシュボードには載せず、アラート `AstDriftAdoptionFollowUpAbandoned` が引く |
| `ast_order_reservation_reconciliations_total` | `outcome` | 発注予約の自動リコンサイル（滞留した予約を証券会社の注文一覧と備考で突き合わせる巡回）の**判定の内訳**。`probe-placed`（照会で発注済みと確定）/ `self-healed`（記録ありの自己修復）/ `held-not-placed`（照会は未発注と答えたが解放の門が閉じているため据え置き。同じ予約が巡回ごとに数え直される）/ `released`（門が閉じている限り 0）/ `indeterminate` / `failed`（据え置き）。🔴 **実際には証券会社に存在する注文に対して `held-not-placed` が出ていたら、解放の門を開けてはならない**（備考が往復していない）。リコンサイルが有効な構成でだけ、発注執行の起動完了時に 0 で作られる。アラートは置かない（鳴らす基準は未決） |
| `ast_llm_cost_jpy_total` | `category` | LLM 費用（上限対象 `Llm` / 対象外 `LlmUncapped`） |
| `ast_llm_cost_limit_ratio_percent` | — | 月次上限に対する比率（80 で間隔延長・100 で停止） |
| `ast_market_monitor_position_rows_degraded_total` | `reason` | 市場監視が保有照会の応答を**そのまま損切り判定へ渡せなかった行**（#957）。🔴 平常時 0 件。`identity-missing` / `stop-line-unknown` はその建玉の損切りを検知していない、`stop-line-approximated` は近似のラインで評価している、`response-unreadable` はその巡回で 1 件も評価していない |
| `ast_information_collection_finnhub_symbol_set_resolutions_total` | `outcome` | 情報収集が Finnhub の対象銘柄を**どこから決めたか**（#1015。市場監視に結線したときだけ、巡回ごとに 1 件）。`watchlist` 以外（`last-known`＝直前に読めた対象 / `configured-fallback`＝構成の固定リスト）は**監視銘柄の変更が収集に届いていない**印である |
| `ast_information_collection_finnhub_symbols_deferred` | — | 1 巡回の要求が巡回間隔に収まらず**後回しにした Finnhub の対象銘柄数**（#1015）。🔴 平常時 0。出ていれば自制レートか巡回間隔の見直しが要る |

> **接尾辞は otel-collector の Prometheus 変換に依存する**（`add_metric_suffixes` 既定 true を前提とする）。
> コード側の計器には `unit` を与えていないため、変換は「ドットを `_` へ」＋「Counter は `_total`」＋
> 「Histogram は `_bucket`/`_count`/`_sum`」の 3 規則で閉じる。
>
> 🔴 **系列名がずれたパネルはエラーを出さず、空のグラフを描く。空のグラフは「異常が起きていない」と読める。**
> そのため `node scripts/check-observability-assets.js` が CI で、ダッシュボードが引く系列とコード側の
> レジストリの**双方向の一致**（実在しない系列を引いていないか／誰も引いていない計器が無いか）を検査する。
> **ダッシュボードを編集したら、このコマンドをローカルでも走らせること。**

### アラートルール（#891 / [IADR-0374](../../.ai-context/adr/IADR-0374_decision-skip-reasons-and-first-alert-rule.md)）

🔴 **ダッシュボードは人が見たときにしか働かない。** 本ディレクトリに長らくダッシュボードしか無かったため、
`RiskManagement:BaseUrl` の誤設定などで**新規建てだけが静かに止まり続けても誰も気付かない**状態だった
（手仕舞いは通るので「取引が全部止まった」形にはならない）。`alerts/` はその穴を埋める 1 件目である。

前例がゼロ件だったため、規約も同 IADR 決定 D で決めた。

| 項目 | 規約 |
| --- | --- |
| 置き場所 | `alerts/ai-stock-trading-alerts.yaml`（1 ファイル・複数 group 可） |
| 種別 | `monitoring.coreos.com/v1` の `PrometheusRule`（kube-prometheus-stack が拾う形） |
| group 名 | `ai-stock-trading.<領域>` |
| alert 名 | **PascalCase の英字**（`AstEntriesBlockedByUnknownHoldings`）。アラート名は識別子であり日本語を入れない |
| 重大度 | `severity: warning` から始める。**実測が無いまま `critical` を置かない**（最初の 1 件で狼少年になる） |
| 猶予（`for`） | **置く**（`30m` のような正の期間。無いと一過性の失敗 1 回で鳴る）。値の下限は無く、見ている事象に合わせて決める。**意図して置かないときは、ルール直前のコメントに理由を書き、その中に「for は置かない」と書く**（検査器はこの語句を印として読む。印の無い欠落は検査で落ちる。#939） |
| 本文 | `summary`（1 行）と `description`（何が起きているか・最初に見る場所）を日本語で。`runbook_url` は対応手順を書いてから足す |

> 🔴 **系列名がずれたアラートはエラーを出さず、ただ永久に鳴らない。** 空のグラフと同じ失敗の形だが、
> **人が見に行かない前提の仕組みである分だけ気付きにくい**。`node scripts/check-observability-assets.js` が
> 本ファイルの `expr` もコード側のレジストリへ突き合わせる（検査 A1・A2）。`expr` は複数行のブロック／折り畳みスカラー（`>-` 等）で書いてよい（本文まで読む）。ダッシュボードでは**同じダッシュボード内のパネル id の重複と配置（`gridPos`）の重なり**も止める（D4・D5。並行 PR が末尾へ同じ id のパネルを足すと、マージでパネルが 1 枚黙って消えるため）。**編集したらローカルでも走らせること。**

- **投入**: 実 stand-up（Prometheus / Alertmanager）は MSP 側の共有 overlay である。本リポジトリは
  ダッシュボードと同じく**資産を置くところまで**を持つ。配備する側は本 YAML を `kubectl apply` するか、
  Helm の追加 manifest として同梱する（`metadata.labels.release` が overlay の `ruleSelector` と一致すること）。
- **閾値の置き方**: 「N 分間に M 件」という形の閾値は**実測してから**決める
  （[`../../docs/observability/observability.md`](../../docs/observability/observability.md)）。
  1 件目のルールが実測なしで置けるのは、**平常時の期待値が 0 件**の事象だけを見ているからである。
  2 件目（`AstDriftAdoptionFollowUpAbandoned`。#942）も同じ性質である（再試行を使い切った打ち切りだけを数える）。
- **`_error` キューの滞留そのものは見ていない。** RabbitMQ のキュー長（`rabbitmq_queue_messages*`）は
  どこからも scrape されておらず（本リポジトリの otel-collector は OTLP しか受けず、基盤側の Prometheus の scrape 対象は
  otel-collector だけ）、その系列を引くルールは永久に鳴らない。🔴 **`check-observability-assets.js` はこれを止めない**
  （突き合わせるのは `ast_*` の系列だけ）。`_error` 全般の監視は未解決であり、2 件目は業務メトリクスで 1 つの経路だけを覆う。

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
