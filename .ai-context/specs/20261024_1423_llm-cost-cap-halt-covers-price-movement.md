---
title: 月次 LLM 費用上限の 100% 到達で、定時・価格変動の両方の判断を LLM の前で見送る（#1423 M-5）
type: spec
status: done
related_ids: [NFR-13, FR-02, FR-04, UC-01, UC-02, ADR-0074, ADR-0057, IADR-0031, IADR-0462, IADR-0582]
author: claude (Claude Code)
created: 2026-10-24
updated: 2026-10-24
---

# 仕様書: 費用上限の停止を価格変動の判断まで及ぼす（#1423 M-5）

> 本仕様書は AST `origin/develop` `b427f998` を起点に作成した。判断は [IADR-0582](../adr/IADR-0582_llm-cost-cap-halt-skips-decisions-before-llm.md) に残す。

## 起点（トレーサビリティ）

- 起点: #1423 M-5。環流 planning#830、裁定 2026-10-23（利用者）→ 計画 ADR-0074（Accepted）。
- 計画: ADR-0074 決定 1〜3（「取引判断サイクル」は定時・価格変動の両方。100% で両方を LLM の前で見送る。80% は定時の間隔延長のみ。
  LLM を使わない保護は止めない）、NFR-13、`05_trading-assumptions` §6.1（2026-10-23 の注）。
- 前提（覆さない）: IADR-0031（費用統制の照会の失敗は Normal＝止めない）、IADR-0462（LLM を呼ぶ前の見送りの出口・台帳の語彙）。
- 範囲外: 80% の扱い（定時の間隔延長は情報収集に既にある。急変は延ばさない＝変更なし）。100% 到達の通知（既存の Critical）。
  Discord 設計書の 100% のアラートの記載（計画側の残作業）。

## 受け入れ基準

1. 費用統制が Halted のとき、判断の合流点（`DecideAsync`）は定時・価格変動のどちらでも LLM を呼ばずに見送り、見送りを記録する
   （`TradeDecisionForgoneBeforeLlm`・`DecisionSkipReason`。理由 `LlmCostCapHalted`）。
2. Halted でない・未結線・照会の失敗では従来どおり（止めない）。
3. 停止は `/costs/state` の `isHalted` が明示的に true のときだけ。
4. LLM を使わない保護（閉場前の手仕舞いの常駐・損切り・利用者の手仕舞い・維持率割れの自動縮小）は合流点を通らない（巻き込まない）。
5. 解除は費用統制の月替わり（費用計上月＝UTC の暦月）で自動（判断側は毎回照会するので追随する）。
6. 本番の構成で取引判断が費用統制を照会する（helm の `CostControl__BaseUrl`）。

## 母集合（規則 9）

| 引いた文字列 | 場所 | 判定 |
| --- | --- | --- |
| `DecideAsync(` の呼び出し元 | `InformationCollectedHandler`（定時）・`PriceMovementDetectedHandler`（価格変動）・`Stage0DecisionRecorder`（別のオーケストレータ） | 前 2 者は合流点を通る＝対象。Stage 0 の記録は自前のオーケストレータで合流点を通らない（ADR-0074 決定 1 の対象外の費用）＝対象外 |
| 閉場前の手仕舞い（`FlattenBeforeClose`） | 取引判断の常駐 | 合流点を通らない＝巻き込まない |
| `DecisionForgoneBeforeLlmReason` / `DecisionSkipReason` の網羅 | 写像・語彙の試験 3 本・文書 4 本 | 末尾へ足して追随 |
| `CostControl__BaseUrl` | helm（情報収集のみ） | 取引判断へ足す（values / values-local） |

## 試験の写像（T-10-3049〜3053。M-3 の PR が T-10-3035〜3048 を使う）

| ID | 受け入れ基準 | 試験 |
| --- | --- | --- |
| T-10-3049 | 1 | `LlmCostCapHaltBeforeLlmTests` |
| T-10-3050 | 2 | 同上（NoOp を直接参照する＝組み立ての監視 W3） |
| T-10-3051 | 3 | 同上（境界の表） |
| T-10-3053 | 3（送り手の本物の型 `CostControlDecision` の直列化。IADR-0420） | `CostControlHaltReadContractTests` |
| T-10-3052 | 1（語彙） | `DecisionSkipReasonTests`・`LedgerGapEventsTests`・`DecisionHeldReportTests` |

変異: 合流点の判定を `false &&` で無効にすると T-10-3049 の 2 件が赤になることを確かめた。4・6 は構成と経路の読みで、5 は費用統制の `MonthKey` の読みで確かめた（試験なし）。
