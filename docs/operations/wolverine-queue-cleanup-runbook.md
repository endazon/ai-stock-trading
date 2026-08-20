---
title: MassTransit 時代の旧キュー（47 本）の削除手順 Runbook
type: runbook
status: draft
created: 2026-08-04
updated: 2026-08-21
author: claude
---
<!-- trace:
ids: [FR-03, FR-10]
adrs: [ADR-0013]
iadrs: [IADR-0106, IADR-0129]
specs: [20260803_354_wolverine-migration, ADR-0013_messaging-follow-wolverine-kafka, ADR-0027_messaging-wolverine, ADR-0028_broker-rabbitmq-kafka]
issues: [#258, #354, #45]
-->


# Runbook: MassTransit 時代の旧キュー（47 本）を RabbitMQ から削除する

> 起点は、メッセージング基盤の Wolverine 移行を定めた計画 ADR である。
> 実装側では「Wolverine 移行のトポロジ設計（キュー名にサービス名を前置し、ローカルルーティングを無効化する）」が
> 本手順の必要性を「結果」の悪い影響として記載し、旧キュー名の導出規則は「consumer クラス名＝キュー名
> （サービス跨ぎの一意性）」（Superseded）が定めていた。
> 作業仕様書は 仕様書: MassTransit を Wolverine へ移行しローカルディスパッチを統一する。
>
> ⚠️ **本手順は「Wolverine 版をデプロイし、正常稼働を確認したあと」に実施する。** 先に消しても得は無く、
> 切り戻し（旧版の再デプロイ）の余地を失うだけである。**急がないこと**が最大の安全策である。
> ⚠️ 本リポジトリに自動デプロイは無く、デプロイは `scripts/k8s-local-deploy.sh` の手動実行である。
> 本手順も**手動**であり、自動化しない（誤爆時の被害が「メッセージの消失」であるため）。

## なぜ要るのか

Wolverine 移行でキュー名の導出規則が変わった（新トポロジ設計の決定 1）。

| | 旧（MassTransit） | 新（Wolverine） |
| --- | --- | --- |
| キュー名 | consumer クラス名から `Consumer` を落としたもの（例 `TradeDecisionMade`） | `<ServiceName>.<メッセージ型名>`（例 `ai-stock-trading.risk-management-service.TradeDecisionMade`） |
| 本数 | **47**（consumer 1 つにつき 1 本） | **45**（1 サービス × 1 イベント型 = 1 本。RiskManagement の `OrderApproved` / `OrderExecuted` は 2 ハンドラが 1 本を共有する） |

新版をデプロイすると、Wolverine が新しい 45 本を自動生成（AutoProvision）する。一方**旧 47 本はブローカ上に
残り続ける**。放置すると次の実害がある。

1. **メッセージが滞留し続ける**。旧キューは旧 exchange（メッセージ型 URN 形式）に bind されたままだが、
   発行側が新 exchange（完全名形式）へ送るようになるため、**通常は新たな流入は無い**。ただし移行前に
   in-flight だったメッセージ・`_error` へ退避済みのメッセージは残る（**消してよいかの判断が要る**。下記の確認手順）。
2. **ディスクを食う**。滞留分がそのまま残る。
3. **運用の誤読**。`rabbitmqctl list_queues` に consumer 0 のキューが 47 本並び、障害調査のときに
   「どれが生きているキューか」が読めなくなる。#258 の調査ではまさにキューと所有者の対応が読めないことが
   遅れの一因だった。

## 前提条件（すべて満たしてから実施する）

- [ ] Wolverine 版（#354 完了版）が全サービスでデプロイ済みで、**24 時間以上**正常稼働している
      （日次の取引サイクル・日報・監査が一巡し、取りこぼしが無いことを確認できる長さ）。
- [ ] 新しい 45 本のキューが存在し、**各々に consumer が付いている**（下記「① 新旧の実在確認」）。
- [ ] 旧キューの `messages` が 0、または残メッセージを**破棄してよい**と判断済み（下記「② 残メッセージの判断」）。
- [ ] 切り戻し（旧版の再デプロイ）を行わないと決めている（削除後は旧版に戻しても**キューは自動再生成される**が、
      滞留していたメッセージは戻らない）。

## 手順

### ① 新旧の実在確認（消す前に、新しい配線が本当に動いていることを見る）

```bash
# RabbitMQ の Pod 名は環境に合わせる（ローカル経路B の例）。
kubectl -n ai-stock-trading exec -it deploy/rabbitmq -- \
  rabbitmqctl list_queues name messages consumers | sort
```

- **新キュー（`ai-stock-trading.` で始まる 45 本）は `consumers >= 1`** であること。
  0 のものがあれば、そのサービスは購読できていない。**削除を中止し原因を調べる**
  （Wolverine の起動失敗・`ServiceName` の取り違え・アセンブリ走査漏れ）。
- 旧キュー（下表の 47 本）は `consumers = 0` であること。1 以上なら**旧版がまだ動いている**。
  デプロイが全サービスに行き渡っていない。**削除を中止する**。

### ② 残メッセージの判断（消してよいか）

```bash
# 旧キューのうち、メッセージが残っているものだけを一覧する。
kubectl -n ai-stock-trading exec -it deploy/rabbitmq -- \
  rabbitmqctl list_queues name messages | awk '$2 > 0'
```

残っている場合、その中身は「旧版が処理し損ねたイベント」である。**捨てる前に必ず中身を見る**
（RabbitMQ 管理 UI の Get messages は `requeue=true` で覗ける。取り出したメッセージは戻すこと）。

- `*_error`（デッドレター）に残っているもの: 処理に失敗した業務イベントである。**捨てる前に監査台帳
  （AuditService）と突き合わせ**、必要なら人手で是正する。エンベロープ形式が MassTransit 互換であるため、
  **Wolverine 版へそのまま再投入しても消費できない**（新トポロジ設計の決定 7）。
  再現が必要なら、内容を読んで**新しいイベントとして発行し直す**（＝再投入ではなく再発行）。
- 通常キューに残っているもの: 移行の瞬間に in-flight だったイベント。上と同じ扱い。

### ③ 削除（1 本ずつ・空でなければ消さない）

`rabbitmqctl delete_queue` は既定で `--if-empty` / `--if-unused` を持つ。**必ず両方を付ける**
（「空でなければ消さない」「consumer が付いていれば消さない」＝②の判断漏れと①の見落としに対する保険）。

```bash
# 1 本ずつ。空でない/使用中なら失敗して残る（それが正しい振る舞い）。
kubectl -n ai-stock-trading exec -it deploy/rabbitmq -- \
  rabbitmqctl delete_queue TradeDecisionMade --if-empty --if-unused
```

まとめて実施する場合も、**一覧をファイルに書き出してから**流す（引数のタイプミスで新キューを消さないため）。

```bash
# 旧キュー名の一覧（下表と同じ 47 本）を old-queues.txt に用意する。
# 新キューは必ず "ai-stock-trading." で始まるため、その接頭辞を持つ行が混ざっていないことを先に確かめる。
grep -c '^ai-stock-trading\.' old-queues.txt   # 0 であること（0 以外なら中止）

while read -r q; do
  echo "--- $q"
  kubectl -n ai-stock-trading exec -i deploy/rabbitmq -- \
    rabbitmqctl delete_queue "$q" --if-empty --if-unused || echo "SKIP(空でない/使用中): $q"
  kubectl -n ai-stock-trading exec -i deploy/rabbitmq -- \
    rabbitmqctl delete_queue "${q}_error" --if-empty --if-unused || echo "SKIP: ${q}_error"
  kubectl -n ai-stock-trading exec -i deploy/rabbitmq -- \
    rabbitmqctl delete_queue "${q}_skipped" --if-empty --if-unused || echo "SKIP: ${q}_skipped"
done < old-queues.txt
```

> `_error` は MassTransit のデッドレター、`_skipped` は「消費されずスキップされた」メッセージの退避先である。
> どちらも存在しないことがある（失敗が一度も起きていないキュー）。存在しなければ削除は失敗するが、無視してよい。

### ④ 旧 exchange の削除（任意・キューの削除後）

MassTransit は「メッセージ型 URN の exchange（`AiStockTrading.Shared.Contracts.Events:<Type>`）」と
「エンドポイントごとの exchange（キューと同名）」を作る。Wolverine の exchange は**完全名**
（`AiStockTrading.Shared.Contracts.Events.<Type>`。区切りが `:` ではなく `.`）であり、**別物として共存する**。

```bash
kubectl -n ai-stock-trading exec -it deploy/rabbitmq -- rabbitmqctl list_exchanges name | grep ':'
# → 旧 URN 形式（コロン区切り）のみが該当する。新形式（ドット区切り）を消さないこと。
kubectl -n ai-stock-trading exec -it deploy/rabbitmq -- \
  rabbitmqctl delete_exchange 'AiStockTrading.Shared.Contracts.Events:TradeDecisionMade'
```

exchange はメッセージを保持しないため、キューほど慎重になる必要はない。ただし**削除は最後**にする
（bind されたキューが残っていると、そのキューへの経路だけが静かに消えるため、順序を守る）。

### ⑤ 事後確認

```bash
kubectl -n ai-stock-trading exec -it deploy/rabbitmq -- \
  rabbitmqctl list_queues name messages consumers | sort
```

- 残るキューが `ai-stock-trading.` 接頭辞の 45 本（＋ `_error` 45 本）だけであること。
- 各サービスのログにエラーが出ていないこと（削除直後に購読が落ちていないか）。
- 取引サイクルを 1 巡させ、`OrderApproved` が発注執行とリスク管理の**両方**へ届いていること
  （fan-out。自動検証は `Category=Integration` の
  `TradeExecutionPipelineE2ETests.同一イベントは購読する全サービスへ届く_...`）。

## ロールバック

**削除したキューは戻せない**（中のメッセージも戻らない）。ただし、次の性質により復旧は容易である。

1. **キューそのものは自動で再生成される。** 旧版（MassTransit）を再デプロイすれば MassTransit が、
   新版なら Wolverine が、起動時にそれぞれの規則でキューと binding を宣言する（AutoProvision）。
   したがって「消したせいでサービスが起動しない／購読できない」状態にはならない。
2. **失われるのは滞留していたメッセージだけ**である。だからこそ手順 ② を飛ばしてはならない。
3. 誤って**新キュー**を消した場合: 該当サービスの Pod を再起動する（`kubectl rollout restart deploy/<svc>`）。
   起動時に再宣言される。その間に発行されたイベントは**そのサービスにだけ届かない**（キューが無い間、
   fanout exchange は宛先が無いメッセージを捨てる）。**監査サービスの台帳と突き合わせて欠落を確認する**こと。

## 旧キュー一覧（47 本・削除対象）

移行前の実測（`IConsumer<T>` 実装 47 件から MassTransit の `DefaultEndpointNameFormatter` 規則で導いた名前）。
各キューには `<name>_error` / `<name>_skipped` が付随し得る（③ で併せて削除する）。

| # | 旧キュー名（削除対象） | 所有していたサービス | 移行後のキュー |
| --- | --- | --- | --- |
| 1 | `AssumptionsChanged` | CostControl（共有ハンドラ） | `ai-stock-trading.cost-control-service.AssumptionsChanged` |
| 2 | `AssumptionsChangedAudit` | Audit | `ai-stock-trading.audit-service.AssumptionsChanged` |
| 3 | `AssumptionsChangedNotification` | Notification | `ai-stock-trading.notification-service.AssumptionsChanged` |
| 4 | `BacktestEvaluatedAudit` | Audit | `ai-stock-trading.audit-service.BacktestEvaluated` |
| 5 | `BacktestEvaluatedProjection` | RiskManagement | `ai-stock-trading.risk-management-service.BacktestEvaluated` |
| 6 | `BrokerPositionsObserved` | RiskManagement | `ai-stock-trading.risk-management-service.BrokerPositionsObserved` |
| 7 | `BrokerPositionsObservedAudit` | Audit | `ai-stock-trading.audit-service.BrokerPositionsObserved` |
| 8 | `CostThresholdReachedAudit` | Audit | `ai-stock-trading.audit-service.CostThresholdReached` |
| 9 | `CostThresholdReachedNotification` | Notification | `ai-stock-trading.notification-service.CostThresholdReached` |
| 10 | `DailyPolicyUnconfirmedAudit` | Audit | `ai-stock-trading.audit-service.DailyPolicyUnconfirmed` |
| 11 | `DailyPolicyUnconfirmedNotification` | Notification | `ai-stock-trading.notification-service.DailyPolicyUnconfirmed` |
| 12 | `InformationCollected` | TradeDecision | `ai-stock-trading.trade-decision-service.InformationCollected` |
| 13 | `InformationCollectedAudit` | Audit | `ai-stock-trading.audit-service.InformationCollected` |
| 14 | `LlmCostIncurred` | CostControl | `ai-stock-trading.cost-control-service.LlmCostIncurred` |
| 15 | `LlmCostIncurredAudit` | Audit | `ai-stock-trading.audit-service.LlmCostIncurred` |
| 16 | `OrderApproved` | OrderExecution | `ai-stock-trading.order-execution-service.OrderApproved` |
| 17 | `OrderApprovedActivity` | RiskManagement | `ai-stock-trading.risk-management-service.OrderApproved`（**18 と統合**） |
| 18 | `OrderApprovedLedger` | RiskManagement | 同上（1 キューを 2 ハンドラが共有。新トポロジ設計の決定 10） |
| 19 | `OrderApprovedAudit` | Audit | `ai-stock-trading.audit-service.OrderApproved` |
| 20 | `OrderCancelledActivity` | RiskManagement | `ai-stock-trading.risk-management-service.OrderCancelled` |
| 21 | `OrderCancelledAudit` | Audit | `ai-stock-trading.audit-service.OrderCancelled` |
| 22 | `OrderExecutedActivity` | RiskManagement | `ai-stock-trading.risk-management-service.OrderExecuted`（**24 と統合**） |
| 23 | `OrderExecutedAudit` | Audit | `ai-stock-trading.audit-service.OrderExecuted` |
| 24 | `OrderExecutedLedger` | RiskManagement | 同 22（1 キューを 2 ハンドラが共有） |
| 25 | `OrderExecutedNotification` | Notification | `ai-stock-trading.notification-service.OrderExecuted` |
| 26 | `OrderModifiedActivity` | RiskManagement | `ai-stock-trading.risk-management-service.OrderModified` |
| 27 | `OrderModifiedAudit` | Audit | `ai-stock-trading.audit-service.OrderModified` |
| 28 | `OrderRejectedAudit` | Audit | `ai-stock-trading.audit-service.OrderRejected` |
| 29 | `OrderRejectedNotification` | Notification | `ai-stock-trading.notification-service.OrderRejected` |
| 30 | `PositionCloseRequestedAudit` | Audit | `ai-stock-trading.audit-service.PositionCloseRequested` |
| 31 | `PositionReconciliationDriftAudit` | Audit | `ai-stock-trading.audit-service.PositionReconciliationDrift` |
| 32 | `PositionReconciliationDriftNotification` | Notification | `ai-stock-trading.notification-service.PositionReconciliationDrift` |
| 33 | `PriceMovementDetected` | TradeDecision | `ai-stock-trading.trade-decision-service.PriceMovementDetected` |
| 34 | `PriceMovementDetectedAudit` | Audit | `ai-stock-trading.audit-service.PriceMovementDetected` |
| 35 | `ReportConfirmedAudit` | Audit | `ai-stock-trading.audit-service.ReportConfirmed` |
| 36 | `ReportConfirmedNotification` | Notification | `ai-stock-trading.notification-service.ReportConfirmed` |
| 37 | `ReportDraftPresentedAudit` | Audit | `ai-stock-trading.audit-service.ReportDraftPresented` |
| 38 | `ReportDraftPresentedNotification` | Notification | `ai-stock-trading.notification-service.ReportDraftPresented` |
| 39 | `StageTransitionedAudit` | Audit | `ai-stock-trading.audit-service.StageTransitioned` |
| 40 | `StopLossTriggered` | RiskManagement | `ai-stock-trading.risk-management-service.StopLossTriggered` |
| 41 | `StopLossTriggeredAudit` | Audit | `ai-stock-trading.audit-service.StopLossTriggered` |
| 42 | `StopLossTriggeredNotification` | Notification | `ai-stock-trading.notification-service.StopLossTriggered` |
| 43 | `TradeDecisionMade` | RiskManagement | `ai-stock-trading.risk-management-service.TradeDecisionMade` |
| 44 | `TradeDecisionMadeAudit` | Audit | `ai-stock-trading.audit-service.TradeDecisionMade` |
| 45 | `TradeDecisionMadeBaseline` | MarketMonitor | `ai-stock-trading.market-monitor-service.TradeDecisionMade` |
| 46 | `WithdrawalTriggeredAudit` | Audit | `ai-stock-trading.audit-service.WithdrawalTriggered` |
| 47 | `WithdrawalTriggeredNotification` | Notification | `ai-stock-trading.notification-service.WithdrawalTriggered` |

> **`TradeDecisionMadeBaseline`の由来**: `MarketMonitorService` の consumer は
> 旧規則（consumer クラス名＝キュー名）のもとで `TradeDecisionMade` と
> キューを分けるために改名されたものである。Wolverine ではクラス名がキュー名に関与しないため、
> この分離はサービス名の前置が担う（改名の役目は終わった）。
>
> **旧 47 本 → 新 45 本**の差は、RiskManagement の `OrderApproved` / `OrderExecuted` が
> 「1 イベント型 = 1 キュー」に統合されたことによる（各 2 ハンドラ）。

## 関連

- 実装ADR: Wolverine 移行のトポロジ設計（キュー名にサービス名を前置し、ローカルルーティングを無効化する。新トポロジ）／
  consumer クラス名＝キュー名（サービス跨ぎの一意性。旧キュー名の規則・Superseded）
- 運用仕様書: [operations.md](operations.md)（再配信の猶予・保持期間の根拠）
- 作業仕様書: 仕様書: MassTransit を Wolverine へ移行しローカルディスパッチを統一する
