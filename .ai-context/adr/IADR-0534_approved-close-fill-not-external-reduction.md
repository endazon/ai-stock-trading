---
title: IADR-0534 保護の突き合わせが確定した減少のうち、承認済みの決済（取引判断の利確・損切り・利用者の手仕舞い・自動縮小）の約定で説明できる分を外部要因と分け、ApprovedCloseReleased（Info）として知らせる。判定は窓の両端（送った時刻 ≦ 最初の観測、約定は確定までに記録へ）で行い、帰属させた約定は記録へ印を付けて 2 回数えない。数量の仕組みは変えない
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-09, FR-11, UC-02, UC-06, ADR-0040, ADR-0050, IADR-0344, IADR-0396, IADR-0412, IADR-0461, IADR-0466, IADR-0515]
author: claude (Claude Code)
created: 2026-10-13
updated: 2026-10-13
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 銘柄別損切りライン・S0〜S3)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定 1 S1)
  - planning:projects/ai-stock-trading/07_adr/ADR-0050_decision-close-nets-in-flight-closes-and-stop-line-exit-only-without-mechanical-stop.md
related_specs:
  - ../specs/20261013_1330_approved-close-not-external-reduction.md
---

# IADR-0534: 承認済みの決済の約定で減った保護対象を「外部要因」に数えない（#1330）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-13
- 決定者: claude（起票 [#1330](https://github.com/endazon/ai-stock-trading/issues/1330)。利用者レビューは PR で受ける）

## 起点・関連

- 計画: FR-10（銘柄別損切りライン。模擬取引の S1）、ADR-0040 決定 1、ADR-0050（判断の決済と処理中の決済）。
- 事象: PoC 2026-10-12 米国セッション。MSFT の利確（取引判断の Sell 456 株・発注 15:19:18Z）の 61 秒後に
  `SoftwareStopExecuted`（`ProtectionReduced`）が出て、Discord へ「外部要因により保護対象を減らしました」と通知された（#1330）。
- 関連 IADR:
  - [IADR-0344](IADR-0344_s1-software-stop-loss.md)（S1 の持ち分。観測 → 2 巡回連続で確定）。**確定の仕組みと数量は変えない**。
  - [IADR-0396](IADR-0396_protective-stop-optimistic-concurrency.md)（楽観並行。「書けなかった減少を減らしたと言わない」を保つ）。
  - [IADR-0515](IADR-0515_order-approval-origin-s1-nets-owner-closes.md)（発注の記録の承認の出どころ）。**本件の判定材料**。
  - [IADR-0412](IADR-0412_unattributed-position-detection-on-position-snapshot.md)（帰属不明の検知）。outcome のログだけを足す。
- 作業仕様書: [20261013_1330_approved-close-not-external-reduction](../specs/20261013_1330_approved-close-not-external-reduction.md)（原因の行・母集合・窓の表・変異）。

## コンテキスト

S1 の帳簿（`RemainingProtected`）を動かすのは、エントリーの確定・S1 自身の決済・外部要因の確定の 3 つだけである（IADR-0344 追記(4)）。
承認経路の決済（取引判断の利確・損切り、利用者の手仕舞い、維持率割れの自動縮小）は発注の記録を書くだけで S1 の帳簿に触れない。
その約定で純額が減ると、常駐ガードの突き合わせ（`ProtectiveStopNetting.ReconcileShares`）が「帳簿 − 純額」を超過として観測し、
2 巡回連続（30 秒 × 2）で確定して**理由を問わず** `ProtectionReduced`（「外部要因（手動決済・強制決済…）」）を出していた。
**減らした数量は正しい**（実際に建玉が減った分）。誤っていたのは分類・文面と、outcome がログに出ないことである。

約定の事実は 2 か所に別々の時刻で現れる: ブローカーの建玉照会と、約定追跡（30 秒周期）が書く発注の記録。moomoo の約定追跡は
終端の時刻を**照会した時刻**で書く（`MoomooBrokerAdapter.GetOrderAsync`）ので、記録の時刻は約定の時刻ではない。

## 決定

1. **確定した減少を分類する（窓の両端）。** `Confirm` が減少を確定するとき、`ApprovedCloseAttribution` がその減少のうち
   承認済みの決済で説明できる分を見積もる。数えるのは次をすべて満たす決済だけである。
   - 発注の記録が `PositionEffect=Close`・決済の方向・同じ銘柄と市場で、**`ApprovalOrigin` が null でない**（承認経路の発注）。
     保護の機構の決済レグ（S0/S3 の逆指値・S1 の決済・成行手仕舞い）と #1222 より前の記録は出どころを持たないので入らない。
   - **前の端**: 送った時刻（予約の `ReservedAt`）が、その減少を**最初に観測した**巡回の時刻（保護記録の新しい列
     `ExternalReductionObservedAt`）以前で、保護記録の作成以後。予約が見つからなければ数えない。
   - **後の端**: 確定の巡回の時点で、記録に**未帰属の約定**（`FilledQuantity − ProtectionReleasedQuantity`）がある。
   古い（送った時刻の早い）決済から減少を割り当て、残りを外部要因とする。`ExternalReductionObservedAt` は観測値が増えた巡回
   （ガードの観測・決済経路の記録の両方）で書き直し、確定・失効で null へ戻す。列を足す前の行（null）は確定の時刻で代える。
2. **帰属させた約定は 2 回数えない。** 発注の記録の新しい列 `ProtectionReleasedQuantity`（0..`FilledQuantity`・単調）へ、
   帰属させた株数を書く（`IExecutedOrderStore.MarkProtectionReleased`。その 1 列だけを書く）。**帳簿を書けた後に**書く——
   帳簿を書けなければ帰属も確定しない（IADR-0396）。印を書けなくても帳簿は正しい（次の確定の文面だけが誤り得る）。
   移行（`20261013123535_AddApprovedCloseProtectionRelease`）で既存の決済行は `FilledQuantity` に揃える（過去の約定を、
   これからの外部要因の減少の証拠にしない）。
3. **通知は分けて出す。** 承認済みの分は新しい outcome `SoftwareStopOutcome.ApprovedCloseReleased`（序数 10・末尾）で、
   `CloseDecisionId` / `CloseOrderId` に帰属させた決済、新しい省略可能な欄 `SoftwareStopExecuted.CloseOrigin` に出どころを載せる。
   通知は **Info**（「承認済みの決済の約定に合わせて保護対象を減らしました」。出どころを名指しし「外部要因ではありません」と書く）。
   外部要因の分は従来どおり `ProtectionReduced`（Warning・従来の文面）。1 回の確定が両方を含めば 2 件出す（合計＝減少量）。
   監査の要約にも足す。
4. **outcome を INF で残す。** 突き合わせが出す outcome（確定した縮小の 2 分類・`ProtectionSuspended`・`UnattributedPosition`）を
   INF で出す。項目は判断 ID（エントリー・帰属させた決済）・注文 ID・数量の前後（帳簿 前 → 後・内訳）・分類の理由。
   netting は static なので `ILogger?` を省略可能な引数で受け、ガードと帰属不明の検知（常駐）が渡す。
5. **迷ったら外部要因へ倒す。** 予約ストアが無い（`ReconcileShares` の `reservations` 省略）・記録を読めない・予約が無い・
   出どころが無い・観測の後に送った決済は、承認済みと数えない。外部要因を承認済みと誤って呼ぶと人手で確かめるべき減少を隠すが、
   逆は確認の手間で済む。

## 窓の形の比較（規則 11。実測）

仕様書の 3 つの形を、同じプローブで変異として実測した（`ApprovedCloseNettingTests`）。

| 形 | P+（増える側: 約定追跡が最初の観測の後に記録を直す） | P−（減る側: 観測の後に送った決済の約定が記録へ先に載る） | P0（吸収） |
| --- | --- | --- | --- |
| (a) 後の端だけ（送った時刻 ≦ 確定時刻） | ✓ | ✗（外部要因 300 株を承認済みと誤る） | ✓ |
| (b) 前の端だけ（最初の観測の時点で記録に約定がある） | ✗（承認済みの利確を外部要因と誤る＝#1330 が残る） | ✓ | ✓ |
| **(c) 両端（採用）** | **✓** | **✓** | **✓** |

（✓/✗ は変異の実測。詳細と赤くなった試験の名前は作業仕様書の「実測」。）

## 却下した案

- **約定の時点で S1 の帳簿を直接減らす（約定追跡・発注・突合の 3 か所に差し込む）。** 建玉照会が先に約定を映して観測が
  確定した後に約定追跡が減らすと**二重に減る（保護が過小）**。差し込む先も増える。数量の仕組みに触れない分類だけで足りる。
- **記録の `ExecutedAt` で約定の時刻を判定する。** 約定追跡が照会した時刻で上書きするため、P+ で約定の時刻が最初の観測より後に見え、
  #1330 が残る（形 (b) と同じ誤り）。
- **吸収の印を持たず、時刻の下限（前回の確定）で 2 回数えを防ぐ。** 観測より前に送り、確定の後に建玉照会へ映った決済
  （P− の後半）を外部要因と誤る。
- **承認済みの分は通知しない。** 帳簿を減らしたことが監査・通知から消える（S0 の行が 0 になって逆指値を取り消す場合も無音になる）。
  Info で残す。

## 帰結

- 取引判断の利確・損切り、利用者の手仕舞い、維持率割れの自動縮小で減った分は「外部要因」と知らせない（Info・出どころつき）。
- 保護の数量（帳簿・実効数量・発動時の決済数量）は是正前と 1 株も変わらない（T-10-2615 が分類の有無で同一を固定）。
- 列が 2 つ増える（`protective_stop_orders.ExternalReductionObservedAt`・`executed_orders.ProtectionReleasedQuantity`）。

## 残余リスク

- **観測より前に送ったが、観測の後に約定した決済**（板に残った指値）と、同時の外部要因が重なると、外部要因の分を承認済みと
  呼び得る（送った時刻しか判定に使えない）。数量には影響しない。
- 約定追跡が確定の巡回（約 60 秒）より遅れて記録を直すと、承認済みの約定でも外部要因と知らせる（是正前と同じ文面）。
  その約定は印が付かないまま残り、同じ保護記録の作成以後に起きた**次の**外部要因の減少を承認済みと呼び得る。
- S1 が主張していない建玉（S2・人手）を売った承認済みの決済の約定は、超過を生まないので印が付かないまま残る（上と同じ向き）。
- 承認済みの分が S0 の行へ割り当たる（S1 で吸収しきれない）場合も Info になる。逆指値の取消は従来どおり起き、文面にもそう書く。
