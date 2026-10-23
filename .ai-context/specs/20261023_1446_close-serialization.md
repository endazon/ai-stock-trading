---
title: 判断の決済と S1 の決済の「読む→送る」が直列化されず、同一建玉へ並行に送り得る（決済を送る全経路を (銘柄, 市場, 決済の方向) の DB advisory lock で直列にする）
type: spec
status: done
related_ids: [NFR, FR-10, FR-05, UC-02, UC-06, ADR-0050, ADR-0040, ADR-0003, IADR-0057, IADR-0129, IADR-0344, IADR-0355, IADR-0461, IADR-0466, IADR-0515, IADR-0529, IADR-0561, IADR-0573, IADR-0574, IADR-0575]
author: claude (Claude Code)
created: 2026-10-23
updated: 2026-10-23
---

# 仕様書: 同じ建玉を売る決済の「読む→送る→記録する」を直列にする（#1446）

> 本仕様書は実装着手前に作成した（着手 2026-10-23。AST `origin/develop` `3237e240`）。判断の記録は IADR-0575。

## 起点（トレーサビリティ）

- 起点: #1446（親 #1423 の M-2。監査は読み取りのみ）。起点 ID は `NFR`（親の監査の分類に合わせる）。統制は FR-10（手仕舞いと損切りは止めない・同じ株を二重に売らない）。
- 計画: ADR-0050 決定 1（判断の手仕舞いは処理中の決済を差し引いた残りだけを送る。損切りも判断の手仕舞いを理由に止まらない）・決定 3（逆指値・未約定の売りが売れる数量を押さえるかは実弾解禁前の確認）。
  決定 4 の表は「二重に売らない扱い（決定 1・IADR-0461、逆向きは IADR-0466）が防ぐ」と書く——**その前提は、両者が相手の記録を読めることである**。
- 前提（覆さない）: IADR-0461（差し引き）・IADR-0466（S1 は判断の手仕舞いを取り消してから送る）・IADR-0515（利用者・自動縮小の決済は差し引く）・IADR-0057（予約→発注→確定）・
  IADR-0561 決定 3-3（承認の受信は 1 通ずつ）・IADR-0573（耐久アウトボックス）・IADR-0574（閉場中は送らない）。
- 採番: develop の IADR の最大は 0574、開いている PR は #1159（CHANGELOG・IADR なし）。本件は IADR-0575（push 直前に再確認）。試験 ID は develop の最大 T-10-3003 の次から T-10-3004〜T-10-3010。

## 原因（コードで確認）

1. 判断の決済（`OrderExecutionAppService.ExecuteCoreAsync` の Close）は、建玉照会 → `CountInFlightClosesAsync`（`InFlightCloses.FindCandidates` は**発注の記録 `ExecutionRecord`** を読む）→ 予約 → 送信 → 記録の保存、の順。
2. S1（`SoftwareStopExecutor.TryCloseCoreAsync`）は、建玉照会 → `YieldDecisionClosesAsync`（`store.FindPendingCloses`＝記録を読む）→ `NetOwnerClosesAsync` → 予約 → 成行 → 記録の保存、の順。
3. **記録は送った後に書かれる。** 先発の送信中（予約の後・記録の前）に後発が読むと、後発は先発を見ずに全量を送る。予約（IADR-0057）は DecisionId ごとであり、異なる DecisionId の 2 本は止めない。
4. 承認の受信の直列化: **Wolverine 6.24.5 の RabbitMQ キューのリスナは既定で `Inline`・`ListenerCount=1`**（実測。下記）——同じキューの中では 1 通ずつだが、
   **キューが違えば並行に走る**（実測。2 キュー同時に最大 2 並行）。S1 は `StopLossTriggered` のキュー（別のリスナ）と常駐ガードの巡回（`ProtectiveStopGuardService`）から来る。
   よって **(a) 承認の受け口のリスナを `ProcessInline()`＋`Sequential()` で明示しても、S1 と判断の決済の競合は塞がらない**（既定がすでにそれと同じ挙動であり、それでも並行する）。

### 実測（Wolverine 6.24.5・実 RabbitMQ 3.13 コンテナ・2026-10-23）

AST の共通配線（`UseAiStockTradingRabbitMq`）と同じ形（`DisableConventionalLocalRouting`・`UseRabbitMq(...).AutoProvision().UseConventionalRouting(QueueNameForListener)`）で
使い捨てのホストを立て、リスナの設定を読み、500 ミリ秒かかるハンドラへ 8 通を流した（プローブはスクラッチパッド・コミットしない）。

| 観測 | 値 |
| --- | --- |
| `rabbitmq://queue/probe.Ping` の `Mode` | `Inline` |
| 同 `ListenerCount` / `MaxDegreeOfParallelism` / `PreFetchCount` | 1 / 5 / 100（Inline では MaxDOP は使われない） |
| 同じキューへ 8 通 | 最大並行 1（8 通完了） |
| 別々の 2 キュー（Ping・Pong）へ 4 通ずつ | **最大並行 2** |

## 母集合（規則 9: 決済を送る呼び出しで走査）

`grep -rn "\.Place[A-Za-z]*Async(" backend/Services/OrderExecutionService --include=*.cs`（試験・アダプタの内側を除く）。

| 呼び出し | 起動のされ方 | 読むもの | 本件の扱い |
| --- | --- | --- | --- |
| `OrderExecutionAppService` の決済（`:436-439`。指値／成行） | 承認（`OrderApproved`）の受信。判断・利用者の手仕舞い・自動縮小・閉場前の手仕舞い（IADR-0561）が同じ口 | 建玉照会・処理中の決済の記録 | **直列化する**（決定 1。照会から記録の保存まで） |
| `SoftwareStopExecutor.TryCloseCoreAsync` の成行（S1） | `StopLossTriggeredHandler`（別キュー）と常駐ガードの撃ち直し | 建玉照会・処理中の判断／利用者の決済の記録 | **直列化する**（決定 1。ガードの巡回の先頭の照会は使わず、直列化の後に照会し直す） |
| `OrderExecutionAppService.CloseUnprotectedPositionAsync`（保護喪失の成行手仕舞い） | 承認のエントリー・突合（`OrderReservationReconciler`→`ProtectAsync`）・約定追跡（`OrderFillPoller`→`ResolveUnconfirmedEntryCancelAsync`） | 自分のエントリーの約定だけ | **直列化する**（決定 3。後から来る判断の決済・S1 がこの記録を読むため） |
| `ProtectiveStopGuard.ReplaceOrCloseAsync` の成行の手仕舞い | 常駐ガードの巡回 | 巡回の先頭の照会（［2026-10-23 追記 / PR #1447 の再レビュー］直列化の後に照会し直す） | **直列化する**（決定 3。数量は min(先頭, 照会し直し)） |
| エントリー（Open）の送信 | 承認の受信 | — | 対象外（決済ではない。新規建ての審査の直列化は IADR-0529） |
| 保護逆指値（`PlaceStopOrderAsync`・`PlaceAlternativeStopOrderAsync`）の初回・張り直し | 承認・ガード | — | **対象外**: 保護レグは処理中の決済として差し引かない（ADR-0050 決定 1）。直列にしても差し引きの算術は変わらない。売れる数量を押さえるかは決定 3 の実弾の確認 |
| 突合（`OrderReservationReconciler`） | 常駐 | — | 自分では送らない（保護喪失の成行は上の行で直列化される） |
| 判断由来の決済の未約定取消（`DecisionCloseUnfilledCanceller`） | 常駐 | — | 送らない（取消だけ） |

入れ子の確認: 4 経路はどれも直列化を握ったまま他の経路を呼ばない（承認の決済は Open の経路〔`CloseUnprotectedPositionAsync`〕を通らない。S1 は取消だけを送る。ガードは S1 の撃ち直しを直列化の外で呼ぶ）。

文書（規則 9: `建玉不足`・`Inline`・`1 通ずつ`・`処理中の決済` で走査）:

- `docs/tests/FR-10_risk-controls-tests.md` → 本件の節を足す（trace ブロックに IADR-0575・本仕様書・#1446）。
- `.ai-context/adr/IADR-0561`（決定 3-3 の「承認の受信は 1 通ずつ」）→ **凍結記録なので書き換えない**。前提の実測と、それだけでは S1 を塞がないことは IADR-0575 に書く。
- `.ai-context/adr/IADR-0461`（残余「送信の結果が不明な処理中の決済は数えない」）→ 本件で事実は変わらない（残余のまま。IADR-0575 にも残余として書く）。
- 除外: `docs/operations/*` は運用手順であり、ロックの待ちは平常時に観測されない（取れなかったときだけ Error ログ）。運用者の操作は変わらない。

## 方式

IADR-0575 決定 1〜4。比較（(a) リスナの明示 / (b) DB advisory lock / プロセス内のロックだけ / 予約の行を差し引きに数える）は同 IADR。

- 決定 1: 承認の決済と S1 の決済は、`IClosePositionLock` を (銘柄, 市場, 決済の方向) の鍵で取り、照会・差し引き・取消・予約・送信・記録の保存までを握る。S1 はガードのスナップショットを捨てて直列化の後に照会し直す。
- 決定 2: 本番は `PostgresClosePositionLock`（専有 DB・ロックだけの接続とトランザクションで `pg_advisory_xact_lock`・`lock_timeout` 2 分・1 ミリ秒未満は拒否）。関係 DB でない DbContext（試験）はホストで 1 つの `InProcessClosePositionLock`（DI の singleton）。
  ［2026-10-23 追記 / PR #1447 の CI・レビュー］初版の static の共有（`Shared`）は、並列の試験のホストどうしを待ち合わせて backend-test の shard 4 を落としたため撤去した。省略時は組み立てごとのインスタンス。
- 決定 3: 保護喪失の成行手仕舞いとガードの成行の手仕舞いも、予約から記録の保存までを同じ鍵で握る。
- 決定 4: 取れなければ送らない（承認＝予約の前に例外で抜けて再配送・S1＝据え置き・保護喪失とガード＝確実に未発注として人手対応）。

## 受け入れ基準 → 試験

| 受け入れ基準 | 試験 |
| --- | --- |
| 異なる DecisionId の判断の決済と S1 の決済が同じ建玉へ並行に来ても、合計の決済数量が建玉を超えない（両方の順） | T-10-3004 |
| 修正を外すと赤になる（直列化が無ければ同じ場面で二重に売る） | T-10-3005（対照）・変異 CS1・CS2（下） |
| 鍵は同じ建玉だけを待たせ、値を変えない。取れなければ例外。待ちの上限は 1 ミリ秒以上 | T-10-3006 |
| 取れないときは送らない（承認・S1）。新規建ては直列化を使わない | T-10-3007 |
| 本番の組み立てで 3 つの組み立てが同じ直列化を受け取り、関係 DB なら advisory lock を選ぶ。InMemory のホストでも 3 つは同じセマフォで、別のホストとは共有しない | T-10-3008（変異 CS3） |
| ガードの成行は直列化を握って送り、取れなければ送らない | T-10-3009 |
| ガードは直列化を待つ間に他の経路が約定したら、照会し直した数量を超えて送らない（一部・全量・照会不能） | T-10-3011（変異 CS4: 照会し直しの数量を使わない → 赤） |
| 実 DB のロックが別の接続（Pod）をまたいで待たせ、上限・不達で取れない例外になり、後発が先発の書き込みを読める | T-10-3010（Testcontainers の結合試験） |
| 既存の試験の契約を壊さない | 既存の T-10-796（楽観並行の後ろ盾は別の直列化を渡して窓を作る）・T-10-1820・T-10-1770・T-10-358（S1 は直列化の後に照会し直す）を追随 |

### 変異（手元で実走）

| 変異 | 内容 | 結果 |
| --- | --- | --- |
| CS1 | `ExecuteAsync` の決済の直列化を外す（条件を偽に） | T-10-3004 の両方の順が赤 |
| CS2 | S1 が経路ごとに新しい直列化を取る | T-10-3004 の両方の順が赤 |
| CS3 | Program.cs のガードへ直列化を渡さない | T-10-3008 が赤 |

## PoC 配備前の手順（閉場後に行う）

1. 閉場後（米東 16:00 以降・保護逆指値ガードと S1 が新しい注文を送らない時間帯。IADR-0574）に配備する。スキーマの変更は無い（Migration なし）。
2. 配備の後、`order-execution` のログに「決済の直列化を取れなかった」（Error）が出ていないことを確かめる。出ていれば DB の接続を確かめる（決済は送られていない）。
3. 翌場で判断の決済と S1 の到達が重なった場合、ログの順が「一方の記録 → 他方の差し引き／取消」になっていることを確かめる（`決済の数量を、同じ建玉を売る処理中の決済の分だけ縮めました` か `判断の手仕舞いを取り消しました`）。
