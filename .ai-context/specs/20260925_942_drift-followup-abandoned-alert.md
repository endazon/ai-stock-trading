---
title: 乖離の取り込みの追随が建玉照会の不明で再試行を使い切ったことを、業務メトリクスとアラートで知らせる
type: spec
status: accepted
related_ids: [FR-10, FR-05, NFR-07, UC-06, IADR-0129, IADR-0255, IADR-0370, IADR-0374, IADR-0395]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10「逆指値なしの建玉を持たない」・NFR-07 可観測性)
---

# 仕様書: 乖離の取り込みの追随の打ち切りをアラートで知らせる（#942）

## 起点

- #942（PR #918 の監査。IADR-0370 2026-09-24 追記 決定 3′ の代償）。
- 事象: 乖離の取り込み（`PositionDriftAdopted`）に保護記録を追随させる直前の建玉照会が **不明（`null`）・例外** のとき、
  発注執行は何も変えずに `ProtectiveStopDriftPositionsUnknownException` を投げ、共通の再試行（2s/10s/30s）を使い切ると
  メッセージは `ai-stock-trading.order-execution-service.PositionDriftAdopted_error` に残る。
  見えるのは **Critical ログ 4 行と `_error` キューの滞留だけ**で、Discord にもアラートにも何も出ない。
- 🔴 その間、取り込みで消えたはずの建玉の**売りの逆指値がブローカーに残る**（発火すると意図しないショート。#853 と同じ帰結）。

## 🔴 選択肢の比較（案 a / 案 b）と実測

| 案 | 系列は実在するか（実測） | 判定 |
| --- | --- | --- |
| a. 業務メトリクス（`BusinessMetrics` / `BusinessMetricNames`）＋ PrometheusRule | **本リポジトリのコードが出す系列**である。名前はレジストリにあり、`check-observability-assets.js` の A2 が突き合わせる。送出経路（OTLP → otel-collector → remote write）は既存の 1 件目のアラート（`AstEntriesBlockedByUnknownHoldings`）と同じ | **採用** |
| b. `_error` キューの滞留（`rabbitmq_queue_messages*{queue=~".*_error"}`） | 🔴 **出ていない。** 本リポジトリの `infra/otel/otel-collector-config.yaml` は OTLP しか受けず、RabbitMQ を scrape しない。基盤（MSP）の経路B も、Prometheus の scrape 対象は `otel-collector:8888` ただ 1 つで（`deploy/local/observability/prometheus.yaml`）、collector が取りに行くのは近接 MTA（`mail-relay:9154`）だけである（`deploy/local/infra/otel-collector.yaml` / `otel-collector-forward.yaml`）。基盤の `deploy/prometheus/alerts.yml` は `RabbitMqDeadLetter` を「要 RabbitMQ prometheus plugin」として**コメントアウトしたまま**である | **不採用**（存在しない系列へのアラートは永久に鳴らない） |
| c.（検討のみ）Wolverine 自身のカウンタ `wolverine-dead-letter-queue`（Meter `Wolverine:<ServiceName>`） | WolverineFx 6.24.5 を逆コンパイルして**計器が在る**ことは確認した（`MetricsConstants.DeadLetterQueue`）。だが `ObservabilityExtensions` は `AddMeter(BusinessMetricNames.MeterName)` しか載せておらず、**今は 1 系列も出ていない** | **本件では採らない**（全 `_error` を覆える汎用策の候補として IADR-0395 のフォローアップに残す） |

🔴 **検査器は案 b を止めない。** `check-observability-assets.js` の A2 は `ast_*` の系列しか突き合わせない（`/\bast_[a-z0-9_]+/g`）。
`rabbitmq_*` を引くルールを書くと**検査は緑のまま、実環境では永久に鳴らない**。案 a を選ぶ理由の半分はここにある ——
案 a の系列は検査器が名前を突き合わせられる。

## 🔴 母集合（走査したファイルと除外理由）

走査に使った語: `PositionDriftAdopted_error` / `#942` / `ProtectiveStopDriftPositionsUnknownException` / `アラートは無い` /
`アラートルールは無` / `アラートも無` / `ai-stock-trading-alerts` / `MIN_ALERT_RULES` / `rabbitmq_queue`
（`git grep`・`CHANGELOG.md` を除く追跡ファイル全件。16 件ヒット）。

| ファイル | 扱い |
| --- | --- |
| `backend/Shared/AiStockTrading.Shared.Contracts/Observability/BusinessMetricNames.cs` / `BusinessMetrics.cs` | **直す**（カウンタ 1 本・理由の語彙 2 値・起動時の 0 の計上） |
| `backend/Services/OrderExecutionService/Features/OrderExecution/AdoptPositionDrift/ProtectiveStopDriftAdopter.cs` | **直す**（最後の配送で打ち切ったときだけ数える。Critical の文面を「_error へ送られる」に分ける） |
| `backend/Services/OrderExecutionService/Infrastructure/Steps/PositionDriftAdoptedHandler.cs` | **直す**（`Envelope.Attempts` から最後の配送かを判定して業務クラスへ渡す） |
| `backend/Services/OrderExecutionService/Program.cs` | **直す**（業務クラスへ `BusinessMetrics` を渡す・起動完了時に 0 を計上する） |
| `backend/TestSupport/AiStockTrading.TestSupport.PlatformShim/Foundation/Extensions/WolverineExtensions.cs` | **直す**（最大配送回数を公開する。再試行間隔の配列から導出し、2 箇所に持たない） |
| `backend/Services/OrderExecutionService/Tests/**`（業務クラス・ハンドラ・合成の試験） | **直す / 足す** |
| `backend/Shared/AiStockTrading.Shared.Contracts.Tests/BusinessMetricsTests.cs` | **直す**（新計器を「全計器を発火させる」一覧へ足す。**足さないとレジストリ一致の試験が落ちる**） |
| `backend/TestSupport/AiStockTrading.TestSupport.PlatformShim.Tests/WolverineTopologyTests.cs` | **足す**（最大配送回数と失敗規則の一致。0 の計上が OTel の exporter まで届くことは `Program.cs` を組む発注執行の合成試験で固定する） |
| `deploy/observability/alerts/ai-stock-trading-alerts.yaml` | **直す**（2 件目のルール・新 group） |
| `deploy/observability/README.md` / `docs/observability/observability.md` | **直す**（系列表に 1 行・アラートの説明） |
| `docs/operations/operations.md` | **直す**（§監視・アラートの表に 1 行・障害対応表の `PositionDriftAdopted_error` 行の「アラートは無い」を書き換える） |
| `docs/tests/FR-10_risk-controls-tests.md` | **直す**（T-10-780〜 の行） |
| `.ai-context/adr/IADR-0370_*.md` | **追記ブロックだけ足す**（決定 3′ の代償の記述を追随させる。本文は書き換えない） |
| `.ai-context/adr/IADR-0395_*.md` / `.ai-context/adr/README.md` | **新設 / 行を足す** |
| `.ai-context/adr/IADR-0374_*.md` | **直さない**。規約（決定 4）に従うだけで、規約そのものは変えない |
| `.ai-context/specs/20260923_858_*` / `20260923_891_*` | **直さない**（確定済みの凍結記録） |
| `scripts/check-observability-assets.js` | **直さない**。新ルールは現行の A1・A2 を通り、#939 のより厳しい検査（`for:` 必須・折り畳みスカラーの `expr` を拒否）も通る形で書く。検査器の拡張は #939 の射程 |
| `backend/.../ProtectiveStopDriftPositionsUnknownException.cs` | **直さない**（文面は「使い切ると _error」で正しい） |

## やること

1. **計器**: `ast.order.drift_adoption_followup_abandoned`（Counter・タグ `reason`）。
   `reason` は **`positions-unknown`（照会が `null`）と `positions-query-failed`（照会が例外）** の 2 値。
   🔴 **空の一覧（照会は成功・0 株）は数えない** —— それは「確かめた」であり、追随は進む（不明・なし・ありを混ぜない）。
2. **数える時点**: 業務クラスが照会の不明・失敗で打ち切り、**かつそれが最後の配送**（再試行を使い切り、この失敗で `_error` へ送られる）のときだけ 1 増やす。
   途中の失敗（再試行で回復し得るもの）は数えない —— 数えると一過性の照会失敗 1 回で鳴るルールになる。
3. **最後の配送の判定**: ハンドラが Wolverine の `Envelope.Attempts` を `WolverineExtensions.MaxDeliveryAttempts`（＝再試行間隔の数＋1）と比べる。
   `>=` で比べる（配送回数が上限を超えて届いた場合——例: `_error` から戻したメッセージが `attempts` ヘッダを引き継いでいた場合——も、その失敗は規則の枠の外で再び `_error` へ行くため）。
4. **起動時に 0 を計上する**（`PrimeDriftAdoptionFollowUpAbandoned`）。🔴 系列が最初の失敗で**初めて現れる**と、
   Prometheus の `increase()` は 1 点目を増分として数えないため、**プロセスが起動してから最初の打ち切りを取りこぼす**。
   計上は `ApplicationStarted` の後（OTel の MeterProvider が立った後）に行う ——それより前の `Add(0)` は誰も聞いていない。
5. **アラート**（`deploy/observability/alerts/ai-stock-trading-alerts.yaml`）:
   `sum(increase(ast_order_drift_adoption_followup_abandoned_total[15m])) > 0`、`for: 1m`、`severity: warning`。
   `expr` は 1 行・`for:` は明示（#939）。
6. 運用仕様書・可観測性資料・試験仕様書・IADR-0370 追記・IADR-0395・ADR 索引を追随させる。

## 受け入れ基準（#942）と写像

| 受け入れ基準 | 満たし方 | 試験 |
| --- | --- | --- |
| 建玉照会の不明・失敗で追随を打ち切ったことが、人が見ていなくてもアラートとして上がる（系列名・ルールが CI の検査を通る） | カウンタ＋ PrometheusRule。`check-observability-assets.js` の A1・A2・R2 が通る | T-10-780〜787・`check-observability-assets.js` |
| 運用仕様書の障害対応表の `PositionDriftAdopted_error` 行の「アラートは無い」を実際の検知手段へ書き換える | `docs/operations/operations.md` | 目視（文書） |
| IADR-0370 決定 3′ の代償の記述を追随させる（追記ブロック） | `［2026-09-25 追記 / #942］` | 目視（文書） |

## 試験（T-10-780〜）

| ID | 何を固定するか | 種別 |
| --- | --- | --- |
| T-10-780 | 最後の配送で照会が `null` → `reason=positions-unknown` を 1 増やし、変えずに投げる | 単体（業務クラス） |
| T-10-781 | 最後の配送で照会が例外 → `reason=positions-query-failed` を 1 増やす | 単体（業務クラス） |
| T-10-782 | 最後でない配送の不明・空の一覧（0 株）・照会が回復して追随が進む場合は**数えない**（否定形・隔離した Meter 名） | 単体（業務クラス・否定形） |
| T-10-783 | ハンドラは `Attempts` 1〜3 で「最後ではない」、4 以上で「最後」を業務クラスへ渡す | 結合（ハンドラ） |
| T-10-784 | `MaxDeliveryAttempts` が共通の失敗規則と一致する（1〜N−1 回目は再試行・N 回目は `_error` へ移す） | 単体（配線） |
| T-10-785 | 🔴 `Program.cs` そのもので組んだ業務クラスが DI のシングルトンの `BusinessMetrics` を保持し、最後の配送の打ち切りを数える（登録を消す・null を渡す変異を殺す） | 結合（本番の組み立て） |
| T-10-786 | 🔴 `Program.cs` が起動完了後に 0 を計上し、それが OTel の exporter まで届く（2 つの理由とも） | 結合（本番の組み立て） |
| T-10-787 | 業務メトリクス単体: 0 の計上は 2 つの理由の系列を作り件数は増えない・理由つきで 1 件ずつ・語彙の外の理由を拒む | 単体（否定形を含む） |

## 🔴 検証できないこと（実バックエンドが要る）

- **Prometheus に `ast_order_drift_adoption_followup_abandoned_total` が現れること**、`increase()` が 0 → 1 を拾ってルールが発火すること、
  Alertmanager へ届くこと。本 PR が固定するのは「計器が 0 から始まり、最後の配送の打ち切りで 1 増え、その名前をルールが引き、検査器が突き合わせる」までである。
- remote write の経路で接尾辞 `_total` が付くこと（`add_metric_suffixes` の既定に依存。既存の 1 件目と同じ前提）。
- 基盤（MSP）の経路B の Prometheus は `PrometheusRule` を読まない（`rule_files` にインラインの `alerts.yml` を読む素の Prometheus である）。
  本ファイルを誰がどう載せるかは IADR-0374 決定 4 のとおり配備側の作業で、1 件目と同じ状態である。
