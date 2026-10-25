---
title: claude-haiku-5-5 の 2 段の単価で、入力トークンの合計が取れない応答を高い側の段で計上する（#1306）
type: spec
status: accepted
related_ids: [NFR-13, FR-04, ADR-0064, ADR-0037, IADR-0550, IADR-0524, IADR-0122, IADR-0055]
author: claude (Claude Code)
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0064_llm-assignment-to-5-5-family-and-stage0-rerun.md（決定 4・フォローアップ 1・実装の現状 R8）
  - planning#783（2026-10-10 のコメントの裁定 6）
---

# claude-haiku-5-5 の 2 段の単価で、入力トークンの合計が取れない応答を高い側の段で計上する（#1306）

## 起点

- [#1306](https://github.com/endazon/ai-stock-trading/issues/1306)。計画 ADR-0064 決定 4「段は要求ごとに、キャッシュ読み・書きを含む入力トークンの合計で判定する。**合計が取れないときは高い側へ倒す**」・フォローアップ 1。
  planning#783 の裁定 6（2026-10-10）「Haiku 5.5 の単価は、要求ごとに 2 段で引く。段はキャッシュを含む入力の合計で決め、合計が取れないときは高い側の段で計上する」。
- 前提: IADR-0524 決定 4（2 段の単価。#1295）・IADR-0122（過小計上を危険側とする）・MSP:IADR-0531 決定 2（基盤はプロンプトキャッシュを使わない）。

## 現況（origin/develop 31b1a087）

| # | 事実 | 場所 |
| --- | --- | --- |
| 1 | 段は `Resolve(model, int inputTokens)` の `inputTokens > 閾値` で決まる | `Shared.Infrastructure/Composable/Llm/LlmPriceTable.cs:114-125` |
| 2 | REST の応答の `InputTokens` は `int?` だが、計測（`LlmUsage`）へ渡すときに `?? 0` で 0 に潰す | `HttpLlmCompletionClient.cs:222`・`HttpReportNarrativeDrafter.cs:176`・`LlmReportPolicyReviser.cs:103` |
| 3 | gRPC の `input_tokens` は proto3 の `int32`。無いことと 0 を区別できず、0 のまま `LlmCompletionPayload` へ渡る | `Protos/platform/llmgateway/v1/completion.proto:82`・`GrpcLlmCompletionTransport.cs:63` |
| 4 | 基盤の REST（MSP `CompletionApiResponse.InputTokens`）も `int` で、使用量を受け取れなかった応答・送信しなかった応答では 0 を返す | `../microservices-platform` `Platform.Shared.Contracts/Dtos/CompletionDto.cs:77`・`CompletionUseCase.cs` |
| 5 | したがって合計が取れない応答は 0 トークン＝**第 1 段（安い側）**で引かれる（出力の単価も第 1 段） | 1〜4 |

## 判断（IADR-0550）

- **「取れない」を `int?` の null で表し、計測まで運ぶ。** `LlmUsage.InputTokens`（2 サービス）を `int?` にし、egress の `?? 0` をやめる。
- **段の判定（`LlmPriceTable.Resolve(model, int? inputTokens)`）は null を高い側（第 2 段）で引く。** 第 2 段を持たない行・未知モデル・空の表は従来どおり。
- **金額の式（`LlmPricing.Compute`）の入力トークン数は 0 のまま**（推定しない）。呼び出し側が `?? 0` を渡す。
- **gRPC は proto を変えない。** 輸送の出口で入力トークン数 0 以下を「取れない」（null）と読む（`LlmReportedInputTokens.From`）。REST も同じ読み方を通す（基盤の REST も int で 0 を返すため。輸送で段が変わらないように揃える）。
  proto を `optional` にする案は基盤（MSP）の契約変更を伴い、AST だけで閉じないため採らない。proto と基線（`scripts/proto-contract-baseline.json`）は変えない。

## やること

1. `LlmPriceTable.Resolve` の入力トークン数を `int?` にし、null は第 2 段。
2. `LlmUsage.InputTokens` を `int?`（TradeDecision・Report の 2 か所）。
3. egress 3 か所の `dto.InputTokens ?? 0` をやめる。
4. 輸送 2 か所（REST・gRPC）で `LlmReportedInputTokens.From` を通す（0 以下は null）。
5. 計上 3 経路（両サービスの `PublishingLlmUsageReporter`・Stage 0 の `CostOf`）は `Resolve(model, usage.InputTokens)`・`Compute(usage.InputTokens ?? 0, …)`。
6. Stage 0 の記録のトークン数の合計（`Stage0DecisionRecorder`）は取れた分の合計（`?? 0`。段の判定とは別）。
7. Helm README（単価の節）に「取れない応答は第 2 段」を足す。
8. IADR-0550 を起票し、索引へ行を足す。

## 受け入れ基準

| # | 基準（issue の受け入れ基準） | 試験 |
| --- | --- | --- |
| 1 | 第 2 段を持つ行で REST の入力トークン数が null → 第 2 段 | `LlmPriceTableTests.入力トークン数が取れない要求は第2段の単価で引く`・`HttpLlmCompletionClientTests.応答にトークンが無い場合は入力を取れないまま計測する`・`LlmTransportUnsentParityTests.REST_の入力トークン数の欠落は取れないまま出口へ出る` |
| 2 | gRPC の入力トークン数が 0 → 第 2 段 | `LlmTransportUnsentParityTests.入力トークン数の_0_は両輸送とも取れないと読む`・`gRPC_で入力トークン数が_0_の応答は第2段の単価で引かれる` |
| 3 | 取れている要求は従来どおり（100,000 は第 1 段・100,001 は第 2 段） | `LlmPriceTableTests.取れている要求は従来どおり閾値で引き分ける`（既存の `第2段を持つ行は入力トークン数で単価を引き分ける` も）・両 `PublishingLlmUsageReporter` の Theory |
| 4 | 第 2 段を持たない行・未知モデル・空の表は取れなくても従来の単価 | `LlmPriceTableTests.第2段の無い行と未知モデルと空の表は取れなくても従来どおり`・`PublishingLlmUsageReporterTests.第2段の無い行は取れなくても従来の単価で計上する` |
| 5 | 計上・Stage 0 の実費・月報の費用（報告書の計上）の 3 経路とも同じ規則 | `PublishingLlmUsageReporterTests.取れない要求は第2段で計上し取れた要求は閾値どおりに計上する`・`Stage0DecisionRecorderTests.実費は入力トークン数が取れない計測を第2段で引く`・`PublishingLlmReportersTests.入力トークン数が取れない要求は第2段で計上する` |
| 6 | 否定形: `?? 0` で潰してから `Resolve` へ渡す経路が無い | egress 3 か所の振る舞い試験（drafter・reviser・client）＋型の固定（`Resolve` の引数・両 `LlmUsage.InputTokens` が `int?`） |
| 7 | IADR に決定 4 の「取れないとき高い側」の実装を書く | IADR-0550 |
| — | 金額の式の入力は 0 のまま（推定しない） | `LlmPriceTableTests.取れない要求の費用は入力を足さず出力を第2段の単価で計算する` |

## 母集合（規則 9・10）

`git grep -n "Resolve(" -- backend`（単価表のもの）と `git grep -n "InputTokens ?? 0\|inputTokens ?? 0"`（作業前の develop 31b1a087）で引いた。

| 箇所 | 種別 | 作業前 | 作業後 |
| --- | --- | --- | --- |
| `TradeDecisionService/.../PublishingLlmUsageReporter.cs:33` | `Resolve` 呼び出し（計上） | `usage.InputTokens`（int。egress で 0 に潰れた値） | `int?` のまま渡す。金額は `?? 0` |
| `ReportService/.../PublishingLlmUsageReporter.cs:34` | `Resolve` 呼び出し（報告書・月報の費用） | 同上 | 同上 |
| `Stage0RecordingUsageCollector.cs:69` | `Resolve` 呼び出し（Stage 0 の実費） | 同上 | 同上 |
| `Stage0DecisionRecorder.cs:128`・`:130` | `Resolve` 呼び出し（Stage 0 の見積り） | 構成の 1 回あたりの入力量（int。常に取れている） | 変えない（取れない場合が無い） |
| `HttpLlmCompletionClient.cs:222` | `?? 0`（計測へ） | `dto.InputTokens ?? 0` | 潰さない |
| `HttpLlmCompletionClient.cs:207` | `?? 0`（ログ） | `dto.InputTokens ?? 0` | 潰さない（ログでも「取れない」と 0 を区別する） |
| `HttpReportNarrativeDrafter.cs:176` | `?? 0`（計測へ） | `dto.InputTokens ?? 0` | 潰さない |
| `LlmReportPolicyReviser.cs:103` | `?? 0`（計測へ） | `dto.InputTokens ?? 0` | 潰さない |
| `GrpcLlmCompletionTransport.cs:63` | proto3 の 0 | `response.InputTokens`（0 のまま） | `LlmReportedInputTokens.From`（0 は null） |
| `RestLlmCompletionTransport.cs:59` | REST の 0 | `dto.InputTokens`（0 のまま） | 同上 |
| `Stage0DecisionRecorder.cs:482`・`:499`・`:508` | 記録のトークン数の合計 | `Sum(u => u.InputTokens)` | `Sum(u => u.InputTokens ?? 0)`（記録の数。段の判定ではない） |
| 試験の `Resolve(…, inputTokens: 0)`（`LlmPriceTableTests` の 1 段の行） | 試験 | int の 0 | 変えない（第 2 段を持たない行・未知モデルでは 0 と null は同じ結果） |

**除外**: 出力トークン数の `?? 0`（`dto.OutputTokens ?? 0`）は段の判定に使わないので変えない。
`deploy/helm/.../README.md` の単価の節は追随した（規則 10。「計上時の入力トークン数で段を引き分ける」の直後に取れないときの扱いを足した）。
`values-local.yaml` のコメントは段の構成値の説明だけで、取れないときの扱いを書いていないため対象外。

## 変異で試験の効きを確かめた

| 変異 | 赤になった試験 |
| --- | --- |
| `Resolve` で null を第 1 段へ（`inputTokens is { } known && known > …`） | `LlmPriceTableTests` 2 件・`LlmTransportUnsentParityTests.gRPC_で入力トークン数が_0_の応答は第2段の単価で引かれる` |
| 閾値を `>=` | `取れている要求は従来どおり閾値で引き分ける(100000)`・既存の境界試験 |
| `LlmReportedInputTokens.From` で 0 を残す | `入力トークン数の_0_は両輸送とも取れないと読む(0/-1)`・`gRPC_で入力トークン数が_0_の応答は第2段の単価で引かれる` |
| `HttpLlmCompletionClient` の計測へ `?? 0` を戻す | `HttpLlmCompletionClientTests` 2 件 |
| 取引判断の `PublishingLlmUsageReporter` で `Resolve(…, ?? 0)` | `取れない要求は第2段で計上し取れた要求は閾値どおりに計上する(null)` |
| Stage 0 の `CostOf` で `Resolve(…, ?? 0)` | `実費は入力トークン数が取れない計測を第2段で引く` |
| 報告書の `PublishingLlmUsageReporter` で `Resolve(…, ?? 0)` | `入力トークン数が取れない要求は第2段で計上する(null)` |
| `HttpReportNarrativeDrafter` の計測へ `?? 0` を戻す | `入力トークン数が取れない応答は取れないまま計上へ渡す` 2 件 |
| `LlmReportPolicyReviser` の計測へ `?? 0` を戻す | `入力トークン数が取れない応答は取れないまま計上へ渡す` |

## 残り（本作業の外）

- キャッシュを使い始めたら、判定量にキャッシュ読み・書きを含める（ADR-0064 実測 2）。本件は先取りしない。
- 基盤が `input_tokens` を `optional` にした場合は、`LlmReportedInputTokens.From` の 0 の読み方を見直す（0 を送った要求が無い前提は変わらないので、急ぎではない）。
