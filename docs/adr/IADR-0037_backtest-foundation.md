---
title: IADR-0037 バックテスト基盤は純ドメイン中心に構成し、実データ源/ホストは後続に切り分ける
type: impl-adr
status: Accepted
related_ids: [FR-15, FR-20, FR-17, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# IADR-0037: バックテスト基盤は純ドメイン中心に構成し、実データ源/ホストは後続に切り分ける

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定（本 ADR は基盤全体の構成方針。
> 個別方式は [IADR-0038](IADR-0038_overfitting-correction.md)（過剰適合補正）・[IADR-0039](IADR-0039_stage0-gate.md)（Stage 0 合格判定）に分ける）。

- 状態: Accepted
- 日付: 2026-07-11
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-15（バックテスト＝実弾投入前の必須ゲート Stage 0）、FR-20（段階ゲート）、FR-17（概算費用関数の共通化）、ADR-0008
- 対象 Issue: [#16](https://github.com/endazon/ai-stock-trading/issues/16)（`Refs #16`。全受け入れ条件充足の最終スライスで `Closes #16`）
- 関連する実装仕様書: [20260711_backtest-foundation](../specs/20260711_backtest-foundation.md)、機能仕様 [FR-15_backtest](../functional/FR-15_backtest.md)
- 参照理論: Bailey & López de Prado（Pseudo-Mathematics / DSR / PBO）、Pardo（ウォークフォワード最適化）。06_daytrading-review §3.2。

## コンテキストと課題

ADR-0008 で FR-15 は Won't → Must に格上げされ、バックテスト合格（Stage 0）が実弾投入（Stage 1 以降）の必須ゲートと
なった。検証条件は「LLM 学習カットオフ後データ（または銘柄匿名化）・現実的コスト計上＋コスト 2 倍の感度分析・ウォーク
フォワード検証・試行数記録と過剰適合補正（DSR/PBO）・生存者バイアスのない銘柄ユニバース」。未実装であり、これがない限り
Stage 1 へ進めない。Issue #16 は「大きめだが純コードで完結できる範囲」と位置づけられている。

## 決定

1. **新規サービス `BacktestService` を追加し、計算・判定ロジックはすべて純ドメイン（`BacktestService.Domain`）に置く**。
   I/O・時刻・乱数・外部 API に依存しない純関数／不変レコードで構成し、全条件をユニットテストで固定する。
2. **オーケストレーションとポートは `BacktestService.Application`** に置く。過去データ供給は `IBarDataSource` ポートで
   抽象化し、テストは決定的な in-memory アダプタ（`InMemoryBarDataSource`）で駆動する。
3. **実データ源コネクタ（J-Quants Free / Stooq 等）・Worker(HTTP/メッセージ)ホスト・実コンテナ E2E は本 Issue のスコープ外**とし、
   後続 Issue に切り分ける。理由: #16 は純コード完結の範囲であり、実 API/実コンテナ統合は CI 緑の単体検証と分離する運用方針
   （実 API はレート制限・鍵・可用性に依存）。ポート境界を先に確定させることで、後続のコネクタ差し込みを非破壊にする。
4. **既存資産を再利用する（重複実装しない）**:
   - コスト計上は FR-17 の `CostCalculator`／`TradingAssumptions`（`ConfigurationService.Domain`）を基礎に、バックテスト固有の
     スリッページと**コスト倍率（感度分析 1x/2x）**を上乗せする薄いラッパ（`BacktestCostModel`）で表現する。
   - 段階は `TradingStage`／`StageSettings`（`RiskManagement.Domain`, FR-20）、市場は `Market`（`Shared.Contracts`）を再利用する。
5. **ルックアヘッド（先読み）を構造的に排除する**: シミュレータは判断関数へ「当日 T までのバー列（`bars[0..T]`）」のみを渡す。
   判断は T の終値で行い、**約定は翌営業日 T+1 の始値（＋スリッページ）**で成立させる（マーケタブルリミット近似）。
6. **生存者バイアスの排除は Point-in-Time メンバーシップで表現する**: ユニバースを上場/上場廃止区間
   `(Symbol, Market, ListedFrom, DelistedOn?)` の集合として保持し、日付 D 時点の構成銘柄（当時上場・後に廃止された銘柄を含む）を
   `MembersAsOf(D)` で返す。現在の上場銘柄だけで検証しない。
7. **スライス分割**（各 PR は `Refs #16`、最終のみ `Closes #16`）:
   - Slice A: シミュレーションコア＋コストモデル＋結果集計（Domain）＋`IBarDataSource`／in-memory（Application）。
   - Slice B: 過剰適合補正ハーネス（ウォークフォワード・試行台帳・DSR・PBO・カットオフ/匿名化）→ [IADR-0038]。
   - Slice C: Stage 0 合格判定＋FR-20 遷移接続（昇格推奨・キルスイッチ）→ [IADR-0039]。

## 理由

- バックテストの数値（リターン・Sharpe・DD・DSR・PBO・合否）は決定的に計算でき、純ドメインに閉じれば全経路をテストで固定できる。
  過剰適合補正のような統計手続きは「入力が同じなら出力も同じ」であることの検証が肝要であり、純関数が最も honest。
- 実データ源は外部 API 依存でありレート制限・鍵・可用性に左右される。ポート抽象を先に切り、実コネクタと実コンテナ E2E を
  後続に分けることで、#16 の受け入れ条件（検証条件の実装）を CI 緑で満たしつつ、実 API 統合はスコープと責務を明確化できる。
- コスト計上を FR-17 と共通化することで、判断時の採算評価・事後集計・バックテストが同一の費用式を参照し、乖離を防ぐ（Issue 明記の要件）。

## 結果

- 良い影響: 検証条件（カットオフ/匿名化・コスト 2 倍感度・ウォークフォワード・DSR/PBO・PIT ユニバース）が純関数として実装され
  全面テスト可能。Stage 0 合格判定が再現可能なプロセスになる。ポート境界確定により実データ源の差し込みが非破壊。
- 悪い影響・トレードオフ: 本 Issue 単体では実データ源・Worker ホスト・実コンテナ E2E は未提供（後続）。したがって「実銘柄での実走」は
  後続コネクタ結線後に可能になる。バックテストは日足バー粒度を前提とし、日中スイングのザラ場詳細（板・出来高分布）は近似。
- フォローアップ（別 Issue 化候補）: 実データ源コネクタ（J-Quants Free/Stooq、ADR-0005 条件で有料 J-Quants）、Worker ホスト、
  実コンテナ E2E、日中バー対応、FR-20 段階遷移承認フロー（#20）との結線。

## 関連

- 計画: FR-15/FR-20（[要求](../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md)）、[ADR-0008](../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md)、06_daytrading-review §3.2/§4
- 実装: [IADR-0038](IADR-0038_overfitting-correction.md)（DSR/PBO/ウォークフォワード）、[IADR-0039](IADR-0039_stage0-gate.md)（Stage 0 合格判定）
- 再利用: FR-17 [`CostCalculator`]・FR-20 [`StageSettings`]
