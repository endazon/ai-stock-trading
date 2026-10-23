---
title: IADR-0573 発注執行の送信を耐久アウトボックス（PostgreSQL）に載せ、約定追跡は記録の更新と同じ書き込みで「発行待ち」の印を立てて発行の後に倒す（未発行の約定は次の巡回で再発行する）
type: impl-adr
status: Accepted
related_ids: [NFR, FR-05, FR-10, ADR-0013, IADR-0113, IADR-0117, IADR-0129, IADR-0149, IADR-0371, IADR-0389, IADR-0406, IADR-0481, IADR-0536, IADR-0560, IADR-0569]
author: claude (Claude Code)
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0013_messaging-follow-wolverine-kafka.md（基盤に追随する。platform ADR-0027「イベント発行の原子性は Wolverine の EF Core + PostgreSQL の Outbox を標準とする」）
related_specs:
  - ../specs/20261023_1442_order-executed-republish.md
---

# IADR-0573: 約定の発行を失わない（耐久アウトボックス ＋ 発行待ちの印）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-23
- 決定者: Claude Code（#1442 の求めに沿う実装の形。計画の規則は変えない）

## 起点・関連

- 起点: #1442（親 #1423 の M-4「約定の記録を先に書き、`OrderExecuted` の発行は後——発行が失敗すると、その約定は台帳へ二度と届かない」）。
- 計画: ADR-0013（メッセージングは基盤に追随。基盤の platform ADR-0027 は発行の原子性に Wolverine の PostgreSQL の Outbox を標準とする）。
  計画に `OrderExecuted` の配送保証の数値・規則は無い。統制の入力（FR-10 の台帳）が約定を知ることが前提である。
- 前提（覆さない）: IADR-0113（約定追跡）、IADR-0129（トポロジ。exchange は型名の fan-out・共通ヘルパ）、IADR-0117（台帳の終端は約定の記録の後）、
  IADR-0149 決定1（発注先は照会したアダプタ）、IADR-0560（S1 の取消の印）、IADR-0481 決定3（打ち切りの印は発行の後）、IADR-0371（突合の 1 件ずつの発行）。

## コンテキスト

1. 約定追跡（`OrderFillPoller`）は `UpdateOutcome` で記録を更新した**後**に `OrderExecuted` を結果に積み、Worker（`OrderFillPollingService`）が
   巡回の末尾で発行する。次の巡回は「変化なし＝書かない・発行しない」ため、間でプロセスが落ちる・発行が投げると、その約定は二度と発行されない。
2. 🔴 **実測（Wolverine 6.24.5・RabbitMQ 3.13。2026-10-23・作業仕様書の実験）**: 本ユニットの RabbitMQ の送信先は既定で `Inline` であり、
   **ブローカーが落ちている間も `PublishAsync` は成功で返る**。送信の代理は裏で 3 回試してから `Discarding message ... after 3 attempts` で**黙って捨て**、
   閂（`Latched`）も立てない。復旧後にも届かない。つまり「発行の後に印を倒す」だけでは RabbitMQ 断を検知できず、M-4 の主な様式（断）は塞がらない。
3. 同じ実験で、Wolverine の耐久アウトボックス（`PersistMessagesWithPostgresql` ＋ `UseDurableOutboxOnAllSendingEndpoints`）では、断の間の発行が
   PostgreSQL に保存され、**復旧後に届いた**。プロセスを落として（`Environment.FailFast`）再起動しても、**再起動の後に届いた**（既定の Balanced では
   落ちたノードの封筒の引き取りに 1〜2 分、Solo では起動の直後）。
4. 🔴 **実測と実装の読み（`IPolicies.AllSenders` の逆コンパイル）**: `UseDurableOutboxOnAllSendingEndpoints` は、送信先の endpoint が**購読を持って**
   compile されるときだけ効く。規約ルーティングが起動時に購読を先に登録するのは**自分が処理する型だけ**である（GH-2588 の修正の射程）。
   発注執行は `OrderExecuted` を処理しないので、再起動の直後に残った封筒を回収する経路（`RecoverOutgoingMessagesCommand`）が送信先を購読なしで作ると、
   その送信先は `Inline` で compile され、**封筒は送られるが保存先から消えない**（再起動のたびに送り直す）うえ、**以後そのプロセスでその型の発行は保存されない**。
   実コンテナの試験で再現し（送信 1 件・行が残る）、exchange を Durable にする方針で解消した（行が消え、送信先は Durable）。
5. 受け手（母集合は作業仕様書）: リスク管理の台帳（`AppendFill` は OrderId 単位の累積数量の単調 upsert、`MarkTerminal` は初回だけ true）、
   注文アクティビティ（絶対値の代入）、GFV の計数（OrderId で先着優先）、Stage 1 の観測（DecisionId で先着優先）は**同じ約定を 2 回受けても 1 回分**。
   監査（`envelope.Id` を Id にする）と通知（重複排除なし）は、**別の封筒**で 2 回届けば 2 件になる。市場監視は `OrderExecuted` を購読しない（台帳の建玉を読む）。

## 決定

### 決定 1: 発注執行の送信を耐久アウトボックスに載せる

- `Program.cs` は共通の RabbitMQ 配線の後に `UseOrderExecutionDurableOutbox(connStr)` を呼ぶ（`Infrastructure/Persistence/WolverineDurableOutbox.cs`）。
  中身は (a) `PersistMessagesWithPostgresql(connStr, "wolverine")`（発注執行の専有 DB の `wolverine` スキーマ。EF の表と分ける。起動時に Wolverine が作る）、
  (b) `UseDurableOutboxOnAllSendingEndpoints()`、(c) **RabbitMQ の exchange（Role=Application）を購読の有無に依らず `EndpointMode.Durable` にする endpoint 方針**
  （コンテキスト 4）、(d) `DurabilityMode.Solo`（replicas: 1。再起動の直後に残った封筒を送る）。
- 構成 `Messaging:DurableOutbox:Enabled`（既定 true）。false は**配備の不具合のときの戻し口**であり常用しない。
- パッケージ `WolverineFx.Postgresql` 6.24.5（MIT。WolverineFx 系と同値）を**発注執行だけ**が参照する。共通ヘルパ（shim）には入れない
  ——他サービスへ依存を広げず、トポロジ（IADR-0129 決定 4）ではなく発注執行の永続化の選択として置く。
- InMemory の試験（`WebApplicationFactory`）は `DisableAllWolverineMessagePersistence()` で DB に触れない。実基盤 E2E は実 PostgreSQL で耐久アウトボックスを通る。

### 決定 2: 約定追跡は「発行待ち」の印を記録の更新と同じ書き込みで立て、発行の後に倒す

- `executed_orders.OrderExecutedPublishPending`（boolean NOT NULL DEFAULT false。移行 `AddExecutedOrderPublishPending`。**既存行は false＝発行済み**で、過去の約定を撃ち直さない）。
- `UpdateOutcome`（約定追跡だけが呼ぶ）が同じ `SaveChanges` で true にする。Worker は `PublishAsync` が返った後に
  `MarkOrderExecutedPublished(orderId, status, filled)` で倒す——**記録の今の状態・約定数が発行したものと同じときだけ**（間に記録が進んでいれば印を残し、進んだ状態を次に出す）。
- 巡回は、印が立ったままの記録（状態を問わない。終端も入る）を `FindOrderExecutedPublishPending` で古い順に上限まで引き、**記録の値から** `OrderExecuted` を組み直して
  この巡回の発行の**先頭**に積む（ブローカーへは照会しない。この巡回で同じ注文を更新していれば新しい値の 1 件だけ）。発注先は本ポーラーのアダプタ（IADR-0149 決定1）、
  取消の主体は更新のときと同じ読み方（IADR-0560）。
- **再発行（結果の先頭の `Republished` 件）の発行の失敗は 1 件ごとに隔離する**: その記録は印を残したまま WRN（`発行し終えていなかった約定の再発行に失敗しました`）を出し、
  巡回の残り（この巡回の新しい約定・S1 の再武装の通知・取消の確認待ちの後始末・打ち切り・照会し直し）の発行は続ける。恒常的に投げる 1 件で資金に関わる事象が毎巡回止まるのを防ぐ。
  再発行には同じ巡回で後に続く事象が無い（後始末は終端化を観測したこの巡回の新しい約定にだけ付く）ので、順序の制約（IADR-0569 決定4）は崩れない。
- この巡回の新しい約定の発行が投げたときは従来どおり巡回を終える（後始末を `OrderExecuted` より先に出さない）。その約定は印が残るので、次の巡回からは再発行として隔離される。
  印を倒す書き込みの失敗は WRN にして巡回を止めない（次の巡回で再発行。受け手は冪等）。
- 印の問い合わせの上限（巡回の `batchSize`）は、この巡回で更新した注文も枠を使う（後で除く）。上限の近くでは再発行が 1 巡回ぶん遅れ得るが、印は残るので失われない。

### 決定 3: 滞留を計器と警報で見る

- 巡回の末尾（発行・印倒し・照会し直し・取消の後）で `CountOrderExecutedPublishPending` を数え、ゲージ `ast.order.executed_publish_pending` に記録する（平常は 0）。
  数えられなかった巡回は記録しない（0 で埋めない）。計器が登録されていない構成では何もしない。
- 警報 `AstOrderExecutedPublishPending`（`max(ast_order_executed_publish_pending) > 0` が 10 分続く・warning）を既存の置き場（`deploy/observability/alerts/`。IADR-0374 決定 4）に足し、
  ダッシュボード（業務）にパネルを 1 枚足した。閾値は実測を要しない（平常の期待値が 0 件）。
- 決定 1 と組み合わせると、`PublishAsync` が返った時点で封筒は DB にある。印が閉じるのは「記録の更新 → 封筒の保存」の間の窓（プロセスの落下・保存の失敗）である。

## 検討した選択肢

| 案 | ブローカー断 | 記録→発行の間の落下 | 発行の後・送信の前の落下 | 規模・リスク | 採否 |
| --- | --- | --- | --- | --- | --- |
| A. 発行待ちの印だけ（推奨案。市場監視の到達の記憶・打ち切りの印と同じ形） | ❌ 検知できない（`PublishAsync` が成功で返る。実測） | ✅ | ❌（Inline の送信はメモリの中） | 小 | 単独では不採用 |
| B. 耐久アウトボックスだけ | ✅（実測） | ❌（記録の書き込みと封筒の保存が別） | ✅（実測） | 中（パッケージ・`wolverine` スキーマ） | 単独では不採用 |
| **C. A ＋ B** | ✅ | ✅ | ✅ | 中 | **採用** |
| D. EF Core の transactional outbox（`WolverineFx.EntityFrameworkCore`。記録と封筒を 1 トランザクション） | ✅ | ✅ | ✅ | 大（ストアの書き込みを全部 Wolverine の文脈へ寄せる。約定追跡は Worker で `IMessageBus` を持たない） | 不採用（効果は C と同じで改修が大きい） |
| E. 送信の健康（閂・接続）を見て発行を控える | ❌（閂が立たない。実測） | — | — | 小 | 不採用 |
| F. 送信先を `SendInline` に固定して例外を待つ | ❌（Inline が既定で、それでも投げない。実測） | — | — | 小 | 不採用 |

- Solo と Balanced: Balanced は落ちたノードの封筒を引き取るまで 1〜2 分かかった（実測）。発注執行は replicas: 1 なので Solo を採る。入れ替えで新旧の Pod が重なる間は
  同じ封筒を両方が送り得る（同じ封筒 Id。受け手は冪等・監査は Id で 1 件）。
- 方針 (c) を shim の共通配線に置く案は採らない（全サービスの送信を変え、パッケージも全サービスへ広がる。必要が出たら別 IADR）。

## 結果

- RabbitMQ 断・プロセスの落下・発行の例外のいずれでも、約定追跡が記録した約定は台帳へ届く（少なくとも 1 回）。台帳・注文アクティビティ・GFV・Stage 1 は 1 回分しか数えない。
- 発注執行の他の発行（発注時の `OrderExecuted`・保護レグ・取消・監査向けのイベント）もすべて耐久アウトボックスを通る（ブローカー断で失われなくなる）。
- 試験: `OrderExecutedPublishPendingTests`（店・ポーラー。EF と InMemory）、`OrderExecutedRepublishPollingTests`（Worker。発行の失敗の注入 → 別のホストで再発行 → 台帳に 1 回／
  成功後は再発行しない／印を書けず 2 回届いても台帳は 1 回）、`OrderExecutionDurableOutboxE2ETests`（実 PostgreSQL・実 RabbitMQ。断の間の発行 → 停止 → 復旧 → 再起動で 1 回届き、
  封筒が消え、送信先は Durable、もう一度起こしても送り直さない。方針 (c) を外すと赤）。`OrderExecutedRepublishPollingTests` の
  `再発行が投げ続けても巡回の残りの発行は止まらず件数が計器に残る`（隔離を外すと赤）・`発行し終えた巡回の末尾では滞留の件数は0である`。

## 残余リスク

1. **監査と通知の重複**: 同じ約定が 2 回届く経路——(a) 発行は成功したが印を倒す書き込みが失敗した（別の封筒）、(b) 配備の入れ替えで新旧の Pod が重なり、
   Solo の新 Pod が旧 Pod の送信中の封筒も送る（同じ封筒 Id。監査は 1 行・通知は 2 通になり得る）——では、監査台帳に `OrderExecuted` が 2 行（(a) のとき）、Discord の「取引実行」が 2 通になる。
   台帳の統制値は変わらない。頻度は「DB への書き込みが直前に成功し直後に失敗する」ときだけ。冪等キーを状態から導く変更は監査の意味を変えるため本件では採らない。
2. **突合（`OrderReservationReconciler`）の窓**: 予約の終端化と発行の間の落下は本件の印の対象外である（IADR-0371 の残余のまま。ブローカー断と発行の後の落下は決定 1 で塞がった）。
   発注時の `OrderExecuted` は `OrderApproved` の再配送で再発行される（IADR-0057 相1）。
3. **実プロセスの落下の試験は実験だけ**: 同じテストプロセスの中でホストを停止せずに破棄すると、旧ホストの送信の代理が生き残って送り、封筒を消せない（実測）。E2E は停止（SIGTERM と同じ）で
   畳む。落下（`FailFast`）は単独の実験で確かめた（作業仕様書）。
4. **`wolverine` スキーマの作成権限**: 起動時に Wolverine がスキーマと表を作る。DB の利用者に作成権限が無ければ発注執行が起動しない。戻し口は決定 1 の構成（false）。
5. **経費の記録（段 2）の前提に再発行が加わる**: 再発行した約定も Worker の経費の記録（`RecordTradeExpensesAsync`）をもう一度通る。段 1 の既定では経費のイベントは 0 件で実害は無い。
   段 2（実費の供給）を有効にする前に必要な `TradeExpense.SourceId` の重複排除（IADR-0300）は、**再発行の分も対象**である（`OrderFillPollingService` の注記に書いた）。
6. **Wolverine の版上げ**: コンテキスト 2・4 は 6.24.5 の挙動である。版を上げるときは E2E（方針 (c) を外すと赤）で確かめ直す。
