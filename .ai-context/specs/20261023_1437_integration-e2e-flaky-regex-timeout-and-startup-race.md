---
title: Integration E2E の断続的な赤（伏せ字の照合の壁時計打ち切り・起動検証の試験の競合）を直す
type: spec
status: done
related_ids: [NFR, FR-04, FR-11, FR-10, FR-02, IADR-0571, IADR-0419]
author: claude (Claude Code)
created: 2026-10-23
updated: 2026-10-23
---

# 仕様書: Integration E2E の断続的な赤を直す（#1437）

> 本仕様書は実装着手前に作成した（AST `origin/develop` `cacfa796`＝#1438 のマージ後）。

## 起点（トレーサビリティ）

- 起点: #1437（自動起票「[CI] Integration E2E が失敗している」）。試験の決定性＝ `NFR`。
  影響する製品の振る舞い: FR-04 / FR-11（LLM ゲートウェイの Sent=false の原因の要約）。
- IADR: [IADR-0571](../adr/IADR-0571_llm-unsent-linear-redaction-and-startup-validation-probe.md)。計画との矛盾: なし。

## 事実（ジョブログの実測）

Integration E2E の step「All tests including real containers」は slnx 全体の `dotnet test` であり、**どれか 1 つの試験プロジェクト**が
赤なら末尾の集計（IntegrationTests 等は `Test Run Successful`）に関わらず `Build FAILED` / exit 1 になる。末尾 400 行より前に赤があった。

| run | コミット | 赤の試験 | 例外 |
| --- | --- | --- | --- |
| 39796921604（14:35Z push） | 2b5f17fc | `OrderExecutionService.Tests.DecisionCloseUnfilledCancelPollingTests.T_10_2675_…(enabled: "true", minutes: "5", starts: False)` | 期待 `OptionsValidationException`、実際 `ObjectDisposedException`（`DeferredHost.StartAsync` → `IServiceProvider` 破棄済み） |
| 39855114961（21:10Z schedule） | cacfa796 | `AiStockTrading.Shared.Infrastructure.Tests.LlmTransportUnsentParityTests.REST_と_gRPC_は同じ入力から同じ記録を作る(c: upstream_error_429)` | `RegexMatchTimeoutException`（`LlmGatewayUnsent.Summarize`・100 ms 予算） |

- 2 件とも**別の試験の非決定**であり、#1436 の新しい試験（UnconfirmedEntryCancel* 等）・#1438 の blame 設定は赤に関与していない
  （blame は「All tests finished running, Sequence file will not be generated.」でハングではないため、ダンプが空なのは正しい）。
- それ以前の赤（21日〜22日 12:19Z）は `TradeExecutionPipelineE2ETests` で、9e034045 で解消済み（本件の対象外）。

## 原因

1. **伏せ字の照合の壁時計打ち切り**: `LlmGatewayUnsent` の 4 つの正規表現が 100 ms のマッチタイムアウトを持つ。壁時計で測るため、
   CPU・GC が混むと数十文字の入力でも打ち切られ、例外が `From`（Sent=false の原因の記録）を突き抜ける。**本番でも同じ条件で起きる**
   （IADR-0419 が監査の自由記述で同型を直した）。
2. **`WebApplicationFactory` の起動失敗時の競合**: アプリ側スレッドは `ValidateOnStart` の失敗で `RunAsync` の `finally` によりホストを
   破棄してから例外を返す。試験側スレッドが破棄後に `IHostApplicationLifetime` を引くと `ObjectDisposedException` になる。
   例外の型が走る順で決まる。同型: `MarketMonitorService.Tests.UndefinedMarketRejectionTests.T_10_1721_…起動時に止める`。

## 是正の母集合（規則 9）

- 壁時計予算の正規表現: `grep -rn 'TimeSpan.FromMilliseconds' --include=*.cs backend | grep -i regex` と `new Regex(` の走査で、
  予算を持つのは `LlmGatewayUnsent` の 4 つだけ（`Shared.Kernel/Trading/Policy*` は予算を持たない。`AuditFreeText` は是正済み）。
- 起動検証（`ValidateOnStart`）の失敗を `WebApplicationFactory` 経由で例外の型として断言する試験: `Throw<OptionsValidationException>` の
  走査で 2 件（上記）。`InvalidOperationException` を断言する他の配線試験は `builder.Build()` 前（Program.cs のトップレベル）で投げる
  ため入口の例外がそのまま返り、本件の競合に当たらない（除外理由）。

## 受け入れ基準

- A1: `LlmGatewayUnsent` の全 `Regex` が `NonBacktracking` かつ `InfiniteMatchTimeout`（型の全フィールドを見る試験）。
- A2: 伏せ字の結果は旧パターンと同一（代表例＋擬似乱数 20,000 件の差分試験）。
- A3: 起動検証の試験は、試験側スレッドの遅れに依らず `OptionsValidationException` とその文言を読み、起動が止まったことも確かめる。
  `ValidateOnStart` を消すと赤（変異）。
- A4: 試験の skip・無効化・隔離をしない。

## 検証（手元）

- 再現（旧形）: 負荷（CPU 12 本＋GC 圧）下で 44 文字の入力に旧パターン（100 ms）を 25,600 回 → 打ち切り 6 回。新形は 0 回。
- 再現（競合）: 試験側の `CreateHost` に 1.5 秒の遅れを入れた一時ファクトリで、旧形の断言は 3/3 で `ObjectDisposedException`、
  新形（`StartupValidationProbe`）は 3/3 で `OptionsValidationException` を読む。遅れ無し・負荷下 30 回では旧形も 30/30 合格（稀な競合）。
- 変異: Program.cs の `ValidateOnStart()` を外すと T_10_2675（starts: False）が赤。
- `dotnet test`: Shared.Contracts.Tests（対象 30 件）・Shared.Infrastructure.Tests 490・OrderExecutionService.Tests 1947・
  MarketMonitorService.Tests 403・TradeDecisionService.Tests（LlmGatewayUnsent 45 件）すべて合格。`dotnet format --verify-no-changes` 合格。
