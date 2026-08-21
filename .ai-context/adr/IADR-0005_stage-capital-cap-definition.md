---
title: IADR-0005 段階資金上限は保有ポジションの取得額合計＋当該注文額（コストベース累計）で判定する
type: impl-adr
status: Accepted
related_ids: [FR-20, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# IADR-0005: 段階資金上限は保有ポジションの取得額合計＋当該注文額（コストベース累計）で判定する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-09
- 決定者: endazon（利用者）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-20（段階ゲート）、ADR-0008（段階ゲートと資金上限）
- 関連する実装仕様書: [20260709_risk-eval-core-fixes](../specs/20260709_risk-eval-core-fixes.md)
- 対象 Issue: #27
- 対象コード: [`PortfolioSnapshot.cs`](../../backend/Services/RiskManagementService/src/RiskManagementService.Domain/PortfolioSnapshot.cs)、
  [`RiskEvaluator.cs`](../../backend/Services/RiskManagementService/src/RiskManagementService.Domain/RiskEvaluator.cs)

## コンテキストと課題

FR-20 は「段階ごとの資金上限を強制できる」ことを要求する。初期実装は `intent.Notional > settings.Stage.CapitalCap`
と**その 1 注文の想定金額のみ**を上限と比較しており、保有ポジションの投入額や既存発注を加味しなかった。
そのため上限内の注文を（日をまたいで）複数回通せば累計投入額が上限を大きく超過でき、段階ゲートの「資金上限」の
意味が骨抜きになる（Issue #27）。「資金上限」を何に対する上限とするか（単一注文か累計か、取得額か時価か）の
定義が未確定だった。

## 検討した選択肢

1. **単一注文額のみで比較（現状）** — 実装は単純だが累計超過を防げず、要求を満たさない
2. **投入中資金（保有ポジションの取得額合計）＋当該注文額 ≦ 上限（コストベース累計）** — 「段階ごとに投入できる
   資金の総量」という語義に一致し、決定的（当日の値動きに依存しない）
3. **時価ベースの累計（保有時価＋当該注文額）** — 値動きで上限判定が揺れ、含み益で新規建て枠が縮む・含み損で
   広がるなど直感に反する。手仕舞い判断とも混線する

## 決定

選択肢 2 を採用する。

- `PortfolioSnapshot` に `InvestedCapital`（保有ポジションの取得額合計＝コストベース）を追加する。
- 段階資金上限の判定を `isEntry && snapshot.InvestedCapital + intent.Notional > settings.Stage.CapitalCap` とする。
- 手仕舞い（Close）は投入額判定の対象外（フェイルセーフ。IADR-0004）。手仕舞いは投入資金を減らす方向のため上限に抵触しない。
- `InvestedCapital` を供給する責務はリスク管理ホスト（#12。約定履歴・保有から集計）が持つ。判定コアは受け取った値で評価する。

## 理由

- 「資金上限」は段階ごとに投入してよい資金の総量を指す運用上の概念であり、取得額（実際に投じた金額）が最も素直な基準。
- コストベースは当日の値動きに依存せず決定的で、監査ログ（FR-11）でも再現・説明が容易。
- 時価ベースは含み損益で判定が揺れ、エントリー枠の予測可能性を損なう。

## 結果

- 良い影響: 上限内注文の積み増しによる累計超過を防げ、段階ゲートの資金上限が実効化する。境界値をテストで固定した。
- 悪い影響・トレードオフ: `InvestedCapital` の正確な集計（部分約定・手数料込みか否か）をホスト側が担う必要がある。
  当面は約定代金合計（手数料抜き）を取得額とし、費用の扱いは費用統制（#23）実装時に再検討する。
- フォローアップ: リスク管理ホスト（#12）で `InvestedCapital` の集計元（保有ストア）と更新タイミングを定義する。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0004](IADR-0004_position-effect-entry-scoping.md)（手仕舞いをエントリー制約から除外する判定）
