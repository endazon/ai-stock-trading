---
title: 相場操縦パターン検知（FR-19）テスト仕様書
type: test-spec
status: draft
related_ids: [FR-19, ADR-0007]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
---

# テスト仕様書: 相場操縦パターン検知（FR-19）

> 作業仕様書 [20260711_manipulation-detector](../specs/20260711_manipulation-detector.md)（#49・[IADR-0037](../adr/IADR-0037_manipulation-detection-algorithm.md)）の
> 受け入れ基準を、実装済み xUnit テストへ写像した対応表。拡張点（判定ポート）の回帰は [FR-10 リスクガードコア](FR-10_risk-guard-core-tests.md) を参照。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-19（相場操縦とみなされ得る発注パターンの禁止）。関連 ADR-0007、IADR-0006/0037。
- 受け入れ基準の所在: `02_requirements/01_requirements.md`（FR-19）、`06_daytrading-review.md` §2.3（見せ玉・過剰訂正取消・板演出の禁止）。

## テスト対象・範囲

- 対象: `ManipulationPatternAnalyzer`（純関数コア）・`ManipulativeOrderPatternDetector`（判定ポート実装）・
  `InMemoryOrderActivitySource`（供給アダプタ）・`OrderScreeningService` 結合・`TradingDefaults` 既定しきい値。
- 対象外（切り分け）: 実注文履歴テレメトリ（#13/#17）供給、本番ホスト DI 登録、実 moomoo/実コンテナ E2E（#82）。

## 受け入れ基準 → テスト対応表

| 受け入れ基準 | テスト（クラス::メソッド） |
| --- | --- |
| 空窓・最小標本未満・正常取引は無嫌疑 | `ManipulationPatternAnalyzerTests::空窓は無嫌疑` / `最小標本未満は取消が多くても無嫌疑` / `正常なデイトレードは無嫌疑` |
| 過剰な取消の検知と境界（比率 0.7） | `過剰な取消を検知する` / `取消比率が境界以下なら過剰取消は検知しない` |
| 過剰な訂正の検知と境界（1 発注 3.0） | `過剰な訂正を検知する` / `過剰訂正は境界ちょうど3では検知しない` |
| 見せ玉（低約定率＋短命取消）の検知と境界 | `見せ玉_低約定率かつ短命取消の反復を検知する` / `見せ玉_約定率が境界ちょうど0_1では検知しない` / `見せ玉_短命取消が閾値ちょうど3件で検知する` / `見せ玉_短命取消が2件では検知しない` / `低約定率でも短命取消が閾値未満なら見せ玉は検知しない` |
| 板演出（同一方向の同時多段生存）の検知と境界 | `板演出_同一方向の約定なし取消の同時多段生存を検知する` / `板演出_時間差で重ならない取消は検知しない` / `板演出_反対方向が混在すると同時本数に数えない` |
| 複合該当は全シグナルを列挙 | `複合該当は全シグナルを列挙する` |
| 検出器が該当銘柄/市場の窓を取得して判定 | `ManipulativeOrderPatternDetectorTests::該当履歴のある銘柄の注文は相場操縦とみなす` / `履歴のない銘柄の注文は相場操縦とみなさない` / `別市場の同一コードは混同しない` / `正常な履歴の銘柄は相場操縦とみなさない` |
| 供給源の窓抽出・窓外刈り込み・（銘柄, 市場）分離 | `InMemoryOrderActivitySourceTests::窓内の記録だけを返す` / `記録のない銘柄は空窓を返す` / `銘柄と市場で記録を分離する` |
| **フラグ ON＋該当→拒否**／該当なし→承認／ガード無効→スキップ | `OrderScreeningManipulationTests::ガード有効かつ該当履歴の注文は相場操縦で拒否される` / `該当履歴がなければ承認される` / `ガード無効時は該当履歴でも相場操縦ではスキップする` |
| 既定しきい値の固定（IADR-0037） | `TradingDefaultsTests::相場操縦検知の既定しきい値はIADR0037の初期値と一致する` |

## テスト方針・件数

- 純関数コア（`ManipulationPatternAnalyzer`）は決定的で、各シグナルの該当/非該当/境界（`>` と `>=`・`<` の意図）を固定する。
- 検出器・供給アダプタは `IClock` 固定と InMemory 源で決定的に検証する。件数は Issue #49 単位（ドメイン +5 / アプリケーション +11 相当）。
- 実供給（#13/#17）・本番結線・実 E2E（#82）は CI 対象外（切り分け）で、テレメトリ確定後に追加する。

## 関連仕様

- 作業仕様書: [20260711_manipulation-detector](../specs/20260711_manipulation-detector.md)
- 機能仕様: [FR-19 取引ガード](../functional/FR-19_trading-guard.md)
- 実装ADR: [IADR-0037](../adr/IADR-0037_manipulation-detection-algorithm.md)（アルゴリズム）／[IADR-0006](../adr/IADR-0006_manipulation-guard-extension-point.md)（拡張点）
