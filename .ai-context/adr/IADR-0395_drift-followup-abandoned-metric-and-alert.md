---
title: IADR-0395 乖離の取り込みの追随を再試行を使い切って打ち切ったことは、業務メトリクスで数えてアラートにする（_error キュー長は系列が無いので使わない）
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-05, NFR-07, UC-06, IADR-0129, IADR-0255, IADR-0370, IADR-0374]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10「逆指値なしの建玉を持たない」・NFR-07 可観測性)
---

# IADR-0395: 追随の打ち切りを業務メトリクスで数え、アラートにする

- 状態: Accepted
- 日付: 2026-09-25
- 決定者: claude (Claude Code) / #942

## 起点・関連

- 関連する計画書 ID: **FR-10**（保護逆指値）・FR-05・**NFR-07**（可観測性）・UC-06
- 関連 IADR: [IADR-0370](IADR-0370_drift-adoption-protective-stop-followup.md)（2026-09-24 追記 決定 3′ の代償を本 IADR が受ける）、
  [IADR-0374](IADR-0374_decision-skip-reasons-and-first-alert-rule.md) 決定 4（アラートの置き場所・命名・重大度の規約。本件はそれに従う）、
  [IADR-0255](IADR-0255_business-metrics-and-dashboards.md)（業務メトリクスの作法）、
  [IADR-0129](IADR-0129_wolverine-messaging-topology.md) 決定 5（共通の再試行 2s/10s/30s → `<queue>_error`）
- 関連する実装仕様書: [20260925_942_drift-followup-abandoned-alert](../specs/20260925_942_drift-followup-abandoned-alert.md)
- 起票: [#942](https://github.com/endazon/ai-stock-trading/issues/942)（PR #918 の監査）

## コンテキストと課題

IADR-0370 決定 3′ により、乖離の取り込みに保護を追随させる直前の建玉照会が不明（`null`）・例外なら、発注執行は何も変えずに投げ、
共通の再試行を使い切るとメッセージは `ai-stock-trading.order-execution-service.PositionDriftAdopted_error` に残る。
**このとき見えるのは Critical ログ 4 行と `_error` キューの滞留だけ**で、Discord にも何も届かず（投げた処理中の発行は Wolverine が捨てる）、
アラートルールも無かった。その間、取り込みで消えたはずの建玉の**売りの逆指値がブローカーに残る**（発火すると意図しないショート。#853 と同じ帰結）。

より一般に、本システムの **Wolverine の `_error` キューはどれも監視されていない**。

## 検討した選択肢

| 案 | 実測 | 判定 |
| --- | --- | --- |
| a. 業務メトリクス（`BusinessMetrics` / `BusinessMetricNames`）で打ち切りを数え、PrometheusRule を置く | 系列は**本リポジトリのコードが出す**。名前はレジストリにあり、`check-observability-assets.js` の A2 が突き合わせる。送出経路は 1 件目のアラート（IADR-0374 決定 5）と同じ | **採用** |
| b. `_error` キューの滞留（`rabbitmq_queue_messages*{queue=~".*_error"}`）を全サービス汎用で鳴らす | 🔴 **系列が存在しない。** 本リポジトリの `infra/otel/otel-collector-config.yaml` は OTLP しか受けない。基盤（MSP。隣接クローンで確認）の経路B では Prometheus の scrape 対象は `otel-collector:8888` だけ（`deploy/local/observability/prometheus.yaml`）で、collector が取りに行くのは `mail-relay:9154` だけ（`deploy/local/infra/otel-collector.yaml`・`otel-collector-forward.yaml`）。基盤の `deploy/prometheus/alerts.yml` は `RabbitMqDeadLetter` を「要 RabbitMQ prometheus plugin」として**コメントアウトしたまま**である | **不採用** |
| c. Wolverine 自身のカウンタ `wolverine-dead-letter-queue`（Meter `Wolverine:<ServiceName>`）を使う | WolverineFx 6.24.5 の逆コンパイルで計器の実在は確認した（`MetricsConstants.DeadLetterQueue`・`WolverineRuntime` が `new Meter("Wolverine:" + ServiceName)`）。だが `ObservabilityExtensions` は `AddMeter(BusinessMetricNames.MeterName)` しか載せていないので**今は出ていない**。Prometheus 上の名前（単位 `Messages` が接尾されるか）は実バックエンドなしでは確かめられず、検査器も `ast_*` 以外を突き合わせない | **本件では採らない**（フォローアップ 1） |

🔴 **案 b は検査器が止めない。** `check-observability-assets.js` の A2 は `/\bast_[a-z0-9_]+/g` に一致する系列しか突き合わせないので、
`rabbitmq_*` を引くルールは**検査が緑のまま、実環境では永久に鳴らない**。「系列が実在することを確かめられる方を採る」の判断はここに依る。

## 決定

### 決定 1: 計器 `ast.order.drift_adoption_followup_abandoned{reason}` を足す

- Counter。Prometheus 名は `ast_order_drift_adoption_followup_abandoned_total`。
- `reason` は **`positions-unknown`（照会が `null`）** と **`positions-query-failed`（照会が例外）** の 2 値だけ。語彙の外は `ArgumentException` で拒む（基数の規律）。
- 🔴 **空の一覧（照会は成功・0 株）は数えない。** それは「確かめた・建玉なし」であり、追随は進む（不明・なし・ありを混ぜない）。
- 計上は業務クラス（`ProtectiveStopDriftAdopter`）が打ち切る地点（`PositionsUnknown`）で行う。計器への記録はプロセス内で即時であり、
  ハンドラが投げても捨てられない（捨てられるのは発行だけ）。

### 決定 2: 数えるのは**最後の配送**の打ち切りだけ

- 途中の配送の打ち切りは再試行で回復し得る。数えると一過性の照会失敗 1 回でアラートが鳴る。
- 最後かどうかはハンドラが Wolverine の `Envelope.Attempts` を `WolverineExtensions.MaxDeliveryAttempts`（＝再試行間隔の数＋1＝4）と比べて決め、
  `ApplyAsync(adopted, finalDeliveryAttempt, ct)` で業務クラスへ渡す（業務クラスはメッセージ基盤を参照しない。受け取るのは真偽値だけ）。
- 🔴 **根拠（WolverineFx 6.24.5 の逆コンパイル）**: 受信経路の `Executor.ExecuteAsync` はハンドラの前で `envelope.Attempts++` する（1 始まり）。
  インラインの再試行（`RetryInlineContinuation`）は `RetryExecutionNowAsync` → パイプライン → `ExecuteAsync` を通るので回ごとに 1 増える。
  失敗規則（`FailureRule.TryCreateContinuation`）は `Attempts` と同じ番号の枠を使い、枠が無ければ `MoveToErrorQueue` にする。
  共通配線の規則は 1〜3 回目が再試行、4 回目が `MoveToErrorQueue` である（T-10-784 が規則そのものを読んで固定）。
- **`>=` で比べる。** 配送回数が上限を超えて届いた場合（例: `_error` から戻したメッセージが `attempts` ヘッダを引き継いでいた場合）も、
  その失敗は枠の外で `_error` へ送られるため。
- 数字は `RetryIntervals` から導出し、2 箇所に持たない。

### 決定 3: 系列は発注執行の**起動完了時に 0 で作る**

- `BusinessMetrics.PrimeDriftAdoptionFollowUpAbandoned()` が 2 つの理由を値 0 で計上する。`Program.cs` が `ApplicationStarted` で呼ぶ。
- 🔴 **なぜ**: この事象の平常時の件数は 0 である。系列が最初の打ち切りで初めて現れると、その時点の値は 1 で、`increase()` は 1 点目を増分に数えない。
  **プロセスの起動から最初の打ち切りをアラートが取りこぼす**——稀な事象ほど、その最初が唯一の 1 回になる。
- 🔴 **`ApplicationStarted` を待つ理由**: OTel の MeterProvider はホストの開始時に立つ。それより前の `Add(0)` は誰も聞いておらず残らない。
  T-10-786 は `Program.cs` そのものを組み、0 が OTel の exporter まで届くことを確かめる。

### 決定 4: アラート `AstDriftAdoptionFollowUpAbandoned`（IADR-0374 決定 4 の規約どおり）

```promql
sum(increase(ast_order_drift_adoption_followup_abandoned_total[15m])) > 0
for: 1m
severity: warning
```

- group は新設の `ai-stock-trading.order-execution`。
- **閾値に実測は要らない**（平常時の期待値が 0 件。1 件目と同じ性質）。`severity` は規約どおり `warning` から始める。
- **平常時**: 系列は 0 のまま → `increase` は 0 → 鳴らない。**市場の開閉**: 事象は利用者の承認で起き、取引時間に依らない。
  市場が閉まると 0 へ戻る型の式ではない（#939 が 1 件目について記録した日次のフラッピングは起きない）。
  **無データ**（サービス停止・送出の断・未配備）: `sum()` が空になり鳴らない。**再起動**: 系列は 0 から作り直され、`increase` はリセットを補正するので鳴らない。
- `for: 1m` は一過性を待つ猶予ではない（事象は既に終端＝再試行を使い切った後）。評価 1 回の揺れで発火させないための最小値で、15 分窓より十分短い。
- `expr` は 1 行・`for:` は明示した（#939 が求める、より厳しい検査を通る形）。

## 理由

- **確かめられる系列を選んだ。** 案 b の系列はどこからも scrape されておらず、しかも検査器が止めない —— 「アラートがある」という前提だけが文書に残る最悪の形になる。
- **「最後の配送だけ」と「0 で始める」は、同じ失敗の両側を塞いでいる。** 前者が無いと一過性の失敗で鳴り（狼少年）、後者が無いと最初の 1 件で鳴らない（無音）。

## 結果

### できるようになったこと

- 建玉照会の不明・失敗で追随を打ち切り、メッセージが `_error` へ送られたことが、人が見ていなくてもアラートとして上がる（配備は基盤側）。
- 最後の配送の Critical ログは「再試行を使い切ったため、このメッセージは _error キューへ送られます」と書き、途中の配送のログと区別できる。
- `reason` で照会の不明と失敗を読み分けられる。

### 🔴 残る制約（実バックエンドなしでは検証できないこと）

- **Prometheus に系列が現れること、`increase()` が 0 → 1 を拾ってルールが発火すること、Alertmanager へ届くこと**は未確認である。
  本リポジトリで固定したのは「計器が起動時に 0 で OTel の exporter まで届き、最後の配送の打ち切りで 1 増え、その名前をルールが引き、検査器が突き合わせる」までである。
- remote write の経路で `_total` が付くこと（`add_metric_suffixes` の既定）と、PromQL の構文（`promtool` が手元に無く未実行）は未検証。前提は 1 件目と同じ。
- 基盤（MSP）の経路B の Prometheus は素の Prometheus で、`rule_files` にインラインの `alerts.yml` を読む。**`PrometheusRule` は読まない**（隣接クローンの `deploy/` に kube-prometheus-stack は無い）。
  本ファイルを誰がどう載せるかは IADR-0374 決定 4 のとおり配備側の作業で、1 件目と同じ状態である。
- **アラートの解消（resolved）は `_error` が空になったことを意味しない。** 1 件の打ち切りは 15 分窓を抜けると解消になるが、メッセージは再投入まで残る。
- **最後の配送の失敗が別の例外**（例: 保存の失敗）なら数えない。数えるのは建玉照会の不明・失敗による打ち切りだけである。
- **`_error` キュー全般（`order-approved_error` ほか）は依然として監視されていない。** 本件は 1 つの経路だけを業務メトリクスで覆った。
- `Envelope.Attempts` の意味は WolverineFx 6.24.5 の実装に依る。版上げで変わると最後の判定がずれ得る。T-10-784 は規則の枠を固定するが、
  `Attempts` の増え方そのものは実 RabbitMQ の受信経路で試験していない（再試行の待ち 42 秒を壁時計で待つことになる）。

### フォローアップ

1. `_error` キュー全般の監視: 案 c（`AddMeter("Wolverine:*")` で Wolverine の `wolverine-dead-letter-queue` を出す）か、
   基盤側で RabbitMQ の prometheus プラグインを collector が取りに行く形（基盤の「唯一の scrape 対象」の不変条件を保つ）。
   いずれも実バックエンドで系列名を確かめてから検査器（`ast_*` 以外の系列）とあわせて決める。
2. 実バックエンドが立ったら、1 件の打ち切りを注入してアラートの発火・解消の時刻を実測し、`for: 1m`・15 分窓を見直す。
