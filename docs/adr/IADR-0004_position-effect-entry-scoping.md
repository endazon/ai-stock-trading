---
title: IADR-0004 エントリー/手仕舞いは建玉効果（PositionEffect）で判定し、売買方向から分離する
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-19, UC-01, UC-02, ADR-0003, ADR-0007]
author: endazon (with Claude Code)
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
---

# IADR-0004: エントリー/手仕舞いは建玉効果（PositionEffect）で判定し、売買方向から分離する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-09
- 決定者: endazon（利用者）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-10（リスク統制）、FR-19（取引ガード）、ADR-0007（信用の両対応）、ADR-0003（AI ガードレール）
- 関連する実装仕様書: [20260709_risk-eval-core-fixes](../specs/20260709_risk-eval-core-fixes.md)
- 対象 Issue: #25
- 対象コード: [`OrderIntent.cs`](../../src/Shared/AiStockTrading.Shared.Contracts/Trading/OrderIntent.cs)、
  [`PositionEffect.cs`](../../src/Shared/AiStockTrading.Shared.Contracts/Trading/PositionEffect.cs)、
  [`RiskEvaluator.cs`](../../src/Services/RiskManagementService/src/RiskManagementService.Domain/RiskEvaluator.cs)

## コンテキストと課題

初期スライスの `RiskEvaluator` は、エントリー専用のリスク統制（kill switch・段階資金上限・1 注文/日次金額上限・
保有数上限・同日再エントリー・日次損失上限・最大 DD）を `isEntry = intent.Side == TradeSide.Buy` で近似していた。
現物ロングのみを扱う限りは「買い = 新規建て」「売り = 手仕舞い」が成立するため正しい。

しかし ADR-0007 は信用取引を取引ガード設定で有効化できる両対応を確定している。信用有効化後は
**ショートエントリー（Side == Sell の新規建て）** が発生し、この近似が破綻する。現契約の `OrderIntent`
にはエントリー/手仕舞いの区別がなく、ショートエントリーが kill switch を含む全エントリー制約をすり抜ける
（計画の受け入れ基準「kill switch 起動後、新規発注が一切行われない」に将来違反する構造的欠陥。Issue #25）。

## 検討した選択肢

1. **保有状況（`PortfolioSnapshot`）から新規建てか否かを推論する** — 同一銘柄の保有有無で判定できるが、
   ドテン（同時に決済＋逆張り建て）や部分決済で曖昧になり、判定コアがポートフォリオ照合ロジックを抱える
2. **`OrderIntent` に建玉効果 `PositionEffect`（Open/Close）を明示的に持たせ、`isEntry = PositionEffect == Open`
   とする** — エントリー/手仕舞いは売買方向と直交する属性であり、注文を生成する取引判断サービスが最も正確に
   知っている。判定コアは受け取った効果を読むだけで純粋関数を保てる

## 決定

選択肢 2 を採用する。

- `OrderIntent` に `PositionEffect PositionEffect`（`Open` = 新規建て / `Close` = 手仕舞い）を追加する。
- 位置指定 record の末尾に既定値 `PositionEffect.Open` を与える。これにより既存の呼び出し（すべて新規買い）は
  変更なしでエントリー扱いを維持し、効果を明示しない注文は**制約を厳しく掛ける安全側（Open）** に倒れる。
- `RiskEvaluator` の `isEntry` を `intent.PositionEffect == PositionEffect.Open` に是正する。
- 建玉効果と売買方向の対応: ロング建て = Buy×Open / ロング決済 = Sell×Close / ショート建て = Sell×Open /
  ショート決済 = Buy×Close。エントリー専用制約は Open にのみ適用し、Close はフェイルセーフでブロックしない。

## 理由

- エントリー/手仕舞いは売買方向から独立した情報であり、注文生成側（取引判断サービス）が確定情報として持つ。
  判定コアが推論するより、意図を明示的に受け取る方が正確かつ監査しやすい（FR-11）。
- 既定 `Open` は「未指定なら新規建てとして最も厳しく扱う」安全側の既定であり、ADR-0003 のフェイルセーフ思想に沿う。

## 結果

- 良い影響: 信用有効化後もエントリー専用制約が正しく効き、kill switch のバイパスを構造的に防げる。
- 悪い影響・トレードオフ: 注文を生成する側が `PositionEffect` を正しく設定する責務を負う。取引判断サービスの
  スライスで「発注意図に必ず建玉効果を設定する」ことを結合テストで担保する必要がある（IADR-0003 のサイジング
  責務と同様の呼び出し側責務）。
- フォローアップ: 発注執行・取引判断サービス実装時に、ドテン・部分決済の注文分解（決済 Close ＋ 建て Open の
  2 注文化）の方針を別途 IADR 化する。**→ [IADR-0038](IADR-0038_order-decomposition-position-effect.md)（符号付きポジションのゼロ跨ぎ分割）で確定済み（#50）。**

## 関連

- Supersedes: なし
- Superseded by: なし
