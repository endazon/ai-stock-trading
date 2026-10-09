---
title: 方針の改訂の出力上限を 8192 へ上げ、出力上限到達を警告ログで観測できるようにする（#243）
type: spec
status: accepted
related_ids: [FR-14, FR-07, FR-04, FR-06, NFR, ADR-0011, ADR-0042, IADR-0522, IADR-0101, IADR-0104, IADR-0431, IADR-0432]
author: claude (Claude Code)
created: 2026-10-10
updated: 2026-10-10
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-14・FR-07)
  - planning:projects/ai-stock-trading/07_adr/ADR-0042_discord-apply-ai-watchlist-proposal-and-revision-limit.md (決定 3: `/policy` の 1 日の回数上限)
---

# 方針の改訂の出力上限を 8192 へ上げ、出力上限到達を警告ログで観測できるようにする（#243）

## 起点

- [#243](https://github.com/endazon/ai-stock-trading/issues/243)（IADR-0101 のフォローアップ 1・3：Opus 5 化後の出力トークン実測と `MaxTokens` 4096 の再調整）。
- PoC の実測（#243 のコメント 2026-10-03・2026-10-09。値は LLM ゲートウェイが返したトークン数を費用計上ログから集計したもの）:

| 用途 | モデル | 出力の最大 | 4096 に対する割合 |
| --- | --- | --- | --- |
| trade-decision-screening | claude-haiku-4-5 | 792（9/30〜10/2）／739（10/6〜10/9） | 約 19% |
| trade-decision | claude-sonnet-5 | 1,017（9/30〜10/2）／930（10/6〜10/9） | 約 25% |
| report-daily | claude-sonnet-5 | 823（9/30〜10/2）／1,001（10/8） | 約 24% |
| report-weekly / report-monthly | claude-opus-5 | 931 / 1,048 | 約 26% |
| **policy-revision** | claude-sonnet-5 | **3,805**（9/30） | **約 93%** |
| **policy-revision** | claude-opus-5 → claude-sonnet-5 | **3,378** → 3,330（10/8。同じ改訂で 2 回呼ばれた） | 約 82% |

- 費用の見積り: 約 3,200 円/月（月の上限 15,000 円の約 21%）。
- 10/8 の 2 回目の呼び出し（sonnet 5）がゲートウェイのフォールバックか再試行かは未確認。打ち切り（`stop_reason=max_tokens`）の有無はログに出ないため未確認。

（上の数は #243 のコメントの表を本作業で読み直して写した。トークンの元ログは本リポジトリの外にあり、再集計はしていない。）

## 現況（origin/develop c7cbda1a）

| # | 事実 | 場所 |
| --- | --- | --- |
| 1 | 方針の改訂の呼び出しは `private const int MaxTokens = 4096` を要求へ載せる。用途キーは報告書と同じ（report-daily 等）、計上区分だけ `policy-revision` | `ReportService/Infrastructure/ExternalServices/LlmReportPolicyReviser.cs` |
| 2 | 報告書の散文（`HttpReportNarrativeDrafter`）と判断（`HttpLlmCompletionClient`。一次スクリーニングと本判断の両方）は呼び出しに `MaxTokens: 4096` を直書きする | 同上・`TradeDecisionService/Infrastructure/ExternalServices/HttpLlmCompletionClient.cs` |
| 3 | 輸送の応答 `LlmCompletionPayload.StopReason` は REST（`CompletionApiResponse.StopReason`）・gRPC（`CompleteResponse.stop_reason`）の両方から写る | `Shared.Contracts/Llm/RestLlmCompletionTransport.cs`・`Shared.Infrastructure/Composable/Llm/GrpcLlmCompletionTransport.cs` |
| 4 | 基盤のゲートウェイ（MSP develop 0172d9b7）は REST・gRPC とも `stop_reason` を返す（Claude は `stop_reason`、OpenAI 互換は `finish_reason=length` を `max_tokens` へ正規化）。`max_tokens` の上限で切り詰める処理は無い（要求の値をそのまま渡す） | MSP `Platform.Shared.Contracts/Dtos/CompletionDto.cs`・`completion.proto`・`LlmGateway/Infrastructure/ExternalServices/*Provider.cs` |
| 5 | 判断と報告書の散文は `stopReason=max_tokens` を既に警告ログへ出す（IADR-0104 決定 5） | `HttpLlmCompletionClient.cs`・`HttpReportNarrativeDrafter.cs` |
| 6 | **方針の改訂だけは上限到達を専用の警告に出さない。** 案の形式違反の警告に `stopReason` が載るだけで、閉じた JSON の直後で切れて案として通った場合は何も残らない（全量ログ `LogPrompts` が有効なときだけ情報ログに出る） | `LlmReportPolicyReviser.cs` |
| 7 | 方針の改訂の上限時間は `Reports:PolicyRevision:TimeoutSeconds`（既定 60 秒）。回数は 1 日 10 回（既定。IADR-0432）。費用は月次 LLM 上限の対象外 | `Program.cs`・IADR-0432 |

## 判断（IADR-0522）

1. **方針の改訂だけ `MaxTokens` を 4096 → 8192 にする。** 判断・一次スクリーニング・報告書の散文は 4096 のまま（実測の最大は 1,017。4096 の 25% 前後で余裕は十分）。これを #243 の「4096 の再調整」の結論とする。
2. **`stopReason=max_tokens` を、方針の改訂でも案の成否と独立に警告ログへ出す。** ゲートウェイは `stop_reason` を返している（現況 4）ため、新しい契約は要らない。判断・報告書の散文は既に警告を出す（現況 5）ため変更しない。
3. 用途別の上限の構成化（`LlmGateway:MaxTokens` 等）はしない（IADR-0101 選択肢 2 と同じ理由。値を変える要件は方針の改訂の 1 件だけ）。

## やること

1. `LlmReportPolicyReviser.MaxTokens` を 8192 へ。注記に実測と IADR-0522 を残す。
2. 同クラスで、拒否の判定の後・案の検証の前に `IsMaxTokens(StopReason)` なら警告ログ（`stopReason`・`maxTokens`・`outputTokens`・`model`）を出す。
3. 試験（下記）・テスト仕様書（`docs/tests/FR-10_risk-controls-tests.md`）・可観測性仕様書（`docs/observability/observability.md` §ログ・トレース）・IADR 索引。

## 受け入れ基準

- [x] 方針の改訂の要求の `MaxTokens` は 8192、報告書の散文の要求は 4096（T-10-2503）。
- [x] 判断の要求の `maxTokens` は一次スクリーニング・本判断とも 4096（T-10-2505）。
- [x] 方針の改訂で `stopReason=max_tokens` なら、案として通った場合も形式違反で捨てた場合も、上限到達の警告が 1 行出て `max_tokens`・上限・出力トークンを運ぶ。`end_turn`・`null` では出ない（T-10-2504）。
- [x] 既存の方針の改訂・報告書・判断の試験は変更なしで緑。
- [x] `dotnet build`（警告 0）・影響サービスの `dotnet test`・`dotnet format --verify-no-changes`・repo の node 検査が通る。

## 試験の採番

`docs/tests/` と backend・scripts・`.ai-context` の全文で `T-10-<N>` の最大を引いた（`grep -rhoE "T-10-[0-9]+"`）。最大は T-10-2502（#1286）。
開いている PR は CHANGELOG の自動更新 1 本だけで、T-10 の帯を使う作業は無い。本件は **T-10-2503〜T-10-2505** を使う。

## 母集合（規則 9・10）

誤りの側の文字列で走査した: `git grep -n "4096\|4,096" -- . ':!.ai-context/specs' ':!CHANGELOG.md'`。

| 箇所 | 扱い |
| --- | --- |
| `LlmReportPolicyReviser.cs` の `MaxTokens` と注記 | **是正**（8192・実測と IADR-0522） |
| `HttpReportNarrativeDrafter.cs`・`HttpLlmCompletionClient.cs` の `MaxTokens: 4096` | **据え置き**（判断 1。試験 T-10-2503・T-10-2505 で固定） |
| `.ai-context/adr/IADR-0432` の費用見積り表（「思考トークン込みの上限 4,096」） | **除外**（凍結記録。本文を書き換えない。改訂後の上限は IADR-0522 が持つ） |
| `.ai-context/adr/IADR-0491` の費用見積り表（報告書の作り直し＝散文の上限 4,096） | **除外**（散文の上限は 4096 のままで誤りにならない） |
| `.ai-context/adr/IADR-0101`・`IADR-0104` と索引の行 | **除外**（その時点の決定の記録。後継の決定は IADR-0522 が引く） |
| `Shared.*` の輸送の試験・proto の注記（`max_tokens=0 → 4096`） | **除外**（基盤の既定値の記述・輸送の試験の入力値であり、用途の上限ではない） |
| `docs/` | 該当なし（`4096` を書く文書は無い）。可観測性仕様書へ上限到達の警告の節を足す |

この変更で新たに誤りになる自分の記述（規則 10）: `LlmReportPolicyReviser.cs` の「上限到達は途中で切れた JSON になり得る——下の検証が捨てる」の注記は、警告を足した後も正しい（検証が捨てる挙動は変えない）。注記を残し、警告の理由を足した。

## 範囲外（#243 に残す）

- 本番経路の LLM 単価の与え方（#243 の 2026-09-04 コメント。IADR-0114 決定 6）。
- 10/8 の 2 回目の呼び出しがフォールバックか再試行かの切り分け（ゲートウェイ側のログ。AST の改訂は再試行しないため、割当の逸脱なら `FallbackFired` の記録に残る）。
- 上限時間 60 秒の見直し（出力が伸びた場合は打ち切りではなくタイムアウト＝案なしになる。構成で上げられる）。
- 基盤の `CompletionApiRequest` 既定値（4096）との値合わせ（MSP#380）。本件は要求に明示するため既定値の影響を受けない。

## 検証

- `dotnet build backend/backend.slnx`（警告 0）・`dotnet format backend/backend.slnx --verify-no-changes`。
- `dotnet test` を ReportService・TradeDecisionService の試験プロジェクトへ。
- `node scripts/check-test-traceability.js`・`check-trace-blocks.js`・`check-adr-index-sync.js`・`gen-knowledge-graph.js --check`・`check-commit-messages.js`・`check-cross-repo-refs.js`。
- 変異: M1（`MaxTokens` を 4096 へ戻す）→ T-10-2503・T-10-2504（警告が運ぶ上限の値）が赤。M2（上限到達の警告を外す）→ T-10-2504 が赤。いずれも戻して緑。
