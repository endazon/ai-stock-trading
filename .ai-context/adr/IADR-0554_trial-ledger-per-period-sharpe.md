---
title: IADR-0554 試行台帳の IS Sharpe は DSR の観測値と同じ 1 期間（日次）の単位で記録する。算出時に換算せず、記録点を BacktestTrial.FromMetrics に寄せ、単位を名前（InSampleSharpePerPeriod）に持たせる
type: impl-adr
status: Accepted
related_ids: [FR-15, ADR-0039, ADR-0008, IADR-0044, IADR-0110, IADR-0337]
author: claude (Claude Code)
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0039_pbo-unevaluable-without-search-and-trial-floor-ownership.md（決定 1・SR0 の説明）
  - planning:docs/glossary.md（期待最大シャープ比）
related_specs:
  - ../specs/20261017_1381_trial-ledger-per-period-sharpe.md
---

# IADR-0554: 試行台帳の IS Sharpe を DSR の観測値と同じ 1 期間（日次）の単位で記録する（#1381）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-17
- 決定者: claude が起案（起点 [#1381](https://github.com/endazon/ai-stock-trading/issues/1381)。レビューは PR で受ける）

## 起点・関連

- 計画: FR-15 検証条件④、ADR-0039 決定 1（試行数の記録は免除しない）、glossary「期待最大シャープ比」（`DSR = Φ((SR − SR0)·√(T−1) / …)`）。
  計画は台帳の単位を定めていない。式は SR と SR0 が同じ頻度であることを前提にしている。
- 前提として覆さない IADR: [IADR-0044](IADR-0044_overfitting-correction.md)（試行台帳・DSR の構成）、[IADR-0110](IADR-0110_stage0-criteria-calibration.md)（較正。ハーネスは既に日次で記録していた）、
  [IADR-0337](IADR-0337_pbo-not-evaluable-without-search.md)（試行 1 本では PBO を評価しない）。
- 仕様書: `.ai-context/specs/20261017_1381_trial-ledger-per-period-sharpe.md`

## コンテキスト

- `Stage0ReplayEvaluation` は台帳へ `BacktestMetrics.SharpeRatio`（`mean / sampleStd × √252`＝年率）を記録していた。
- `Stage0GateService` は DSR の観測値に `SampleMoments.SharpePerPeriod`（日次）を使い、SR0 を台帳の分散から求める。
- 台帳が年率だと分散は 252 倍、SR0 は √252（約 15.9）倍になる。試行 2 本以上では SR0 が日次の観測値を越え、DSR は事実上必ず不合格になる。
- 試行 1 本の現行運用では `ExpectedMaxSharpe` が 0 を返すため表に出ていなかった。

## 検討した選択肢

| | A. 台帳へ日次を記録する（採用） | B. 算出時に換算する（分散 ÷ 252） |
| --- | --- | --- |
| 台帳の値の意味 | 1 つ（日次）。名前で読める | 記録点ごとに違い得る。換算の前提が判定器に隠れる |
| 較正ハーネス（IADR-0110） | 既に日次。変更不要 | 判定器で換算すると、ハーネスとの間で単位の約束が 2 通りになる。`ExpectedMaxSharpe` で換算するとハーネスの値が 1/√252 に縮む |
| 観測値との一致 | 同じ `SampleMomentsCalculator` を通すため、試行 1 本の台帳の値は観測値と一致する | 年率→日次の換算は 252 を判定器が知っている前提。日足以外の頻度で試行を記録すると再び食い違う |
| 変更の広さ | 記録点 1 箇所・型の名前 | 判定器 1 箇所。ただし台帳の値は年率のまま残る |

A を採る。B は単位の約束を判定器の中に隠し、台帳を読む他の利用者（最良候補の選択・将来の探索）に食い違いを残す。

## 決定

1. **台帳の IS Sharpe は 1 期間（日次）の Sharpe である。** `BacktestTrial.InSampleSharpe` を `InSampleSharpePerPeriod` へ改名し、単位を名前に持たせる。
   改名により、記録点はコンパイルで洗い出される（本番 1 箇所・試験・較正ハーネス）。
2. **記録の作り方を `BacktestTrial.FromMetrics(name, metrics, outOfSampleReturn)` に寄せる。** 値は
   `SampleMomentsCalculator.Compute(metrics.DailyReturns).SharpePerPeriod`（判定器が DSR の観測値に使う計算と同じ）。
   `Stage0ReplayEvaluation` はこれを使う。将来の探索も同じ口から記録する。
3. **判定器（`Stage0GateService`）と `DeflatedSharpeRatio` の式は変えない。** `BacktestMetrics.SharpeRatio`（年率）は表示・報告の値として残す。

## 結果

- 試行 2 本以上でも観測値と SR0 が同じ単位で比べられる（試験で固定）。
- 年率で記録した台帳では SR0 が √252 倍に膨らむことを対照の試験で固定した。記録点を年率へ戻す変異で、記録点の試験 4 本が赤になることを確かめた。
- 試行 1 本の現行の判定結果（DSR・失敗した条件・昇格推奨・PBO の判定結果）は、台帳の単位に依らず一致する（試験で固定）。

## 残余リスク

- 単位を型で強制してはいない（`double` のまま）。`new BacktestTrial(name, 年率, …)` と書けば再び食い違う。名前と `FromMetrics` で防ぐ。機械検査は同型の事故が 2 回起きたら検討する。
