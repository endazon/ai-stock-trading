---
title: IADR-0142 Stage 1 の合格集計から内蔵 paper を構造的に排除し、除外営業日数を別掲する
type: impl-adr
status: Accepted
related_ids: [FR-20, FR-12, FR-15, UC-06, SC-03, ADR-0008, IADR-0137, IADR-0140]
author: endazon (with Claude Code)
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/05_screens/01_screens.md
  - planning:projects/ai-stock-trading/06_technical/06_daytrading-review.md
---

# IADR-0142: Stage 1 の合格集計から内蔵 `paper` を構造的に排除し、除外営業日数を別掲する

- 状態: Accepted
- 日付: 2026-08-05
- 決定者: 実装（Claude Code）／ 起点 issue [#334](https://github.com/endazon/ai-stock-trading/issues/334)
- 作業仕様書: [20260805_334_broker-provider-axis](../specs/20260805_334_broker-provider-axis.md)

## コンテキストと課題

計画（FR-20（計画リポ））は次を定める。

> **Stage 1 の合格判定（経過営業日数・取引件数・統制違反件数 …）は `SIMULATE` の約定のみで集計し、
> 内蔵 `paper` の約定・稼働日数を算入してはならない**（`paper` で稼働した営業日は除外日数として別に数え、
> 進捗表示に併記する）。内蔵 `paper` は外部へ一度も発注しないため、算入を許すと 60 営業日・100 件という
> 合格証跡が擬似約定で積み上がる

[#333](https://github.com/endazon/ai-stock-trading/issues/333)（[IADR-0137](IADR-0137_stage1-trading-day-counting.md)）は
期間カウントの純ロジック（`Stage1TradingDayObservation` / `Stage1DayQualification` / `Stage1Gate`）を作ったが、
**観測に発注先の情報が無い**。稼働 100% の 1 日が内蔵 `paper` で積まれたものか SIMULATE で積まれたものかを
型の上で区別できず、集計側が「除外し忘れる」ことを止める手段が存在しなかった。

## 検討した選択肢

1. **集計を行う側（#386 の供給元）で内蔵 `paper` を除く** — 除き忘れが起こりうる。除き忘れは
   「合格証跡が擬似約定で積み上がる」という計画が名指しする最悪の結果に直結し、しかも**成功したように見える**。
2. **観測の型に発注先を必須で持たせ、集計関数が `MoomooSimulate` 以外を落とす（採用）** — 呼び出し側が
   発注先を書かないとコンパイルが通らない。除き忘れは型で不可能になる。
3. 発注先の既定値を `MoomooSimulate` にして省略可能にする — 書き忘れが**算入される側**へ倒れる。
   フェイルオープンであり採らない。

## 決定

### 決定1: Stage 1 の観測（稼働日・約定）は発注先を**必須**で伴う

- `Stage1TradingDayObservation(SessionDateEasternTime, RegularSessionMinutes, OperationalMinutes, Provider)`
- `Stage1FillObservation(SessionDateEasternTime, Provider)`

いずれも `Provider` に既定値を与えない。既定値を与えると「書き忘れ」が黙って通る。

### 決定2: 算入は `MoomooSimulate` の**許可制**（allow-list）とする

`Stage1Aggregation.IsCounted(provider) => provider == BrokerProvider.MoomooSimulate`。
`InternalPaper` を除外する形（deny-list）は採らない。

**計画は `MoomooReal` の扱いを名指ししていない。** 許可制なら、将来 4 つ目の発注先が増えたときに
「名指しされていない値」は黙って合格証跡へ流れ込まず、**明示的に許可するまで算入されない**。
拒否リスト方式だと、新しい値は既定で算入される側へ落ちる。統制の検証機構が誤った値で緑になるのは
最も悪い失敗モードである（[IADR-0135](IADR-0135_fx-freshness-plan-transcription-and-section3-scope.md) 決定6 と同じ規律）。

なお `MoomooReal` の約定が Stage 1 に現れること自体が異常事態である（段階が実弾を許していない）。
算入しないことは、その異常を合格証跡へ混ぜないという意味でも正しい。

### 決定3: 除外営業日数を `Stage1Progress` の第一級の値として持つ

`Stage1Progress(QualifiedTradingDays, TradeCount, ExcludedInternalPaperDays)`。

計画（05_screens（計画リポ） SC-03）は
「経過 42 / 60 営業日（`paper` 稼働により 3 日を除外）」という併記を求める。**除外の事実が見えないと
進捗の数字を説明できなくなる**——Stage 1 の途中で内蔵 `paper` へ落として戻す操作は起こり得るため、
「算入されなかった期間があること自体」を画面に出す必要がある。

除外日数に数えるのは「発注先が内蔵 `paper` であり、**それ以外の条件（稼働率 50% 以上）は満たしていた**日」に
限る。市場休場日・稼働率不足の日はもともと算入されない日であり、`paper` を理由に除外されたわけではない。
混ぜると「`paper` により 3 日を除外」という説明が嘘になる。

### 決定4: 集計の**供給元**は本 PR の範囲外（#386）

本 PR が作るのは「区別できる構造」と純関数だけである。日次の稼働分数を記録するドライバ、約定と発注先を
結び付ける経路、`StagePerformance` への注入は [#386](https://github.com/endazon/ai-stock-trading/issues/386) が担う。
`Stage1Progress` の既定（0 / 0 / 0）は fail-safe であり、供給が無い限り昇格しない
（[IADR-0137](IADR-0137_stage1-trading-day-counting.md) と同じ）。

## 結果

- 「内蔵 `paper` の約定・稼働日を Stage 1 の合格証跡へ混ぜる」経路が型の上で存在しなくなった。
- 除外日数が第一級の値になり、SC-03 の併記表示が計画どおり作れる。

### 残余リスク

- **本 PR の集計関数はまだ呼ばれていない。** 供給元（#386）が繋がるまで、Stage 1 の進捗は 0 のままである。
  これは安全側（昇格しない）だが、「実装した ≠ 効いている」の典型であり、#386 の完了までは
  画面の進捗表示も 0 を出し続ける。
