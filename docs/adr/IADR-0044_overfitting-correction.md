---
title: IADR-0044 過剰適合補正はウォークフォワード＋DSR＋PBO(CSCV)で構成し、純関数で実装する
type: impl-adr
status: Accepted
related_ids: [FR-15, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# IADR-0044: 過剰適合補正はウォークフォワード＋DSR＋PBO(CSCV)で構成し、純関数で実装する

> 実装リポジトリ内の意思決定記録。[IADR-0043](IADR-0043_backtest-foundation.md)（基盤構成）の Slice B に対応。

- 状態: Accepted
- 日付: 2026-07-11
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-15（検証条件①③④）、ADR-0008、06_daytrading-review §3.2。
- 対象 Issue: [#16](https://github.com/endazon/ai-stock-trading/issues/16)（Slice B・`Refs #16`）。
- 参照理論: Bailey & López de Prado「Deflated Sharpe Ratio」/「Probability of Backtest Overfitting（CSCV）」/「Pseudo-Mathematics」、Pardo「Walk-Forward Optimization」。

## コンテキストと課題

多数の戦略候補を試すこと自体が過剰適合を必然にする（PBO）。ナイーブなバックテストの Sharpe は多重検定で上振れする。
FR-15 は「ウォークフォワード検証・試行数記録・DSR/PBO による補正・カットオフ後データ/匿名化」を要求する。これらを
決定的な純関数として実装し、Stage 0 合格判定（Slice C）の入力にする。

## 決定

1. **ウォークフォワード**（`WalkForwardSplitter`）: 期間を In-Sample→Out-of-Sample の窓に分割する。アンカー
   （IS 起点固定・拡大）とローリング（固定幅スライド）の双方を提供する。OOS を跨ぐ評価で IS 最適化の楽観を排す。
2. **試行台帳**（`TrialLedger` / `BacktestTrial`）: 戦略構成候補ごとの Sharpe・OOS 実績・**試行数 N** を記録する。
   DSR/PBO は「何回試したか」を明示的入力に取る（試行数の記録が補正の前提）。
3. **Deflated Sharpe Ratio**（`DeflatedSharpeRatio`）: Bailey & López de Prado の定式を実装する。
   - 帰無下の期待最大 Sharpe `SR0 = √V · [(1−γ)·Z⁻¹(1−1/N) + γ·Z⁻¹(1−1/(N·e))]`（`V`=試行 Sharpe の分散、`γ`=オイラー・マスケローニ定数、`Z⁻¹`=標準正規逆 CDF）。
   - `DSR = Φ( (SR−SR0)·√(T−1) / √(1 − γ₃·SR + (γ₄−1)/4·SR²) )`（`SR`=選択戦略の1期間 Sharpe、`T`=標本長、`γ₃`歪度、`γ₄`尖度）。
   - 標準正規 CDF は誤差関数近似、逆 CDF は Acklam のアルゴリズムで純実装する（`NormalDistribution`）。
4. **PBO**（`ProbabilityOfBacktestOverfitting`・CSCV）: 観測をブロックに区切り、S 分割の半々を IS/OOS に組合せ的に割当て、
   各分割で「IS 最良戦略の OOS 相対順位」のロジット λ を求め、`PBO = P(λ ≤ 0)` を推定する。
   - 戦略のパフォーマンス指標は**平均リターン**を採用する（CSCV は指標に依存せず順位付けの一貫性のみを要する。
     平均は分散 0 区間でも定義でき決定的でテスト容易）。順位はタイ平均・percentile を `(0,1)` にクランプしてロジットの発散を防ぐ。
5. **LLM 汚染対策**（`DataCutoffPolicy` / `SymbolAnonymizer`）: 全バー日付が LLM 学習カットオフより後であることを検証する、
   または銘柄を決定的に**仮名化**して LLM がプレーンテキストのティッカーを文脈から認識できないようにする（FR-15 検証条件①）。
   仮名化は無鍵の決定的 SHA-256 であり、本用途（LLM の文脈認識防止）には十分だが、小さな既知空間に対する総当たり再特定は理論上可能
   （暗号学的秘匿ではない）。厳格な秘匿が要る場合は鍵付き HMAC 等を後続で検討する。

## 理由

- DSR/PBO は「多数試行による見せかけのエッジ」を数値で割り引く標準手法であり、ADR-0008 の合格判定に不可欠。
- 純関数・決定的実装により、統計手続きの性質（試行数増で SR0 上昇＝DSR 低下、支配戦略で PBO→0、対称構成で PBO 高）をテストで固定できる。
- 正規分布関数を外部依存なしで実装することで、CI 緑・再現性・監査可能性を確保する。

## 結果

- 良い影響: 過剰適合補正が全面テスト可能な純関数として整い、Stage 0 合格判定（Slice C）へ供給できる。
- 悪い影響・トレードオフ: PBO の指標は平均リターン近似（Sharpe 版は将来拡張）。DSR は 1 期間 Sharpe・標本モーメントの推定精度に依存する。
  逆正規 CDF は近似（Acklam・相対誤差 ~1e-9）。日中詳細ではなく日足前提の統計。
- フォローアップ: PBO の Sharpe 指標版、ブロックサイズ/分割数の自動選定、実データでの試行台帳蓄積（後続の実行基盤）。

## 関連

- [IADR-0043](IADR-0043_backtest-foundation.md)（基盤構成）、[IADR-0045](IADR-0045_stage0-gate.md)（Stage 0 合格判定・DSR/PBO を消費）
- 仕様: [20260711_backtest-foundation](../specs/20260711_backtest-foundation.md)、[FR-15_backtest](../functional/FR-15_backtest.md)
