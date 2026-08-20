---
title: IADR-0014 市場監視は検知しイベントを発行、損切り執行はリスク管理が担う（責務境界とイベント契約）
type: impl-adr
status: Accepted
related_ids: [FR-03, FR-10, UC-02, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - planning:projects/ai-stock-trading/04_workflows/02_event-driven-trading.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# IADR-0014: 市場監視は検知しイベントを発行、損切り執行はリスク管理が担う

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-03（価格変動監視）、FR-10（損切りはリスク統制）、UC-02、ADR-0003（損切りは機械執行・AI 迂回）
- 対象 Issue: [#10](https://github.com/endazon/ai-stock-trading/issues/10)、依存解消先 [#12](https://github.com/endazon/ai-stock-trading/issues/12) Slice C
- 関連する実装仕様書: [20260710_market-monitor-core](../specs/20260710_market-monitor-core.md)
- 関連 IADR: [IADR-0009](IADR-0009_async-contract-format.md)（イベント契約は record・Markdown 通信仕様で管理）

## コンテキストと課題

イベント駆動フロー（`04_workflows/02_event-driven-trading.md`）では、市場監視が価格をポーリングし、(1) 変動閾値超過で
取引サイクルを起動、(2) 損切りライン到達で **LLM を迂回してリスク管理が決済**する。この 2 系統をどのイベント契約で表し、
「損切りの検知」と「損切りの執行」の責務をどのサービスに割るかを決める必要がある。特に #12 Slice C（損切り執行）は
市場監視が出すイベント契約に依存しており、契約を先に確定して依存を解消する必要がある。

## 検討した選択肢

1. **市場監視が損切り注文まで発行する** — 監視サービスが発注執行へ直接指示する。リスク統制（kill switch・上限・監査）を
   バイパスしかねず、ADR-0003「リスク管理が損切りを機械執行」に反する。責務も市場監視に寄りすぎる。
2. **市場監視は「検知」してイベント発行、リスク管理が「執行」する** — 監視は `StopLossTriggered` を発行するだけ。
   リスク管理（#12 Slice C）が購読し、フェイルセーフ方針（手仕舞いは常に通す）に沿って Close 注文を発行する。
   検知と執行を分離し、執行側にリスク統制・監査を集約する。

## 決定

選択肢 2 を採用する。

- イベント契約（`AiStockTrading.Shared.Contracts/Events`・record・IADR-0009 準拠）:
  - `PriceMovementDetected(EventId, Symbol, Market, Price, BaselinePrice, ChangeRatio, DetectedAt)` — 変動検知。
    取引判断/サイクルが購読して対象銘柄限定のサイクルを起動する。
  - `StopLossTriggered(EventId, Symbol, Market, PositionSide, Quantity, Price, StopLossPrice, DetectedAt)` — 損切り検知。
    リスク管理（#12 Slice C）が購読し、LLM を迂回して決済（Close）注文を発行する。数量・建玉方向を載せ、購読側が
    Close の `OrderIntent` を組み立てられるようにする。
- **責務境界**: 市場監視 = 検知＋イベント発行のみ。損切りの**執行**（Close 注文の発行・リスク統制・監査）はリスク管理。
- 損切り評価は変動判定・クールダウン・kill switch と独立に常に行う（フェイルセーフ。NFR「損切り監視は最後まで維持」）。
- 変動判定の基準値は「前回 AI 判断時点の価格」とする（前日終値だと寄付きギャップで毎朝誤発火するため）。

## 理由

- ADR-0003 が「リスク管理が損切りを機械執行」と定めるため、検知（監視）と執行（リスク管理）を分離するのが方針に合致する。
- 執行側にリスク統制・監査を集約することで、損切りであってもガード・kill switch のフェイルセーフ扱い（手仕舞いは通す）と
  監査記録を一貫させられる。
- イベント契約を先に確定することで #12 Slice C の依存を解消し、両サービスを疎結合に並行実装できる。

## 結果

- 良い影響: 市場監視とリスク管理が疎結合。損切りの執行経路が 1 箇所（リスク管理）に集約され、監査・統制が一貫する。
- 悪い影響・トレードオフ: 損切りの「検知 → 発行 → 購読 → 執行」に MassTransit を挟むため、同一プロセス直呼びより
  レイテンシが増える。NFR（検知から発注完了まで 5 分以内）には十分収まるが、極端な急変時の遅延は監視間隔と併せて
  Slice B で評価する。
- フォローアップ: #12 Slice C で `StopLossTriggered` 購読→Close 注文発行。市場監視 Slice B でポーリング・発行・基準値更新。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0009](IADR-0009_async-contract-format.md)、[IADR-0010](IADR-0010_risk-service-layering-and-slicing.md)
