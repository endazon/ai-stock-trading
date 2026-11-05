---
title: 取引判断のプロンプトの固定の接頭辞（前文と当日の日報方針）をゲートウェイの cacheablePrefix で分けて渡し、費用の計上をキャッシュの書き・読みの欄で直す
type: spec
status: accepted
related_ids: [FR-04, NFR, NFR-13, ADR-0064, IADR-0607, IADR-0122, IADR-0524, IADR-0550, IADR-0553, IADR-0212, IADR-0318]
author: claude (Claude Code)
created: 2026-11-05
updated: 2026-11-05
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§6.1 月次 LLM 費用上限・163.71 円/USD)
  - planning:projects/ai-stock-trading/07_adr/ADR-0064_llm-assignment-to-5-5-family-and-stage0-rerun.md (決定4 入力トークン数が取れないときの段・決定9 未知モデルの計上単価)
---

# 仕様書: 取引判断のプロンプトキャッシュの接頭辞と、キャッシュの欄による費用の計上（#1523・MSP#2396 の AST 側）

## 起点

- #1523。MSP#2396（MSP develop `b05b19b9`・MSP の IADR-0671）で、LLM ゲートウェイがプロンプトキャッシュに対応した。
- ゲートウェイの新しい契約（いずれも末尾追加。既定は従前どおり）:
  - 要求 `CacheablePrefix`（REST `cacheablePrefix`・proto `CompleteRequest.cacheable_prefix = 6`）。モデルが読む全文は `cacheablePrefix + prompt`（区切りを足さない）。
  - 応答 `CacheCreationInputTokens` / `CacheReadInputTokens`（proto `CompleteResponse` 11・12、`CompletionStreamEvent` 12・13）。`InputTokens` はプロンプト全体のまま（書き・読みを含む）。
- **AST が分けて渡さない限り、ゲートウェイの挙動も費用も変わらない。**

## 決定（IADR-0607）

1. **接頭辞は「前文 ＋ 当日の日報方針の節」だけ**にする（本判断・一次スクリーニングとも）。監視銘柄の節から先は残りに置く。
   - 監視銘柄の節には判断対象の所属の行（銘柄ごとに変わる）がある。一覧そのものも市場監視の読み取りのたびに変わり得る（並び・件数）。
   - 時刻・現在値・保有・銘柄は、いずれも方針の節より後にある。
2. **全文は変えない。** 接頭辞の文字列はプロンプトを組む関数（`Build` / `BuildScreening`）と同じ関数から作る（`DecisionCacheablePrefix` / `ScreeningCacheablePrefix`）。送る直前に `全文.StartsWith(接頭辞, Ordinal)` を確かめる。合わなければ接頭辞を付けずに全文を送る（`LlmPrompt.Split`。従前と同じ形）。
3. **model・effort・max_tokens は変えない。** 呼び出しの引数は従前のまま。足すのは接頭辞だけ。
4. **呼び手のポートは既定の実装つきの多重定義で足す**（`ILlmCompletionClient.CompleteAsync(LlmPrompt, …)`）。既定の実装は全文を従前の多重定義へ渡す。ゲートウェイへ送る実装（`HttpLlmCompletionClient`）と、Stage 0 の記録の中継（`Stage0DecisionRecorder` の内側のクライアント）だけが接頭辞を運ぶ。試験の偽物は全文を受け取り続ける（既存の試験の期待は変わらない）。
5. **費用 = 残り × 入力単価 ＋ 書き × 書き単価 ＋ 読み × 読み単価 ＋ 出力 × 出力単価**（残り = `InputTokens − 書き − 読み`。負は 0）。
   - Haiku 5.5 の段は、従前どおり `InputTokens`（プロンプト全体）で選ぶ。
   - 単価は MSP の `LlmGateway/appsettings.json` の単価表（USD/百万トークン）に合わせ、AST の単位（円/1k トークン・163.71 円/USD・既存の行と同じ丸め）で `values-local.yaml` の 2 サービスへ足す:

     | モデル | 書き（USD/M → 円/1k） | 読み（USD/M → 円/1k） |
     | --- | --- | --- |
     | claude-opus-5-5 | 5.0 → 0.819 | 0.20 → 0.0327 |
     | claude-sonnet-5-5 | 2.50 → 0.409 | 0.10 → 0.0164 |
     | claude-haiku-5-5（≦100K） | 0.125 → 0.0205 | 0.01 → 0.00164 |
     | claude-haiku-5-5（>100K） | 0.625 → 0.102 | 0.05 → 0.00819 |

6. **書き・読みの単価が無い行（未設定・解析できない）と未知モデルでは、書き＝入力単価 × 1.25、読み＝入力単価で数える。** 書きは提供元の割増の比、読みは従前の計上（全量を入力単価）と同じであり、過小にはならない側である。

## 母集合（規則 9）

- **ゲートウェイを呼ぶ取引判断の呼び出し**: `git grep -n "CompleteAsync(" -- backend/Services/TradeDecisionService ':!*/Tests/*'`
  - `DecisionOrchestrator`（一次・二次の 2 か所）→ 接頭辞を渡す。
  - `Stage0DecisionRecorder` の内側のクライアント（中継）→ 接頭辞を運ぶ。
  - `HttpLlmCompletionClient`（送る側）→ 接頭辞を要求へ載せる。
  - `PlaceholderProviders`（未構成の既定）→ 既定の実装（全文）でよい。
- **プロンプトを組む呼び出し**: `git grep -n "PromptBuilder.Build(\|BuildScreening(" -- backend/Services/TradeDecisionService ':!*/Tests/*'`
  - `TradeDecisionAppService`（本番）と `Stage0DecisionRecorder`（Stage 0 の記録）の 2 か所。どちらも `DecisionOrchestrator.DecideAsync` へ接頭辞を渡す。
- **輸送**: `RestLlmCompletionTransport` / `GrpcLlmCompletionTransport` の 2 つ（`ILlmCompletionTransport` の実装はこの 2 つだけ）。
- **費用の計上**: `git grep -n "LlmPricing.Compute(" -- backend ':!*Tests*'`
  - `TradeDecisionService/PublishingLlmUsageReporter` と `Stage0RecordingUsageCollector.CostOf` → キャッシュの欄で数える。
  - `ReportService/PublishingLlmUsageReporter` は**除外**。報告書は接頭辞を渡さないため、ゲートウェイの書き・読みは 0 である（MSP の IADR-0671）。式に 0 を渡すのと金額は同じになる。報告書が接頭辞を使うときに同じ式へ移す。
- **単価の構成**: `git grep -n "new LlmPriceRow(" -- backend ':!*Tests*'`（TradeDecisionService・ReportService の `Program.cs`）、`deploy/helm/ai-stock-trading/values-local.yaml` の単価の行（2 サービス）。
- **単価の文書**: `git grep -ln "InputPer1kTokens" -- docs deploy ':!*.cs'` → `docs/operations/operations.md`（単価の見直し契機の表へ 1 行）・`deploy/helm/ai-stock-trading/README.md`（単価の節へ 1 段落）。検査は `.github/workflows/helm.yml` の values-local の単価の行の固定（両 Deployment）へ 8 行を足す。
- **proto の写し**: `backend/Shared/AiStockTrading.Shared.Infrastructure/Protos/platform/llmgateway/v1/completion.proto` の 1 つ。baseline は `scripts/proto-contract-baseline.json`。
- **submodule**: 無い（`.gitmodules` が無い）。取り込みは要らない。

## 受け入れ基準

- [x] 接頭辞＋残りが、従前のプロンプトと 1 文字も違わない（本判断・一次。xUnit）。
- [x] 同じ日の 2 回の呼び出しで、時刻・現在値・保有・銘柄・監視銘柄が違っても、接頭辞が 1 バイトも違わない（xUnit）。
- [x] 接頭辞が全文の先頭でないときは、接頭辞を付けずに全文を送る（xUnit）。
- [x] 一次・二次の要求に、それぞれの接頭辞が載る。model・purpose・max_tokens は従前のまま（xUnit）。
- [x] REST の要求に `cacheablePrefix` が載り、応答の `cacheCreationInputTokens` / `cacheReadInputTokens` が計上へ渡る（xUnit）。
- [x] gRPC の要求の `cacheable_prefix`（6）と応答の 11・12 が往復する。proto の写しは MSP の正本と同じ番号・名前（xUnit・`check-proto-contracts`）。
- [x] 書き・読みを含む応答の計上の金額が、上の単価どおりになる。Haiku の段は全体の入力トークン数で選ぶ（xUnit）。
- [x] 書き・読みの単価が無い行・未知モデルでは、書き＝入力 × 1.25・読み＝入力で数える（xUnit）。
- [x] `values-local.yaml` の 2 サービスに書き・読みの単価が載る（helm.yml の検査）。
- [x] `/verify` が通る。

## PoC での確かめ方（PR 本文にも書く）

- ゲートウェイの Grafana `llm_tokens_total{llm_token_type="cache_read"}` と `cache_write` を、用途（`trade-decision-screening` / `trade-decision`）別に 1 回の場で見る。**読みの率 ＝ cache_read ÷（入力の全体）**。
- 1 回あたりの費用: AST の `ast_llm_cost_jpy_total` の増分 ÷ 呼び出し回数を、用途別に配備の前後で比べる。
- 接頭辞が 512 トークン未満だとキャッシュされない（エラーにはならず、書きも読みも 0 のまま）。読みが 0 のままなら、当日の方針の長さを確かめる。
