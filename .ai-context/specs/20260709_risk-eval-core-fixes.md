---
title: リスク評価コアの是正（エントリー判定・差金決済・段階資金上限・相場操縦ガード）
type: spec
status: review
related_ids: [FR-10, FR-19, FR-20, UC-01, UC-02, UC-06, ADR-0003, ADR-0007, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# 仕様書: リスク評価コアの是正

> 初期スライス（[20260708_risk-guard-core](20260708_risk-guard-core.md)）で実装した `RiskEvaluator` /
> `PortfolioSnapshot` / `OrderIntent` に対して報告された 4 件の欠陥（Issue #25 / #26 / #27 / #28）を是正する
> 作業仕様。既存の 38 テストを緑に保ったまま、判定ロジックの正確性を高める。

## 起点となる計画書・課題（トレーサビリティ）

| Issue | 種別 | 起点 ID | 概要 |
| --- | --- | --- | --- |
| #25 | fix | FR-10, FR-19 | 信用有効化時、売り建て（ショートエントリー）が kill switch 含む全エントリー制約をバイパスする |
| #26 | fix | FR-19 | 同日再エントリー判定が市場を無視して銘柄コードのみで照合している |
| #27 | fix | FR-20 | 段階資金上限が単一注文額のみで判定され、累計投入資金を考慮しない |
| #28 | fix | FR-19 | 相場操縦パターン禁止のガード設定・判定ポイントが仕様書の記載に反して存在しない |
| #31 | fix/docs | FR-10 | 日次損失上限を実現損益＋含み損益の合算で判定する（利用者決定 A 案・2026-07-09） |

- 関連 ADR: ADR-0003（AI 判断のガードレール）、ADR-0007（取引ガードのソフト設定強制）、ADR-0008（段階ゲート）
- 関連 IADR（本作業で新規作成）: IADR-0004（建玉効果によるエントリー判定）、IADR-0005（段階資金上限の定義）、
  IADR-0006（相場操縦パターン判定の拡張ポイント）、IADR-0008（日次損失上限の判定基準）

## 目的・背景

初期スライスは「新規発注（エントリー）にのみ適用し、手仕舞い（売り）はブロックしない」というフェイルセーフを
`isEntry = intent.Side == TradeSide.Buy` で近似していた。現物ロングのみを扱う限りは正しいが、ADR-0007 が確定した
「信用を設定で有効化する両対応」ではショートエントリー（Side == Sell の新規建て）が発生し、この近似が破綻する。
本作業は「エントリー/手仕舞い」を売買方向から分離して正しく判定し、あわせて差金決済防止・段階資金上限・相場操縦
ガードの各欠陥を是正する。

## 対象範囲

- `AiStockTrading.Shared.Contracts`
  - `OrderIntent` に建玉効果 `PositionEffect`（`Open` = 新規建て / `Close` = 手仕舞い）を追加（既定 `Open`）
  - `RejectionReason` に `ManipulativeOrderPattern` を追加
- `RiskManagementService.Domain`
  - `RiskEvaluator`: `isEntry` を `PositionEffect == Open` に基づくよう是正（#25）／同日再エントリーを
    （銘柄, 市場）で照合（#26）／段階資金上限を「投入中資金＋当該注文額」で判定（#27）／相場操縦パターンの
    判定ポイントを追加（#28）
  - `PortfolioSnapshot`: `SymbolsTradedToday` を（銘柄, 市場）ペア化（#26）／`InvestedCapital`（保有ポジションの
    取得額合計）を追加（#27）
  - `TradingGuardSettings`: `ProhibitManipulativeOrderPatterns`（既定 true）を追加（#28）
  - `IManipulativeOrderPatternDetector`（判定ポート。実装は後続スライス）を追加（#28）
- テスト: 上記の受け入れ基準を xUnit に写像（既存テストの意図に合わせて手仕舞いテストを `Close` へ更新）
- 対象外: 相場操縦検知アルゴリズム本体（注文履歴統計。閾値は運用データ待ち）、リスク管理ホスト（#12）

## 設計判断

- **#25 建玉効果**: エントリー/手仕舞いは売買方向と直交する（ロング建て=買+Open、ロング決済=売+Close、
  ショート建て=売+Open、ショート決済=買+Close）。`OrderIntent.PositionEffect` を一次情報とし、
  `isEntry = PositionEffect == Open`。既定は `Open`（不明な注文はエントリー扱い＝制約を厳しく掛ける安全側）。→ IADR-0004
- **#26 差金決済防止**: 禁止銘柄判定（銘柄+市場）と対称に、同日再エントリーも（銘柄, 市場）で照合する。
  `SymbolsTradedToday` を `IReadOnlySet<(string Symbol, Market Market)>` に変更。
- **#27 段階資金上限**: 「資金上限」を保有ポジションの**取得額合計（コストベース）＋当該注文額**と定義する
  （時価ベースではない。決定的で「投入資金」の語義に一致）。`PortfolioSnapshot.InvestedCapital` を追加。→ IADR-0005
- **#28 相場操縦ガード**: `TradingGuardSettings.ProhibitManipulativeOrderPatterns`（既定 true）＋
  `RejectionReason.ManipulativeOrderPattern` ＋ 判定ポート `IManipulativeOrderPatternDetector` を用意。
  `RiskEvaluator.Evaluate` は検出器が注入され、かつフラグ有効のときのみ判定を呼ぶ（未注入時は現行挙動を維持）。
  検出アルゴリズムは後続スライス。→ IADR-0006
- **#31 日次損失上限の判定基準**: 利用者決定（2026-07-09・A 案）により、**実現損益＋含み損益の合算**で判定する。
  `PortfolioSnapshot.UnrealizedPnl`（含み損益・日次終値評価）を追加し、`DailyRealizedPnl + UnrealizedPnl` を
  しきい値と比較する。エントリー専用・手仕舞い除外はフェイルセーフどおり。ロックアウト状態管理はホスト（#12）の
  責務として明記。機能仕様は [FR-10_risk-controls](../../docs/functional/FR-10_risk-controls.md)。→ IADR-0008

## 受け入れ基準

- [ ] #25: kill switch 起動中は Buy/Sell を問わず**新規建て（Open）**が全て拒否され、手仕舞い（Close）は承認される
- [ ] #25: ショートエントリー（Margin × Sell × Open）に金額上限・段階資金上限・日次損失上限等が適用される
- [ ] #26: 同日再エントリー判定が（銘柄, 市場）で行われ、別市場の同一コードは誤拒否されない
- [ ] #27: 保有ポジションの取得額を含む累計投入額が段階上限を超える新規注文が拒否される（手仕舞いは対象外）
- [ ] #28: ガード設定・理由コード・判定ポートが存在し、無効化時／検出器未注入時は判定をスキップする
- [ ] #31: 日次損失上限が実現損益＋含み損益の合算で判定され、含み益は相殺し、手仕舞いは対象外
- [ ] `dotnet build` / `dotnet test` が全緑（既存 38 テスト＋追加テスト）

## テスト方針

- 受け入れ基準を `[Fact]`/`[Theory]` に 1 対 1 で写像し、境界値（上限ちょうど・上限+1）を検証する
- 既存の手仕舞いテストは `PositionEffect.Close` を明示し、意図（フェイルセーフ）を保つ
- 相場操縦は「フラグ ON＋検出器が true を返す」「検出器未注入では現行どおり承認」の 2 系統を固定する

## 計画書との差異

- 差異なし。いずれも仕様書 [20260708_risk-guard-core](20260708_risk-guard-core.md) と計画書（ADR-0007 の
  信用両対応、§5 の差金決済・発注パターン禁止、ADR-0008 の段階資金上限）の意図に実装を一致させる是正である。

## 未決事項

- 相場操縦検知の具体閾値（過剰な訂正/取消の統計）は運用データが必要なため後続スライスで確定（本作業は拡張点のみ）。
- 日次損失上限のロックアウト（翌営業日まで）の状態管理はステートレスな `RiskEvaluator` の範囲外。
  リスク管理ホスト（#12）の責務として実装する（判定基準そのものは #31 / IADR-0008 で確定済み）。
