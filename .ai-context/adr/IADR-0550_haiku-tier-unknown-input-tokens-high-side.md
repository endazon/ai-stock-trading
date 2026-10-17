---
title: IADR-0550 入力トークン数が取れない要求は 2 段の単価の高い側で引く。「取れない」は int? の null で計測まで運び、輸送の出口で欠落と 0 以下を null と読む（proto は変えない）。金額の式の入力は 0 のまま
type: impl-adr
status: Accepted
related_ids: [NFR-13, FR-04, ADR-0064, ADR-0037, IADR-0524, IADR-0122, IADR-0055]
author: claude (Claude Code)
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0064_llm-assignment-to-5-5-family-and-stage0-rerun.md（決定 4・フォローアップ 1・実装の現状 R8）
  - planning#783（2026-10-10 のコメントの裁定 6）
related_specs:
  - ../specs/20261017_1306_haiku-tier-unknown-input-tokens.md
---

# IADR-0550: 入力トークン数が取れない要求は 2 段の単価の高い側で引く（#1306）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-17
- 決定者: claude が起案（起点 [#1306](https://github.com/endazon/ai-stock-trading/issues/1306)・計画 ADR-0064 決定 4・planning#783 の裁定 6。レビューは PR で受ける）

## 起点・関連

- 計画: **ADR-0064 決定 4**「段は要求ごとに、キャッシュ読み・書きを含む入力トークンの合計で判定する。合計が取れないときは高い側へ倒す」、同 フォローアップ 1（実装の現状 R8 では見当たらない）。
- 前提として覆さない IADR: [IADR-0524](IADR-0524_claude-5-5-models-with-previous-generation-transition.md) 決定 4（2 段の単価・閾値を**超える**と第 2 段）、
  [IADR-0122](IADR-0122_per-model-llm-pricing.md)（費用統制の危険側は過小計上）、IADR-0055（計測は best-effort）。
- 仕様書: `.ai-context/specs/20261017_1306_haiku-tier-unknown-input-tokens.md`

## コンテキスト

- 段の判定 `LlmPriceTable.Resolve(model, int inputTokens)` は入力トークン数を `int` で受けていた。
  REST の応答は `int?` だが egress 3 か所（取引判断・報告書の散文・方針の改訂）が `?? 0` で 0 に潰し、gRPC は proto3 の `int32` で欠落と 0 を区別できない。
- 基盤の REST（`CompletionApiResponse.InputTokens`）も `int` であり、使用量を受け取れなかった応答では 0 を返す。**REST の null も 0 も、gRPC の 0 も、いずれも「取れない」が 0 として届く。**
- 0 は閾値（100,000）以下なので、取れない要求は第 1 段（安い側）＝入力・出力とも 1/5 の単価で計上される。IADR-0122 の危険側（過小計上）である。
- 送った要求の入力が 0 トークンになることは無い（プロンプトは空にならない）。

## 決定

### 決定 1: 「取れない」は `int?` の null で表し、計測まで運ぶ

- `LlmUsage.InputTokens`（取引判断・報告書の 2 か所）を `int?` にする。null＝取れない。egress の `dto.InputTokens ?? 0` をやめる。
- 段の判定は `Resolve(string? model, int? inputTokens)`。**null は第 2 段（高い側）**。取れていれば従来どおり閾値を**超える**ときだけ第 2 段（100,000 ちょうどは第 1 段）。
- 第 2 段を持たない行・未知モデル（最大単価）・空の表（既定ペア）は、取れなくても従来の単価（段の選択だけを変える）。
- 入力トークン数を取らない多重定義は引き続き置かない。

### 決定 2: 輸送の出口で、欠落と 0 以下を「取れない」と読む（proto は変えない）

- `LlmReportedInputTokens.From(int? reported)`（`Shared.Contracts`）を置き、REST・gRPC の両輸送が `LlmCompletionPayload` を作るときに通す。`> 0` だけを取れた値とし、それ以外は null。
- **REST にも同じ読み方を通す。** 基盤の REST も 0 を返すので、REST だけ「0 は取れた」と読むと、輸送の選択によって段が変わる（`LlmTransportUnsentParityTests` が両輸送の出口の一致を固定している）。
- proto（`completion.proto` の写し）を `optional int32` にする案は採らない（下の選択肢 B）。proto と基線（`scripts/proto-contract-baseline.json`）は変えない。

### 決定 3: 金額の式の入力トークン数は 0 のまま

- `LlmPricing.Compute(usage.InputTokens ?? 0, …)`。取れない入力トークン数を推定して金額へ足さない（推測値を書かない）。変わるのは単価の段（出力の単価も第 2 段になる）だけである。
- Stage 0 の記録が持つトークン数の合計（`Stage0DecisionRecorder`）も取れた分の合計（取れない要求は 0）とする。段の判定とは別の量である。

### 決定 4: 3 経路を同じ規則で引く

計上（取引判断の `PublishingLlmUsageReporter`）・Stage 0 の実費（`Stage0RecordingUsageCollector.CostOf`）・月報の費用（報告書の `PublishingLlmUsageReporter`）は、いずれも `Resolve(usage.Model, usage.InputTokens)` を `int?` のまま呼ぶ。
Stage 0 の見積り（`Stage0DecisionRecorder.EstimateFor`）は構成の 1 回あたりの入力量で引き、取れない場合が無いので変えない。

## 検討した選択肢

| 選択肢 | 採否 | 理由 |
| --- | --- | --- |
| A. **`int?` で運び、輸送の出口で 0 以下を null と読む（本決定）** | 採用 | AST だけで閉じる。両輸送の読み方が 1 か所に揃い、egress・計上は型で「取れない」を運ぶ |
| B. proto の `input_tokens` を `optional` にする | 不採用 | 写しの正本は基盤（MSP）の契約であり、AST だけでは閉じない。基盤の REST も 0 を返すので、proto を変えても REST の 0 の読み方は別に要る |
| C. `Resolve` の中で `inputTokens <= 0` を高い側と読む（型は `int` のまま） | 不採用 | 型で「取れない」を表せず、egress が `?? 0` で潰しても検出できない。Stage 0 の見積りの構成値 0 まで高い側になる |
| D. 取れない入力トークン数を推定して金額へ足す | 不採用 | 推測値を書かない（#1306 の注意）。決めるのは段だけ |

## 結果・影響

- 取れない応答の `claude-haiku-5-5` の計上は、出力 1k あたり 0.0819 円 → 0.409 円（第 2 段）になる（入力は 0 のまま）。他のモデルの計上は変わらない。
- `LlmCompletionPayload.InputTokens` は 0 を持たなくなる（null になる）。読む側は 3 つの egress だけで、いずれも計測へそのまま渡す。
- 取引判断のログ（`LLM 応答: … tokens=`）は取れない入力を 0 ではなく null の表記で出す（0 と区別する）。

## 試験

| 内容 | 試験 |
| --- | --- |
| null は第 2 段・取れた値は閾値どおり（100,000 / 100,001）・第 2 段の無い行と未知モデルと空の表は従来どおり・金額の入力は 0 のまま・`Resolve` の引数は `int?` | `LlmPriceTableTests` |
| 両輸送で 0 以下は null・取れた値はそのまま・REST の欠落は null・gRPC の 0 は第 2 段で引かれる | `LlmTransportUnsentParityTests` |
| egress 3 か所は欠落・0 を null のまま計測へ渡す | `HttpLlmCompletionClientTests`・`HttpReportNarrativeDrafterVisibilityTests`・`LlmReportPolicyReviserTests` |
| 3 経路で null は第 2 段・取れた値は閾値どおり・`LlmUsage.InputTokens` は `int?` | 両 `PublishingLlmUsageReporter` の試験・`Stage0DecisionRecorderTests` |

変異（null を第 1 段へ・閾値を `>=`・0 を残す・egress と 3 経路で `?? 0` を戻す）でそれぞれ赤になることを確かめた（仕様書 §変異）。

## フォローアップ

1. キャッシュを使い始めたら、判定量にキャッシュ読み・書きを含める（ADR-0064 実測 2）。本決定は先取りしない。
2. 基盤が入力トークン数の欠落を明示できる形（`optional` 等）にしたら、決定 2 の 0 の読み方を見直す（急ぎではない。送った要求の入力が 0 にならない前提は変わらない）。
