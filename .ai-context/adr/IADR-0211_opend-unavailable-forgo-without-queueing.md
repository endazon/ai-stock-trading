---
title: IADR-0211 OpenD へ確実に届いていない発注は「見送り」とし、キューイングも Rejected への丸め込みもしない
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-10, FR-11, UC-06, ADR-0002, ADR-0024, IADR-0057, IADR-0074, IADR-0092, IADR-0117, IADR-0210, IADR-0362]
author: claude (Claude Code)
created: 2026-08-28
updated: 2026-09-19
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

   ［2026-09-19 追記 / [#848](https://github.com/endazon/ai-stock-trading/issues/848)］
   🔴 **上の列挙の 3 番目（送信後の分類不能な失敗）は本追記で外れた。**
   当時これを `Rejected` に含めてよかったのは、`Rejected` が「台帳に約定を載せない」以上の意味を
   持たなかったからである。**IADR-0117（2026-09-19 追記）が `Rejected` を在庫解放の引き金にした時点で、
   この分類は fail-safe から fail-open へ反転した** —— 送信後の失敗は**届いたか不明**であり、
   在庫の押さえを解く根拠にならない（決済では二重決済でショート化し、エントリーでは
   「建玉が生じていない」という仮定になって無保護の建玉を残す）。
   送信後に結果を確認できなかった失敗は、本 ADR 決定 1 が定めた「対象外＝予約とリコンサイルが守る」を
   **型として持つ** `BrokerDispatchIndeterminateException`（本例外と対になる新しい契約）で伝播させる。
   **現在 `Rejected` に限定されるのは 2 事象**（不正注文の事前弾き・ブローカー応答の拒否状態）である。
   決定 1・3・4・5 は変更しない（**確実に未発注**の見送りと理由列挙はそのまま）。
   詳細は IADR-0117 の改定 6。
   🔴 **「リコンサイルが守る」は条件つきである**（PR #851 の 3 巡目監査）。予約が**二重発注を防ぐ**ことは
   リコンサイルの有無に依らず成立するが、**滞留した予約の解消**は自動リコンサイルが有効なときだけ自動で進む。
   既定は無効（`Reconciliation:Enabled=false`・`UseBrokerProbe=false`）で、いまの配備では人が解決する（有効化は #856）。
   🔴 ［2026-09-19 追記 / [#856](https://github.com/endazon/ai-stock-trading/issues/856)・IADR-0362］
   **「いまの配備では人が解決する」は偽になった。** 配備（`deploy/helm/ai-stock-trading/values.yaml`）が
   `Reconciliation__Enabled` / `__UseBrokerProbe` を有効にした（アプリ既定は `false` のままで、そちらは真）。
   **ただし解放（`NotPlaced`）は新設の門 `Reconciliation__ReleaseOnNotPlaced=false` で閉じている**ため、
   自動で片付くのは「発注済みと確定できた」側だけである。**本 ADR の「確実に未発注だけが解放してよい」という
   規律は、突合の側でも同じ形で守られている**——「未発注」の根拠が remark 突合という未検証の前提に依るあいだは、
   それを「確実に未発注」と呼ばない。門を開けるのは実機で偽陽性が無いことを示した後（#856）。
   また、本例外と対になる `BrokerDispatchIndeterminateException` を**一括 catch で受ける呼び出し側**は
   「確実に未発注」と取り違えてはならない —— 予約を解放してよいのは本例外（`BrokerUnavailableException`）だけである
   （IADR-0117 の改定 7。保護逆指値ガードの成行手仕舞いがこれを取り違え、巡回ごとに撃ち直していた）。
   🔴 **［2026-09-19 追記 / #848・B5］上の「現在 2 事象」の 2 つ目（ブローカー応答の拒否状態）は、
   発注応答では `retType == -1`（`Failed`）に限る**（PR #851 の 4 巡目監査）。当初の実装は `retType != 0` を
   丸ごと「ブローカー応答の拒否」と読んでいたが、`-100`（TimeOut）/ `-200`（DisConnect）/ `-400`（Unknown）/
   `-500`（Invalid）は**送信後に返事を読めなかった**ことを SDK が応答の形に包んだ値であり
   （`-100` と `-500` は SDK がクライアント側で合成する。`-100` は 12 秒の打ち切りで、既定の返信待ち 15 秒より先に来る）、
   決定 1 の言う「発注送信後の失敗＝届いたか不明」そのものである。これらは `BrokerDispatchIndeterminateException` で
   伝播させる。稼働環境で実測した拒否 2 件（#844 の価格精度・#809 の `Paper trading does not support Stop order`）は
   どちらも `retType=-1` であり `Rejected` のままである。詳細は IADR-0117 の改定 8。
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
