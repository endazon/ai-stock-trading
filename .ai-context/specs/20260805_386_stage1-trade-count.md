---
title: 作業仕様書 — Stage 1 の取引件数を発注執行の実発注先つき約定から集計する
type: work
status: review
related_ids: [FR-20, FR-12, FR-05, UC-06, SC-03, ADR-0008, IADR-0137, IADR-0140, IADR-0142, IADR-0148, IADR-0149]
author: endazon (with Claude Code)
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - planning:projects/ai-stock-trading/INDEX.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
related_specs:
  - ../adr/IADR-0149_stage1-trade-count-supply.md
  - ../adr/IADR-0142_stage1-simulate-only-aggregation.md
  - ../adr/IADR-0148_control-violation-supply-and-unavailable-state.md
  - ../adr/IADR-0140_broker-provider-axis.md
  - ../../docs/functional/FR-20_staged-gates.md
  - ../../docs/tests/FR-20_staged-gates-tests.md
  - 20260804_333_stage-gate.md
  - 20260805_334_broker-provider-axis.md
  - 20260805_387_class-c-violation-count.md
---

# 作業仕様書: Stage 1 の取引件数の供給（[#386](https://github.com/endazon/ai-stock-trading/issues/386)）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-20**（段階ゲート・Stage 1 の合格判定は `SIMULATE` の約定のみで集計）／
  **FR-12**（内蔵 `paper` はデバッグ用であり Stage 1 の実績に算入しない）／ **FR-05**（発注執行）
- ユースケース（UC）: **UC-06**（段階の参照・承認）
- 画面（SC）: **SC-03**（Stage 1 進捗の表示）
- 関連 ADR: **ADR-0008**（段階ゲート）
- 実装 ADR: [IADR-0149](../adr/IADR-0149_stage1-trade-count-supply.md)（本作業の決定）／
  [IADR-0142](../adr/IADR-0142_stage1-simulate-only-aggregation.md)（観測は発注先を必須で伴う・算入は許可制）／
  [IADR-0148](../adr/IADR-0148_control-violation-supply-and-unavailable-state.md)（観測ログによる供給の形）／
  [IADR-0140](../adr/IADR-0140_broker-provider-axis.md)（発注先の 2 軸分離。**段階の `Mode` は現在の発注先ではない**）
- 計画書リンク:
  06_daytrading-review §4.1 条件3 / §4.3（計画リポ）／
  02_requirements FR-20（計画リポ）

## 目的・背景

計画 §4.1 条件3 は Stage 1 → Stage 2 の合格条件として「**最小取引件数 100 件**」を定め、
FR-20 は「経過営業日数・**取引件数（100 件）**・統制違反件数のいずれも `SIMULATE` の約定のみで数え」ると定める。

[#333](https://github.com/endazon/ai-stock-trading/issues/333)（PR #384）は判定側（`Stage1Gate`）を、
[#334](https://github.com/endazon/ai-stock-trading/issues/334)（PR、[IADR-0142](../adr/IADR-0142_stage1-simulate-only-aggregation.md)）は
集計の純関数（`Stage1FillObservation` / `Stage1Aggregation.CountTrades`）を実装したが、
**いずれも呼ばれていない**。IADR-0142 自身が残余リスクとして「本 PR の集計関数はまだ呼ばれておらず、
進捗は 0 のままである」と記録している。本作業はその**供給を作る**ものであり、集計ロジックを作り直すものではない。

### 最大の障害: 約定イベントが発注先を持たない

`OrderExecuted(DecisionId, OrderId, Status, FilledQuantity, AveragePrice, ExecutedAt)` は**発注先を持たない**。
一方 `Stage1FillObservation` は `Provider` を**必須**で要求する（IADR-0142 決定1）。
したがって「約定イベントを購読して数える」だけでは実装できず、**発注先をどこから得るか**の設計判断が要る。

既存の記録から引く案は**採れない**。`ApprovedOrderRow.Mode` も `order_screening_observations.Provider` も
実体は `OrderIntent.Mode` であり、その値は `SizingContextService` が `settings.Stage.Mode`
（＝**段階が定める既定の発注先**）から作っている。[IADR-0140](../adr/IADR-0140_broker-provider-axis.md) 決定3/4 が
明言するとおり、これは**現在の発注先ではない**。Stage 1 の `Stage.Mode` は常に `MoomooSimulate` であるため、
内蔵 `paper` で稼働していても約定が `SIMULATE` として計上され、**計画が名指しで禁じた汚染がそのまま成立する**。

## 対象範囲

- 対象:
  - `OrderExecuted` が**実際に発注したアダプタの発注先**を運ぶようにする（`IBrokerAdapter.Provider`）
  - 約定を Stage 1 の取引件数の観測として記録し、`Stage1Progress.TradeCount` へ供給する
  - **計上単位**（分割約定・再送・建玉効果）を確定し、根拠を記録する
  - 観測窓を受理された段階遷移で区切る（起算点＝Stage 1 遷移日・§4.2）
  - 永続化（新テーブル＋マイグレーション）と、死んだ列（`stage_performance.Stage1TradeCount`）の削除
- 対象外:
  - 稼働営業日数の供給（[#385](https://github.com/endazon/ai-stock-trading/issues/385)）
  - 段階の自動昇格（設計上すべて利用者承認を要する）
  - `LiveTradingGate.LiveTradingReleased` の閂（触れない）
  - 発注先設定（`RiskManagementSettings.BrokerProvider`）と実発注経路の結線（IADR-0140 残余リスク・別 issue）

## 設計

### 1. 発注先の出どころ＝**実際に発注したアダプタ**（[IADR-0149](../adr/IADR-0149_stage1-trade-count-supply.md) 決定1）

- `IBrokerAdapter` に `BrokerProvider Provider { get; }` を足す。
  `PaperBrokerAdapter` → `InternalPaper`、`MoomooBrokerAdapter` → `BrokerSelection` から解決した値。
- `OrderExecuted` に `BrokerProvider Provider` を**既定値なし**で足す（IADR-0142 決定1 の踏襲）。
  発行元は `OrderExecutionService.ExecuteAsync` と `OrderFillPoller.PollOnceAsync` の 2 か所のみで、
  いずれも `broker.Provider` を用いる。
- 本フィールドを持たない在庫メッセージは enum 既定 0 ＝ `InternalPaper` へ落ちる。
  **これは算入されない側**であり fail-safe である。

### 2. 計上単位（[IADR-0149](../adr/IADR-0149_stage1-trade-count-supply.md) 決定2）

計画は「100 件」の**単位そのものを定義していない**。実装は次を前提として採り、環流記録を残す
（feedback/20260805_fr20-stage1-trade-count-unit.md（環流記録））。

> **1 件 ＝ 算入対象の発注先で約定が成立した新規建て（`PositionEffect.Open`）注文 1 件**
> （`DecisionId` で一意。分割約定・イベント再送でも 1 件）

- **分割約定を 1 件にする**のは、`OrderExecuted` の `FilledQuantity` が**累積値**であり
  （[IADR-0113](../adr/IADR-0113_moomoo-fill-polling.md)）、moomoo が `Accepted`(0) → 部分約定 → 全量約定と
  同一注文について複数回発行するためである。イベントを数えると同じ注文が 2〜3 件になる。
- **手仕舞い（`Close`）を数えない**のは、計画自身が同じ出典（1 日 3〜5 件）を根拠に
  「1 注文上限いっぱいでも **6 件（＝2 回転）**まで収まる」（05_trading-assumptions（計画リポ） §5）と
  書いており、その 6 件が**新規建てのみ**（1 日あたりの発注金額上限は手仕舞いを算入しない）を数えているためである。
  §4.3 の「100 件 ÷ 60 営業日 ＝ 1 営業日あたり約 1.7 件」も同じ出典・同じ単位で比較している。
  条件3 の目的（勝率・平均損益の推定）にとっても、標本は往復 1 回であり新規建て 1 件と 1 対 1 に対応する。
- **建玉効果が引けない約定は算入しない。** 承認台帳（`approved_orders`）に相関する行が無い場合は記録しない。
  不明を算入すると、内蔵 `paper` の擬似約定が合格証跡へ混入し得る（計画が名指しした最悪の結果）。

### 3. 集計（純関数・既存の型を拡張）

- `Stage1FillObservation(DecisionId, SessionDateEasternTime, Provider, PositionEffect)`。
  `DecisionId` と `PositionEffect` を追加する（#334 で作った型は供給が無かったため、計上単位を表現できていなかった）。
- `Stage1Aggregation`
  - `IsCounted(provider)` は既存のまま（`MoomooSimulate` の**許可制**・IADR-0142 決定2）
  - `CountsAsTrade(observation)` ＝ 算入対象の発注先 ∧ `PositionEffect.Open`
  - `CountTrades(fills)` ＝ `CountsAsTrade` を満たす観測の **`DecisionId` の相異なる個数**

### 4. 供給（結線）

```
OrderApproved   → OrderApprovedLedgerHandler   → approved_orders（建玉効果の出どころ）
OrderExecuted   → OrderExecutedStage1FillHandler
                    ├ FilledQuantity <= 0 なら何もしない（約定していない結果は数えない）
                    ├ approved_orders に相関が無ければ何もしない（不明は算入しない）
                    └ IStage1FillObservationStore.Record（DecisionId で冪等）
StageGateService → IStage1FillObservationStore.GetTradeCount() を StagePerformance へ重ねて判定へ渡す
                 → 受理された段階遷移で ResetWindow()（起算点＝Stage 1 遷移日）
```

- **`0` は fail-safe である。** 統制違反件数（#387・IADR-0148）と違い、取引件数の 0 は
  「条件未充足＝昇格しない」に倒れる。したがって `ControlViolationTally` のような
  「未供給」と「0」を型で区別する仕組みは**作らない**（同じ形を機械的に真似しない）。

### 5. 永続化

- 新テーブル `stage1_fill_observations`（`DecisionId` 主キー＝冪等・1 注文 1 行）。
  列は `ObservedAtUtc` / `SessionDateEasternTime` / `Provider` / `PositionEffect` / `CountsTowardStage1`。
  `CountsTowardStage1` は**記録時に純関数 `Stage1Aggregation.CountsAsTrade` が決めた結果**であり、
  SQL 側へ算入規則を写さない（IADR-0148 と同じ規律）。
- `stage_performance.Stage1TradeCount` 列は**削除**する。供給元が別テーブルへ移った以上この列は死ぬ。
  死んだ列を残すと「まだ使う値」に見え、次の実装者が判定へ結線し直す余地が残る
  （[IADR-0137](../adr/IADR-0137_stage1-trading-day-counting.md) 決定2・IADR-0148 決定2 と同じ規律）。
- マイグレーション `AddStage1FillObservations`。`Up` / `Down` を**実 PostgreSQL・既存データ**で確認する。

## 受け入れ基準

計画（§4.1 条件3 / §4.3・FR-20 の受け入れ基準）および #386 より。

- [ ] `SIMULATE` の約定件数が `StagePerformance.Stage1TradeCount`（＝ `Stage1Progress.TradeCount`）へ反映される
- [ ] **否定形（最重要）**: 内蔵 `paper` の約定を混ぜたデータで件数が汚染されない
- [ ] **否定形**: `MoomooReal` の約定も算入されない（許可制）
- [ ] **否定形**: 供給が途絶えても件数が水増しされない（記録が無ければ 0＝昇格しない）
- [ ] **否定形**: 同一注文の分割約定（累積 `FilledQuantity` の複数イベント）で二重計上しない
- [ ] **否定形**: 約定していない結果（`Accepted` / 約定 0 の取消・拒否）は計上しない
- [ ] **否定形**: 手仕舞い（`Close`）の約定は計上しない
- [ ] **否定形**: 承認台帳に相関が無い約定は計上しない（不明は算入しない）
- [ ] 受理された段階遷移で観測窓が区切られ、直後は 0 に戻る
- [ ] **否定形**: 受理されなかった遷移要求では窓が区切られない
- [ ] 発注執行が実際に用いたアダプタの発注先が `OrderExecuted` に載る

## テスト方針

| 観点 | 層 | 種別 |
| --- | --- | --- |
| `SIMULATE` の新規建て約定が 1 件として数えられる | Domain（`Stage1TradeCountAggregationTests`） | 正 |
| 内蔵 `paper` / `MoomooReal` を混ぜても件数が増えない | Domain | 否定形 |
| 同一 `DecisionId` の重複観測が 1 件になる | Domain・Infrastructure（EF 冪等） | 否定形 |
| `Close` の約定を数えない | Domain | 否定形 |
| 約定 0・承認台帳に無い約定を記録しない | Infrastructure（ハンドラ） | 否定形 |
| 実際に用いたアダプタの発注先が `OrderExecuted` に載る | Application（`OrderExecutionServiceTests` / `OrderFillPollerTests`） | 正 |
| 件数が段階ゲートへ供給され、100 件で昇格可能になる | Application（`StageGateServiceTests`） | 正 |
| 段階遷移で窓が区切られる／受理されなければ区切られない | Application | 正・否定形 |

## 計画書との差異

- 差異: **計上単位は計画に記述が無い**。実装は「約定した新規建て注文 1 件」を前提として採り、
  根拠を [IADR-0149](../adr/IADR-0149_stage1-trade-count-supply.md) 決定2 に、環流を
  feedback/20260805_fr20-stage1-trade-count-unit.md（環流記録） に残す。
- 差異: 計画は Stage 1 の「約定のみで集計」と定めるが、**発注先の判定に段階の既定モードを使ってはならない**という
  含意は計画に明示が無い。実装は IADR-0140 決定3/4 の帰結として実発注先を用いる（計画に反しない補強）。

## 未決事項

- **観測ログの保持期間**（retention）。計画に記述が無いため上限を設けない（#387 と同じ扱い）。
- **在庫メッセージ／再配送で発注先が取り違えられる窓**。`ExecutedOrderRow` は発注先を保持しないため、
  「発注時の観測が届かないまま構成の発注先を変更し、その後に同じ `OrderApproved` が再配送される」
  という順序でのみ誤った発注先が載り得る。観測は `DecisionId` で先着優先のため、
  一度でも記録されていれば上書きされない。残余リスクとして IADR-0149 に記録した。
