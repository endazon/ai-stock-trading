---
title: IADR-0635 Wolverine の transport の既定のデッドレター（既定の名前は全サービスで同じ wolverine-dead-letter-queue）を、共通配線でサービスごとの名前（<ServiceName>.dead-letter）にする。受信キューのデッドレター（<queue>_error）は変えない。ブローカの権限に共有のデッドレターが要らなくなる
type: impl-adr
status: Accepted
related_ids: [NFR-18, ADR-0013, IADR-0129, IADR-0628, MSP:IADR-0681, MSP:IADR-0694, MSP:IADR-0699]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0013_messaging-follow-wolverine-kafka.md （メッセージング）
---

# IADR-0635: デッドレターをサービスごとにする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-07
- 決定者: 所有者の裁定（2026-11-07「全部直してから稼働」）・claude（方式）

## 起点・関連

- 非機能 **NFR-18**（ブローカの最小権限。IADR-0628 と同じ起点）。
- 前提: IADR-0129 決定 5（受信キューのデッドレターは `<queue>_error`）、IADR-0628（クラウドの購読側は購読の exchange を受動で宣言する）。
- 基盤の受け手: MSP:IADR-0699（ブローカの権限の生成器から共有のデッドレターを外す）。基盤の権限の生成は MSP:IADR-0681・MSP:IADR-0694。
- 作業仕様書: `.ai-context/specs/20261107_broker-per-service-dead-letter.md`。手順書: `docs/operations/broker-per-service-dead-letter-runbook.md`。

## コンテキストと課題

受信キューのデッドレターは IADR-0129 決定 5 で `<queue>_error`（自分の接頭辞の中）にしてある。ところが Wolverine（WolverineFx.RabbitMQ 6.24.5）は、
これとは別に **transport の既定のデッドレター**を起動のたびに宣言する（`RabbitMqTransport.DeadLetterQueue`。既定の名前は全サービスで同じ
`wolverine-dead-letter-queue`。キュー・同名の exchange・束ねの 3 つ。逆コンパイルした `RabbitMqTransport.enabledDeadLetterQueues` が、
WolverineStorage でない限り無条件に返す）。受信キューを持たない（発行だけの）サービスも宣言する。

このためブローカの権限（基盤の生成器）は、取引ユニットの全 11 利用者に同じ 1 本の configure・write・read を与えていた。
1 本を共有すると、利用者の間をブローカの権限で隔てられない（宣言・削除・読み取りが互いに届く）。

## 決定

### 決定 1 — transport の既定のデッドレターをサービスごとの名前にする

共通配線（`UseAiStockTradingRabbitMq`）で `CustomizeDeadLetterQueueing(new DeadLetterQueue(ServiceDeadLetterQueueNameFor(serviceName)))` を当てる。
名前は `<ServiceName>.dead-letter`（例 `ai-stock-trading.audit-service.dead-letter`）。キュー・exchange・束ねの名前がすべてこれになる。

- 各利用者の権限の接頭辞 `^<ServiceName>\..*$` の中に入るので、基盤は共有のデッドレターの権限を外せる（MSP:IADR-0699）。
- 受信キューの名前 `<ServiceName>.<型名>` と衝突しない（型名にハイフンは使えない）。
- 受信キューのデッドレター（`<queue>_error`）は変えない（`ConfigureListeners` が個別に与えるのが優先する）。受信キューの引数
  （`x-dead-letter-exchange`）は変わらないので、在るキューの宣言し直しが PRECONDITION_FAILED にならない。
- 規則の出所は 1 か所（`ServiceDeadLetterQueueNameFor`）。基盤の生成器はこの関数の字面と、`CustomizeDeadLetterQueueing` の呼び出しを読んで導出し、
  無ければ止まる（黙って共有の名前の権限へ戻らない）。

### 決定 2 — 無効化（WolverineStorage）は採らない

`DisableDeadLetterQueueing()` で transport の既定のデッドレターを宣言しない案は採らない。transport の既定は受信キュー以外の既定の受け皿で
（`TryBuildDeadLetterSender` が既定へ落ちる経路がある）、無効にすると Wolverine の他の経路の挙動まで変える。名前を変えるだけなら挙動は今と同じ。

### 決定 3 — 固定の仕方

- 単体（PlatformShim.Tests `PerServiceDeadLetterQueueTests`）: 全 11 サービス（各 `Program.cs` のサービス名の定数）で、起動の構成が宣言する
  デッドレター（transport の既定と受信キューの分。キュー・exchange・束ね）の名前がすべて自分の接頭辞の中にあり、共有の名前が無い。
  陰性対照: 共通配線を通さない既定では共有の名前が出て、同じ検査が拾う。`CustomizeDeadLetterQueueing` を外すと 11 件とも赤になることを確かめた。
- 実ブローカ（Integration `BrokerPerServiceDeadLetterE2ETests`）: 共有のデッドレターの権限が無い利用者でも起動し、失敗し続けたメッセージは
  再試行を使い切った後に自分の `<queue>_error` へ移る。共有の名前のキューは作られない。その利用者は共有の名前を宣言できない（403）。
  `BrokerSubscriberLeastPrivilegeE2ETests` の権限からも共有のデッドレターを外した。

## 結果

- 良い: 取引ユニットの利用者は、他のサービスのデッドレターも共有の受け皿も宣言・削除・読み取りできない（基盤の権限を当てた後）。
  どのサービスで死んだかがキュー名から読める。
- 稼働中への影響: 新しいイメージの Pod は起動のときに `<ServiceName>.dead-letter`（キュー・exchange・束ね）を新たに宣言する。
  受信キューと `<queue>_error` はそのまま。旧の共有のキューはブローカに残る（新しいイメージは使わない）。
- 適用の順序: **取引ユニットのイメージを先に、基盤の権限を後に**当てる。逆にすると旧のイメージの Pod は共有の名前の宣言が 403 で拒まれ、
  起動できない。旧の共有のキューの扱い（件数を読みだけで確かめ、空なら消す）は手順書にある。

## 残余

- 基盤（MSP）自身のサービスはローカルで共有のデッドレターを使い続ける（クラウドのブローカに基盤のサービスの利用者は居ない。基盤の判断）。
- 旧の共有の exchange（同名）は手順書では消さない（メッセージを持たず、権限を当てた後は誰も触れない）。
