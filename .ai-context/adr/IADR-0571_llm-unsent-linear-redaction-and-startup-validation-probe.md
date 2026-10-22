---
title: IADR-0571 LLM の Sent=false の要約の伏せ字を線形時間の照合へ替え、起動検証の試験を破棄との競合から外す
type: impl-adr
status: Accepted
related_ids: [NFR, FR-04, FR-11, IADR-0419, IADR-0517]
author: claude (Claude Code)
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04, FR-11)
---

# IADR-0571: LLM の Sent=false の要約の伏せ字を線形時間の照合へ替え、起動検証の試験を破棄との競合から外す

- 状態: Accepted
- 日付: 2026-10-23
- 決定者: claude（起票 #1437。利用者レビューは PR で受ける）

## 起点・関連

- 対象 Issue: #1437（Integration E2E が断続的に赤）。
- 関連する実装仕様書: [20261023_1437_integration-e2e-flaky-regex-timeout-and-startup-race](../specs/20261023_1437_integration-e2e-flaky-regex-timeout-and-startup-race.md)
- 関連 IADR: [IADR-0419](IADR-0419_audit-free-text-linear-time-redaction.md)（監査の自由記述で同じ判断）、IADR-0517（Sent=false の原因の語彙と要約）。

## コンテキストと課題

- `LlmGatewayUnsent.Summarize` は 4 つの伏せ字の正規表現に 100 ms のマッチタイムアウトを持たせていた。壁時計で測るため、CI の並列試験下で
  数十文字の入力でも打ち切られ、`RegexMatchTimeoutException` が `From` を突き抜けた（2026-10-22 21:10Z）。本番でも混めば
  Sent=false の原因の記録・通知の経路が例外で落ちる。
- 起動時の検証（`ValidateOnStart`）で止まることを `WebApplicationFactory` の例外の型で断言する試験は、アプリ側スレッドがホストを破棄してから
  例外を返すため、試験側の問い合わせが遅れると `ObjectDisposedException` を受け取る（2026-10-22 14:35Z）。

## 検討した選択肢

1. 予算を伸ばす — 「混めば落ちる」が残る（IADR-0419 の実測で壁時計は最大 50 秒）。**却下**。
2. 打ち切りを捕まえて定型文へ置き換える — 記録が実行時の混み具合で変わる（IADR-0419 が退けた形）。**却下**。
3. **`RegexOptions.NonBacktracking` ＋ `Regex.InfiniteMatchTimeout`**（入力は先に 2,000 文字で切る）—— **採用**（決定 1）。
4. 試験で `ObjectDisposedException` も許す — 起動が止まった理由を確かめなくなる。**却下**。
5. 試験の再試行 — 非決定を残したまま隠す。**却下**。
6. **起動の検証の直前（`IHostLifetime.WaitForStartAsync`）で、本番と同じ `IStartupValidator` を走らせて結果を記録する** —— **採用**（決定 2）。

## 決定

1. `LlmGatewayUnsent` の伏せ字の照合は `NonBacktracking` で行い、`InfiniteMatchTimeout` を明示する（未指定だと
   `REGEX_DEFAULT_MATCH_TIMEOUT` で予算が戻る）。パターンは変えない（NonBacktracking が受け付ける構文だけ）。旧パターンとの差分試験と、
   型の全 `Regex` を見る試験で固定する。
2. 試験支援 `AiStockTrading.TestSupport.Composition.StartupValidationProbe` を置く。`IHostLifetime` を差し替え、ホストが検証へ進む直前に
   ホストのコンテナから `IStartupValidator` を引いて走らせ、例外を記録する。起動の例外は**有無だけ**を見る（型は競合で決まるため）。
   起動検証の失敗を断言する試験はこれを使う（現時点で OrderExecutionService の T-10-2675・MarketMonitorService の T-10-1721）。

## 理由

- 伏せ字の結果と成否が入力だけで決まる。NonBacktracking の線形性は .NET が保証する。
- 記録は試験側の待ちの経路と独立しており、スレッドの走る順に依らない。本番の組み立て（Program.cs）の検証をそのまま走らせるので、
  `ValidateOnStart` を外すと赤になる（変異で確認）。

## 結果・残余リスク

- `IHostLifetime` を差し替えるため、プローブを通す試験ではコンソールのシグナル処理が無い（試験には不要）。
- `Program.cs` のトップレベル（`builder.Build()` 前）で投げる配線試験は入口の例外がそのまま返り、本件の競合に当たらない。
  ホストの起動中（検証・常駐の `StartAsync`）で止まる形を新たに断言するときは、本プローブを使う。
