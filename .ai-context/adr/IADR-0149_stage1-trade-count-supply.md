---
title: IADR-0149 Stage 1 の取引件数を「実発注したアダプタの発注先」つき約定から集計し、計上単位を新規建て 1 注文に定める
type: impl-adr
status: Accepted
related_ids: [FR-20, FR-12, FR-05, UC-06, SC-03, ADR-0008, IADR-0113, IADR-0137, IADR-0140, IADR-0142, IADR-0148]
author: endazon (with Claude Code)
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# IADR-0149: Stage 1 の取引件数を「実発注したアダプタの発注先」つき約定から集計し、計上単位を新規建て 1 注文に定める

- 状態: Accepted
- 日付: 2026-08-05
- 決定者: 実装（Claude Code）／ 起点 issue [#386](https://github.com/endazon/ai-stock-trading/issues/386)
- 作業仕様書: [20260805_386_stage1-trade-count](../specs/20260805_386_stage1-trade-count.md)

## 起点・関連

- 関連する計画書 ID: **FR-20**（Stage 1 の合格判定は `SIMULATE` の約定のみで集計）／ **FR-12**（内蔵 `paper` はデバッグ用）／
  06_daytrading-review §4.1 条件3 / §4.3（計画リポ）
- 先行する実装 ADR: [IADR-0142](IADR-0142_stage1-simulate-only-aggregation.md)（観測は発注先を必須で伴う・算入は許可制）／
  [IADR-0140](IADR-0140_broker-provider-axis.md)（発注先の 2 軸分離）／
  [IADR-0148](IADR-0148_control-violation-supply-and-unavailable-state.md)（観測ログによる供給の形）

## コンテキストと課題

計画 §4.1 条件3 は Stage 1 → 2 の合格条件として「**最小取引件数 100 件**」を定め、FR-20 は
「経過営業日数・**取引件数（100 件）**・統制違反件数のいずれも `SIMULATE` の約定のみで数え」ると定める。

[#333](https://github.com/endazon/ai-stock-trading/issues/333) は判定側を、
[#334](https://github.com/endazon/ai-stock-trading/issues/334)（[IADR-0142](IADR-0142_stage1-simulate-only-aggregation.md)）は
集計の純関数（`Stage1FillObservation` / `Stage1Aggregation.CountTrades`）を作ったが、**どちらも呼ばれていない**。
IADR-0142 自身が残余リスクに「本 PR の集計関数はまだ呼ばれておらず、進捗は 0 のままである」と書いている。

決めるべきは 2 点である。

1. **発注先をどこから得るか**（`OrderExecuted` は発注先を持たない）
2. **100 件の計上単位は何か**（計画は単位を定義していない）

## 検討した選択肢

### 論点1: 発注先の出どころ

1. **既存の記録から `DecisionId` で引く**（`approved_orders.Mode` ／
   [#387](https://github.com/endazon/ai-stock-trading/issues/387) の `order_screening_observations.Provider`）—
   イベント契約を変えずに済むが、**両者の実体は `OrderIntent.Mode` であり現在の発注先ではない**。
   `SizingContextService` が `settings.Stage.Mode`（＝**段階が定める既定の発注先**）から作っており、
   [IADR-0140](IADR-0140_broker-provider-axis.md) 決定3/4 がそう明言している。
   Stage 1 の `Stage.Mode` は常に `MoomooSimulate` であるため、**内蔵 `paper` で稼働していても約定が
   `SIMULATE` として計上される**。計画が名指しで禁じた汚染がそのまま成立するため採れない。
2. **`RiskManagementSettings.BrokerProvider`（現在の発注先設定）を使う** — 段階から独立した「現在値」ではあるが、
   [IADR-0140](IADR-0140_broker-provider-axis.md) 残余リスクのとおり**この設定はまだ発注経路を動かさない**。
   実際にどのアダプタへ出るかは起動時構成（`Broker:Provider` / `Broker:Environment`）が決める。
   設定と実体が食い違い得る値を証跡の根拠にはできない。
3. **実際に発注したアダプタが `OrderExecuted` に載せる（採用）** — 「どこへ発注したか」を知っているのは
   発注執行サービスだけである。契約変更を伴うが、**真値を運ぶ唯一の経路**である。

### 論点2: 計上単位

計画は「100 件」の単位を定義していない。分割約定・再送・手仕舞いの扱いで件数は最大 2〜3 倍変わる。

1. **`OrderExecuted` 1 通＝1 件** — `FilledQuantity` はブローカの**累積**値であり、moomoo は同一注文について
   `Accepted`(0) → 部分約定 → 全量約定と複数回発行する（[IADR-0113](IADR-0113_moomoo-fill-polling.md)）。
   1 注文が 2〜3 件に膨らむ。膨らむ側は「合格に早く届く」＝緩い側であり、しかも成功したように見える。
2. **約定した注文 1 件＝1 件（新規建て・手仕舞いの両方を数える）** — 分割約定は畳めるが、
   手仕舞いを別件として数えるため、計画が比較に用いた単位のおよそ 2 倍になる。
3. **約定した新規建て注文 1 件＝1 件（採用）** — 計画の記述と単位が一致する（後述）。
4. 決済済みの往復（ラウンドトリップ）1 回＝1 件 — 意味としては 3 とほぼ同じだが、
   建玉の対応付け（どの決済がどの新規建てに対応するか）を実装が持つ必要がある。
   計画は決済追跡を**不要**と裁定しており（§4.3 注記）、対応付けの機構を新設する根拠が無い。

## 決定

### 決定1: 発注先は**実際に発注したアダプタ**が `OrderExecuted` に載せる

- `IBrokerAdapter` に `BrokerProvider Provider { get; }` を足す。
  `PaperBrokerAdapter` → `InternalPaper`、`MoomooBrokerAdapter` → `BrokerSelection.ToBrokerProvider()` の解決結果
  （対応表は [IADR-0140](IADR-0140_broker-provider-axis.md) 決定6 のものをそのまま関数化した。新しい規則は作っていない）。
- `OrderExecuted` に `BrokerProvider Provider` を**既定値なし**で足す
  （[IADR-0142](IADR-0142_stage1-simulate-only-aggregation.md) 決定1 の踏襲。省略できるようにすると
  書き忘れが「算入される側」へ倒れる）。実際、この変更で全発行箇所がコンパイルエラーになり、
  明示的に「何を載せるか」を書かせた。
- **本フィールドを持たない在庫メッセージは enum 既定 0 ＝ `InternalPaper` へ落ちる。**
  これは算入されない側であり fail-safe である。IADR-0140 決定3 では同じ既定 0 が
  「実弾判定を通してしまう」フェイルオープンだったが、Stage 1 の算入判定では向きが逆になる
  （許可制のため、名指しされない値は数えられない）。

### 決定2: 計上単位は「算入対象の発注先で約定が成立した**新規建て注文 1 件**」

`DecisionId`（1 取引判断＝1 注文）で一意とし、分割約定の続報・イベント再送でも 1 件である。
`Stage1FillObservation(DecisionId, SessionDateEasternTime, Provider, PositionEffect)` が単位を型で表し、
`Stage1Aggregation.CountsAsTrade` / `CountTrades`（`DecisionId` で `Distinct`）が集計する。

**計画は単位を定義していないが、単位を推し量れる記述が 2 か所ある。**

- 05_trading-assumptions §5（計画リポ）
  「1 日あたりの発注金額上限」は**新規建ての発注代金の合計**で判定し手仕舞いを算入しないと定めたうえで、
  「個人デイトレーダーの**取引件数**は 1 日 3〜5 件が一般的であり、1 注文上限いっぱいでも **6 件（＝2 回転）**まで
  収まる」と書く。ここでの「件」は**新規建ての件数**である。
- §4.3 は同じ出典（1 日 3〜5 件）を用いて「100 件 ÷ 60 営業日 ＝ 1 営業日あたり約 1.7 件」と比較している。
  比較が成立するには、100 件も同じ単位でなければならない。

加えて条件3 の目的（「勝率の推定が判断材料になる」標本数）にとって、標本は往復 1 回であり
新規建て 1 件と 1 対 1 に対応する。手仕舞いを別件として数えると標本数を約 2 倍に見積もることになる。

**この読み方は計画に明記が無いため、環流記録を残した**
（feedback/20260805_fr20-stage1-trade-count-unit.md（環流記録））。
迷った場合に**少なく数える側**（＝昇格が遅れる側）を選んだのは、件数の水増しが
「実力が無いまま実弾へ進む」という不可逆な失敗に直結するためである。

**建玉効果は承認台帳（`approved_orders`）から `DecisionId` で引く。** `OrderExecuted` は建玉効果を運ばない。
**引けなければ算入しない**——不明を「新規建て」と決め打つと、算入される側へ倒れる。

### 決定3: 供給は約定の観測ログとし、`stage_performance.Stage1TradeCount` 列は削除する

`OrderExecutedStage1FillHandler` が `OrderExecuted` を購読し、`stage1_fill_observations`
（`DecisionId` 主キー＝計上単位そのもの）へ記録する。`StageGateService` が判定の直前に
`StagePerformance` へ件数を重ねる。

- **件数を実績行の列にも持たせない。** 供給元が 2 つになれば必ず食い違う——観測ログだけが計上単位
  （1 注文 1 行）を担保するのに対し、列は担保しない。死んだ列を残すと「まだ使う値」に見え、
  次の実装者が判定へ結線し直す余地が残る（[IADR-0137](IADR-0137_stage1-trading-day-counting.md) 決定2 /
  [IADR-0148](IADR-0148_control-violation-supply-and-unavailable-state.md) 決定2 と同じ規律）。
- **「未供給」と「0 件」を型で区別しない。** [#387](https://github.com/endazon/ai-stock-trading/issues/387) が
  `ControlViolationTally?` を要したのは、違反件数の 0 が「条件充足」を意味する唯一 fail-safe でない入力
  だったためである。**取引件数の 0 は「条件未充足＝昇格しない」に倒れる。**
  同じ形を機械的に真似すると、意味の無い区別が判定を複雑にし、複雑さそのものが次の欠陥を隠す。
- **観測窓は受理された段階遷移で区切る**（統制違反の窓・IADR-0148 決定4 と同条件）。
  計画は起算点を「Stage 1 遷移日」と定める（§4.2）。3 指標が同じ「Stage 1 の期間」を指す必要がある。
  受理されなかった遷移要求では区切らない（否定形テストで固定した）。

## 結果

- **#334 で作った集計関数が初めて実際に呼ばれる。** Stage 1 の進捗表示（SC-03）が実測値を出すようになる。
- 「どこへ発注したか」の真値がイベントに載り、内蔵 `paper` の擬似約定が合格証跡へ混入する経路が
  型と結線の両方で塞がった。
- 計上単位が主キー制約（`DecisionId`）として表現され、分割約定・再送での膨張が構造的に起こらない。

### 悪い影響 / トレードオフ

- **`OrderExecuted` は破壊的変更である。** 発行元 3 か所（`OrderExecutionService` / `OrderFillPoller` /
  `OrderReservationReconciler`）と全テストがコンパイルエラーになり、明示的な追随を要した。
  在庫メッセージは既定 `InternalPaper` に落ちるため、移行中の約定は算入されない（安全側だが過小になる）。
- **`stage_performance.Stage1TradeCount` の削除は破壊的である。** `Down` で列は復元できるが値は 0 に戻る。
  もっとも、この列に意味のある値が入る経路は一度も存在しなかったため実質的な損失は無い。
- **約定 1 件につき 1 行が増える。** 計画の想定件数（100 件 / 60 営業日）では問題にならないが、
  保持期間の規定は計画に無い（#387 と同じく未決）。

### 残余リスク

- **計上単位は計画の明記ではなく実装の読み取りである。** 環流記録で確認を求めた。もし計画側が
  「手仕舞いも数える」と裁定した場合、本実装の件数は約半分に見積もられており、昇格が遅れる方向へ外れる。
- **再発行される `OrderExecuted` の発注先が当時と異なり得る。** `ExecutedOrderRow` は発注先を保持しないため、
  `OrderExecutionService` の再発行経路（相1・既存結果の再発行）と `OrderReservationReconciler` は
  **現在の**アダプタの値を載せる。観測は `DecisionId` で先着優先のため、一度でも記録されていれば
  上書きされない。誤った値が載るのは「発注時の観測が届かないまま構成の発注先を変更し、
  その後に同じ `OrderApproved` が再配送される」順序に限られる（構成変更は再デプロイを伴う）。
  完全に塞ぐには `executed_orders` へ発注先の列を足す必要があり、別 issue の範囲とした。
- **`SessionDateEasternTime` はタイムゾーン変換で導出する**（`MarketCalendar` と同方式）。
  本値は日次の突合・監査のためだけに持ち、**件数の判定には用いない**ため、解決が誤っても件数は汚染されない。
- **`StopLossExecutionService` 等の機械執行も新規建てなら算入され得る。** 現状これらは手仕舞い（`Close`）
  しか生成しないため件数には影響しない。

## 関連

- 計画: 06_daytrading-review §4.1 条件3 / §4.3（計画リポ）／
  02_requirements FR-20（計画リポ）／
  05_trading-assumptions §5（計画リポ）
- 実装 ADR: [IADR-0142](IADR-0142_stage1-simulate-only-aggregation.md)（許可制・観測は発注先を必須で伴う）／
  [IADR-0140](IADR-0140_broker-provider-axis.md)（発注先の 2 軸分離・段階の `Mode` は現在値ではない）／
  [IADR-0148](IADR-0148_control-violation-supply-and-unavailable-state.md)（観測ログによる供給・窓の区切り）／
  [IADR-0113](IADR-0113_moomoo-fill-polling.md)（約定数は累積値・同一注文で複数回発行される）
- 環流: 20260805_fr20-stage1-trade-count-unit（環流記録）
- 仕様書: [作業仕様書 20260805_386](../specs/20260805_386_stage1-trade-count.md)／
  [FR-20 機能仕様書](../../docs/functional/FR-20_staged-gates.md)／[FR-20 テスト仕様書](../../docs/tests/FR-20_staged-gates-tests.md)
