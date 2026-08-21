---
title: IADR-0008 日次損失上限は実現損益と含み損益の合算で判定する
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-11]
author: endazon (with Claude Code)
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# IADR-0008: 日次損失上限は実現損益と含み損益の合算で判定する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-09
- 決定者: endazon（利用者。2026-07-09 に A 案採用を決定）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-10（リスク統制・日次損失上限）、FR-11（監査）
- 一次情報: 全体前提条件（`05_trading-assumptions.md` §5：日次損失上限 = 資金の 2%、到達で当日全停止・翌営業日までロックアウト）
- 関連する実装仕様書: [20260709_risk-eval-core-fixes](../specs/20260709_risk-eval-core-fixes.md)、
  機能仕様書 [FR-10_risk-controls](../../docs/functional/FR-10_risk-controls.md)
- 対象 Issue: #31
- 対象コード: [`PortfolioSnapshot.cs`](../../backend/Services/RiskManagementService/src/RiskManagementService.Domain/PortfolioSnapshot.cs)、
  [`RiskEvaluator.cs`](../../backend/Services/RiskManagementService/src/RiskManagementService.Domain/RiskEvaluator.cs)

## コンテキストと課題

初期実装の日次損失上限は `snapshot.DailyRealizedPnl`（実現損益のみ）で 2% 到達を判定していた。計画書 §5 は
「資金の 2%（到達で当日全停止・翌営業日までロックアウト）＝デイリーストップ」とだけ定義し、含み損の扱いが
未確定だった（Issue #31）。日中スイングでは含み損の大きいポジションを抱えたまま実現損益ゼロというケースがあり、
実現ベースのみでは「当日 2% 到達」の検知が遅れる。判定に評価損益（含み損益）を含めるか否かの決定が必要だった。

## 検討した選択肢

1. **実現損益のみで判定（B 案）** — 決定的で単純だが、含み損局面の検知が遅れ、デイリーストップが本来の
   「当日の出血を止める」目的を果たせない
2. **実現損益＋含み損益の合算で判定（A 案）** — 実現ゼロでも含み損が上限に達すれば新規発注を止められ、
   デイリーストップの趣旨に合致する。含み損益の供給（日次終値ベース評価）が必要

## 決定

利用者決定（2026-07-09）により **A 案** を採用する。

- `PortfolioSnapshot` に `UnrealizedPnl`（含み損益。負値 = 含み損）を追加する。
- 日次損失の判定を `DailyRealizedPnl + UnrealizedPnl <= -(Capital × DailyLossLimitRatio)` とする。
- エントリー専用（IADR-0004）。手仕舞い（Close）は含み損を実現・縮小する方向のため対象外（フェイルセーフ）。
- 含み損益は日次終値（全体前提条件 §5 の為替評価方法＝評価損益は日次終値）で算出することを想定する。
  集計はリスク管理ホスト（#12）の責務とする。
- **ロックアウト（翌営業日まで）の状態管理は本判定の範囲外**。`RiskEvaluator` はステートレスで「翌営業日まで」を
  表現できないため、ロックアウトの保持・解除はリスク管理ホスト（#12）の責務とし、そこで kill switch 相当の
  当日ロックとして扱う。

## 理由

- デイリーストップは当日の資金の目減りを止める安全装置であり、含み損を無視すると大きな未実現損を抱えたまま
  新規発注を続けられてしまう。利用者の保守的なフェイルセーフ方針に合致する。
- §5 が評価損益を日次終値で定義しているため、含み損益の算出基準は既に前提条件側に存在する。

## 結果

- 良い影響: 実現ゼロでも含み損の合算で日次損失上限に到達し、新規発注を止められる。含み益は実現損を相殺する
  （合算がプラス寄りなら到達しない）ため、直感に沿う。境界値・相殺・手仕舞い除外をテストで固定した。
- 悪い影響・トレードオフ: 含み損益の正確な算出（日次終値・為替評価）をホストが供給する必要がある。評価が
  古い/欠損すると判定精度が落ちるため、`UnrealizedPnl` の鮮度をホスト側で担保する必要がある。
- フォローアップ: リスク管理ホスト（#12）で `UnrealizedPnl` の算出（日次終値・為替）とロックアウト状態管理
  （翌営業日までの当日ロック）を実装する。**算出ロジックは [IADR-0036](IADR-0036_unrealized-pnl-valuation.md) で純関数化済み（#81）。現在値の実供給は #22/#82 後続。**

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0004](IADR-0004_position-effect-entry-scoping.md)（手仕舞いをエントリー制約から除外）
