---
title: 相場操縦パターン検知アルゴリズムの実装（IADR-0006 の後続・注文履歴統計から見せ玉/過剰訂正取消/板演出を検知）
type: spec
status: done
related_ids: [FR-19, ADR-0007]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md
---

# 仕様書: 相場操縦パターン検知アルゴリズムの実装

> Issue [#49](https://github.com/endazon/ai-stock-trading/issues/49)（`Refs #49`）。PR #41（Issue #28・[IADR-0006](../adr/IADR-0006_manipulation-guard-extension-point.md)）で
> **拡張点のみ**（`TradingGuardSettings.ProhibitManipulativeOrderPatterns`／`RejectionReason.ManipulativeOrderPattern`／
> 判定ポート `IManipulativeOrderPatternDetector`）が用意された。本作業はその判定ポートの**具体実装**＝注文履歴の統計から
> 相場操縦とみなされ得る発注パターンを検知するアルゴリズムを、純関数コア＋アダプタとして実装する。

## 起点となる計画書・課題（トレーサビリティ）

- FR-19（Must・取引ガードに「相場操縦とみなされ得る発注パターンの禁止」を含める）、ADR-0007（取引ガードをソフト設定で発注前に決定的強制）。
- 計画上の禁止対象（[06_daytrading-review §2.3](../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md)）:
  - **見せ玉（約定意思のない発注）**／**板を演出する型（レイヤリング）**／**過剰な注文訂正・取消の反復**。
- 拡張点（PR #41）: `IManipulativeOrderPatternDetector.IsSuspectedManipulation(OrderIntent, PortfolioSnapshot)` を
  `RiskEvaluator`／`OrderScreeningService` が「ガード有効かつ検出器注入時のみ」呼ぶ（IADR-0006）。
- 本作業で新規 [IADR-0037](../adr/IADR-0037_manipulation-detection-algorithm.md)（検知アルゴリズムと既定しきい値の確定）。

## 設計方針

判定コアの純関数性（IADR-0003/0004）を保つため、**純アルゴリズム（Domain）**と**データ供給アダプタ（Application）**を分離する。

### Domain（純関数コア・CI で緑）

- `OrderActivityRecord`: 直近窓内の 1 注文のライフサイクル要約（発注時刻・状態・数量/約定数量・売買方向・訂正回数・終端時刻）。
  - 派生: `IsCancelledWithoutFill`（約定ゼロで取消/失効）・`LifetimeSeconds`（発注→終端の経過秒）。
- `OrderActivityWindow`: ある（銘柄, 市場）の直近窓の `OrderActivityRecord` 群＋基準時刻 `AsOf`。
- `ManipulationSignal`（enum）: `ExcessiveCancellations` / `ExcessiveAmendments` / `NoExecutionIntent` / `Layering`。
- `ManipulationVerdict`: `IsSuspected`＋該当 `Signals`（複数列挙。監査/将来のログ用に理由を保持）。
- `ManipulationDetectionSettings`: 窓長・最小標本数・各しきい値（[IADR-0037](../adr/IADR-0037_manipulation-detection-algorithm.md) に既定値と逆算根拠）。
- `ManipulationPatternAnalyzer.Analyze(window, settings) → ManipulationVerdict`: **純関数**。標本数が最小未満なら常に無嫌疑（低頻度の正常取引で誤検知しない安全側）。

判定ロジック（窓内・`placements = 発注数`）:

1. `ExcessiveCancellations`: `placements ≥ MinimumSampleSize` かつ `約定なし取消数 / placements > MaxCancellationRatio`。
2. `ExcessiveAmendments`: `placements ≥ MinimumSampleSize` かつ `訂正総数 / placements > MaxAmendmentsPerOrder`。
3. `NoExecutionIntent`（見せ玉）: `placements ≥ MinimumSampleSize` かつ 約定率が低い（`約定/一部約定数 / placements < MinFillRatio`）かつ
   短命取消（`LifetimeSeconds ≤ ShortLivedCancelSeconds` の約定なし取消）が `≥ MaxShortLivedCancels` 件。
4. `Layering`（板演出）: 同一売買方向の「約定なし取消」注文で、**生存区間が同時に重なる最大本数**が `≥ LayeringOrderCount`（板に複数段の見せ板を同時に並べる型）。

### Application（アダプタ・CI で緑）

- ポート `IOrderActivitySource.GetRecentActivity(symbol, market, asOf, lookback) → OrderActivityWindow`（同期。`RiskEvaluator` が同期純関数のため）。
- `ManipulativeOrderPatternDetector : IManipulativeOrderPatternDetector`: `IOrderActivitySource`＋`ManipulationDetectionSettings`＋`IClock` に依存し、
  `intent` の（銘柄, 市場）で窓を取得→`ManipulationPatternAnalyzer` を実行→`IsSuspected` を返す。
- `InMemoryOrderActivitySource`: （銘柄, 市場）別の直近リングバッファ。`Record(...)` で追記し窓外を刈る。テスト・将来のホスト結線で使う実装。

## 対象範囲（本 PR）

- Domain: `OrderActivityRecord` / `OrderActivityWindow` / `ManipulationSignal` / `ManipulationVerdict` / `ManipulationDetectionSettings` / `ManipulationPatternAnalyzer`。
- Application: `IOrderActivitySource` / `ManipulativeOrderPatternDetector` / `InMemoryOrderActivitySource`。
- `TradingDefaults.CreateManipulationDetectionSettings()`（既定しきい値）。
- テスト: アルゴリズムの各シグナル（該当/非該当・境界）・アダプタ（窓取得→判定）・`OrderScreeningService` 結合（**フラグ ON＋該当→拒否**）。
- IADR-0037（アルゴリズムとしきい値の確定）。

## 受け入れ基準

CI で緑にする範囲（ユニット・InMemory・結合）:
- [x] `ManipulationPatternAnalyzer` が 4 シグナルをそれぞれ検知し、最小標本未満・正常取引では無嫌疑を返す（境界含む）。
- [x] `ManipulativeOrderPatternDetector` が `intent` の銘柄/市場の窓を取得し、該当時に `true`・非該当時に `false` を返す。
- [x] `OrderScreeningService` にガード有効＋検出器を注入し、該当履歴では `OrderRejected`（`ManipulativeOrderPattern`）、正常履歴では承認（**フラグ ON＋該当→拒否**の担保）。
- [x] `TradingDefaults` の既定しきい値をテストで固定する（全体前提条件・IADR-0037 と一致）。
- [x] 既存テスト（拡張点・回帰）を緑に保つ。`nullable` 有効・警告ゼロ・`dotnet format` 準拠。

実 API/実コンテナ前提（CI 既定では実行しない・切り分け）:
- [ ] リスク管理ホスト（#12）本番 DI での検出器注入と、実注文履歴テレメトリ（発注・訂正・取消イベントの永続化 #13/#17）からの `IOrderActivitySource` 供給。
- [ ] 実 moomoo/実コンテナでの発注→検知の E2E（#82）。

## 対象外（後続）

- 実注文履歴の永続化（注文・訂正/取消イベント）＝ #13/#17 連動。本 PR の `InMemoryOrderActivitySource` は結線先確定までのプロセス内実装。
- 板の**外部**気配（他者注文）を用いた相場操縦検知（本 PR は**自口座**の発注統計に限定）。市場全体の板厚データ供給は対象外。
- しきい値の運用データによる較正（IADR-0037 のフォローアップ）。監査へ該当シグナル詳細を記録する拡張（#17/#80 連動）。

## テスト方針

- `ManipulationPatternAnalyzerTests`（Domain）: 各シグナルの該当/非該当/境界、最小標本未満、正常取引、複合該当。
- `ManipulativeOrderPatternDetectorTests`（Application）: 銘柄/市場別窓の取得・該当/非該当、`IClock` 固定。
- `InMemoryOrderActivitySourceTests`（Application）: 追記・窓外の刈り込み・（銘柄, 市場）分離。
- `OrderScreeningServiceTests`（Application）: 検出器注入で該当→拒否・正常→承認・ガード無効時スキップ。

## 関連仕様

- 実装ADR: [IADR-0037](../adr/IADR-0037_manipulation-detection-algorithm.md)（本作業）／[IADR-0006](../adr/IADR-0006_manipulation-guard-extension-point.md)（拡張点）。
- 連携: [20260708_risk-guard-core](20260708_risk-guard-core.md)（取引ガード）／[20260709_risk-eval-core-fixes](20260709_risk-eval-core-fixes.md)。
- 機能仕様: [FR-19_trading-guard](../functional/FR-19_trading-guard.md)。

## 未決事項

- 本番テレメトリ（#13/#17）確定後、`IOrderActivitySource` の実装差し替えとホスト DI 登録・実 E2E（#82）で `Closes #49` を確定する。
- 既定しきい値は初期値。運用ログで誤検知/見逃しを評価し IADR-0037 のフォローアップで較正する。
