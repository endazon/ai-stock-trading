---
title: 建玉効果の注文分解方針（ドテン/部分決済）の確定
type: spec
status: done
related_ids: [FR-04, FR-05, FR-10, FR-19, ADR-0003, ADR-0007]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md
---

# 仕様書: 建玉効果の注文分解方針（ドテン/部分決済）の確定

> Issue [#50](https://github.com/endazon/ai-stock-trading/issues/50)（[IADR-0004](../adr/IADR-0004_position-effect-entry-scoping.md) のフォローアップ）。
> **設計文書タスク**。ドテン（反転）・部分決済を `Close`＋`Open` の注文へ分解する方針を [IADR-0038](../adr/IADR-0038_order-decomposition-position-effect.md) で設計・確定する。
> 実装（分解ロジックの結線）は信用有効化スライス（ADR-0007/#50）で行う後続とし、本作業は方針確定に限定する。

## 起点となる計画書・課題（トレーサビリティ）

- FR-04（取引判断が注文意図を生成）、FR-05（発注執行）、FR-10（エントリー専用リスク統制）、FR-19（取引ガード）。
- 課題: [IADR-0004](../adr/IADR-0004_position-effect-entry-scoping.md) で `PositionEffect`（Open/Close）を導入したが、反転・部分決済の分解則が未定。
  反転を単一の相殺注文で表すと新規建て部分にエントリー統制が正しく効かず、kill switch すり抜け等の構造的欠陥が残る（IADR-0004 と同じ穴の再発）。
- 関連 IADR: [IADR-0004](../adr/IADR-0004_position-effect-entry-scoping.md)（建玉効果）、[IADR-0033](../adr/IADR-0033_shared-inventory-fold.md)（`SignedInventory` の反転畳み込み）、
  [IADR-0018](../adr/IADR-0018_portfolio-ledger-projection.md)（net 1 建玉）、[IADR-0015](../adr/IADR-0015_stop-loss-mechanical-close.md)（損切りは純 Close・分解対象外）、
  [IADR-0026](../adr/IADR-0026_audit-deterministic-correlation.md)（決定的相関）、[IADR-0035](../adr/IADR-0035_stop-loss-authoritative.md)、[IADR-0017](../adr/IADR-0017_trade-decision-structure.md)（サイジング）。
  本作業で新規 [IADR-0038](../adr/IADR-0038_order-decomposition-position-effect.md)。
- 対象 Issue: #50。

## 対象範囲（本作業）

本作業は方針確定（IADR）に限定し、コードは変更しない。

- [IADR-0038](../adr/IADR-0038_order-decomposition-position-effect.md) を Accepted で作成する。
  - 分解則（符号付きポジションのゼロ跨ぎ分割）、各注文の `PositionEffect`・数量の決め方、2 意図の運搬（別 `TradeDecisionMade`＋決定的 `DecisionId`）、
    建玉効果の常時明示（不変条件）、損切り機械執行の非適用、現物での自然縮退を確定する。
  - 代替案 A（単一相殺注文）/B（執行層分解）/C（コア推論）を比較し却下理由を明記する。
- `docs/adr/README.md` の一覧に IADR-0038 を追記する。

## 分解則（確定内容の要約）

現在ネット建玉 `p`（+ロング/−ショート/0）と注文 `q`（+買い/−売り）の遷移 `p→p+q` をゼロ点で分割する（`SignedInventory.Apply` の反転処理と同一境界）。

| ケース | 条件 | 意図 |
| --- | --- | --- |
| 新規/建て増し | `p==0` または `sign(p)==sign(q)` | 単一 Open（数量 `|q|`） |
| 部分決済 | `sign(p)!=sign(q)` かつ `|q|<|p|` | 単一 Close（数量 `|q|`） |
| 全決済 | `sign(p)!=sign(q)` かつ `|q|==|p|` | 単一 Close（数量 `|p|`） |
| 反転（ドテン） | `sign(p)!=sign(q)` かつ `|q|>|p|` | 2 意図: ① Close `|p|` ＋ ② Open `|q|−|p|` |

- Close 脚数量は `|p|` を上限にクランプ（保有超過決済を作らない）。Open 脚数量はサイジング（`PositionSizer`・post-close 残枠）。反転 2 脚は差引きせず独立に組み立てる。

## 受け入れ基準

設計文書タスクのため、成果物はドキュメントである。

- [x] [IADR-0038](../adr/IADR-0038_order-decomposition-position-effect.md) が Accepted で存在し、分解則・`PositionEffect`・数量・2 意図運搬・不変条件・非適用・現物縮退を定義している。
- [x] 代替案 A/B/C の比較と却下理由が IADR に含まれる。
- [x] 既存 IADR（0004/0033/0018/0015/0026/0035/0017）との整合と相互リンクが取れている。
- [x] `docs/adr/README.md` に IADR-0038 が追記されている。
- [x] `PositionEffect` 常時明示の不変条件が既存実装で満たされていることを確認（取引判断は `PositionEffect.Open` を明示・損切りは `PositionEffect.Close` を明示。既存テスト
      `TradeDecisionServiceTests`/`StopLossExecutionServiceTests` が assert 済み）。

## 対象外（後続・信用有効化スライスで実装）

- 取引判断への分解ロジック実装（ゼロ跨ぎ分割の純関数化）。現在ネット建玉のサイジング文脈への供給。
- 反転分割・不変条件（建玉効果常時設定）の結合テスト。反転 2 脚の決定的 `DecisionId` 相関。差金決済防止・相場操縦ガードの Open 脚適用確認。
- 両建て（ロング/ショート別ロット）会計（ADR-0007/#50）。原子的反転／注文内ネッティングは追わない（IADR-0038 で明示）。

## テスト方針

本作業はドキュメントのみのため新規テストは追加しない。既存テスト（`TradeDecisionServiceTests`・`StopLossExecutionServiceTests`・
`PortfolioProjectionTests`・`SignedInventory` 関連）を緑に保つ（コード無変更のため回帰なし）。分解ロジックのテストは後続スライスで追加する。

## 関連仕様

- 実装ADR: [IADR-0038](../adr/IADR-0038_order-decomposition-position-effect.md)／[IADR-0004](../adr/IADR-0004_position-effect-entry-scoping.md)／[IADR-0033](../adr/IADR-0033_shared-inventory-fold.md)
- 連携: [20260709_risk-eval-core-fixes](20260709_risk-eval-core-fixes.md)（IADR-0004 導入）、[20260710_trade-decision-core](20260710_trade-decision-core.md)（取引判断のサイジング/意図生成）

## 未決事項

- 分解ロジックの実装配置（`SignedInventory` 隣接の純関数 vs 取引判断サービス内）は後続スライスで確定する（IADR-0038 はフォローアップとして候補を提示）。
- 反転 Open 脚の post-close 資本評価と `RiskEvaluator` 再評価の非同期窓の緩和（キャッシュ/リトライ）は信用有効化時に詰める。
