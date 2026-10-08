---
title: IADR-0515 発注の記録に承認の出どころを持たせ、S1 の決済の前に取り消すのは判断の手仕舞いに限る（利用者の手仕舞い・維持率割れの自動縮小は取り消さず差し引く。出どころが分からなければ取り消す側）
type: impl-adr
status: Accepted
related_ids: [FR-10, UC-06, UC-02, ADR-0050, ADR-0003, IADR-0466, IADR-0461, IADR-0344, IADR-0357, IADR-0495, IADR-0486, IADR-0211, IADR-0057, IADR-0092, IADR-0389, IADR-0362]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0050_decision-close-nets-in-flight-closes-and-stop-line-exit-only-without-mechanical-stop.md
related_specs:
  - ../specs/20261008_1222_order-approval-origin.md
  - ../specs/20261008_1253_reservation-approval-origin.md
  - ../specs/20261008_1262_reservation-position-effect.md
---

# IADR-0515: 発注の記録に承認の出どころを持たせ、S1 の決済の前に取り消すのは判断の手仕舞いに限る（#1222）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-08
- 決定者: Claude Code（実装）。計画 ADR-0050 決定 1（判断の手仕舞いは処理中の決済〔**利用者の手仕舞いを含む**〕を差し引いた残りだけを送る。
  損切り〔S1 の決済〕も判断の手仕舞いが処理中であることを理由に止まらない）と FR-10（手仕舞いと損切りは止めない）の範囲の実装判断である。計画の裁定は要らない。

## 起点・関連

- 関連する計画書 ID: FR-10・UC-06（利用者の手仕舞い・維持率割れの自動縮小）・UC-02・ADR-0050 決定 1・ADR-0003
- 起票: [#1222](https://github.com/endazon/ai-stock-trading/issues/1222)（起票元 #1204・第 4 回全体監査 B-18「ADR-0050 決定 1 の残余」）
- 作業仕様書: [`.ai-context/specs/20261008_1222_order-approval-origin.md`](../specs/20261008_1222_order-approval-origin.md)（現況・窓の表・母集合）
- 前提:
  - [IADR-0466](IADR-0466_s1-close-cancels-in-flight-decision-close.md): S1 の決済の前に、同じ建玉を売る判断の手仕舞いを取り消す。残余「利用者の成行の手仕舞い・
    維持率割れの自動縮小は見分けられず取り消す」を本 IADR が扱う（同 IADR 決定 2 の 3 つ目の小項目を改める）。
  - [IADR-0461](IADR-0461_close-quantity-subtracts-in-flight-closes.md): 判断の決済は、証券会社が生きていると答えた処理中の決済の残りを引いて送る（S0/S3 の保護レグは引かない）。
  - [IADR-0495](IADR-0495_min-entry-notional-and-decision-exit-same-day-reentry.md) 決定 4: `OrderApproved.FromTradeDecision`（取引台帳の承認行の由来）。
  - [IADR-0486](IADR-0486_stop-width-floor-atr14-flag-and-floor-marker-on-order-intent.md) 決定 6: 発注の記録へ承認の印を写す前例（`StopFloorSource`・列の追加だけ）。

## コンテキストと課題

S1 の決済の前の取消（IADR-0466）は、発注の記録に出どころが無いため、利用者の成行の手仕舞い（UC-06）と維持率割れの自動縮小の決済も判断の手仕舞いと
見分けられず取り消していた（出し直さない）。S1 は自分の記録の残保護数量しか売らないので、同じ銘柄に S1 の守っていない株数があれば、利用者・自動縮小が
売るはずだった分が消える。利用者・自動縮小の DecisionId は承認の側で無作為に採る（`Guid.NewGuid()`）ため、DecisionId の導出では見分けられない。

`OrderApproved.FromTradeDecision`（#1176）は既定 false が「判断ではない」と「旧いメッセージ」を区別できず、利用者・自動縮小を**積極的に**名指しできない。

## 決定

1. **出どころの契約**: `Shared.Contracts` に `OrderApprovalOrigin`（`Unknown = 0` / `TradeDecision = 1` / `OwnerClose = 2` / `MaintenanceMarginReduction = 3`。
   序数固定・追加は末尾）を足し、`OrderApproved.Origin`（任意・既定 `Unknown`）で運ぶ。書き手 3 つがそれぞれ明示する
   （発注前審査＝`TradeDecision`・`PositionCloseService`＝`OwnerClose`・`MaintenanceMarginReductionService`＝`MaintenanceMarginReduction`）。
   旧いメッセージ・書き手の渡し忘れは `Unknown`。発注執行の突合が組み直す新規建ての承認は `Unknown` のまま（Open なので S1 の取消の対象外）。
2. **記録**: `ExecutionRecord.ApprovalOrigin`（null＝分からない）と `executed_orders.ApprovalOrigin`（integer NULL）。マイグレーション
   `20261008014627_AddExecutedOrderApprovalOrigin` は**列の追加だけ**で、🔴 **既存行は null のまま埋めない**（出どころを推測で埋めると利用者の手仕舞いと取り違える）。
   書くのは承認の経路の発注（`OrderExecutionAppService` の相 4）だけで、`Unknown` は null で書く。保護の機構の記録・S1 の決済・突合で確定した記録は null。
   約定の更新（`UpdateOutcome`）は列に触れない。
3. **S1 の取消（IADR-0466 決定 2 の改訂）**: 出どころが `OwnerClose` / `MaintenanceMarginReduction` で、記録の時刻（`ExecutedAt`。約定追跡〔`OrderFillPoller`〕は非終端のあいだこの値を進めない＝発注の時刻）から
   `NettedCloseGrace`（2 分＝常駐ガードの巡回 4 回分）以内の記録は**取り消さない**。それ以外は従来どおり保護の機構の見分けを経て取り消す。
   - 🔴 **出どころが分からない（null・`Unknown`）は判断の手仕舞いと同じく取り消す側へ倒す**（受け入れ基準 4）。是正前と同じ挙動で、S1 は送られる（損切りを止めない）。
     反対へ倒す（分からないものを差し引く）と、約定しない判断の手仕舞い（指値）が板に残るあいだ S1 が据え置かれ続け、ADR-0050 決定 1 の後段に反する。
   - 🔴 **猶予を過ぎても処理中の利用者・自動縮小の決済は取り消してから送る。** 自動縮小は指値（参照価格）で、利用者も指値を選べる（IADR-0357）。
     約定しない指値を差し引き続けると S1 が据え置かれ続け、損切りが実質止まる。猶予の後は IADR-0466 の挙動（取り消して S1 が送る）へ戻す。
   - 保護の機構の見分け（IADR-0466 決定 2 の保護記録・新規建ての記録からの導出）は変えない（受け入れ基準 5）。
4. **S1 の差し引き（ADR-0050 決定 1 の物差しを S1 の側へ）**: 取消の段の後（取消でやり直す 1 回でも）、同じ銘柄・市場・決済の方向の非終端の Close の記録のうち
   取り消さなかった利用者・自動縮小の決済が**証券会社に生きている**なら、
   - 🔴 **差し引きの段は猶予を見ない。** 取消の段が取り消さなかった生きている利用者・自動縮小の決済は、猶予の内外を問わずすべて引く（不変条件:
     取り消さなかった生きている利用者・自動縮小の決済を引かずに送らない）。2 つの段がそれぞれ時計を読んで猶予を判定すると、1 回の試行の中で猶予を
     またいだ決済が「取消の段では猶予の内（取り消さない）・差し引きの段では猶予の外（引かない）」になり、全量を送る（PR #1252 の独立監査 R1。
     押さえない証券会社では二重に売る）。猶予を過ぎて取り消した決済は終端なので数えない。
   - 送る数量＝min(残保護数量, 決済方向の建玉 − 処理中の決済の残りの合計)。処理中の決済＝生きている非終端の Close の記録から**ブローカー側の保護逆指値レグ
     （S0/S3）を除いた**もの（利用者・自動縮小・S1 の決済〔自分の前の試行・他の記録〕・保護喪失の成行手仕舞い）。残り＝記録の数量 − 約定（記録と照会の大きい方）。
     数え方は判断側の IADR-0461 と同じ。
   - **0 以下なら据え置く**（送らない・失敗に数えない・待ち時間を置かない・到達の記録は残す）。利用者・自動縮小が売り切った建玉の減少は外部要因の観測
     （IADR-0344 追記(7)）が割り当て、S1 は重ねて売らない。猶予を過ぎれば決定 3 で取り消して送る。
   - 縮めて送るときは Warning ログに残す（黙って数量を変えない）。
   - 🔴 照会の順は「記録 → 注文照会 → **建玉照会（新しく照会し直す）**」。間に約定が進むと差し引き過ぎる（少なく売り、残りは次の巡回で売る）側に倒れる。
     逆の順・ガードの巡回の先頭のスナップショットの流用は差し引き不足（二重に売り得る）になるので採らない（作業仕様書の窓の表）。
   - 生きている利用者・自動縮小の決済が無い（無い・照会 null・例外・終端）なら何もしない（従来の挙動。注文照会・建玉照会を増やさない）。
     確かめられないものを差し引かないのは IADR-0461 決定 4 と同じ「是正前と同じ側」（拒否され得るが撃ち直しは続く）。
   - 記録の読み出しの失敗は差し引かずに送る（是正前と同じ側）。差し引くための建玉照会が不明なら据え置く（既存の「建玉不明は据え置き」と同じ）。
5. **窓の形は前の端だけ**（IADR-0466 決定 6 と同じ。作業仕様書の規則 11 の表）。増える側（読んだ後に利用者の決済が載る）は 1 回拒否され、撃ち直しで差し引いて通る。

## 採らなかった案

- **承認の側で DecisionId を決定的に導出する**（issue の案の一つ）: 利用者の各要求は独立した注文で、導出の種（何から導くか）が無い。再送の重複排除は既に予約が担う
  （IADR-0057）。導出の規則をリスク管理と発注執行の両方に持つと、片方の変更で黙って見分けが壊れる。
- **`FromTradeDecision` で見分ける**: 既定 false が旧いメッセージと区別できない（決定 1）。false を利用者と読むと、旧版のリスク管理が出した判断の手仕舞いを
  取り消さず S1 が据え置かれ得る（損切りを止める側）。
- **予約の行（`order_dispatch_reservations`）にも列を足し、突合で確定した記録へ写す**（`StopFloorSource` の前例）: 突合で確定するのは送信結果が不明だった記録だけで、
  null は取り消す側（是正前と同じ）に倒れるため安全側である。マイグレーションをもう 1 本足すほどの利得が無い（残余）。
- **猶予を置かず、生きている限り差し引き続ける**: 約定しない指値で S1 が止まる（決定 3）。
- **猶予の後は差し引かずに送る（取り消さない）**: 売れる数量を押さえる証券会社（SIMULATE）では拒否され続け、押さえない証券会社では二重に売る。
- **差し引きを常に掛ける（利用者・自動縮小が無くても、他の S1 の決済の処理中を引く）**: 本件の範囲外の挙動変更になり、既存の S1 の試験の前提（前の試行の決済が
  板に残る形）を変える。利用者・自動縮小が生きているときだけ掛ける。

## 結果

- 利用者の成行の手仕舞い・維持率割れの自動縮小は S1 に取り消されず、S1 は差し引いた残りだけを送る（同じ株を二重に売らない）。
- 判断の手仕舞い・出どころの分からない決済は従来どおり取り消され、S1 は送られる（損切りは止まらない）。
- 利用者・自動縮小が生きていない平常時は、注文照会・建玉照会は増えない（読み取りは発注の記録の 1 回）。
- **残余**:
  - 列を足す前の記録・切り替え前のリスク管理が出した承認・突合で確定した記録は出どころを持たず、利用者の手仕舞いでも取り消される（是正前と同じ）。
    突合で確定した記録へ出どころを運ぶ（予約の行に列を足す）のは [#1253](https://github.com/endazon/ai-stock-trading/issues/1253) で扱う。
  - 利用者・自動縮小の決済が全量を覆うあいだは S1 を据え置く（最長 `NettedCloseGrace`＝2 分。起点は発注の時刻で、一部約定しても延びない）。
    猶予を過ぎれば取り消して送る。
  - 猶予を過ぎた決済の取消が確定しないあいだは据え置く（IADR-0466 決定 4。据え置きが 15 分を超えれば `CloseStalled` の Critical）。
  - 照会の順により、窓の中で約定が進むと少なく売る（次の巡回で残りを売る）。
  - 🔴 ブローカー側の逆指値（S0/S3）が実弾で売れる数量を押さえるかは未確認（ADR-0050 決定 3）。差し引きは保護レグを引かない（IADR-0461 と同じ前提）。

## ［2026-10-08 追記 / #1253］(1) 予約の行にも出どころを残し、突合で確定した記録へ写す

- 「採らなかった案」の 3 つ目（予約の行 `order_dispatch_reservations` にも列を足し、突合で確定した記録へ写す）を**採る**。残余「突合で確定した記録は
  出どころを持たない」を塞ぐ（[#1253](https://github.com/endazon/ai-stock-trading/issues/1253)。作業仕様書
  [`.ai-context/specs/20261008_1253_reservation-approval-origin.md`](../specs/20261008_1253_reservation-approval-origin.md)）。形は
  [IADR-0486](IADR-0486_stop-width-floor-atr14-flag-and-floor-marker-on-order-intent.md) 決定 6（`StopFloorSource`）と同じ。
- **予約の行**: `OrderDispatchReservationRow.ApprovalOrigin` と列 `order_dispatch_reservations.ApprovalOrigin`（integer NULL）。マイグレーション
  `20261008045915_AddReservationApprovalOrigin` は**列の追加だけ**で、🔴 **既存行は null のまま埋めない**（分からない＝決定 3 のとおり取り消す側）。
- **書き手**: 承認の経路の相 1（`OrderExecutionAppService` の `TryReserve`）だけが、相 4 と同じ値（`Unknown` は null）を渡す。
  `IOrderReservationStore.TryReserve` の末尾引数 `approvalOrigin`（既定 null）・`OrderDispatchReservation.ApprovalOrigin`。保護の機構の予約は渡さない（null）。
  EF・インメモリの両方が保存し、`Find` / `FindStalledReserved` で読み戻す。`MarkCompleted` は触れない。
- **突合**: `OrderReservationReconciler.BuildRecord` が予約の行の出どころを組み直した記録の `ApprovalOrigin` へ写す（決定 2 の「突合で確定した記録は null」を改める）。
  予約の行が null なら null。自己修復・競合の経路は既存の記録を使う（変えない）。
- 決定 3・4（取消・差し引き・猶予）は変えない。突合で確定した記録も通常の経路の記録と同じ規則で扱う（突合の経路だけ猶予を延ばさない）。
- 試験 T-10-2452〜T-10-2455。
- **残余（本追記で新たに分かったもの）**:
  - 🔴 本番の照会（`MoomooReservationBrokerProbe`）は発注意図の `PositionEffect` を Open で近似する（`ToBrokerOrder`。moomoo の注文から一意に復元できない。照会は [IADR-0092](IADR-0092_reservation-broker-probe-moomoo.md)）。突合で確定した決済の記録は
    Open として残り、S1 の処理中の決済の読み出し（Close だけ）に載らない。本追記の是正は照会が Close を復元できるときに効き、現状の本番では
    突合で確定した決済は S1 に取り消されも差し引かれもしない。決済の向きの復元は別の件。
  - 猶予の起点は記録の時刻（証券会社が答えた発注の時刻。答えなければ突合の時刻）。突合は滞留の閾値（下限 1 時間）より古い予約しか扱わないので、
    発注の時刻が分かる限り記録は猶予を過ぎており、取り消される（通常の経路と同じ）。差し引かれるのは発注の時刻が分からないときに限られる。

## ［2026-10-08 追記 / #1262］(2) 予約の行に建て・決済の別も残し、突合で確定した記録をその値で書く

- 追記(1) の残余 1 つ目（本番の照会は `PositionEffect` を Open で近似し、突合で確定した決済が S1 の読み出しに載らない）を塞ぐ
  （[#1262](https://github.com/endazon/ai-stock-trading/issues/1262)。作業仕様書
  [`.ai-context/specs/20261008_1262_reservation-position-effect.md`](../specs/20261008_1262_reservation-position-effect.md)）。形は追記(1)・
  [IADR-0486](IADR-0486_stop-width-floor-atr14-flag-and-floor-marker-on-order-intent.md) 決定 6 と同じ（照会の写像は直さず、予約の行に残して突合の 1 か所で写す）。
- **予約の行**: `OrderDispatchReservationRow.PositionEffect` と列 `order_dispatch_reservations.PositionEffect`（integer NULL。0＝Open / 1＝Close）。マイグレーション
  `20261008053053_AddReservationPositionEffect` は**列の追加だけ**で、🔴 **既存行は null のまま埋めない**。
- **書き手（`TryReserve` の呼び出し 6 か所すべて）**: 通常の経路が発注の記録に書くのと同じ値を、末尾引数 `positionEffect`（既定 null）で渡す。
  承認の相 1 は `intent.PositionEffect`、保護の機構の 5 か所（S0 の逆指値レグ・保護喪失の成行・S1 の決済・常駐ガードの逆指値・常駐ガードの成行）は `Close`。
  突合で確定した記録は通常の経路の記録と同じ形になり、読み出し側の除外の規則（S0/S3 の保護レグを引かない等）は変えない。
- **突合**: `OrderReservationReconciler.BuildRecord` は記録の `PositionEffect` を `予約の行の値 ?? 照会の値` で書く。`MoomooReservationBrokerProbe` は Open の近似のまま（注記のみ改める）。
  自己修復・競合の経路は既存の記録を使う（変えない）。保護レグを張るかの判別は引き続き保護記録で行う（IADR-0362 決定 3。列を足す前の予約は値を持たないため）。
- 🔴 **列を足す前の行（null）は照会の値（本番では Open）のまま＝是正前と同じ。保守側としてこちらを採る。**
  - 推測で `Close` に倒すと、実際はエントリーだった予約が決済として記録され、`FindPendingCloses` に載る（売り建てでは買いのエントリーが処理中の買い戻しになり、
    S1 の取消の段がエントリーを取り消し得る・判断の手仕舞いが差し引き過ぎる）。`FindRecentOpens` と床の遡及（Open の記録だけが対象）からもエントリーが消える。
    誤りの害がエントリー側へ広がり、損切りの経路の外にも及ぶ。
  - Open のままの害は、配備の時点で滞留していた決済の予約が S1 に取り消されも差し引かれもしないこと（#1262 以前と同じ）に限られ、
    突合で確定した 1 件は既存の Critical の所見（IADR-0362 の ProbeTerminalized）で人に知らされる。対象は時間とともに 0 になる。
  - 保護記録の有無で推測する案も採らない（保護記録を書く前に止まったエントリーを決済と読む）。
- 是正で新たに載るもの: 突合で確定した S1 の決済が取り消された・照会できないとき、再武装・通知（[IADR-0389](IADR-0389_rearm-software-stop-on-confirmed-unfilled-close.md)。Close の記録だけが対象）に載る
  （是正前は Open のため黙って外れていた）。判断の手仕舞いの差し引き（[IADR-0461](IADR-0461_close-quantity-subtracts-in-flight-closes.md)）も突合で確定した決済を数える。
- 試験 T-10-2456〜T-10-2460（本番の照会の写像を通す端から端までの試験を含む）。
- **残余**: 列を足す前の予約の行は是正前と同じ（上）。突合で確定した S0/S3 の逆指値レグも Close になり、Active でない行の古い試行のレグが生きていれば
  判断の手仕舞いの差し引きに数えられる（通常の経路で送ったレグと同じ扱いで、新しい型ではない。少なく売る側）。
