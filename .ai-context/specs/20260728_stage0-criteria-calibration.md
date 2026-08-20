---
title: Stage 0 合格基準（Stage0GateCriteria）の閾値較正と Stooq 取得不可の記録
type: spec
status: review
related_ids: [FR-15, FR-20, ADR-0004, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-28
updated: 2026-07-28
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/02_datasource-candidates.md
  - planning:projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0004_datasource-selection.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# 仕様書: Stage 0 合格基準の閾値較正（#208 残作業）

> Issue [#208](https://github.com/endazon/ai-stock-trading/issues/208) の残受け入れ基準②
> 「Stage 0 の 7 条件判定が実データで実行でき、**閾値較正の根拠が IADR に残る**」のうち、較正部分を閉じる。
> データ源アダプタ（Stooq）と no-op 既定は [PR #255](https://github.com/endazon/ai-stock-trading/pull/255)
> （[IADR-0105](../adr/IADR-0105_backtest-historical-bar-source.md)）で実装済み。
>
> **実弾には一切触れない。** 実弾 triple-latch（`Broker__Provider=paper` / `Broker:Moomoo:TrdEnv=simulate` /
> 起動時 real 拒否・[IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md)）は不変。本作業は
> 純ドメインの既定閾値 1 個とテストのみを変更し、稼働中の環境には触れない。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-15（バックテスト＝Stage 0 の前提・Must）、FR-20（段階ゲート）
- ADR: ADR-0008（計画リポ）（段階ゲートとバックテスト）、
  ADR-0004（計画リポ）（情報源＝案A+。検証・学習用に J-Quants Free ＋ Stooq）
- 計画書（技術検討）: 06_daytrading-review.md（計画リポ） §3.2
  （試行数の記録＋DSR による補正が標準）、02_datasource-candidates.md（計画リポ）（Stooq は個人系サイトで SLA なし）
- 関連 IADR: [IADR-0043](../adr/IADR-0043_backtest-foundation.md)、[IADR-0044](../adr/IADR-0044_overfitting-correction.md)、
  [IADR-0045](../adr/IADR-0045_stage0-gate.md)（**`MinTrials=1` が較正前の暫定値であることの明記**）、
  [IADR-0105](../adr/IADR-0105_backtest-historical-bar-source.md)（実過去データ源アダプタ・安全既定）、
  本作業で新規 [IADR-0110](../adr/IADR-0110_stage0-criteria-calibration.md)
- 対象 Issue: [#208](https://github.com/endazon/ai-stock-trading/issues/208)（`Refs #208`）

## 現状（develop `cc93e3a` で確認）

| 閾値 | 既定値 | 状態 |
| --- | --- | --- |
| `MinDeflatedSharpe` | 0.95 | 「真 Sharpe > 0 の確率」の要求水準 |
| `MaxProbabilityOfOverfitting` | 0.50 | CSCV の PBO 上限 |
| `MaxDrawdownTolerance` | 0.15 | 計画書（05_trading-assumptions）の DD 上限由来 |
| `MinTrials` | **1** | IADR-0045 が「較正前の暫定値」と明記 |

### 実データ取得の可否（着手時に実測）

**Stooq は現在プログラムから取得できない。** `stooq.com` / `stooq.pl` のいずれも、CSV ではなく
JavaScript の proof-of-work によるボット検知チャレンジ（HTTP 200・約 796 バイト）を返す。
正直な識別用 User-Agent を付けても同じだった。

```
<noscript>This site requires JavaScript to verify your browser.</noscript>
<script>… SHA-256 の総当たりで先頭 4 桁 0 を探し /__verify へ POST …</script>
```

**このチャレンジの回避は行わない**（ボット検知の回避に当たるため）。他の keyless な日足 OHLC 源は
計画書の情報源一覧に無い（J-Quants Free＝アカウント＋リフレッシュトークン、Finnhub／FRED＝API キー）。

## 目的

1. `MinTrials` の暫定値を根拠のある値へ較正し、根拠を IADR に残す（#208 受け入れ基準②の較正部分）。
2. 他の 3 閾値について「変更しない」判断も測定に基づいて記録する（据え置きの根拠を空白にしない）。
3. Stooq が取得不可であるという運用事実と、その場合に fail-safe が効くことを記録・固定する。

## スコープ

### 対象

1. **較正ハーネス**（`BacktestService.Domain.Tests/Calibration/`・テスト専用）
   - `DeterministicNormalSampler`: splitmix64 ＋ Box-Muller の決定論的正規乱数源。
     `System.Random` は種を固定してもランタイム実装で系列が変わり得るため、較正値の再現性のために自前実装する。
   - `Stage0NoiseCalibration`: 真のエッジ 0 の候補を `searchSize` 個試して IS Sharpe 最良を選び、
     台帳へ `recordedTrials` 件だけ記録したときの DSR を求める（**過少申告の再現**）。
     偽陽性率・SR0 の推定ばらつき・PBO 分布を測る。
   - `Stage0CalibrationReportTests`: IADR に載せた表を再生成する実行（`STAGE0_CALIBRATION_REPORT=1` で有効化・
     既定スキップ＝CI では走らせない）。
2. **`Stage0GateCriteria.Default.MinTrials` を 1 → 20 へ変更**（唯一の本番変更）。
3. **回帰テスト**: 較正後の下限未満（1/2/19 件）の台帳は他 6 条件を満たしても不合格。
4. **Stooq のチャレンジ応答が欠測として扱われる回帰テスト**（実際に受信した応答形をもとに固定）。
5. 既存テストの追随（合格を意図するケースの試行数を下限へ揃える）。

### 対象外（#208 に残置）

| 項目 | 理由 |
| --- | --- |
| 実市場データによる水準確認（実際の Sharpe/DD 分布に照らした妥当性） | Stooq が取得不可で、代替は資格情報を要する。**資格情報の要求・投入は行わない**（提供可否は利用者判断） |
| `MaxProbabilityOfOverfitting` の厳格化 | 雑音と既知エッジの識別力が乏しく（後述）、実データ無しでは偽陰性の代償を確定できない |
| J-Quants Free アダプタの実装 | 2 段認証＋ページングの契約確認に実アカウントが要る |
| 定時トリガ・`BacktestEvaluated` の実 publish・実コンテナ E2E | #82（本番戦略 `IBacktestStrategy` の実装が前提） |

## 較正の方法

**測るのは統計的性質であり、市場の性質ではない。** 「真のエッジが 0 の候補を N 個試して最良を選ぶ」ときに
Stage 0 のエッジ有意条件がどれだけ誤って合格を出すか（偽陽性率）は、合成標本で決まる。実市場データが要るのは
「実在の戦略がこの基準を通せるか（偽陰性の水準）」であり、そちらは残置する。

- 標本長 252 営業日（1 年）、DSR 閾値 0.95、反復 20,000 回、種 20260728（固定）。
- 過少申告の再現: 台帳には**最良候補を含む** `recordedTrials` 件だけを記録する（最も緩い記録）。
- PBO は戦略 10・区間 8・分割 8・区間長 63 の性能行列で測り、対照として戦略 0 に年率 Sharpe 1.0 の
  エッジを注入した場合の合格率（偽陰性の代償）も測る。

## 受け入れ基準 → テスト写像

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 較正用乱数源が決定論的（同じ種で同じ系列）かつ標準正規に従う | `DeterministicNormalSamplerTests.*`（3 ケース） |
| 2 | 記録 1 件では期待最大 Sharpe が 0＝多重検定補正が不活性 | `Stage0NoiseCalibrationTests.記録試行数1では期待最大Sharpeが0になる_多重検定補正が不活性` |
| 3 | 同じ探索結果でも記録件数を増やすと DSR は下がる（緩まない） | `Stage0NoiseCalibrationTests.同じ標本でも記録試行数を増やすとDSRは下がる_補正が効く` |
| 4 | 単一候補を正直に記録した場合の偽陽性率は名目 5% 付近 | `Stage0NoiseCalibrationTests.単一候補を正直に記録した場合の偽陽性率は名目水準付近` |
| 5 | 過少申告で偽陽性率が跳ね上がる（`MinTrials=1` の実害） | `Stage0NoiseCalibrationTests.探索を過少申告すると偽陽性率が跳ね上がる_MinTrials1の実害` |
| 6 | 正直に記録すれば探索を広げても偽陽性率は抑えられる | `Stage0NoiseCalibrationTests.正直に記録すれば探索を広げても偽陽性率は抑えられる` |
| 7 | 真のエッジ 0 の PBO は 0.5 付近＝閾値 0.5 は雑音の中心 | `Stage0NoiseCalibrationTests.真のエッジ0の性能行列ではPBOが05付近に集まる_閾値05は雑音の中心` |
| 8 | 記録 2 件では SR0 の推定が不安定（下限を 2 に置けない根拠） | `Stage0NoiseCalibrationTests.記録試行数2は期待最大Sharpeの推定が不安定_下限を2に置けない根拠` |
| 9 | 既定の最小試行数が 20（較正結果の固定） | `Stage0GateCriteriaTests.最小試行数の既定は20_多重検定補正が効く水準` |
| 10 | 下限は構造的境界（2）より上（trials<2 で補正が消える） | `Stage0GateCriteriaTests.最小試行数は2以上でなければ補正が消える_構造的下限` |
| 11 | 据え置いた 3 閾値の値を固定 | `Stage0GateCriteriaTests.較正で変更しない閾値は据え置く` |
| 12 | 下限未満の台帳は他条件を満たしても不合格 | `Stage0GateEvaluatorTests.較正後の下限未満の台帳は他条件を満たしても不合格`（Theory 3 ケース） |
| 13 | Stooq のボット検知チャレンジ応答は欠測として扱う | `StooqDailyCsvParserTests.ボット検知チャレンジ応答は解析失敗として扱う` |

## 完了条件

- `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx` が緑。
- `dotnet format` 適用済み・警告ゼロ。
- 較正表が `STAGE0_CALIBRATION_REPORT=1` で再生成でき、IADR-0110 の数値と一致する。
- CI では較正レポートを走らせない（既定スキップ）。
- 実弾 OFF／SIMULATE 固定は不変。稼働中の環境へは触れない（helm/kubectl 操作なし）。

## 残課題（本 PR 外・#208 に残置）

- 実市場データによる水準確認（資格情報付きデータ源が必要。提供可否は利用者判断）。
- `MaxProbabilityOfOverfitting` の再検討（実データでの偽陰性測定が前提）。
- J-Quants Free アダプタ、定時トリガ・実 publish・実コンテナ E2E（#82）。
