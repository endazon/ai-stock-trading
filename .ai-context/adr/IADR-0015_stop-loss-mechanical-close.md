---
title: IADR-0015 損切りの決済注文はスクリーニングを通さず無条件に Close 承認を発行する
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-03, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - planning:projects/ai-stock-trading/04_workflows/02_event-driven-trading.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# IADR-0015: 損切りの決済注文はスクリーニングを通さず無条件に Close 承認を発行する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-10（損切り）、FR-03（損切りライン検知）、ADR-0003（損切りは機械執行・AI 迂回）
- 対象 Issue: [#12](https://github.com/endazon/ai-stock-trading/issues/12) Slice C
- 関連する実装仕様書: [20260710_stop-loss-execution](../specs/20260710_stop-loss-execution.md)
- 関連 IADR: [IADR-0014](IADR-0014_market-monitor-events-and-boundary.md)（検知/執行の責務境界）、[IADR-0004](IADR-0004_position-effect-entry-scoping.md)（建玉効果でエントリー判定）

## コンテキストと課題

`StopLossTriggered`（市場監視・#10）を受けたリスク管理は、決済（Close）注文を発行する。ここで「通常の発注前スクリーニング
（`OrderScreeningService` / `RiskEvaluator`）を通すか」を決める必要がある。ワークフロー 02・ADR-0003 は「損切りは AI 判断を
経由せず機械的に決済」「kill switch 起動中・日報未確定・LLM 障害中でも必ず実行」と定める。`RiskEvaluator` は手仕舞い（Close）を
フェイルセーフで通すが、相場操縦ガード（`ProhibitManipulativeOrderPatterns`）だけはエントリー/手仕舞いを問わず適用するため、
検出器が注入されると理論上は Close も拒否され得る。損切りを「必ず実行」と両立させる方針を明確にする必要がある。

## 検討した選択肢

1. **通常のスクリーニングを通す** — `OrderScreeningService.Screen` に Close 注文を渡す。ほとんどの制約はエントリー限定で
   Close を通すが、相場操縦ガードが Close にも適用され得るため「必ず実行」を保証できない。判定コアの将来変更で損切りが
   ブロックされるリスクが残る。
2. **スクリーニングを通さず無条件に Close 承認を発行する** — 損切りは資産保全の最後の砦であり ADR-0003 で「必ず実行」と
   定めるため、`StopLossExecutionService` が `OrderApproved`(Close) を直接組み立てて発行する。エントリー制約・相場操縦ガード・
   kill switch・ロックアウトのいずれでも止めない。

## 決定

選択肢 2 を採用する。

- `StopLossExecutionService.BuildCloseApproval(StopLossTriggered)` が Close の `OrderApproved` を直接組み立てる（純粋関数）。
  - 決済方向は建玉方向の反対（Buy 建て→Sell / Sell 建て→Buy）。`PositionEffect = Close`。
  - `Mode` は現行段階（`settings.Stage.Mode`）、`ProductType` は `Cash`（現物のみ有効な現段階）。
  - `Quantity`・`Price` はイベントの値。先行する `TradeDecisionMade` は無い（LLM 迂回）ため、`DecisionId` は
    `StopLossTriggered.EventId` から**決定的に採る**（冪等性）。MassTransit の再送で同一イベントが再処理されても
    同じ `DecisionId` になり、発注執行（#13）側の `DecisionId` ベース重複排除がすり抜けない。
- **スクリーニング（`RiskEvaluator`）を通さない**。損切りは kill switch・ロックアウト・相場操縦ガード・各上限のいずれでも
  止めない無条件執行とする。
- 発行先は `OrderApproved`（発注執行が購読）。損切りであることは `PositionEffect.Close`＋先行判断の不在で表される。実行は
  情報ログに残す（FR-11 監査・FR-09 通知の起点は後続 #15/#17）。

## 理由

- ADR-0003・ワークフロー 02 が損切りの「必ず実行」を明記しており、統制でブロックし得る経路（相場操縦ガード等）を通すのは
  方針に反する。損切りは AI・統制の上流にある安全装置であり、無条件執行が正しい。
- 直接組み立てにより、判定コアの将来変更（相場操縦検出器 #49 の注入等）が損切り執行を巻き込まないことを構造的に保証できる。

## 結果

- 良い影響: 損切りが kill switch・ロックアウト・相場操縦ガードに一切影響されず必ず発行される。判定コアの変更から独立。
- 悪い影響・トレードオフ: 損切り経路は通常のスクリーニングの監査ログを経ないため、損切り固有のログ/通知/監査を別途用意する
  必要がある（本 Slice は警告ログで可視化、永続監査は #17、通知は #15）。`ProductType` は現段階 Cash 固定で、信用有効化時に拡張が要る。
- 冪等性: `DecisionId` を `StopLossTriggered.EventId` から決定的に採ることで、再送時の重複 `OrderApproved` を防ぐ。
- フォローアップ: 発注執行（#13）で Close 発注、通知（#15）・監査（#17）連携、信用時の ProductType 供給。
- **既知の重複窓（クロスサービス・#10/#13）**: `DecisionId = EventId` の冪等化は**ブローカ再送**（同一 `StopLossTriggered` の
  再配送）を吸収するが、市場監視（#10）が**次の巡回で同一の損切り条件を再検知**すると新しい `EventId` の別イベントを発行し得る
  （損切りはクールダウンを掛けない＝フェイルセーフのため）。ポジションが実際に決済されるまでの窓で重複の `OrderApproved`(Close) が
  発行され得る。恒久対策は発注執行（#13）の約定フィードバックでポジション状態が消えること、および必要なら市場監視側の
  「損切り執行中」ガード（`OrderExecuted` 購読での抑制）であり、#13 連携で確定する。本 Slice の範囲（購読→執行）外のため後続とする。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0014](IADR-0014_market-monitor-events-and-boundary.md)、[IADR-0004](IADR-0004_position-effect-entry-scoping.md)、[ADR-0003]
