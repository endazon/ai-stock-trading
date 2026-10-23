---
title: 約定の記録と OrderExecuted の発行が分離し、発行に失敗した約定が台帳へ届かない（耐久アウトボックス ＋ 発行待ちの印）
type: spec
status: done
related_ids: [NFR, FR-05, FR-10, ADR-0013, IADR-0113, IADR-0129, IADR-0149, IADR-0371, IADR-0481, IADR-0560, IADR-0569, IADR-0573]
author: claude (Claude Code)
created: 2026-10-23
updated: 2026-10-23
---

# 仕様書: 約定の発行を失わない（#1442）

> 本仕様書は実装着手前に作成した（着手 2026-10-23。AST `origin/develop` `572bf1ff`）。判断の記録は IADR-0573。

## 起点（トレーサビリティ）

- 起点: #1442（親 #1423 の M-4。監査は develop `40de5a61`・読み取りのみ）。起点 ID は `NFR`（配送の信頼性）。統制の入力は FR-10（台帳）・発注結果は FR-05。
- 計画: ADR-0013（基盤に追随。基盤の platform ADR-0027 は発行の原子性を Wolverine の PostgreSQL の Outbox で担うのを標準とする）。
  `05_trading-assumptions` に配送保証の定めは無い（統制値は台帳が約定を知っていることを前提にしている）。計画の変更は要らない。
- 採番: develop の IADR の最大は 0572、開いている PR に IADR は無い（push 直前に再確認）。本件は IADR-0573。

## 原因（コードと実測で確認）

1. `OrderFillPoller.ApplyAsync` は `store.UpdateOutcome` の**後**に `executed.Add`（`OrderExecuted`）し、Worker が巡回の末尾で `bus.PublishAsync` する。
   次の巡回は「変化なし＝書かない・発行しない」。記録の更新と発行の間で落ちる・発行が投げると、その約定は二度と発行されない。
2. **実験（スクラッチの単独コンソール・Wolverine 6.24.5・RabbitMQ 3.13-alpine・postgres:16-alpine）**:
   - 既定（Inline）: 断の間の `PublishAsync` は 16 ms・1 ms で**成功**、`InlineSendingAgent` が `Discarding message ... after 3 attempts`。復旧後も届かない。
     送信の代理の `Latched` は false のまま（健康を見て控える案は成り立たない）。
   - 耐久アウトボックス: 断の間の発行は保存され、復旧後に届いた（2・3 とも受信）。発行の後にプロセスを `Environment.FailFast` で落とし、復旧後に別プロセスで起こすと届いた
     （Balanced は約 2 分後に `Recovered 1 messages`、Solo は起動の直後）。
   - 🔴 処理しない型（`Pong`）では、回収された封筒は送られるが**行が残った**。送信先は `Inline`。exchange を Durable にする endpoint 方針を足すと行が消え、送信先は `Durable`。
     原因は `IPolicies.AllSenders` が `Subscriptions.Any()` の endpoint にだけ効くこと（逆コンパイルで確認）。発注執行は `OrderExecuted` を処理しないので該当する。

## 母集合（規則 9: 誤りの側の文字列で走査）

### OrderExecuted の発行点（`grep -rn "OrderExecuted(" backend/Services/OrderExecutionService --include=*.cs`・試験を除く）

| 発行点 | 記録との順序 | 本件の扱い |
| --- | --- | --- |
| `OrderFillPoller.ApplyAsync`（通常の追跡・打ち切り前の照会し直し） → `OrderFillPollingService.PollOnceAsync` | 記録の更新 → 巡回の末尾で発行 | **決定 2（印）＋ 決定 1** |
| `OrderFillPoller.RecheckAbandonedClosesAsync` → `OrderFillPollingService.RecheckAbandonedClosesAsync` | 同上（同じ `ApplyAsync`） | **決定 2 ＋ 決定 1**（発行の後に印を倒す） |
| `OrderExecutionAppService`（発注・既存結果の再発行）→ `OrderApprovedHandler` | 予約の完了 → ハンドラの中で発行 | 決定 1。落下は `OrderApproved` の再配送で相1 が再発行する（IADR-0057） |
| `OrderReservationReconciler.ToOrderExecuted` → `OrderReservationReconciliationService` | 予約の終端化 → 1 件ずつ発行（IADR-0371） | 決定 1。終端化と発行の間の落下は IADR-0371 の残余のまま（範囲外・IADR-0573 残余 2） |

### 受け手と冪等キー（`grep -rln "OrderExecuted" backend/Services --include=*.cs`・発注執行と試験を除く）

| 受け手 | 書き込み | 冪等キー | 同じ約定を 2 回受けたとき |
| --- | --- | --- | --- |
| リスク管理 `OrderExecutedLedgerHandler` | `AppendFill`・`MarkTerminal`・`PositionCloseAbandoned` | OrderId の累積数量の単調 upsert／終端は初回だけ true | 1 回分（失効通知も初回だけ） |
| リスク管理 `OrderExecutedActivityHandler` | `RecordExecution` | 絶対値の代入 | 同じ状態 |
| リスク管理 `OrderExecutedGoodFaithViolationHandler` | GFV の計数 | OrderId で先着優先 | 増えない |
| リスク管理 `OrderExecutedStage1FillHandler` | Stage 1 の観測 | DecisionId で先着優先 | 増えない |
| 監査 `OrderExecutedAuditHandler` | 監査の行 | `envelope.Id` | 同じ封筒の再送は 1 行／**別の封筒なら 2 行**（残余 1） |
| 通知 `OrderExecutedNotificationHandler` | Discord | なし | **2 通**（残余 1） |
| 市場監視 | 購読しない（台帳の建玉を読む） | — | — |
| 取引判断 `PriceMovementDetectedHandler` | 注記の言及のみ（購読しない） | — | — |

除外: `NotificationFormatter`・`AuditEntryFactory`・`ProtectiveStopWaiverSettlement`・`PositionCloseAbandonment`・`Stage1Progress`・`LedgerFill` 等は上の受け手の部品（同じ経路）。
報告・費用統制は `OrderExecuted` を購読しない（grep に該当なし）。

### 文書（規則 9: `durable outbox`・`アウトボックス`・`TrackingAbandonedFrom` で走査）

- `docs/tests/FR-10_risk-controls-tests.md` の突合の残余「ゼロにするには送信の永続化（durable outbox）が要る」→ 送信の永続化が入った旨を日付つきで追記（記録と発行の間の窓は残る）。
- `docs/operations/operations.md` → 配備の確認（`wolverine` スキーマ・再発行の WRN・戻し口）を追記。
- `.ai-context/adr/README.md` の IADR-0129 行 → 発注執行だけ永続化を足した旨の追記。

## 方式

IADR-0573 決定 1（耐久アウトボックス・exchange を Durable・Solo・戻し口の構成）と決定 2（発行待ちの印・発行の後に状態一致で倒す・印から再発行）。比較は IADR-0573 §検討した選択肢。

## 受け入れ基準 → 試験

| 受け入れ基準 | 試験 |
| --- | --- |
| 発行失敗（例外注入）→ 次の巡回で再発行され、受け手で 1 回だけ計上される | `OrderExecutedRepublishPollingTests.発行に失敗した約定は再起動後の巡回で再発行され台帳に1回だけ載る`（経路の解決で投げる注入・別のホスト・リスク管理の台帳ハンドラ） |
| 発行成功後は再発行しない | `OrderExecutedRepublishPollingTests.発行に成功した約定は再発行しない`、`OrderExecutedPublishPendingTests.発行した後に印を倒せば次の巡回で再発行しない` |
| プロセス再起動を挟んでも未発行が残る（永続化） | `OrderExecutedPublishPendingTests.発行し終えていない約定は次の巡回で記録の値のまま1回だけ再発行される`（EF・新しい DbContext）、`…移行は既存行を発行済みとして扱う列の追加だけである` |
| 2 回届いても台帳は 1 回 | `OrderExecutedRepublishPollingTests.印を書けず同じ約定が2回届いても台帳は1回だけ数える` |
| RabbitMQ 断の後に約定が届く（親の試験の穴 5） | `OrderExecutionDurableOutboxE2ETests`（実コンテナ。Integration E2E） |
| 境界: 発行の後に記録が進めば印は残る／同じ巡回で進めば 1 件／S1 の取消の印を落とさない／読み取りの失敗で追跡を止めない | `OrderExecutedPublishPendingTests` の各試験 |

変異: (1) 再発行を積まない → 6 件赤（店・ポーラー・Worker）。(2) 印を発行の前に倒す → Worker の試験 1 件赤。(3) exchange を Durable にしない → E2E 赤（行が残る）。いずれも戻した。

## 影響範囲

- 発注執行: `Program.cs`（永続化の配線）、`WolverineDurableOutbox`（新規）、`IExecutedOrderStore`（既定実装つきの 2 メソッド）・EF / InMemory の店、`ExecutedOrderRow`・移行、
  `OrderFillPoller`（再発行）、`OrderFillPollingService`（印を倒す）、パッケージ `WolverineFx.Postgresql`。
- 試験: InMemory の `WebApplicationFactory` 18 箇所に `DisableAllWolverineMessagePersistence()`、既存 2 件をポーラーだけの試験として Worker の印を真似るよう直した（`BrokerStopLegFillTrackingTests`）。
- 他サービス・計画・helm の値は変えない（既定 true のため環境変数は要らない）。

## PoC での確認（配備前の手順）

1. **閉場後に配備する**（約定追跡と発注の経路が再起動する）。EF の移行が 1 本ある（列の追加だけ。起動時に自動適用）。Wolverine が起動時に `wolverine` スキーマを作る。
2. 起動の確認: `kubectl -n ai-stock-trading logs deploy/order-execution-service | grep -E "wolverine|Durab|Solo"` に例外が無いこと。
   `psql order_execution_svc -c '\dt wolverine.*'` で `wolverine_outgoing_envelopes` 等が在ること。
3. 平常時の確認: `select count(*) from wolverine.wolverine_outgoing_envelopes;` が 0 に近い（送った封筒は消える）。
   `select count(*) from executed_orders where "OrderExecutedPublishPending";` が 0（発行のたびに倒れる）。
4. 再発行が起きたとき: WRN `発行し終えていなかった約定を再発行します` が出る。台帳の数量が二重にならないこと（リスク管理の `trade_fills`）。
5. 戻し口: 起動しない・送信が止まる等の不具合なら `Messaging__DurableOutbox__Enabled=false` を発注執行の extraEnv に置いて再配備する（印による再発行は残る）。
