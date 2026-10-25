---
title: 仕様書 — in-process のホストで停止が 2 回並んで呼ばれ、Wolverine の排出の途中でコンテナが破棄されて Integration E2E の後片付けが永久に止まるのを、停止を 1 本の作業へ束ねて直す（MSP#2265）
type: spec
status: accepted
related_ids: [NFR, IADR-0129, IADR-0591]
author: claude (Claude Code)
created: 2026-10-26
updated: 2026-10-26
---

# 仕様書: Wolverine の停止の二重呼び出しによる後片付けの停止（MSP#2265）

> 本仕様書は実装の前に作成した。起点は AST `origin/develop` `316bf9f9`（作業ブランチ `claude/eloquent-gates-ss7l2b` へ develop を取り込んだ `36b9b504`）。判断は [IADR-0591](../adr/IADR-0591_wolverine-single-stop-gate.md) に残す。
> 🔴 稼働中の PoC・k3s には触れない。手元の再現は、このセッション限りの dockerd と Testcontainers の使い捨てのコンテナだけで行った。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR・無採番。CI の信頼性）。
- 関連する実装ADR: IADR-0129（Wolverine への移行・共通配線）。
- 起票: MSP#2265（MSP の develop の Integration が赤。AST の `TradeExecutionPipelineE2ETests` が 5 分止まり、blame-hang がテストホストを中断した）。

## 観測（実測）

- MSP の定時実行 run 40229948691（MSP `150a9e3f`・submodule の AST `b427f998`）で、`TradeExecutionPipelineE2ETests.取引判断が承認され発注執行まで複数サービスを跨いで流れる` の実行中に、出力が 5 分止まった。同じコミットの push の実行（40229536861）は緑で、間欠的である。
- 手元で同じクラスを走らせ、1 回目に止まった（10 分の上限で打ち切った後も、テストホストは生き残った）。そのテストホストの heap dump を `dotnet-dump` の `dumpasync` で読んだ。待っている非同期の連鎖は 1 本だけだった（下から上へ）:
  `SemaphoreSlim.WaitAsync` ← `AutorecoveringConnection.DeleteRecordedChannelAsync` ← `AutorecoveringChannel.CloseAsync` ← `RabbitMqChannelAgent.teardownChannel` ← `RabbitMqListener.DisposeAsync` ← `ListeningAgent.StopAndDrainAsync` ← `EndpointCollection.DrainAsync` ← `WolverineRuntime.StopAsync` ← `Host.StopAsync` ← `WebApplicationFactory<Program>.DisposeAsync` ← `E2EInfrastructure.DisposeQuietlyAsync` ← 試験の `DisposeAsync`。
  - **試験の本体ではなく、後片付け（ホストの停止）で止まっている。**
- 待たれているセマフォは、その接続の 2 つのセマフォ（`_recordedEntitiesSemaphore`・`_channelsSemaphore`）である。生のメモリを読むと、どちらも**破棄済み**（`m_lockObjAndDisposed` が true）で、待ち行列は空、数は 0 だった。
  - 待ち手は、破棄で待ち行列から外された。そのため、もう誰にも起こされない。GC の根からも辿れない（`gcroot` 0 件）。
  - **排出の途中で、接続が破棄された**ことになる。
- 誰が破棄したかを、一時的な観測用の試験（コミットしない）で確かめた。発注執行のホストに、停止の回数と呼び出し元、コンテナの破棄の呼び出し元を記録する口を差し込んだ。
  - `IHostedService.StopAsync` は **2 回、並んで**呼ばれる。
    - 1 回目は `WebApplicationFactory.DisposeAsync` から `DeferredHost.StopAsync` を経る。
    - 2 回目は、エントリポイント（`Program`）の `app.RunAsync()` の中の `WaitForShutdownAsync` からで、`ApplicationStopping` を受けて呼ばれる。
  - **コンテナの破棄は `RunAsync` の `finally`（`Host.DisposeAsync`）が行う。** `WebApplicationFactory` ではない。
- Wolverine 6.24.5 の `WolverineRuntime.StopAsync` は、冒頭で `_hasStopped = true` にしてから排出（`DrainAsync`）に入る。2 回目の呼び出しは、この旗を見て**すぐに戻る**。
- よって、窓は次の順で開く。
  1. 1 回目の `Host.StopAsync` が、Wolverine の排出に入る。
  2. 2 回目の `Host.StopAsync` が、Wolverine を素通りして先に終わる。
  3. `RunAsync` が `Host.DisposeAsync` でコンテナを破棄する。
  4. `WolverineRuntime.DisposeAsync` が `_hasStopped` を見て停止を飛ばし、トランスポートを破棄する。これで RabbitMQ の接続が破棄される。
  5. 1 回目の排出の中で、チャネルを閉じていたリスナが、破棄されたセマフォを待ったまま止まる。
  - 排出が先に終われば、何も起きない（緑の回）。間欠的になるのはこのためである。

## 根の所在と直す場所

- 停止が 2 回呼ばれるのは、in-process のホスト（`WebApplicationFactory`）と `app.RunAsync()` の組み合わせの仕様である。
  - 本番（1 プロセス 1 サービス。SIGTERM で `RunAsync` だけが止める）では、停止の呼び出しは 1 回で、窓は開かない。
- 壊れているのは「**2 回目の停止の呼び出しが、1 回目の排出の完了を待たずに終わる**」ことである。
  - Wolverine の 2 回目の `StopAsync` は旗だけを見て戻る。そのため、`RunAsync` は排出の途中でコンテナを破棄できてしまう。
- 直す場所は共通配線（`UseAiStockTradingRabbitMq`）である。全サービスの `Program` がこれを通る。
  - Wolverine の停止を 1 本の作業に束ねる hosted service を、Wolverine の runtime より**後に**登録する（停止は登録の逆順なので、Wolverine より**先に**呼ばれる）。
  - **どの呼び出し元も、同じ 1 本の排出の完了を待つ**ようにする（IADR-0591 決定 1）。
- 後片付けの待ちに上限を付ける。これは補助であり、根の修正ではない（IADR-0591 決定 2）。
  - `E2EInfrastructure.DisposeQuietlyAsync` に、ホストの破棄の上限（既定 2 分）を置く。超えたら「どのホストの破棄が終わらないか」を名指しした `TimeoutException` で**試験を赤にする**。
  - 黙って続けることはしない。blame-hang の 5 分の無言の中断より、速く、原因を名指して落とす。

## 窓（規則 11）

時間差の窓は「1 回目の停止の呼び出しが Wolverine の排出を始めてから終えるまで」と「2 回目の停止の呼び出しが終わってから、`RunAsync` がコンテナを破棄するまで」が重なる区間である。形を確定する前に、両側のプローブで 3 つの形を比べた。

プローブ（どれも、停止の本体〔Wolverine の `StopAsync`〕を、完了を外から制御できる偽物にして測る）:

- **P1（増える側）**: 停止の呼び出しが 2 本、並んで来る（in-process のホスト）。期待は、**どちらも本体の完了より前に終わらない**こと。本体は 1 回だけ走る。
- **P2（減る側）**: 停止の呼び出しが 1 本だけ来る（本番）。期待は、本体の完了で終わること（待ち過ぎない）。本体は 1 回走る。
- **P3（遅れて来る側）**: 停止が終わった後に、さらに停止が呼ばれる。期待は、すぐに終わること。本体は二度走らない。
- **P4（失敗の側）**: 本体が例外で終わる。期待は、どの呼び出し元にも同じ例外が届くこと（握りつぶさず、止まらない）。

| 形 | P1 | P2 | P3 | P4 |
| --- | --- | --- | --- | --- |
| 後の端だけ（`RunAsync` が `ApplicationStopped` を待ってから破棄する） | ×（`ApplicationStopped` は先に終わった 2 回目の停止で立つ） | 〇 | 〇 | 〇 |
| 前の端だけ（最初の呼び出しだけが本体を走らせ、他は旗を見て戻る＝Wolverine の現状） | ×（2 回目が本体の完了を待たない。今回の事故） | 〇 | 〇 | ×（2 回目には例外が届かない） |
| **両端（全ての呼び出し元が 1 本の本体の作業を共有して待つ）** | 〇 | 〇 | 〇 | 〇 |

→ **両端の形**（IADR-0591 決定 1）を採る。試験は P1〜P4 を 1 対 1 で写し、試験名に P 番号を入れる。

## 母集合（規則 9: 根の語で全文を走査した）

- `git grep -n "UseWolverine" -- backend` で、Wolverine を起こす `Program` を引いた。
  - 11 サービスすべてが `opts.UseAiStockTradingRabbitMq(...)` を通る（`git grep -l "UseAiStockTradingRabbitMq" -- "backend/Services/*/Program.cs"` も 11 件）。
  - `OpendAuthGateway` は Wolverine を使わない（`app.Run()`。対象外）。
  - 共通配線の 1 か所に置けば、全サービスに効く。
- `git grep -n "RunAiStockTradingAsync\|app.RunAsync\|app.Run()" -- backend` で、`RunAsync` の終端を引いた。11 サービスが `RunAiStockTradingAsync`（中は `app.RunAsync()`）である。
- `git grep -lE "WebApplicationFactory" -- backend/Tests/AiStockTrading.IntegrationTests` と `RabbitMq` の両方を含む fixture は、#1128 と同じ 4 本である。
  - `TradeExecutionPipelineE2ETests`・`OrderExecutionPipelineE2ETests`・`KeycloakOwnerOnlyEndpointE2ETests`・`ServiceTokenSyncQueryE2ETests`。
  - どれも `E2EInfrastructure.DisposeQuietlyAsync` で破棄するので、上限は 1 か所に置けば効く。
  - 各サービスの単体試験のファクトリは `DisableAllExternalWolverineTransports()` を呼ぶ。RabbitMQ の接続が無く、窓は開かない（対象外。ただし共通配線の停止の束ねは同じく通る）。
- 停止を自前で 2 回呼ぶ箇所は無い（`git grep -n "StopAsync(" -- backend` で、試験の後片付け以外に Wolverine の停止を直接呼ぶものは無い）。

## 試験

- 共通配線の単体試験（PlatformShim.Tests。Docker 不要）:
  - P1〜P4（束ねの gate に、完了を外から制御する偽物の停止の本体を渡す）。
  - 登録の順: `UseWolverine` ＋ `UseAiStockTradingRabbitMq` の後で、gate の hosted service が Wolverine の runtime の hosted service より**後に**登録されている。停止の順で、Wolverine より先に呼ばれる。
  - 変異: gate を「前の端だけ」へ戻す（2 回目以降はすぐ戻る）と P1・P4 が、登録を外すと登録の順の試験が赤になる。
- 後片付けの上限の単体試験（Integration の印なし・Docker 不要）: 終わらない破棄は、上限で、ホストの名前を含む `TimeoutException` になる。
- 実基盤の回帰試験 `HostDoubleStopDrainE2ETests`（Integration・使い捨てのコンテナ）は、規則 11 の P1 を実ホストで写す。
  - 処理中のメッセージを 1 通、排出の最中に残す。停止状態の照会元を「入ったら 4 秒待つ」差し替えにする。
  - その状態でホストを破棄し、「**コンテナの破棄は、処理中のメッセージの完了より後**」を表明する。
  - 永久に止まる交差そのものはまれだが、破棄が排出より先に来る形は、エントリポイント側の停止が後から Wolverine に着いた回には毎回起きる。それをここで捕まえる。

## 実測の結果

一時的な観測用の試験（コミットしない）で、処理中のメッセージを残してホストを破棄したときの時刻を記録した。

| | 停止の開始 | `ApplicationStopped` | コンテナの破棄 | 処理中のメッセージの完了 |
| --- | --- | --- | --- | --- |
| gate なし | 21:42:49.400 | 21:42:49.475 | 21:42:49.476（**完了の約 3.9 秒前**） | 21:42:53.396 |
| gate あり | 21:44:12.549 | 21:44:16.829 | 21:44:16.832（完了の後） | 21:44:16.548 |

繰り返しの結果（手元・使い捨てのコンテナ）は次のとおりである。

| 試験 | gate なし（変異） | gate あり |
| --- | --- | --- |
| `HostDoubleStopDrainE2ETests`（P1 の実ホスト版） | 13 回中 7 回 赤 | 13 回中 0 回 赤 |
| `TradeExecutionPipelineE2ETests`＋`OrderExecutionPipelineE2ETests`（永久の停止そのもの） | 修正前の手元の 3 回の実行中 1 回 停止（heap dump を取った回）。その後の 12 回は 0 回 | （下の PR の CI と Integration で見る） |

- 永久の停止そのものは低い率でしか再現しない（CI でも、同じコミットの push の実行は緑だった）。
- そのため、根の窓（破棄が排出より先に来る）を回帰試験の表明にした。

単体の変異（PlatformShim.Tests の `WolverineSingleStopGateTests` 7 件）:

| 変異 | 赤になった試験 |
| --- | --- |
| gate を「前の端だけ」へ戻す（2 本目以降は、すぐ完了を返す） | P1・P4（非同期の失敗）・P4（同期の失敗）の 3 件 |
| 共通配線から gate の登録を外す | 登録の順の試験の 1 件 |
| 戻した後 | 7 件すべて緑 |

## MSP 側への影響

- MSP の Integration は、AST を submodule（`src/ai-stock-trading`、現在 `b427f998`）で取り込んで走らせる。
- AST の修正が develop に入った後、MSP 側で submodule の参照を進める PR が要る。

## 残余

- 停止が 2 回並んで呼ばれること自体は、in-process のホストの仕様であり、残る。束ねるのは Wolverine の停止だけである。
  - ほかの hosted service（`BackgroundService` 等）の `StopAsync` も 2 回呼ばれる。
  - ただし、ほかの hosted service は Wolverine より前に登録されているので、2 回目の停止でも Wolverine の停止の後に呼ばれる。束ねによって 2 回目が排出の完了まで待つので、コンテナの破棄はその後になる。
- Wolverine の 2 回目の `StopAsync` が旗だけを見て戻る挙動は、Wolverine 側に残る。版を上げるときに挙動が変わったら、gate の要否を見直す（IADR-0591 の見直しの条件）。
