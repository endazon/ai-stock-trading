---
title: Wolverine の transport の既定のデッドレターをサービスごとの名前（<ServiceName>.dead-letter）にし、ブローカの権限に共有のデッドレターが要らないようにする（共通配線・単体と実ブローカの試験・移行の手順書）
type: spec
status: accepted
related_ids: [NFR-18, ADR-0013, IADR-0635, IADR-0129, IADR-0628, MSP:IADR-0699, MSP:IADR-0681, MSP:IADR-0694]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0013_messaging-follow-wolverine-kafka.md （メッセージング）
---

# 仕様書: デッドレターをサービスごとにする

## 起点

- 所有者の裁定（2026-11-07「全部直してから稼働」）の一部。基盤（MSP）のクラウドのブローカの権限で、取引ユニットの全 11 利用者が共有の
  デッドレター `wolverine-dead-letter-queue` に configure・write・read を持つ。これをサービスごとにし、各利用者は自分の DLQ だけを持つ。
- 起点 ID はブローカの最小権限の一連（IADR-0628・MSP:IADR-0681／0694）と同じ `NFR-18`。決定は [IADR-0635](../adr/IADR-0635_per-service-dead-letter-queue.md)。
- 基盤の側は MSP の作業仕様書 `20261107_broker-per-service-dead-letter`（MSP:IADR-0699）。

## 1. 現状（develop `2e95ee467`。shallow ではない）

- 受信キューの DLQ は `<queue>_error`（`WolverineExtensions.DeadLetterQueueNameFor`。IADR-0129 決定 5）。自分の接頭辞の中。
- それとは別に、WolverineFx.RabbitMQ 6.24.5 は transport の既定の DLQ（`RabbitMqTransport.DeadLetterQueue`・既定の名前
  `wolverine-dead-letter-queue`）を起動のたびに宣言する。逆コンパイル（ilspycmd 11.2）で確かめた:
  - `RabbitMqTransport.enabledDeadLetterQueues()` は、transport の既定の Mode が WolverineStorage でなければ**無条件に**それを返し、
    続けて受信の役のキューの DLQ を返す。初期化はその各々について `Queues[name]`・`Exchanges[exchangeName]`・束ねを作る（AutoProvision で宣言）。
  - `DeadLetterQueue.ExchangeName`・`BindingName` は既定でキューの名前と同じ。
  - `RabbitMqTransportExpression.CustomizeDeadLetterQueueing(DeadLetterQueue)` が既定の Mode・名前・exchange・束ねを置き換える。
  - 受信の役のキューは作るときに既定の DLQ を複製し（`RabbitMqQueue` の構築子）、規約の `ConfigureListeners` が `<queue>_error` で上書きする。
- 基盤の生成器（MSP `scripts/gen-rabbitmq-permissions.js`）は、これを全利用者に共有の名前の権限として与えている。

## 2. 母集合（規則 9・10: 誤りの側の文字列で走査した）

`grep -rn "wolverine-dead-letter\|共有のデッドレター\|共有の DLQ\|デッドレター"`（`.ai-context/specs`・`.ai-context/adr`・CHANGELOG を除く）。

| 箇所 | 扱い |
| --- | --- |
| `PlatformShim/.../WolverineExtensions.cs`（`DeadLetterQueueNameFor` の注記の「既定は共有」） | 正しい（受信キューの説明）。transport の既定の名前の関数への参照を足す |
| `IntegrationTests/BrokerSubscriberLeastPrivilegeE2ETests.cs` の権限（共有のデッドレター） | 外す（基盤の生成器が与える形が変わる） |
| `docs/operations/operations.md` の §メッセージング（デッドレターは `<queue>_error`） | `<ServiceName>.dead-letter` を足し、手順書を引く |
| `docs/operations/wolverine-queue-cleanup-runbook.md`（MassTransit の `_error`） | 変えない（別の主題） |
| `NotificationService` の注記（再試行→デッドレター） | 変えない（一般の記述で正しい） |
| `SubscribedExchangeDeclarationTests`（デッドレターの exchange は受動にしない） | 変えない（受信キューの DLQ の話で正しい） |
| 既存の IADR-0129・IADR-0395 の本文 | 凍結記録のため書き換えない。transport の既定の扱いは IADR-0635 に書く |

## 3. 作るもの

1. 共通配線: `ServiceDeadLetterQueueNameFor(serviceName)`（`<ServiceName>.dead-letter`）を足し、`UseAiStockTradingRabbitMq` で
   `CustomizeDeadLetterQueueing(new DeadLetterQueue(...))` を当てる（共通の組み立ての 1 か所。サービス側は変えない）。
2. 単体 `PerServiceDeadLetterQueueTests`（PlatformShim.Tests）: 全サービス（`Program.cs` のサービス名の定数。11 個）で宣言されるデッドレターの
   名前が自分の接頭辞の中・共有の名前が無い。陰性対照（共通配線を通さない既定）。
3. 実ブローカ `BrokerPerServiceDeadLetterE2ETests`（Integration）: 共有の権限の無い利用者で起動・失敗したメッセージが `<queue>_error` へ移る・
   共有の名前が作られない・共有の名前を宣言できない（403）。`BrokerSubscriberLeastPrivilegeE2ETests` の権限から共有を外す。
4. 手順書 `docs/operations/broker-per-service-dead-letter-runbook.md`（適用の順序・旧の共有のキューの扱い・戻し方）。operations.md から引く。

## 4. 受け入れ基準

1. 全 11 サービスの共通配線で、transport の既定の DLQ は `<ServiceName>.dead-letter`、受信キューの DLQ は `<queue>_error`。
   どちらのキュー・exchange・束ねも `^<ServiceName>\..*$` に入り、`wolverine-dead-letter-queue` は宣言されない。
2. 1 を外す変異（`CustomizeDeadLetterQueueing` を消す）で単体の試験が赤になる。陰性対照が共有の名前を拾う。
3. 実ブローカで、共有のデッドレターの権限が無い利用者が起動し、失敗し続けたメッセージは再試行（2s・10s・30s）を使い切った後に `<queue>_error` へ移る。
4. 受信キューの宣言の引数（`x-dead-letter-exchange`）は変わらない（在るキューの宣言し直しで止まらない）。
5. `dotnet build/test backend/backend.slnx`・`dotnet format --verify-no-changes`・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・helm の描画の検査が通る。

## 5. 実測

- 単体: 修正ありで `PerServiceDeadLetterQueueTests` 17 件が緑。`CustomizeDeadLetterQueueing` の 1 行を消すと、全 11 サービスの理論の試験が赤（11 件）。
- 実ブローカ（手元の docker・Testcontainers の RabbitMQ 3.13）: `BrokerPerServiceDeadLetterE2ETests` と `BrokerSubscriberLeastPrivilegeE2ETests` の
  5 件が緑（1 分 53 秒）。修正を外すと、権限から共有を外した購読側は起動できず赤になった。

## 6. 稼働中への影響と適用の順序

- 新しいイメージの Pod は起動で `<ServiceName>.dead-letter`（キュー・exchange・束ね）を新たに宣言する。受信キュー・`<queue>_error` の宣言は同じ。
  ローリングの更新で旧・新の Pod が並んでも、受信キューを取り合うのは従来どおりで、取り残しは無い。
- **取引ユニットのイメージを先に（全 11 サービス）、基盤の権限（MSP の `rabbitmq-permissions.yaml`）を後に**当てる。逆にすると旧のイメージの Pod は
  共有の名前の宣言が 403 で拒まれ、起動できない。詳しくは手順書。

## 7. 範囲外・残り

- メッセージの発行元の確かめの段（values-cloud の `global.messageOrigin.mode`）は別担当（#1587）の範囲。本変更では触れない。
  確かめた結果: values-cloud は `Enforce`（段 2）。
- 基盤のサービス自身のデッドレター（ローカル）は基盤の判断（MSP:IADR-0699 の残余）。
