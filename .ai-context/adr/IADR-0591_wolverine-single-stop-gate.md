---
title: IADR-0591 Wolverine の停止を共通配線で 1 本の作業に束ね、どの停止の呼び出し元もその完了を待つ。in-process のホストで停止が 2 回並ぶと、2 回目が排出を待たずに終わり、排出の途中でコンテナ（RabbitMQ の接続）が破棄されて後片付けが永久に止まるため。後片付けの破棄には、ホストを名指して落とす上限を補助として置く
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0129]
author: claude (Claude Code)
created: 2026-10-26
updated: 2026-10-26
related_specs:
  - ../specs/20261026_msp2265_wolverine-double-stop-hang.md
---

# IADR-0591: Wolverine の停止を 1 本に束ねる（MSP#2265）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-26
- 決定者: claude（MSP#2265。方針の条件はコーディネーターの依頼 2026-10-26: 待機に上限を付けるだけで済ませず、ハングの原因そのものを直す。上限は失敗を速く・診断可能にする補助）

## 起点・関連

- 起点: MSP#2265（MSP の develop の Integration が赤。AST の `TradeExecutionPipelineE2ETests` が後片付けで 5 分止まった）。
- 関連: [IADR-0129](./IADR-0129_wolverine-messaging-topology.md)（Wolverine の共通配線）。#1128（同じ後片付けで、RabbitMQ の接続を閉じる待ちが打ち切られた。下の「結果」）。
- 作業仕様書: [20261026_msp2265_wolverine-double-stop-hang](../specs/20261026_msp2265_wolverine-double-stop-hang.md)（観測・規則 11 の表・母集合）。

## コンテキストと課題

- in-process のホスト（`WebApplicationFactory`）では、ホストの停止が 2 回、並んで呼ばれる。手元の観測で、呼び出し元と破棄の主体を確かめた。
  - 1 回目は `WebApplicationFactory.DisposeAsync` から `DeferredHost.StopAsync` を経る。
  - 2 回目はエントリポイントの `app.RunAsync()` の `WaitForShutdownAsync` からである。
  - コンテナの破棄は 2 回目の側の `RunAsync` の `finally` が行う。
- Wolverine 6.24.5 の `WolverineRuntime.StopAsync` は、冒頭で「停止した」旗を立ててから排出に入る。2 回目の呼び出しは旗を見てすぐ戻る。
  - そのため、2 回目の停止が先に終わり、`RunAsync` が排出の途中でコンテナを破棄できる。
  - `WolverineRuntime.DisposeAsync` も旗を見て停止を飛ばし、トランスポート（RabbitMQ の接続）を破棄する。
- その結果、排出の中でチャネルを閉じていたリスナが、接続の破棄済みのセマフォを待ったまま止まる。
  - 手元のテストホストの heap dump で、待ち手の連鎖と、両セマフォが破棄済みであることを読んだ。
  - 待ち手は破棄で待ち行列から外れており、誰にも起こされない。
- 間欠的になる理由: 排出が 2 回目の停止と破棄より先に終われば、何も起きない。

## 検討した選択肢

| 案 | 内容 | 窓（規則 11 の表） | 判断 |
| --- | --- | --- | --- |
| 後片付けに上限を付けるだけ | 止まったら時間で打ち切る | 窓は開いたまま（毎回ではないが、止まる・落ちる） | 採らない（根を直さない。決定 2 で補助としてだけ置く） |
| 試験の側で、エントリポイントに停止させてから破棄する | `StopApplication` で `RunAsync` に止めさせ、`ApplicationStopped` を待ってから `WebApplicationFactory` を破棄する | 破棄済みのホストへ `DeferredHost.StopAsync` が再び来る。`BackgroundService` の破棄済みの CTS で投げる | 採らない |
| `RunAsync` が `ApplicationStopped` を待ってから破棄する（後の端だけ） | 共通の終端 `RunAiStockTradingAsync` で待つ | P1 ×（`ApplicationStopped` は先に終わった 2 回目の停止で立つ） | 採らない |
| **停止を 1 本の作業に束ね、全ての呼び出し元がそれを待つ（両端）** | 共通配線に gate の hosted service を置く | P1〜P4 すべて 〇 | **採用** |
| Wolverine / RabbitMQ.Client の版を上げる | 上流の挙動が変わるのを待つ | 未確認（6.53.0 / 7.2.2 が出ているが、旗の挙動が変わったかは確かめていない） | 採らない（版は基盤と揃える約束。見直しの条件に残す） |

## 決定

### 決定 1: Wolverine の停止を共通配線で 1 本の作業に束ねる

- `UseAiStockTradingRabbitMq` の中で、`WolverineSingleStopGate` を `IHostedService` として登録する。
  - 登録は configure の中で行う。`UseWolverine` は configure より前に runtime を hosted service 登録するので、gate は runtime より後に並ぶ。
  - 停止は登録の逆順なので、各停止の呼び出しで、gate は Wolverine 自身より先に呼ばれる。
- gate の `StopAsync` は、最初の呼び出しが runtime の `StopAsync` を 1 回だけ走らせる。
  - 以後の呼び出しは、同じ作業（完了も例外も）を返す。
  - どの停止の呼び出しも排出の完了より前に終わらないので、`RunAsync` の破棄は排出の後になる。
- Wolverine 自身の hosted service の停止は、その後に呼ばれ、旗を見て戻る（二重には走らない）。
- 本番（1 プロセス 1 サービス・SIGTERM で `RunAsync` だけが止める）では呼び出しは 1 回で、排出の位置も従来と同じである。
  - 厳密には、Wolverine が runtime の後に登録する hosted service（`SoloHeartbeatService` 等）より先に排出する。いずれも排出に依らない。

### 決定 2: 後片付けの破棄に上限を置き、超えたらホストを名指して落とす（補助）

- `E2EInfrastructure.DisposeQuietlyAsync` に上限（既定 2 分）を置く。
- 超えたら、ホストの名前と疑うべき窓（本 IADR）を書いた `TimeoutException` で試験を赤にする。
- 黙って続けない。integration の blame-hang（5 分）の無言の中断より速く、原因を名指して落とす。
- 握る例外は従来どおり打ち切り（`OperationCanceledException`）だけである（#1128）。

## 結果

- in-process のホストの二重の停止で、排出の途中に接続が破棄される窓が閉じる。
- 規則 11 の表の P1〜P4 を、共通配線の単体試験で固定した。
- 手元の実基盤（使い捨てのコンテナ）での繰り返しの結果は、PR に表で載せる。
- #1128 で観測された「接続を閉じる待ちの打ち切り（`TaskCanceledException` ← `ConnectionMonitor.DisposeAsync` ← `WebApplicationFactory.DisposeAsync`）」も、同じ二重の停止の下で起きていた可能性が高い。
  - #1128 の打ち切りを握る処理は残す。打ち切りは別の原因（ランナーの負荷）でも起き得るからである。
- 残余:
  - 停止が 2 回並ぶこと自体は in-process のホストの仕様であり、残る。束ねるのは Wolverine の停止だけである。
  - ほかの hosted service の `StopAsync` は従来どおり 2 回呼ばれる。いずれも排出の後に呼ばれるので、本件の窓には当たらない。
  - **見直しの条件**: Wolverine の版を上げて、2 回目の `StopAsync` が 1 回目の完了を待つようになったら、gate は不要になる。外すかどうかを判断する。
