---
title: IADR-0211 OpenD へ確実に届いていない発注は「見送り」とし、キューイングも Rejected への丸め込みもしない
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-10, ADR-0002, ADR-0024, IADR-0057, IADR-0092, IADR-0210]
author: claude (Claude Code)
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-05)
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md (OpenD 常駐・SPOF・INDEX 決定 33)
  - planning:projects/ai-stock-trading/07_adr/ADR-0024_opend-unattended-restart-conditional.md
---

# IADR-0211: OpenD へ確実に届いていない発注は「見送り」とし、キューイングも Rejected への丸め込みもしない

- 状態: Accepted
- 日付: 2026-08-28
- 決定者: claude（起票 #331。利用者レビューは PR で受ける）

## 起点・関連

- 関連する計画書 ID: FR-05（「拒否」＝**証券会社が発注を受理しなかった状態**。planning#60 裁定）、
  ADR-0002/ADR-0024（OpenD 常駐・SPOF。再起動中は発注不可＝INDEX 決定 33）
- 対象 Issue: #331（スコープ 3「OpenD 切断時はキューイングせず見送り＋通知」）
- 関連する実装仕様書: [20260828_331_order-execution-stop-loss-and-rejection](../specs/20260828_331_order-execution-stop-loss-and-rejection.md)
- 関連 IADR: [IADR-0057](IADR-0057_order-dispatch-idempotency.md)（発注 3 相・予約）、
  [IADR-0092](IADR-0092_reservation-broker-probe-moomoo.md)（不明の据え置き）、[IADR-0210](IADR-0210_broker-side-stop-loss-unification.md)

## コンテキストと課題

現行の `MoomooBrokerAdapter` は OpenD 不達・SDK 例外をすべて終端 `Rejected` へ丸める。これは 2 つの意味で
issue #331 の要求と食い違う。

1. FR-05 の「拒否」は**証券会社が受理しなかった状態**である。OpenD が落ちていて**注文が証券会社に届いてすら
   いない**事象を混ぜると、「拒否」の別集計（事前拒否との区別）が接続障害で汚染される。
2. OpenD は SPOF であり（ADR-0002。再起動中は発注不可）、切断時の裁定済み挙動は「**キューイングせず見送り＋通知**」
   である。丸め込みは見送り自体は満たすが、通知が「約定 Warning」に紛れ、監査上も拒否として残る。

一方、Wolverine の共通再試行に例外を投げて委ねると、メッセージが再試行キュー・error キューに滞留する
——それは「キューイング」であり、数分後の再送は**判断時点の価格から乖離した注文の遅延執行**になる。

## 検討した選択肢

1. `OrderStatus` へ新メンバ（`Unplaced` 等）を追加し注文状態として表す — FR-05 の状態集合
   （受付・約定・失注・取消・拒否）は fixed であり、**発注されていないものは注文状態を持たない**のが正しい。
   enum 序数は HTTP 経路の互換制約もある（IADR-0134）。**却下**。
2. 例外をそのまま伝播し Wolverine の再試行 → error キューに委ねる — 再試行＝時間差の自動再発注であり
   「キューイングせず見送り」に反する。**却下**。
3. **確実に未発注と言い切れる失敗だけを専用例外 `BrokerUnavailableException` に分類し、発注執行が予約を解放して
   「見送り」イベントで正常終了する**（採用）。

## 決定

1. **`BrokerUnavailableException`（Shared.Contracts.Ports）を新設する。** 送出してよいのは
   「**注文がブローカーへ届き得ない段階**」の失敗だけである。moomoo では接続確立（`EnsureConnectedAsync`＝
   InitConnect 失敗・接続応答タイムアウト・口座列挙失敗）に限る。**発注送信後の失敗（応答タイムアウト等）は
   対象外**——届いたか不明であり、従来どおり予約（IADR-0057）とリコンサイル（IADR-0092）が守る。
2. **`MoomooBrokerAdapter` は同例外を `Rejected` へ丸めず伝播する。** `Rejected` は「証券会社が受理しなかった」
   事象（不正注文の事前弾き・ブローカー応答の拒否状態・送信後の分類不能な失敗）に限定される。
3. **発注執行は同例外を捕捉し、(a) 予約を解放（確実に未発注のため二重発注の窓は無い）、(b) `ExecutionRecord`
   を残さず（注文は存在しない）、(c) 新イベント `OrderDispatchForgone`（DecisionId・Intent・理由・時刻）を
   発行して正常終了する。** ハンドラが例外を投げないため Wolverine の再試行・error キュー滞留は発生しない。
   **再発注は次の取引判断からのみ**（見送った注文の自動リプレイ経路を作らない）。
4. **見送りの理由は列挙 `OrderDispatchForgoneReason` で持つ**: `BrokerUnavailable`（OpenD 切断）／
   `StopLossPriceMissing`・`StopOrderUnsupported`（IADR-0210 決定 1 の fail-closed。逆指値を張れない Open は
   建玉を作らない）。いずれも**発注前**に確定する見送りである。
5. **通知（Warning）と監査記録を必ず伴う。** 通知本文に「発注は再試行されない（見送り）」を明記する。
   監査台帳の EventType は `OrderDispatchForgone` であり、`OrderRejected`（事前拒否）・`OrderExecuted`
   （Status=Rejected＝証券会社拒否）と**別集計**になる。

## 理由

- 「見送り」を注文状態ではなくイベントで表すのは、FR-05 の状態集合が**ブローカーに存在する注文**の
  ライフサイクルだからである。存在しない注文に状態を与えると、注文数・拒否数の集計が実態とずれる。
- 予約を解放してよいのは接続確立前の失敗に限られる——この限定こそが本決定の中核であり、それ以外の失敗を
  同例外で送出することを禁じる（送信後の失敗に使うと、届いていた注文の予約を解放し再配送で二重発注する）。
- Warning（Critical でない）とするのは、見送り時点で建玉は増えておらずリスクが発生していないため。
  実際に止まる事象（損切り到達・保護喪失）の Critical が埋もれない重み付け（IADR-0196 と同じ判断）。

## 残余リスク

- 見送りの多発（OpenD 長期停止）は Warning の並びでしか見えない。稼働観測（IADR-0150）が沈黙で Stage 1 を
  止めるため統制上は安全側だが、運用者への集約通知（N 回連続で昇格）は将来課題として残す。
- `EnsureConnectedAsync` の失敗分類は moomoo SDK の挙動（InitConnect が false を返す条件）に依存する。
  実機の切断パターン（`OnDisconnect` 後の再接続失敗など）は #342 の PoC で確認する。
