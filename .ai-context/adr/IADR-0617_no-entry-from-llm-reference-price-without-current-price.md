---
title: IADR-0617 権威ある現在値の供給が無い構成では、LLM が返した参照価格で新規建てを作らず見送る（手仕舞いは止めない。従前の挙動は明示の opt-in）
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-05, FR-10, ADR-0003, ADR-0049, ADR-0067, IADR-0099, IADR-0374]
author: claude (Claude Code)
created: 2026-11-05
updated: 2026-11-05
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0049_stop-width-floor-atr14-widen-no-ceiling.md (実測 2)
  - planning:projects/ai-stock-trading/07_adr/ADR-0067_decision-exit-limit-unfilled-cancel-only.md (実測 3)
---

# IADR-0617: 現在値の無い新規建ては見送る

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-11-05
- 決定者: Claude Code（実装）／ オーケストレーター（参照価格が無いとき LLM の値へ退避するなら、取引しない側へ倒す、の指示）

## 起点・関連

- issue: #1529（3 件の 1 つ）
- 前提: IADR-0099（現在値へのアンカー。決定 3「供給が有効で取れないなら見送る」・未有効化は現行挙動を保つ）・IADR-0374（見送りの理由）。
- 実装仕様書: [`.ai-context/specs/20261105_1529_fail-closed-auth-price-policy.md`](../specs/20261105_1529_fail-closed-auth-price-policy.md) §2

## コンテキストと課題

`referencePrice = currentPrice ?? decision.ReferencePrice`。現在値の供給が無い構成（chart の既定の no-op を含む）では、LLM が返した参照価格でサイジング・損切りライン・発注価格が決まる。IADR-0099 決定 3 はこの構成を「現行挙動を保つ」としていた（SIMULATE の検証を止めないため）。
計画は「価格の判断は LLM」と定めていない（ADR-0067 実測 3）。ADR-0049 の「LLM の参照価格へ退避する」は実測の行であり、決定ではない。ADR-0003 は AI の判断を機械的統制で拘束する。

## 決定

1. 現在値が無く、新規建て（Open）なら、発注意図を作らずに見送る（`DecisionSkipReason.ReferencePriceInvalid`。新しい値を足さない）。Warning を残す。
2. 手仕舞い（Close）は止めない（FR-10）。数量は保有の全量で、価格は従前どおり。
3. 従前の挙動は構成 `TradeDecision:AllowUnanchoredEntryPrice=true` でだけ（Program.cs の既定は false）。`UnanchoredEntryPriceOptions` で渡す。クラスの未指定（null）の既定は「許す」で、現在値の供給を持たない既存の試験の前提を保つためだけのものである（本番の経路は必ず登録する）。
4. IADR-0099 決定 3 の「未有効化は現行挙動」を、新規建てについて本決定で改める（手仕舞いは現行のまま）。

## 結果

- 現在値の供給が無い構成では新規建てが出なくなる（docker-compose の既定を含む）。検証で流すときは opt-in を明示する。
- values-local（finnhub）でキーが無いと no-op になり、新規建てが止まる（意図した fail-closed）。

## 残余

1. 計画 ADR-0049 実測 2 の記述は本決定の後の実態と食い違う。計画への環流が要る（オーナーの判断）。
