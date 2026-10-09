---
title: ログ・可観測性仕様書（AST）
type: observability-spec
status: draft
created: 2026-07-19
updated: 2026-10-10
author: endazon (with Claude Code)
---
<!-- trace:
ids: [NFR-01, NFR-02, NFR-03, NFR-07, NFR-09, FR-04, FR-09, FR-10, FR-02, FR-14, FR-06, FR-11]
adrs: [ADR-0006, ADR-0045, ADR-0003]
iadrs: [IADR-0052, IADR-0061, IADR-0094, IADR-0121, IADR-0255, IADR-0307, IADR-0333, IADR-0374, MSP:IADR-0077, IADR-0395, IADR-0441, IADR-0444, IADR-0463, IADR-0471, IADR-0495, IADR-0500, IADR-0521, IADR-0104, IADR-0522, IADR-0525]
specs: [20260828_287_business-metrics-and-dashboards, 20260904_689_nfr-01-02-end-to-end-latency-metrics, 20260911_751_trace-uri-redaction, 20260923_891_decision-skip-reasons-and-first-alert, 20260925_942_drift-followup-abandoned-alert, 20260926_856_reconciler-broker-action-map-and-metrics, 20260927_1051_release-gate-per-trading-env, 20260930_1113_entry-blockers-before-llm, 20261001_1130_held-add-on-before-llm, 20261007_1176_min-notional-and-decision-exit-reentry, 20261007_1174_pre-llm-one-share-skip, 20261009_1286_held-positions-in-judgment, 20261010_243_policy-revision-max-tokens, 20261010_1290_screening-rationale-garble]
issues: [#24, #287, #689, #751, #891, #942, #856, #1051, #1113, #1130, #1176, #1174, #1286, #243, #1290]
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
| 取引サイクル | `ast_trade_cycle_decision_skips_total` | `reason` / `trigger` | 🔴 **見送りの理由**の内訳（方針なし・Hold・鮮度切れ・数量 0・裸の新規売り・保有不明・新規建てが審査で必ず拒否される〔`EntryBlockedByRiskControls`。LLM を呼ぶ前〕・保有中の買い増しが審査で必ず拒否される〔`AddOnBlockedByRiskControls`。LLM の後〕・新規建てに使える金額の上限が最小の名目額に届かない〔`EntryCapacityBelowMinimumNotional`。LLM の前〕・サイジングの名目額が最小に満たない〔`SizedBelowMinimumNotional`。LLM の後〕・残枠が現在値の 1 株に届かない〔`EntryCapacityBelowOneShare`。LLM の前。従来の数量 0 から移った分〕・監視銘柄の外の保有銘柄の出口専用の判断で新規建てが返った〔`ExitOnlyOpenOutsideWatchlist`。LLM の後〕・出口専用の判断で保有が 0 または不明〔`ExitOnlyWithoutHolding`。LLM の前〕ほか 20 種）。上の `action=no-trade` は「何回見送ったか」しか語らず、**平常（Hold）と異常（保有照会が壊れて新規建てだけが静かに止まっている）が同じ 1 値に落ちる**。**置き換えではなく並置**であり、1 回の見送りで両方が 1 ずつ増える |
| 取引サイクル | `ast_trade_cycle_decision_duration_ms_*` | `trigger` | 判断レイテンシ（ヒストグラム）。**1 サービス内の判断 1 回**であり、端点間ではない |
| 取引サイクル | `ast_trade_cycle_order_completion_latency_ms_*` | `trigger` | **起点イベント → 発注完了**の端点間所要（ヒストグラム）。価格変動検知起点は `trigger=price-movement` の系列で読む（目標 5 分＝300,000 ms） |
| 取引サイクル | `ast_trade_cycle_record_completion_latency_ms_*` | `trigger` | **起点イベント → 記録完了**（監査台帳へ記録した時点）の端点間所要。定時サイクルは `trigger=scheduled` の系列で読む（目標 10 分＝600,000 ms） |
| 取引サイクル | `ast_trade_cycle_latency_unobserved_total` | `stage` / `reason` | 🔴 **端点間の所要を確定できなかった件数。**起点を持たない注文（利用者の手仕舞い・維持証拠金の自動縮小・約定追跡の後追い）や時計ずれで負になった区間はここへ出し、**ヒストグラムには 1 件も入れない**（0 ms を入れると目標を満たしているように見えるため） |
| 統制 | `ast_risk_screenings_total` | `outcome` | 発注前審査。**承認も拒否も数える**（拒否だけを数えると「違反 0 件」と「審査が動いていない」を区別できない） |
| 統制 | `ast_risk_rejections_total` | `reason` | 見送り理由の内訳（上限超過・緊急停止・一時停止・禁止銘柄ほか）。🔴 保有 0・未約定なしで新規建てが必ず拒否される銘柄は、判断が LLM を呼ぶ前に見送るため、ここではなく上の `decision_skips{reason="EntryBlockedByRiskControls"}` に出る。保有中の銘柄で LLM が返した買い増し・売り増しのうち、LLM の前に読んだ可否が塞がっていたものも `decision_skips{reason="AddOnBlockedByRiskControls"}` に出る（審査は変わらない。件数の推移を比べるときは両方を足して読む） |
| 発注 | `ast_order_executions_total` | `status` / `provider` | 発注結果と発注先 |
| 発注 | `ast_order_dispatch_forgone_total` | `reason` | 発注に**届いていない**見送り。ブローカーの拒否（`status=Rejected`）と混ぜない |
| 発注 | `ast_order_drift_adoption_followup_abandoned_total` | `reason` | 🔴 乖離の取り込みの追随を、建玉照会の**不明**（`positions-unknown`）・**失敗**（`positions-query-failed`）のまま再試行を使い切って打ち切った件数。**空の一覧（0 株）は数えない**（確かめた結果であり追随は進む）。途中の配送も数えない。発注執行の起動完了時に 0 で作られる |
| 発注 | `ast_order_reservation_reconciliations_total` | `outcome`, `provider` | 発注予約の自動リコンサイルの**判定の内訳**（`probe-placed` / `self-healed` / `held-not-placed` / `released` / `indeterminate` / `failed`）。🔴 `held-not-placed` は解放の門が閉じているため据え置いた「未発注」判定で、同じ予約が巡回ごとに数え直される。**実際には証券会社に存在する注文に対してこれが出ていたら、門を開けてはならない**。`provider` は**予約の取引環境**（`MoomooSimulate` / `MoomooReal` / `InternalPaper`、不明は `Unknown`）。解放の門は取引環境ごとに分かれており、**門の判断は同じ `provider` の系列だけで行う**。リコンサイルが有効な構成でだけ起動時に 0 で作られる（判定 6 × 取引環境 4） |
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
- **LLM の出力上限への到達**: LLM ゲートウェイの応答の終了理由が `max_tokens`（出力上限で打ち切られた）なら、呼び出し元が警告ログを 1 行出す。
  対象は取引判断（一次スクリーニング・本判断）・報告書の散文・方針の改訂の 3 つで、方針の改訂の行は上限（`maxTokens`）と出力トークン（`outputTokens`）を運ぶ。
  Loki では `stopReason=max_tokens` で引く（3 つとも警告の本文にこの形で載る）。🔴「出力上限に到達」の文言では漏れる——取引判断と報告書の散文は、
  本文が空のまま打ち切られると「応答本文が空です（stopReason=max_tokens）」の行だけを出し、「出力上限に到達」の行を出さない。
  逆に `stopReason=max_tokens` では 1 回の打ち切りが複数行に当たることがある（方針の改訂の形式違反の警告・全量ログ〔既定オフ〕にも同じ形で載る）。出力上限は方針の改訂だけ 8192、他は 4096 である（稼働 PoC の実測で、方針の改訂の出力の最大が 4096 の 93% に達したため）。
  方針の改訂の上限時間は既定 95 秒で、基盤のゲートウェイが上流の LLM を待つ 100 秒（固定）が実効の天井になる。出力の速さを毎秒 50〜80 トークンとすると
  約 4,700〜7,600 トークンで時間切れ（案なし・警告「方針の改訂 LLM がタイムアウトしました」）になり、8192 の全量には届かない。
  🔴 打ち切りの頻度の閾値やアラートは置いていない（実測してから決める。下の注記と同じ理由）。
- **判断理由（根拠文）の文字化けの疑い**: 取引判断の LLM の根拠文に化けの疑い（置換文字 U+FFFD、漢字・かなに接するキリル文字、漢字に挟まれた小文字を含む英字列）が
  あると、根拠文を受け取った地点で 1 回だけ判定し、警告ログを 1 行出す。🔴 判断（action・数量）は変えない。疑いのある根拠文は、先頭に目印
  「⚠ 判断理由に文字化けの疑い: 」を付けて転記する（原文は残す）。Loki での引き方:
  - 件数を数える（1 件の化けにつき 1 行）: `{namespace="ai-stock-trading"} |= "判断理由に文字化けの疑い（action は変えない"`。
    一次スクリーニングは「一次スクリーニングの判断理由に文字化けの疑い」、本判断は「本判断の判断理由に文字化けの疑い」で始まる。
  - 判断の記録（「LLM 判断:」の行）で引く: `|= "screeningRationaleGarble=True"`（一次の根拠文）・`|= "decisionRationaleGarble=True"`（本判断の根拠文）。
    構造化の属性名は `ScreeningRationaleGarble`・`DecisionRationaleGarble`（真偽値）。
  - 🔴 目印の文字列「判断理由に文字化けの疑い」だけで引くと、1 件の化けが警告・判断の記録・見送りの行など複数行に当たる（数えるのには使わない）。

  | 化けた根拠文 | 警告ログ | 判断の記録（LLM 判断:）の目印 | 判断の記録の属性 | Stage 0 の記録 | 監査台帳・日報 | KB（RAG で後の判断へ） | Discord |
  | --- | --- | --- | --- | --- | --- | --- | --- |
  | 一次スクリーニング・見送り（Hold） | 出る | 根拠文に付く | `screeningRationaleGarble=True` | 一次の根拠と多数決の根拠に付く | 届かない（見送りは判断の事象を出さない） | 届かない | 届かない |
  | 一次スクリーニング・本判断へ進んだ | 出る | 付かない（記録される根拠文は本判断のもの） | `screeningRationaleGarble=True` | 一次の根拠に付く（多数決の根拠は本判断のもの） | 届かない | 届かない | 届かない |
  | 本判断（多数決で採った根拠文） | 出る | 根拠文に付く | `decisionRationaleGarble=True` | 多数決の根拠に付く（各票の生の根拠には付けない） | 付く（判断の事象の根拠文に前置済み。台帳の要約・日報の明細「判断根拠（要約）」にそのまま出る） | 入る（確定した日報は本文ごと `report` タグで KB へ入り、判断の検索はこのタグを許可する） | 届かない（通知は判断の根拠文を運ばない。日報は閲覧リンクから読む） |

  一次が本判断へ進んだときの一次の根拠文の化けは、警告ログと `screeningRationaleGarble` にしか残らない。運用ではこの 2 つで拾う。
  KB（RAG）の列: 目印付きの本判断の根拠文は、確定した日報の明細ごと KB へ入り、後の判断のプロンプトへ検索結果として入り得る。害は小さい
  （化けた文を隠さず「化けの疑い」と明示して渡す）。KB から除く・目印を剥がす処理は置かない。
  判定の限界: 別の有効な漢字への化け（例「監視銘牌」）と、句読点・文末の直前の化けた英字列は判定しない。漢字に挟まれた英語の固有名・略語は誤って
  目印が付く（現実的な根拠文 30 文中 5 文〔約 17%〕。例「米国Apple社」「同社iPhone需要」「前年比YoYで」「米Microsoft社のAzure」「決算後gapupした」）。
  大文字を含む並びを除くなどの締め方は実測の化け「監視銘HeaderItem」を取りこぼすため採っていない。目印が付くだけで判断は変わらない。
- **トレース**: サービス間（s2s）呼び出しは Tempo で追跡する。Grafana の Trace→Logs 相関を有効化済み（MSP datasource）。
- **URI 自体が資格情報である送信先は、トレースでも宛先を伏せる。** HTTP クライアントのスパンが持つフル URL は、
  出ていく直前に**スキーム＋ホストだけ**へ落とす（パスにトークンを載せる送信先——通知の Webhook——が対象）。
  🔴 **クエリの秘匿では足りない。** トークンは**パス**に載るため、実行環境の既定のクエリ秘匿も計装の既定も効かない。
  抑止は資格情報を含む形の URL に限り、他の送信のパスは残す（障害切り分けを落とさない）。
  スパン自体・ステータス・所要時間・宛先ホストは従来どおり残る。

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

## アラートルール（`#891`）

ダッシュボードは**人が見たときにしか働かない**。アラートルールの 1 件目を
[`deploy/observability/alerts/`](../../deploy/observability/alerts/ai-stock-trading-alerts.yaml) に置いた。
置き場所・命名・重大度の規約は [`deploy/observability/README.md`](../../deploy/observability/README.md) が正本である。

🔴 **上の「閾値は実測してから決める」と矛盾しない。** 1 件目が実測なしで置けるのは、
見ている事象（保有照会が実結線のもとで不明を返し、新規建てが見送られ続ける）の**平常時の期待値が 0 件**
だからである。「N 分間に M 件なら異常」という形の閾値は、これまでどおり実測してから置く。

🔴 **系列名がずれたアラートはエラーを出さず、ただ永久に鳴らない。** 空のグラフと同じ失敗の形であり、
人が見に行かない前提の仕組みである分だけ気付きにくい。`node scripts/check-observability-assets.js` が
アラートの参照する系列もコード側のレジストリへ突き合わせる。

### 2 件目: 乖離の取り込みの追随の打ち切り（`#942`）

利用者が承認した乖離の取り込みに保護を追随させる直前の建玉照会が不明・失敗のまま再試行を使い切ると、
メッセージは発注執行の `PositionDriftAdopted_error` キューに残り、**取り込みで消えたはずの建玉の売りの逆指値が
ブローカーに残る**。それまで見えたのは Critical ログと `_error` キューの滞留だけだった。

- **`_error` キューの滞留を直接見る案は採らなかった。** RabbitMQ のキュー長はどこからも scrape されていない
  （本リポジトリの otel-collector は OTLP しか受けず、基盤側の Prometheus の scrape 対象は otel-collector だけである）。
  存在しない系列へのアラートは永久に鳴らず、しかも検査器は `ast_*` 以外の系列を突き合わせないので**止まらない**。
- **数えるのは最後の配送だけ**（途中の失敗は再試行で回復し得る）。平常時の期待値は 0 件であり、閾値に実測は要らない。
- 🔴 **系列は発注執行の起動完了時に 0 で作る。** 系列が最初の打ち切りで初めて現れると、`increase()` はその 1 点目を
  増分に数えず、起動後の最初の打ち切りを取りこぼす（稀な事象ほど、それが唯一の 1 回になる）。
- **系列が無いときは鳴らない**（`sum()` が空になる）。無データを異常と読まない代わりに、送出の断はこのアラートでは
  分からない（`absent()` を置く件は別の課題として残る）。

## Tier 3（対象外）

- 稼働率 99%（開場時間帯）の**実測**・アラート閾値の実運用調整は Hetzner 実環境依存（[`docs/infra/infra.md`](../infra/infra.md) の Tier 境界）。
