---
title: 方針の改訂の出力上限を思考の分を見込んで 16384 へ上げ、出力上限での打ち切りを利用者への応答で区別する（#1383）
type: spec
status: accepted
related_ids: [FR-14, FR-07, NFR, ADR-0064, ADR-0042, IADR-0555, IADR-0522, IADR-0101, IADR-0104, IADR-0431, IADR-0432]
author: claude (Claude Code)
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-14・FR-07)
  - planning:projects/ai-stock-trading/07_adr/ADR-0064_llm-assignment-to-5-5-family-and-stage0-rerun.md (決定 5: effort は原則だけ。既定は提供元の既定、変えるなら実測を IADR へ。effort はゲートウェイの用途別設定)
  - planning:projects/microservices-platform/07_adr/ADR-0138_claude-models-5-5-allocation.md (決定 4: 同上。Sonnet 5.5 の既定 effort は high・thinking は無効化できない)
---

# 方針の改訂の出力上限を思考の分を見込んで 16384 へ上げ、出力上限での打ち切りを利用者への応答で区別する（#1383）

## 起点

- [#1383](https://github.com/endazon/ai-stock-trading/issues/1383)。PoC 2026-10-17 18:11 JST（AST rev 49 `31b1a087`）の `/policy`（指示 412 文字）が
  `in=5898 out=8192 amount=15.338950`・`stopReason=max_tokens`・本文 1,609 文字で失敗し、利用者には「AI の出力が JSON ではありませんでした」と返った。
  直前（18:09）の改訂は `out=5,697` で通った。
- 前提: IADR-0522（4096 → 8192。実測 3,805 / 3,378 は 4.5 / 5 系）。5.5 系は thinking を無効にできず（Sonnet 5.5 の `disabled` は 400）、Sonnet 5.5 の既定 effort は `high`。

## 現況（origin/develop cac866f6）

| # | 事実 | 場所 |
| --- | --- | --- |
| 1 | 方針の改訂は `MaxTokens = 8192` を要求へ載せる。**用途キー（purpose）は報告書の散文と同じ report-daily / weekly / monthly**（種別から）。計上区分だけ `policy-revision` | `ReportService/Infrastructure/ExternalServices/LlmReportPolicyReviser.cs` |
| 2 | 形式違反は `stopReason` に関係なく `InvalidOutput` で、利用者への理由はパーサの理由（「AI の出力が JSON ではありませんでした」等）。上限到達の警告ログは別に出る（IADR-0522 決定 2） | 同上 |
| 3 | effort は基盤のゲートウェイの `Llm:PurposeEffort`（purpose → effort）で決まる。既定は `rerank=low` だけ。AST は effort・thinking を送らない | MSP `LlmGateway/Infrastructure/ExternalServices/ClaudeEffort.cs`・`appsettings.json`（MSP:IADR-0531） |
| 4 | 方針の改訂の上限時間は既定 95 秒、通知サービスの外側は 120 秒。ゲートウェイの上流の待ちは `Llm:AnthropicTimeoutSeconds`（既定 100 秒。MSP#1872 で構成化済み・既定は変えていない） | `ReportService/Program.cs`・IADR-0522 追記 1 |
| 5 | 1 日の改訂の回数は既定 10 回（失敗も 1 回に数える） | `PolicyRevisionLimit.DefaultDailyLimit` |
| 6 | 報告書の散文（`HttpReportNarrativeDrafter`）は `MaxTokens: 4096`。打ち切られた本文は捨てずに採り（IADR-0104 決定 5）、空ならプレースホルダ散文へ倒す。JSON は解析しない。上限到達の警告は `stopReason` だけを運ぶ | `HttpReportNarrativeDrafter.cs` |
| 7 | 単価（values-local）: sonnet-5-5 入力 0.327・出力 1.637 円/1k、opus-5-5 入力 0.655・出力 3.274 円/1k。失敗の 1 回 ¥15.34 ＝ 5898×0.327＋8192×1.637（円/1k）と一致 | `deploy/helm/ai-stock-trading/values-local.yaml` |

## 判断（IADR-0555）

1. **`MaxTokens` を 8192 → 16384 に上げる。effort は下げない。** 理由は IADR-0555（effort は purpose 単位で報告書の散文と共有・基盤側の設定・計画は既定から変えるなら実測を求める）。
2. **`stopReason=max_tokens` で案が形式違反になったら、失敗の種別を `Truncated` とし、利用者には「出力上限で途中で切れた。再試行で通ることがある。通らなければ指示を短く」と返す。** 閉じた JSON で通った場合は従来どおり案として返す。
3. **応答を受け取った時点で所要時間と出力トークンを情報ログへ 1 行出す**（PoC で出力の速さ＝上限時間の内に出せる出力の天井を測るため）。
4. **報告書の散文は上限 4096 と扱い（本文を捨てない・空ならプレースホルダ）を変えない。** 上限到達の警告に `maxTokens`・`outputTokens`・`model` を足し、5.5 系での実測を取れるようにする。理由は IADR-0555 決定 4。

## 母集合（規則 9・10）

- 走査語: `8192`・`8,192`・`MaxTokens`・`InvalidOutput`・`50〜80 トークン`・`4,700〜7,600`（`git grep` で全文書・コード）。
- 追随する: `LlmReportPolicyReviser.cs`・`IReportPolicyReviser.cs`・`HttpReportNarrativeDrafter.cs`・`LlmReportPolicyReviserTests.cs`・`HttpReportNarrativeDrafterTests.cs`・
  `docs/tests/FR-10_risk-controls-tests.md`（§LLM の出力上限）・`docs/observability/observability.md`（§ログ・トレース）・IADR 索引・IADR-0522（日付つき追記）。
- 除外: `.ai-context/specs/` の過去の仕様書（凍結記録）。IADR-0522 の本文（凍結。追記で指す）。`CHANGELOG.md`（生成物）。

## 受け入れ基準

| ID | 基準 | 試験 |
| --- | --- | --- |
| T-10-2717 | 方針の改訂の要求の上限は 16384、報告書の散文は 4096 | `LlmReportPolicyReviserTests`（T-10-2503 を改める） |
| T-10-2718 | `max_tokens` で JSON が壊れた（途中で切れた・空）なら `Truncated` で、理由は出力上限・再試行・指示を短くの 3 点を含み「JSON ではありませんでした」を含まない。`end_turn` の形式違反は `InvalidOutput` のまま。閉じた JSON は `max_tokens` でも案になる | `LlmReportPolicyReviserTests` |
| T-10-2719 | 改訂のサービスは `Truncated` の理由をそのまま利用者への応答に載せる（「AI の案を作れませんでした（…）。方針は変わっていません。」） | `ReportPolicyRevisionServiceTests` |
| T-10-2720 | 応答を受け取ったら所要時間（ms）と出力トークンを運ぶ情報ログが 1 行出る | `LlmReportPolicyReviserTests` |
| T-10-2721 | 報告書の散文の上限到達の警告は `maxTokens=4096`・`outputTokens` を運び、本文は捨てない | `HttpReportNarrativeDrafterTests` |

変異で確かめる: 上限を 8192 に戻す（T-10-2717 赤）／`Truncated` の分岐を外す（T-10-2718 赤）／`end_turn` でも `Truncated` にする（T-10-2718 赤）。

## PoC で取る計測（実 API はこの作業では呼べない）

IADR-0555 §PoC での計測 に書く。要旨: `/policy` を同じ規模の指示で 10 回以上、`out`・`stopReason`・所要時間・成否を集め、思考の分（`out` − 本文の推定トークン）と出力の速さを出す。

## 検証

- `dotnet build` / `dotnet test`（ReportService.Tests）・`dotnet format --verify-no-changes`・文書系の検査（`check-trace-blocks`・`gen-knowledge-graph --check`・`check-cross-repo-refs`・`check-plan-id-qualification`・`check-reading-budget`）。helm は触らない。
