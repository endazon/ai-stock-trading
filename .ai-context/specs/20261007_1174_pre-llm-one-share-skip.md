---
title: 段階資金の残枠が現在値 × 1 株に満たない新規建ては LLM を呼ばずに見送る（#1174）
type: spec
status: accepted
related_ids: [FR-10, FR-04, FR-11, UC-01, UC-02, ADR-0003, IADR-0500, IADR-0463, IADR-0495, IADR-0471, IADR-0462, IADR-0452, IADR-0374, IADR-0107, IADR-0099, IADR-0003, IADR-0017, IADR-0134]
author: claude (Claude Code)
created: 2026-10-07
updated: 2026-10-07
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 リスク統制・FR-04 判断)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003 (リスク管理の権威と直列の配置)
---

# 段階資金の残枠が 1 株の価格に満たない新規建ては LLM を呼ばずに見送る（#1174）

## 背景（issue の観測）

- 2026-10-05 US セッションで、取引判断のログに `サイジングで数量 0 のため見送り: GOOGL` が 48 回出た。各回の直前に `LLM 判断: GOOGL action=Buy` があり、
  一次と二次の LLM を経たうえでサイジングで数量 0 になっていた。
- 原因: `availableCapital = min(段階残枠, 日次残枠)` は LLM の後でしか使われない。LLM の前の見送り（IADR-0463）は「金額に依存しない理由」に限り、
  段階資金を対象外と明記している。#1176（IADR-0495 決定 2）が「上限が最小の名目額（equity の 1%）に届かない」場合だけを LLM の前へ移したが、
  **残枠が 1% 以上でも 1 株の価格に満たない**場合（GOOGL の形）は LLM を呼んでいた。

## 裁定（オーナー・2026-10-07。issue のコメント）

**見送る。** サイジングの文脈を取得した時点で段階資金の残枠（`min(段階残枠, 日次残枠)`）が「現在値 × 1 株」に満たない新規建ては、LLM を呼ばずに見送る
（IADR-0463 の LLM 前見送りの境界を広げる改定 IADR を置く）。見送りの理由は監査台帳で区別できる形で残す。

## 計画の確認（読み取り専用）

- 起点は **FR-10**（統制の金額上限＝段階資金・日次枠）。FR-04（判断）は LLM の前の見送りの地点として触る。計画 ADR-0003（リスク管理の権威と直列の配置）は変えない
  （審査は残す。省くのは費用の最適化であり統制ではない）。
- 計画書に LLM の前の見送りの地点の規定は無い（実装の判断。IADR-0463 / IADR-0495 と同じ）。計画への環流は不要。

## 実測（origin/develop c17abf46）

- `TradeDecisionAppService.DecideAsync` の順序: 方針 → `sizingProvider.GetContextAsync`（context）→ 現在値（`currentPrice`）→ 換算レート（`rateToBase`）→
  保有・未約定 → [鮮度切れ・保有なしの見送り] → **[#1176 の LLM 前の下界]** → [#1113 の可否の照会] → RAG ほか → LLM → … → サイジング。
- サイジングの参照価格は `referencePrice = currentPrice ?? decision.ReferencePrice`（IADR-0099 決定 2 のアンカリング）。**現在値があるときは LLM の価格に依らず
  現在値がそのまま使われる**。換算は `referencePriceBase = referencePrice * rateToBase`（同じ `fxReading`）。
- `PositionSizer.CalculateCappedQuantity`: `amountCap = min(maxOrderAmount, availableCapital)`、`amountBasedQuantity = amountCap <= 0 ? 0 : floor(amountCap / referencePrice)`、
  結果は `min(riskBased, amountBased)`。したがって **`availableCapital / referencePriceBase < 1` なら数量は必ず 0**（`amountCap ≤ availableCapital` で、decimal の除算は
  被除数について単調）。損切り幅（LLM 依存）・equity・1 注文上限に依らない。
- `availableCapital = max(0, min(StageCapitalRemaining ?? 0, DailyOrderRemaining ?? 0))`。未供給（null）は 0 に畳まれる（#869）。
- 単元株（東証の 100 株）の規則は**コードに無い**（`backend` に `単元`・`LotSize` 等の実装なし。サイジングの単位は 1 株）。IADR-0495 §結果 が「日本株（単元 100 株）」と
  書くのは市場の慣行の注記で、統制ではない。→ 本件の判定は「1 株」で正しい。将来単元の規則が入っても、1 株が買えないなら単元も買えないので下界のまま成り立つ。
- 換算レートの鮮度切れで保有 0 の銘柄は、この地点より前に `FxRateStaleNoHolding` で見送られる（この地点の保有 0 の銘柄はレートが新規建てに使える）。
- 列挙の末尾: `DecisionForgoneBeforeLlmReason` は 6 値（末尾 `EntryCapacityBelowMinimumNotional`）、`DecisionSkipReason` は 17 値（末尾 `SizedBelowMinimumNotional`）。
  いずれも proto には載らない（`check-proto-contracts` の対象外）。監査台帳は理由を名前（`ToString`）で残す（`AuditEntryFactory` は列挙の値を個別に持たない）。
  夜間の要約 §11 は `Reason` で group by する（値を個別に持たない）。通知は本イベントを購読しない。
- 採番: IADR は 0495〜0497 が使用済み・0498/0499 は並行作業の予約 → **IADR-0500**。試験 ID は develop が T-10-2337 と T-10-2350〜2365 まで使用済み、
  並行 PR を避けて **T-10-2370〜** を使う。

## 設計

### 「新規建て」の線引き（裁定の「新規建て」）

**保有が既知で 0、かつ未約定の新規建てが既知で空**の銘柄に限る（IADR-0463 決定 4・IADR-0495 決定 2 と同じ線引き）。

- この銘柄では LLM の結論は「新規の買い（数量 0 で必ず見送り）」「売り（裸の新規売りとして `NakedShortOpen` で必ず見送り）」「Hold」しか無い。
  よって LLM を省いても結論は変わらない。
- 保有中の銘柄は省かない。LLM は決済（手仕舞い）を返し得る（FR-10「手仕舞いは止めない」）。買い増しは LLM の後に従来どおり数量 0 で見送られる（IADR-0471 と同じく
  決済の判断を残す）。
- 未約定ありの銘柄・保有や未約定が不明の銘柄は省かない（#1176 と同じ。不明を「無い」と読まない。IADR-0358 / IADR-0390）。

### 判定（真の下界）

次のすべてを満たすときだけ、`TradeDecisionForgoneBeforeLlm(EntryCapacityBelowOneShare)` で LLM を呼ばずに見送る。

1. 保有が既知で 0・未約定が既知で空。
2. **現在値が既知で正**（`currentPrice is > 0`）。現在値ソースが未有効（NoOp＝null）の構成では、サイジングは LLM の参照価格を使うため LLM の前には分からない
   → 省かない（従来どおり）。
3. **段階残枠・日次残枠がいずれも既知**（null でない）。未供給は「足りない」と読まない（#1176 と同じ）。equity（`Capital`）は判定に使わない（1 株の判定は equity に依存しない）。
4. `PositionSizer.CannotAffordOneShare(max(0, min(段階残枠, 日次残枠)), currentPrice × rateToBase)` が真。関数は **`referencePrice > 0` かつ
   （`availableCapital <= 0` または `availableCapital / referencePrice < 1`）**。サイジングと同じ除算（同じ decimal の式）で判定し、`PositionSizer` に置く（単一の情報源）。
   **ちょうど等しい（残枠 ＝ 1 株の価格）は 1 株買えるので省かない。**

- 判定は手元の値だけで決まるので、IADR-0463 の照会（ネットワーク）より**先**に置く。
- 価格は LLM の前に読んだ `currentPrice`（アンカー後の参照価格と同じ値）に、同じ `rateToBase` を掛けた基準通貨（USD）。日本株（JPY）は換算して比べる。

### 2 つの LLM 前の金額の判定の順序

**#1176 の `EntryCapacityBelowMinimumNotional` を先に評価し、本件はその後**（両方に当たる銘柄は #1176 の理由で記録される）。

- 残枠が equity の 1% に届かないときは、銘柄の価格に依らずすべての銘柄の新規建てが見送られる（原因は資金の枯渇）。本件の理由は「残枠は 1% 以上あるが、この銘柄の
  1 株の価格に届かない」（原因は銘柄の価格）に絞られ、計器で読み分けられる（GOOGL の形）。
- #1176 の既存の試験（残枠 0 の `EntryCapacityBelowMinimumNotional`）の意味を変えない。
- しきい値 0（#1176 の統制を外す構成）や equity 未供給のときは #1176 の判定が働かないため、残枠 0 の銘柄は本件の理由で見送られる。

### 変更点

| 対象 | 変更 |
| --- | --- |
| `RiskManagementService.Domain.PositionSizer` | `CannotAffordOneShare(availableCapital, referencePrice)` を足す（サイジングと同じ式の下界） |
| `TradeDecisionAppService.DecideAsync` | #1176 の判定の直後・#1113 の照会の前に上の判定を置き、`SkipBeforeLlmAsync(EntryCapacityBelowOneShare)` |
| `DecisionForgoneBeforeLlmReason` | **末尾**に `EntryCapacityBelowOneShare`（7 値） |
| `DecisionSkipReason` | **末尾**に `EntryCapacityBelowOneShare`（18 値。LLM の前・上と同名） |
| `TradeDecisionAppService.ToSkipReason` | 写像を足す |
| IADR-0500（新規） | 本件の決定。IADR-0463・IADR-0495 の LLM の前の境界を広げる改定 |
| IADR-0463 / IADR-0495 / 索引 README | 日付つき追記（本文は書き換えない） |
| `docs/`（FR-10 機能仕様・試験仕様・監査イベント・イベントとポート・観測・運用 README・夜間の要約の手順書） | 新しい理由と地点数（7）・語彙数（18） |

### 規則 9・10 の母集合（走査の結果）

誤りの側の文字列で走査した（`金額に依存`・`6 地点`・`6 値`・`17 値`・`LLM の後にしか`・`SizingZeroQuantity`・`EntryCapacityBelowMinimumNotional`）。

| 箇所 | 対応 |
| --- | --- |
| IADR-0463 決定 2「金額に依存するもの（…段階資金…）は対象外」・2026-10-07 追記「6 地点」 | 日付つき追記（口の対象は不変。LLM の前の見送りは 7 地点。段階資金・日次枠は判断側の手元の判定で 2 つ扱う） |
| IADR-0495 決定 2「省かない: …価格に依存して最小を割る場合」「LLM の費用は節約されない」 | 日付つき追記（1 株に届かない場合は LLM の前へ移った） |
| 索引 README の IADR-0463・IADR-0495 の行 | 追記ブロックを足す（原文は残す）。IADR-0500 の行を足す |
| `TradeDecisionAppService`（「6 地点」「6 値」）・`TradeDecisionForgoneBeforeLlm.cs`（「6 値」） | 7 へ直す |
| `DecisionSkipReasonTests`（17 値）・`DecisionHeldReportTests`（判断前 6 地点・17 値）・`LedgerGapEventsTests`（6 値・6 地点） | 18 値・判断前 7 地点・7 値へ |
| `docs/functional/FR-10_risk-controls.md` §最小の名目額（LLM の前・LLM の後・計器の読み方） | 1 株に届かない行と計器の移動を足す |
| `docs/data/audit-events.md`（理由は 6 つ）・`docs/api/events-and-ports.md`（Reason の列挙）・`docs/observability/observability.md`・`deploy/observability/README.md`・`docs/operations/nightly-ledger-summary-runbook.md`（§11 の理由） | 新しい理由を足す |

除外（理由）: `.ai-context/specs/` の確定済み記録（point-in-time）。IADR-0452 / IADR-0462 の「4 地点」と試験仕様書の T-10-1772・T-10-1793・T-10-1907・T-10-2322 の行（その時点の
記録で、後続の追記・行が数を更新している。#1176 も同じ扱い）。`docs/tests/FR-10_risk-controls-tests.md` の「金額に依存する上限は口の対象に入れていない」（**口**の対象の話で、
本件でも口は変えない）。`scripts/nightly-ledger-summary.sh`（理由で group by し値を持たない）。ダッシュボード（`sum by (reason)` で閉じた列挙を持たない）。

## 受け入れ基準 → 試験

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| 1 | 下界の関数はサイジングと一致する: 下界が真ならサイジングの数量は必ず 0、偽なら残枠の金額キャップで 1 株以上買える。ちょうど等しいは偽。価格が 0 以下は偽。残枠 0 以下は真 | T-10-2370 `PositionSizerTests` |
| 2 | GOOGL の形（equity $100,000・残枠 $2,000〔1% 以上〕・現在値 $2,500）で保有 0・未約定なし → LLM を呼ばず `EntryCapacityBelowOneShare`（LLM 前の見送りの記録 1 件・判断後の見送りなし） | T-10-2371 `OneShareCapacityDecisionTests` |
| 3 | 境界: 残枠 ＝ 1 株の価格は LLM を呼び 1 株の発注意図になる。1 セント足りないと LLM を呼ばずに見送る。日次残枠だけが足りないときも見送る | T-10-2372 同上 |
| 4 | 日本株（JPY）は基準通貨へ換算して比べる（レート 0.0064・残枠 $2,000: ¥312,500＝$2,000 ちょうどは LLM を呼ぶ、¥312,501 は見送る） | T-10-2373 同上 |
| 5 | 省かない: 保有中（買い増しは LLM の後に数量 0）・未約定あり・現在値ソース未有効（LLM の価格を使う）・段階残枠または日次残枠が未供給 | T-10-2374 同上 |
| 6 | 順序: 残枠が 1% にも 1 株にも届かないときは `EntryCapacityBelowMinimumNotional`（#1176 が勝つ）。しきい値 0 のときは本件の理由。equity 未供給でも残枠が既知なら本件の理由 | T-10-2375 同上 |
| 7 | 語彙: `DecisionForgoneBeforeLlmReason` は 7 値・`DecisionSkipReason` は 18 値で末尾が `EntryCapacityBelowOneShare`、同名で写る。判断前 7 地点・判断後 11 地点で語彙を覆う。見送りの表で地点ごとに異なる値 | T-10-2376 `LedgerGapEventsTests`・`DecisionSkipReasonTests`・`DecisionHeldReportTests` |
| 8 | 監査台帳に理由の名前で残る（`TradeDecisionForgoneBeforeLlm` の要約・詳細） | T-10-2377 `AuditEntryFactoryTests` |
| 9 | `dotnet build` 警告 0・関係するサービスの試験が緑・`dotnet format`・node の検査が緑 | 実測（PR 本文） |

### 変異（主要な分岐）

| # | 変異 |
| --- | --- |
| N1 | 本件の判定を外す |
| N2 | 比較を「以下」にする（ちょうど 1 株を省く） |
| N3 | 保有 0・未約定なしの条件を外す（保有中でも省く） |
| N4 | 換算を外す（ローカル通貨の価格で比べる） |
| N5 | 段階残枠だけで読む（日次残枠を見ない） |
| N6 | 現在値が無いとき LLM の参照価格の代わりに 0 や trigger の価格で判定する（現在値の条件を外す） |
| N7 | 未供給の残枠を 0 と読む（null で省く） |
| N8 | 順序を入れ替える（本件を #1176 より先に評価する） |

## 残余

- 保有中の銘柄の買い増しで残枠が 1 株に届かない場合は LLM を呼ぶ（決済の判断を残すため）。結論が買い増しなら従来どおり数量 0 で見送る。費用は節約されない。
- 現在値ソースが未有効の構成（既定 NoOp）では省かない（価格が LLM の後にしか決まらない）。本番は現在値ソースが有効。
- 1 注文上限（equity × 25%）が 1 株の価格に満たない場合も数量 0 が確定するが、裁定の範囲（段階・日次の残枠）の外であり本件では扱わない（equity が小さい
  ときだけ起こる）。
- 稼働での確認（GOOGL の形が `decision_skips{reason="EntryCapacityBelowOneShare"}` と台帳に出て、`LLM 判断` のログが消える）は PoC セッションで行う。
