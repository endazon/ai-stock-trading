---
title: IADR-0006 相場操縦パターン禁止はガード設定＋判定ポートの拡張点として用意し、検知本体は後続スライスとする
type: impl-adr
status: Accepted
related_ids: [FR-19, ADR-0007]
author: endazon (with Claude Code)
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
---

# IADR-0006: 相場操縦パターン禁止はガード設定＋判定ポートの拡張点として用意し、検知本体は後続スライスとする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-09
- 決定者: endazon（利用者）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-19（取引ガード。相場操縦とみなされ得る発注パターンの禁止）、ADR-0007（取引ガードのソフト設定強制）
- 関連する実装仕様書: [20260709_risk-eval-core-fixes](../specs/20260709_risk-eval-core-fixes.md)、
  [20260708_risk-guard-core](../specs/20260708_risk-guard-core.md)
- 対象 Issue: #28
- 対象コード: [`TradingGuardSettings.cs`](../../src/Services/RiskManagementService/src/RiskManagementService.Domain/TradingGuardSettings.cs)、
  [`IManipulativeOrderPatternDetector.cs`](../../src/Services/RiskManagementService/src/RiskManagementService.Domain/IManipulativeOrderPatternDetector.cs)、
  [`RiskEvaluator.cs`](../../src/Services/RiskManagementService/src/RiskManagementService.Domain/RiskEvaluator.cs)、
  [`RejectionReason.cs`](../../src/Shared/AiStockTrading.Shared.Contracts/Trading/RejectionReason.cs)

## コンテキストと課題

FR-19（Must）は「相場操縦とみなされ得る発注パターンの禁止」を取引ガードに含める。初期スライスの作業仕様書
（`20260708_risk-guard-core.md`）は本スライスの範囲を「ガード設定のフラグとポイント（判定インターフェース）のみ
用意する」と明記していたが、実装にはガードフラグ・理由コード・判定インターフェースのいずれも存在しなかった
（仕様書と実装の齟齬。Issue #28）。過剰な訂正/取消の統計検知は運用データが必要で本スライスでは確定できないが、
後続スライスで拡張する足場は今用意しておく必要がある。

## 検討した選択肢

1. **検知アルゴリズムまで本スライスで実装する** — 閾値（過剰の定義）が運用データ待ちで確定できず、恣意的な値を
   埋め込むことになる。仕様書の範囲（拡張点のみ）とも矛盾する
2. **ガード設定フラグ・理由コード・判定ポート（インターフェース）だけを用意し、検知本体は後続スライスに委ねる** —
   仕様書の範囲に一致し、後続の結線先が確定する。判定コアは検出器が注入されたときのみ判定を呼ぶ

## 決定

選択肢 2 を採用する。

- `TradingGuardSettings.ProhibitManipulativeOrderPatterns`（既定 true）を追加する。
- `RejectionReason.ManipulativeOrderPattern` を追加する（監査ログ・Discord 通知で利用）。
- 判定ポート `IManipulativeOrderPatternDetector.IsSuspectedManipulation(OrderIntent, PortfolioSnapshot)` を定義する。
- `RiskEvaluator.Evaluate` に任意引数 `IManipulativeOrderPatternDetector? patternDetector = null` を追加し、
  **ガード有効かつ検出器が注入されたときのみ**判定を呼ぶ。未注入時は初期スライスの挙動を維持する（回帰なし）。
- 判定はエントリー/手仕舞いを問わず適用する（相場操縦は建玉効果に依存しない）。
- 検知アルゴリズム（注文履歴の統計・過剰な訂正/取消の閾値）は運用データが必要なため後続スライスで実装する。

## 理由

- 拡張点（設定・理由コード・ポート）を先に固定すると、後続スライスは検出器の実装と結線だけで済み、判定コアの
  純粋関数性（IADR-0003/0004）を保てる。
- 既定 true は「禁止をデフォルト有効」にする安全側の既定。検出器未注入では no-op のため、段階的に有効化できる。

## 結果

- 良い影響: 仕様書と実装の齟齬を解消し、相場操縦検知の結線先が確定した。
- 悪い影響・トレードオフ: ガードは既定 true でも検出器未注入の間は実質無効。ホスト結線時に検出器を必ず注入する
  ことを結合テストで担保する必要がある。
- フォローアップ: 発注執行・注文履歴の統計基盤ができ次第、`IManipulativeOrderPatternDetector` の実装（過剰な
  訂正/取消・約定意思のない発注の検知）と閾値を別 IADR で確定する。

## 関連

- Supersedes: なし
- Superseded by: なし
