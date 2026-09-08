---
title: IADR-0310 Stage 0 の駆動は既定無効の常駐とし、空データとプレースホルダ戦略はいずれも不合格固定で publish する
type: impl-adr
status: Accepted
related_ids: [FR-15, FR-20, ADR-0008, ADR-0023, ADR-0033, IADR-0089, IADR-0105, IADR-0129, IADR-0157, IADR-0276, IADR-0281, IADR-0304]
author: endazon (with Claude Code)
created: 2026-09-09
updated: 2026-09-09
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0033_stage0-evaluation-target-is-ai-decision-replay.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0023_us-daily-ohlc-history-source.md
---

# IADR-0310: Stage 0 の駆動は既定無効の常駐とし、空データとプレースホルダ戦略はいずれも不合格固定で publish する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は planning へ issue で環流する（`feedback.yml` テンプレート）。

- 状態: Accepted
- 日付: 2026-09-09
- 決定者: endazon（マージ判断）/ Claude Code（起案・実測）

## 起点・関連

- 関連する計画書 ID（FR/UC/SC/ADR）: **FR-15**（バックテスト＝Stage 0 の必須ゲート）・**FR-20**（段階ゲート）・
  **ADR-0008**（段階ゲートとバックテスト）・**ADR-0033**（Stage 0 の評価対象は AI 判断そのもの・記録再生方式。2026-09-05 裁定）・
  ADR-0023 決定5（米国株日足 OHLC 履歴源。未確認 2 点により既定は `none`）
- 対象 Issue: [#688](https://github.com/endazon/ai-stock-trading/issues/688)（親 [#632](https://github.com/endazon/ai-stock-trading/issues/632)）
- 関連する実装仕様書: [20260909_688_stage0-bus-and-driver](../specs/20260909_688_stage0-bus-and-driver.md)
- 関連 IADR: [IADR-0089](IADR-0089_backtest-verdict-supply.md)（verdict はイベントで供給し Risk が read-modify-write で射影。
  **本 IADR はその「go-live 側の発行ホスト」の第一歩である**）・[IADR-0105](IADR-0105_backtest-historical-bar-source.md)（過去データ源の安全既定）・
  [IADR-0129](IADR-0129_wolverine-messaging-topology.md)（Wolverine/RabbitMQ 共通配線）・
  [IADR-0276](IADR-0276_claude-md-vsa-correction-and-hosted-placement.md)（`Hosted/` は第4の頂点）・
  [IADR-0304](IADR-0304_short-sell-strategy-observed-not-declared.md)（空売りは走行の観測から導く）

## コンテキストと課題

`BacktestService` は Stage 0 判定の器（`Stage0GateService` / `BacktestEvaluatedFactory`）を持ちながら、
**Wolverine/RabbitMQ を参照しておらず、`BacktestEvaluated` を publish する経路が無かった**（csproj で実測）。
受け側の `BacktestEvaluatedProjectionHandler`（RiskManagementService）は購読を配線済みで、**一通も届いていない**。
IADR-0089 は「実 publish ホストは #82／go-live 側で結線する」と分離しており、本作業がその結線である。

同時に、**評価対象（本番戦略）はまだ実装できない**。ADR-0033（2026-09-05 の利用者裁定・環流 planning#533 の回答）は
評価対象を「取引判断サービスの AI 判断そのもの」と定め、記録・再生方式で評価すると決めたが、記録器・記録再生戦略は未実装である。
**同 ADR の「統制と現在の実現手段」表は #688 を名指しで「プレースホルダ戦略での動作確認に留める」と定めている。**

したがって論点は次の 4 点である。

1. **駆動の形**（定時常駐か、明示的 HTTP エンドポイントか。既定は有効か無効か）
2. **過去データが空のときの振る舞い**（既定 provider は `none` であり、実運用は常にこの経路である）
3. **プレースホルダ戦略の verdict をどう扱うか**（本番の合否として記録されてはならない）
4. **RabbitMQ 配線の同型性**（サービスごとにトポロジを選ばない）

### 🔴 実測 —— `DataCutoffPolicy` は空バーを違反と見なさない

`DataCutoffPolicy.IsAllAfterCutoff(bars, cutoff)` は `bars.All(...)` であり、**空リストに対して `true` を返す**（真空的に真）。
`Stage0GateEvaluator` の 7 条件のうち、空バーを直接の理由として弾くものは無い。従来は
「バーが 0 本なら DSR・コスト頑健性・ウォークフォワードが不成立になるから不合格へ倒れる」という**間接的な**担保だけがあり、
`Program.cs` のコメントもこの論点（「DataCutoff は空を検出しない」）を未解決として残していた。
**間接的な担保は、入力の作り方が変われば崩れる**（例: 試行台帳や OOS リターンを別経路から供給する実装が入った瞬間、
バー 0 本でも 7 条件が揃い得る）。駆動側で明示的に塞ぐ。

## 検討した選択肢

| # | 駆動の形 | 評価 |
| --- | --- | --- |
| A | 定時常駐（`BackgroundService`）・**既定無効** | 他サービスの `Hosted/` と同型。無効時は 1 リクエストも出ない。**採用** |
| B | 定時常駐・既定有効 | 既定 provider が `none` である以上、無意味な巡回と verdict を毎日出し続ける。fail-safe でもない |
| C | HTTP エンドポイント（run-once） | `BacktestService` は HTTP 端点を持たない設計（IADR-0289 追記1 が「移送対象なし」と確定）。認可の口も無い。入口を増やす |

| # | プレースホルダ verdict | 評価 |
| --- | --- | --- |
| P1 | **不合格固定**（`Passed=false`・理由に `PlaceholderStrategy`） | 受け手（RMS）は「記録はされたが昇格は止まる」状態になる。**採用** |
| P2 | publish しない | 経路が通ったことを観測できない（#688 の受け入れ基準を満たせない）。配線の腐りに気付けない |
| P3 | 実際の判定結果をそのまま publish | **プレースホルダの成績が本番の合否として記録され得る**。最も避けるべき失敗様式 |

## 決定

### 決定1: 駆動は `Hosted/Stage0EvaluationService`（定時常駐）とし、**既定は無効**である

構成 `Backtest:Stage0:Enabled`（既定 `false`）。無効なら `ExecuteAsync` は最初の 1 行で return し、
**巡回もバー取得も publish も一切起きない**。有効時のみ `IntervalSeconds`（既定 86,400・下限 60）で巡回する。
置き場所が `Hosted/` なのは IADR-0276 決定と IADR-0289 追記1（`BackgroundService` は `Hosted/`）に従う。
HTTP の run-once は**作らない**（`BacktestService` は HTTP 端点を持たないサービスであり、入口と認可を増やす）。

**実効状態は自己申告に載せる**（`GET /internal/introspection` の port `stage0-driver` が `enabled` / `disabled`）。
「有効化したつもりで効いていない」を過去データ源と同じ手段で確認できるようにするためである（IADR-0105 決定5.1 と同型）。

### 決定2: 過去データが空なら**判定を走らせない**（fail-closed）

`MaterializedBarDataSource` が返したバーが 0 本のとき、駆動は `Stage0GateService.Evaluate` を**呼ばない**。
かわりに `NoHistoricalBars` を未達理由に載せた**不合格固定**の verdict を publish する。
**「空でも 7 条件が揃えば合格し得る」経路を、判定の手前で構造的に断つ。** 上記の実測（空は真空的に真）が理由である。

### 決定3: プレースホルダ戦略の verdict は**不合格固定**であり、合格を作れる口を持たない

- 評価対象は `PlaceholderStrategy`（注文を 1 件も出さない `IBacktestStrategy`）。`StrategyId` は `placeholder/no-op`。
- verdict の組み立ては `Stage0DriverVerdict`（純関数）に閉じ、**`Stage0GateResult(Passed: false, ...)` を直接組む**。
  この型は `Passed: true` を作る経路を持たない（`Stage0GateEvaluator` を呼ばない）。
- 未達理由に `PlaceholderStrategy` を**必ず**含める。受け手（RMS・監査）は `FailedChecks` を読めば
  「本番の合否ではない」ことが判る。
- `Stage0GateCheck` へ `NoHistoricalBars` / `PlaceholderStrategy` の 2 値を足した。理由文字列の作り方を
  `FormatFailedChecks()` の単一情報源に保つためであり、**`Stage0GateEvaluator` はこの 2 値を出さない**
  （判定器の 7 条件は不変）。
- カットオフ日（`Backtest:Stage0:LlmTrainingCutoff`）が未構成、またはバーがカットオフ以前を含むときは
  `DataCutoff` も未達に載せる（ADR-0033 決定3。カットオフ日の供給元が計画側に未登録である現状を、合格側へ倒さない）。

### 決定4: RabbitMQ 配線は他サービスと同型（共通ヘルパ 1 行）とし、本サービスは発行専用である

`builder.Host.UseWolverine(opts => opts.UseAiStockTradingRabbitMq(ServiceName, builder.Configuration["RabbitMq:ConnectionString"]))`
のみを呼ぶ（キュー名・fan-out・再試行・DLQ をサービス側で選ばない。IADR-0129 決定4。`check-consumer-endpoint-names.js` の N2/N3）。
ハンドラは持たない（`InformationCollectionService` と同型）。接続文字列は Helm / compose が既に全サービスへ注入している
（`RabbitMq__ConnectionString`）ため、配備側の変更は不要である。

### 決定5: 実 publish の E2E（実 RabbitMQ・実過去データ）は実環境の残件である

本 PR が担保するのは Wolverine のテストハーネス（`StubAllExternalTransports`）までである。
実ブローカを介した疎通は [#82](https://github.com/endazon/ai-stock-trading/issues/82) に残す（IADR-0089 決定4 の分離を維持する）。

## 理由

- **fail-safe の向きを揃えた。** 「駆動が無効」「バーが空」「戦略がプレースホルダ」のいずれも、
  結果は `BacktestPassed=false`＝昇格拒否である。**どの経路を通っても合格側へは倒れない。**
- **publish はする。** 経路が生きていることは観測できねばならない（配線の腐りは静かに起きる。IADR-0129 決定11 の失敗様式）。
  不合格固定の verdict は、経路の観測と昇格拒否の両方を同時に満たす唯一の形である。
- **理由を機械可読にした。** `FailedChecks` に `PlaceholderStrategy` / `NoHistoricalBars` が載るため、
  受け手も監査台帳も「なぜ不合格か」を文字列比較で読める。`Passed=false` だけでは
  「基準に届かなかった」と「そもそも評価していない」を区別できない。

## 結果

- 良い影響:
  - IADR-0089 が分離していた発行側の配線が実装され、`BacktestEvaluated` の経路が端から端まで（テストで）通った。
  - 空データの fail-closed が**判定の手前**に入ったため、将来 Stage 0 の入力供給が変わっても合格へ倒れない。
  - `BacktestService` がバスを持ったことで、本番戦略（ADR-0033 の記録・再生）が載った時点で
    **駆動と publish は書き換え不要**になる（差し替えるのは `IBacktestStrategy` の実装と評価文脈の供給だけ）。
- 悪い影響・トレードオフ:
  - `Stage0GateCheck` に「判定器が出さない値」が 2 つ増えた。**判定器の 7 条件と駆動側の事前条件が同じ enum に同居する**。
    分けると `FailedChecks` 文字列の作り方が 2 系統になり、区切り文字のドリフト（IADR-0089 が避けた失敗）が戻るため同居を選んだ。
  - 有効化しても**現状は必ず不合格**である。「動かしたのに合格しない」は仕様であり、`FailedChecks` を読めば理由が判る。
- フォローアップ:
  - 本番戦略（ADR-0033 決定1・2 の記録・再生）の実装 issue。載った時点で `PlaceholderStrategy` の未達理由は消える。
  - LLM 学習カットオフ日の計画側登録（ADR-0033 決定3）。登録されるまで `DataCutoff` は未達のままである。
  - 実 RabbitMQ / 実過去データでの E2E（#82）。
  - 試行台帳・PBO 行列・ウォークフォワード OOS の実供給（本番戦略と同時に要る）。

## 関連

- Supersedes: なし
- Superseded by: なし
