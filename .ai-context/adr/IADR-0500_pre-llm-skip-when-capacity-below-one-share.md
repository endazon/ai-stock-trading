---
title: IADR-0500 段階残枠と日次残枠の小さい方が現在値 × 1 株（基準通貨）に満たない新規建ては LLM を呼ばずに見送る（IADR-0463・IADR-0495 の LLM 前の見送りの境界を広げる）
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-04, FR-11, UC-01, UC-02, ADR-0003, IADR-0463, IADR-0495, IADR-0471, IADR-0462, IADR-0452, IADR-0374, IADR-0358, IADR-0390, IADR-0099, IADR-0107, IADR-0003, IADR-0017, IADR-0134]
author: claude (Claude Code)
created: 2026-10-07
updated: 2026-10-07
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 リスク統制・FR-04 判断)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003 (リスク管理の権威と直列の配置)
---

# IADR-0500: 残枠が 1 株の価格に満たない新規建ては LLM を呼ばずに見送る（#1174）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-07
- 決定者: オーナー裁定（[#1174](https://github.com/endazon/ai-stock-trading/issues/1174) の 2026-10-07 のコメント「見送る」）を claude が実装。
  裁定が実装に委ねた点（「新規建て」の線引き・価格の取り方・換算・単元・未供給・2 つの判定の順序・理由の名前）は本 IADR が決める。

## 起点・関連

- 関連する計画書 ID: **FR-10**（段階資金・日次枠の金額上限）。FR-04（判断）は LLM の前の見送りの地点として触る。計画 ADR-0003 は変えない（審査は残す）。
- 対象 Issue: [#1174](https://github.com/endazon/ai-stock-trading/issues/1174)
- 関連する実装仕様書: [20261007_1174_pre-llm-one-share-skip](../specs/20261007_1174_pre-llm-one-share-skip.md)（母集合・試験・変異）
- **改める**: [IADR-0463](IADR-0463_entry-blockers-before-llm.md)（LLM の前の見送りは金額に依存しない理由に限る）と
  [IADR-0495](IADR-0495_min-entry-notional-and-decision-exit-same-day-reentry.md) 決定 2（価格に依存するものは省かない）の、LLM の前の見送りの境界。
- 前提: [IADR-0099](IADR-0099_current-price-context-for-decision.md) 決定 2（参照価格を現在値へアンカリング）、[IADR-0107](IADR-0107_base-currency-conversion.md)（サイジングは基準通貨）、
  [IADR-0003](IADR-0003_position-sizing-responsibility.md) / [IADR-0017](IADR-0017_trade-decision-structure.md)（サイジングは判断サービス・残枠 min）、
  [IADR-0462](IADR-0462_ledger-position-query-status-and-pre-llm-skips.md) 決定 4（LLM の前の見送りは台帳へ 1 回ごと）、
  [IADR-0452](IADR-0452_baseline-advances-on-judged-skip.md) 決定 1（LLM の前の見送りで基準値を進めない）、
  [IADR-0134](IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md) 決定 2（列挙は末尾へ足す）。

## コンテキストと課題

2026-10-05 の US セッションで、GOOGL について `LLM 判断: GOOGL action=Buy` の直後に `サイジングで数量 0 のため見送り` が 48 回出た。一次と二次の LLM を経て、
`availableCapital = min(段階残枠, 日次残枠)` が 1 株の価格に満たないため数量 0 になっていた。この残枠は LLM の後でしか使われていない。
IADR-0463 は LLM の前の見送りを「金額に依存しない理由」（リスク管理の新規建ての可否の口）に限り、IADR-0495 決定 2 は残枠が最小の名目額（equity の 1%）に
届かない場合だけを LLM の前へ移した。**残枠は 1% 以上あるが、この銘柄の 1 株に届かない**場合（GOOGL の形）は LLM を呼んでいた。

## 決定

### 決定 1: 判定は「サイジングと同じ式の下界」で、LLM の結論に依らず数量 0 が確定するときだけ真

- `PositionSizer.CannotAffordOneShare(availableCapital, referencePrice)` ＝ **`referencePrice > 0` かつ（`availableCapital <= 0` または `availableCapital / referencePrice < 1`）**。
  サイジング（`CalculateCappedQuantity`）の金額キャップ `min(1 注文上限, availableCapital)` は `availableCapital` 以下で、decimal の除算は被除数について単調だから、
  真なら損切り幅（LLM 依存）・equity・1 注文上限・縮小係数に依らず数量は 0 である（試験 T-10-2380 が網羅で確かめる）。
- **ちょうど等しい（残枠 ＝ 1 株の価格）は偽**（1 株買える）。参照価格が正でなければ偽（下界として何も言えない。LLM の後の `ReferencePriceInvalid` のまま）。
- 関数は `PositionSizer` に置く（サイジングの式の単一の情報源の隣。判断サービスは extern alias で読む）。

### 決定 2: 取引判断は次のすべてを満たすときだけ LLM を呼ばずに見送る

1. **保有が既知で 0・未約定の新規建てが既知で空**（裁定の「新規建て」）。この銘柄では LLM の結論は新規の買い（数量 0 で必ず見送り）・売り（裸の新規売りとして
   `NakedShortOpen` で必ず見送り）・Hold しか無い。IADR-0463 決定 4・IADR-0495 決定 2 と同じ線引きである。保有中の銘柄は決済の判断を残すため省かない
   （買い増しは LLM の後に従来どおり数量 0。IADR-0471 と同じく LLM は必ず呼ぶ）。未約定あり・保有や未約定の不明も省かない。
2. **現在値が既知で正**。サイジングの参照価格は `currentPrice ?? decision.ReferencePrice`（IADR-0099 決定 2）であり、現在値があれば LLM の参照価格は使われない
   （＝LLM の前に読んだ値がそのまま参照価格）。現在値ソースが未有効（NoOp）の構成では参照価格が LLM の後にしか決まらないので省かない。
3. **段階残枠と日次残枠がいずれも既知**。未供給（null）を「足りない」と読まない（IADR-0495 決定 2 と同じ）。equity は判定に使わない（1 株の判定は equity に依存しない）。
4. `CannotAffordOneShare(max(0, min(段階残枠, 日次残枠)), 現在値 × rateToBase)`。換算はサイジングと同じ `fxReading` のレート（基準通貨＝USD。日本株は円 × レート）。
   この地点の保有 0 の銘柄は、レートが新規建てに使える（鮮度切れで保有 0 の銘柄は先に `FxRateStaleNoHolding` で見送られている）。

- 記録は `TradeDecisionForgoneBeforeLlm(EntryCapacityBelowOneShare)`・`decision_skips{reason="EntryCapacityBelowOneShare"}`（**新しい理由**。監査台帳で
  `EntryCapacityBelowMinimumNotional` と区別できる＝裁定「見送りの理由は監査台帳で区別できる形で残す」）。`TradeDecisionHeld` は出さない（IADR-0452 決定 1）。
- 列挙は**末尾へ足す**: `DecisionForgoneBeforeLlmReason`（7 値）・`DecisionSkipReason`（18 値）。どちらも proto に載らず、監査台帳と夜間の要約は名前で数える
  （値を個別に持つ消費者は `TradeDecisionAppService.ToSkipReason` だけ）。
- **単元株**: 東証の 100 株の規則はコードに無く、サイジングの単位は 1 株である。よって判定は「1 株」で正しい。将来単元の規則が入っても、1 株が買えなければ単元も
  買えないので本判定は下界のまま成り立つ（単元で広げるのは別の決定）。

### 決定 3: 2 つの LLM 前の金額の判定の順序 —— 最小の名目額（#1176）を先に、本件を後に

- 残枠が最小の名目額（equity × しきい値）にも届かないときは、銘柄の価格に依らず全銘柄の新規建てが見送られる（原因は資金の枯渇）。その記録は
  `EntryCapacityBelowMinimumNotional` に寄せ、本件の理由は「残枠はあるがこの銘柄の 1 株に届かない」（原因は銘柄の価格）に絞る。計器の内訳で読み分けられる。
- IADR-0495 の既存の記録（残枠 0 の銘柄は `EntryCapacityBelowMinimumNotional`）の意味を変えない。
- しきい値 0（#1176 の統制を外す構成）・equity の未供給（#1176 の判定が働かない）では、残枠 0 の銘柄も本件の理由で見送る。
- いずれも手元の値だけで決まるので、IADR-0463 の可否の照会（ネットワーク）より**先**に置く（並び: 最小の名目額 → 1 株 → 可否の照会）。

## 結果

- 良い影響: GOOGL の形（残枠が 1 株に届かない銘柄）で一次・二次の LLM を呼ばない。判断サイクルの所要も縮む（#1169 の打ち切りから遠ざかる）。理由が名前で台帳・計器に出る。
- 悪い影響・トレードオフ:
  - 🔴 **計器の移動**: 保有 0 の銘柄で残枠が 1 株に届かない状態は、従来 LLM の後の `SizingZeroQuantity` だったが、LLM の前の `EntryCapacityBelowOneShare` へ移る。
  - LLM の前に読んだ残枠と、LLM の後のサイジングで使う残枠は同じ `context`（1 回の取得）であり、窓の問題は無い。現在値も同じ値を使う。
  - 保有中の買い増し・現在値ソース未有効の構成では費用は節約されない（決定 2）。
  - 1 注文上限（equity × 25%）が 1 株の価格に満たない場合も数量 0 が確定するが、裁定の範囲（段階・日次の残枠）の外であり扱わない（equity が小さいときだけ起こる）。
- 審査は変わらない（両端で止める。省くのは費用の最適化であって統制ではない）。

## 関連

- [#1174](https://github.com/endazon/ai-stock-trading/issues/1174)（裁定）
- 実装（`backend/` 配下）: `Services/RiskManagementService/Domain/PositionSizer.cs`・
  `Services/TradeDecisionService/Features/TradeDecision/DecideTrade/TradeDecisionAppService.cs`・
  `Shared/AiStockTrading.Shared.Contracts/{Events/TradeDecisionForgoneBeforeLlm,Observability/DecisionSkipReason}.cs`
- テスト: T-10-2380〜T-10-2387（`docs/tests/FR-10_risk-controls-tests.md`）
