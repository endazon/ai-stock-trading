---
title: 試行台帳へ記録する IS Sharpe を DSR の観測値と同じ 1 期間（日次）の単位に揃える（#1381）
type: spec
status: done
related_ids: [FR-15, ADR-0039, ADR-0008, ADR-0036, IADR-0044, IADR-0110, IADR-0337, IADR-0554]
author: claude (Claude Code)
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0039_pbo-unevaluable-without-search-and-trial-floor-ownership.md (決定 1・SR0 の説明)
  - planning:projects/ai-stock-trading/07_adr/ADR-0036_stage0-input-completeness-and-split-fixation.md
  - planning:docs/glossary.md (期待最大シャープ比)
---

# 仕様書: 試行台帳の IS Sharpe を日次に揃える（#1381）

> 本仕様書は実装着手前に作成した（着手 2026-10-17）。基点は AST `origin/develop` `98f28a22`。
> 計画は project-planning `origin/main` `55e464a` で読んだ。

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **FR-15**（検証条件④ 試行数記録と過剰適合補正）。計画 ADR-0039 決定 1（試行数の記録は免除しない）、ADR-0008。
- 起点 issue: **#1381**（`Closes #1381`）。発見元 #1307（planning#806 に付記済み）。
- 新規 IADR: **IADR-0554**（develop の最大は IADR-0553。open PR は dependabot・CHANGELOG の 2 本で IADR を足さない。欠番可・最大＋1。push 直前に再確認する）。

## 現状（コードで確かめた事実）

1. `Stage0ReplayEvaluation.Prepare` は `baseline.Metrics.SharpeRatio` を `TrialLedger` に記録する。`BacktestMetricsCalculator.AnnualizedSharpe` は `mean / sampleStd × √252`（**年率**）。
2. `Stage0GateService.Evaluate` は DSR の観測値に `SampleMomentsCalculator.Compute(DailyReturns).SharpePerPeriod`（**日次**）を、SR0 に `ExpectedMaxSharpe(Trials.InSampleSharpeVariance(), Trials.Count)` を使う。台帳が年率なら SR0 は √252 倍（分散は 252 倍）になる。
3. 試行 1 本では `ExpectedMaxSharpe` が `trials < 2` で 0 を返すため、現行の判定には影響しない。
4. 較正ハーネス（`Tests/Calibration/Stage0NoiseCalibration`）は既に `SharpePerPeriod`（日次）を台帳に記録している —— 本番経路だけが年率だった。

## 母集合（規則 9・10）

- 走査: `grep -rn "InSampleSharpe\|BacktestTrial(\|new TrialLedger" backend --include=*.cs`（bin/obj 除く）。
  - 本番の記録点: `Stage0ReplayEvaluation.cs`（1 箇所・年率＝是正対象）。
  - 本番の読み手: `Stage0GateService.cs`（`InSampleSharpeVariance`・`Count`）。`BestByInSampleSharpe` の本番利用者は無い（順位は単調変換で不変）。
  - 試験: `TrialLedgerTests`（単位を持たない合成値）、`Stage0GateServiceTests`（合成値 2.8〜3.0。観測値の `RisingMetrics` の日次 Sharpe ≈ 3.0 と同じ桁であり、日次として読める＝値は変えず注記だけ直す）、`Stage0NoiseCalibration`（日次・変更不要）。
- 文書: `grep -rn "TrialLedger\|試行台帳"` で `docs/functional/FR-15_backtest.md`・`docs/tests/FR-15_backtest-tests.md`・`IADR-0044`・`IADR-0337`。いずれも単位を書いていない。`docs/functional/FR-15_backtest.md` 検証条件④の行に単位を足す（10: 単位を明記しないと同じ誤りが再発する）。`docs/tests` に新しい試験を載せる。凍結の IADR-0044 は本文を書き換えず、索引の行へ日付つき追記を置く。
- 計画: ADR-0039・ADR-0036・ADR-0008・glossary「期待最大シャープ比」に台帳の単位の記述は無い（`grep -n "年率\|日次\|単位\|√\|252"`）。DSR の式は観測値と SR0 が同じ頻度であることを前提にしており、本是正と矛盾しない ⇒ 環流 issue は起票しない。

## 方式（比較は IADR-0554）

- **台帳へ日次を記録する**（算出時の換算は採らない）。
- `BacktestTrial.InSampleSharpe` を `InSampleSharpePerPeriod` へ改名し、単位を型の名前に持たせる（記録点がコンパイルで洗い出される）。
- 記録の作り方を 1 箇所に寄せる: `BacktestTrial.FromMetrics(name, metrics, outOfSampleReturn)` が `SampleMomentsCalculator.Compute(metrics.DailyReturns).SharpePerPeriod` を記録する（判定器の観測値と同じ計算）。`Stage0ReplayEvaluation` はこれを使う。
- 判定器・`DeflatedSharpeRatio` の式は変えない。

## 受け入れ基準（試験へ写像）

| # | 基準 | 試験 |
| --- | --- | --- |
| A1 | 試行 2 本以上の台帳で、観測値と SR0 が同じ単位（日次）で比べられる | `TrialLedgerTests.FromMetricsは判定器の観測値と同じ1期間Sharpeを記録する`、`Stage0GateServiceTests.試行2本の台帳のSR0は日次Sharpeの分散から求まる` |
| A2 | 年率で記録した台帳では SR0 が √252 倍に膨らむ（変異の対照） | `Stage0GateServiceTests.年率で記録した台帳ではSR0がルート252倍に膨らむ_単位の食い違いの対照` |
| A3 | 本番の記録点が日次を記録する | `Stage0ReplayEvaluationTests.試行台帳には判定器の観測値と同じ日次Sharpeを記録する` |
| A4 | 試行 1 本の現行の判定結果が変わらない | `Stage0ReplayEvaluationTests.試行1本の判定結果は台帳の単位に依らず不変である` |

変異: `BacktestTrial.FromMetrics` を `metrics.SharpeRatio`（年率）に戻すと A1・A3 が赤になることを手元で確かめる。

## 範囲外

- 探索（試行 2 本以上）の実装そのもの。
- `BacktestMetrics.SharpeRatio`（年率）の表示用途・契約は変えない。
