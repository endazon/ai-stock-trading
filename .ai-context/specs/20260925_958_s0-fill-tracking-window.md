---
title: 武装から 24 時間を超えて約定したブローカー側逆指値（S0）を約定追跡の窓から落とさず、台帳へ届ける（#958）
type: spec
status: accepted
related_ids: [FR-10, FR-05, UC-02, ADR-0040, IADR-0113, IADR-0210, IADR-0344, IADR-0394, IADR-0406]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 損切り・FR-05 発注執行)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (S0 / S1)
---

# 仕様書: S0 の決済レグを追跡上限の対象外にする（#958。PR #949 の監査で判明した既存の欠落）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-10（損切り・統制）、FR-05（発注執行・約定の反映）
- ユースケース（UC）: UC-02（損切り）
- 画面（SC）: なし
- 関連 ADR: ADR-0040（S0 / S1 の区別。決定は変えない）
- 関連 IADR: IADR-0113（約定追跡のポーリングと追跡上限）、IADR-0210（S0 の保護記録とガード）、
  IADR-0394（損切りを約定で数える。§結果 3 がこの欠落を記録）。本作業の判断は **IADR-0406** に残す
- 計画書リンク: 上記 plan_refs

## 目的・背景

約定追跡（`OrderFillPoller`）は `store.FindPendingSince(now - MaxTracking, batch)` で、**記録から
`FillPolling:MaxTrackingHours`（既定 24 時間）以内**の非終端の記録だけを照会する。S0（ブローカー側逆指値）の
決済レグの `ExecutionRecord` は**武装の時刻**（`OrderExecutionAppService` の武装・`ProtectiveStopGuard` の再発注の
`clock.UtcNow`）で作られる。したがって**武装から 24 時間を超えて約定した S0 の約定は `OrderExecuted` として
一度も発行されず**、リスク管理の取引台帳へ届かない。`OrderExecuted` を出すのは発注時（`OrderExecutionAppService`）と
約定追跡だけである（`grep -rn "new OrderExecuted(" backend/Services --include=*.cs` の実測 3 箇所）。

影響: IADR-0394 の統制（損切りした銘柄の当日・同方向の新規建てを止める）は S0 を**約定**で数えるので、
その損切りは数えられず、その日の同じ方向の新規建ては止まらない。台帳の建玉・実現損益にも反映されない。

### 建玉観測の乖離検知が別経路で拾うか（issue の確認事項・調査結果）

**拾わない（数量の食い違いを知らせるだけで、約定としては届かない）。**

- 発注執行の建玉観測（`BrokerPositionSnapshotService` → `BrokerPositionsObserved`）をリスク管理の
  `BrokerPositionsObservedHandler` が台帳の射影と突き合わせ、連続観測の条件を満たせば
  `PositionReconciliationDrift` を発行する（IADR-0118）。**台帳は書かない**（「検知・記録・通知のみで是正しない」）。
- 利用者が乖離を取り込む経路（`PositionDriftAdoptionService`。計画 ADR-0041）は**数量だけ**を台帳へ入れる。
  取り込みはシステム外の売買として記録され、**損切り（S0 の約定）としては数えられない**ので IADR-0394 の統制は働かない。
  実現損益も約定価格では入らない。
- 🔴 **ショートの S0（買い戻しの逆指値）では誤った推定を起こす。** `BuyInInferenceService` は
  「自らの決済指示（処理中の決済承認）で説明できない建玉の消失」を**強制買戻しと推定**して、その銘柄の新規空売りを
  禁止する。S0 の武装の承認は武装時刻で書かれ、処理中の窓（30 分）を 24 時間後にはとうに過ぎているため、
  届かなかった S0 の買い戻しは強制買戻しと取り違えられ得る。本修正で約定が台帳へ届けば、この誤推定の入力も消える。

## 対象範囲

- 対象:
  - `IExecutedOrderStore` に `FindPendingByOrderIds`（注文 ID を指定して非終端の記録を返す）を足し、EF・インメモリの 2 実装に入れる
  - `OrderFillPoller`: **Active な S0 の保護記録の現試行の逆指値レグ**を、追跡上限に関係なく照会対象に足す
    （保護記録ストアは読むだけ。省略時は従来どおり）
  - `IExecutedOrderStore` に `RenewTracking`（非終端の記録の追跡の起点＝`ExecutedAt` だけを進める）を足し、2 実装に入れる
  - `ProtectiveStopGuard`: S0 のレグの終端（約定・失効）を観測したとき・建玉消滅で取り消したとき、保護記録を完了させる
    （または再発注する）**前に**、そのレグの記録の追跡の起点を観測の時刻へ進める
  - `Program.cs`: 約定追跡へ保護記録ストアを渡す
  - 文書: 機能仕様書（FR-10）の「数えられない」記述、テスト仕様書（FR-10）の未カバー、IADR-0394 §結果 3 と
    IADR-0113 への日付つき追記
- 対象外:
  - 追跡上限の既定値・クランプ（`FillPollingOptions`）: 変えない（照会件数が増えるため延ばさない）
  - S1 の決済レグ（IADR-0389 の「追跡上限を過ぎた決済レグは照会対象から外れる」）: 成行であり、
    24 時間を超えて約定待ちになるのは滞留である。issue の射程外
  - エントリーの記録・通常の成行: 変えない
  - PR #950 / #956 が触る `SoftwareStopExecutor`・保護記録ストア（`EfProtectiveStopOrderStore` /
    `InMemoryProtectiveStopOrderStore` / `IProtectiveStopOrderStore`）・マイグレーション: **触らない**
    （既存の `FindActive` を読むだけで足りる形を選んだ。スキーマも変えない）

### 是正の母集合（規則 9〜11。自分で引いた結果と除外理由）

**(1) 追跡上限を参照する箇所**（`git grep -n "MaxTrackingHours\|追跡上限\|FindPendingSince" -- ':!.ai-context/specs'`、テストを除く）:

| 箇所 | 扱い |
| --- | --- |
| `OrderFillPoller.cs:49`（`FindPendingSince(now - maxTracking)`） | **是正**（S0 レグを足す） |
| `IExecutedOrderStore` / `EfExecutedOrderStore` / `InMemoryExecutedOrderStore` の `FindPendingSince` | 意味は変えない。隣に `FindPendingByOrderIds` を足す |
| `FillPollingOptions.cs`（既定 24・クランプ） | 変えない。コメントに S0 の例外を足す |
| `OrderFillPollingService.cs:44`（開始ログ） | 変えない（上限の値のログ） |
| `ReconciliationOptions.cs:44` / `deploy/helm/.../values.yaml:462`（予約の突合は 23 時間以下） | 対象外（予約の滞留の話で、S0 のレグと無関係） |
| `IADR-0346:101`（新規建ての作業中注文） | 対象外（エントリーの注文） |
| `IADR-0389:112`（S1 の決済レグ） | 対象外（上の対象外を参照） |
| `IADR-0113:80` / `:140` | 日付つき追記で S0 の例外を記す |
| `IADR-0394:178-181`（§結果 3） | 日付つき追記で是正を記す |
| `docs/functional/FR-10_risk-controls.md:867-868` | **是正**（「数えられない」を消し、仕組みを書く） |
| `docs/tests/FR-10_risk-controls-tests.md:2019-2021` | **是正**（未カバーから外し、本作業のテスト ID を足す） |
| `docs/tests/FR-10_risk-controls-tests.md:1439`（S1 の決済レグ） | 対象外（S1） |

**(2) S0 の保護記録を完了させる経路**（`grep -rn "ProtectiveStopState.Completed" backend/Services/OrderExecutionService --include=*.cs`、テスト・比較を除く）:

| 箇所 | S0 のレグの約定が落ちるか | 扱い |
| --- | --- | --- |
| ガード `EvaluateAsync` の `Filled` 分岐（`MarkCompleted`） | 🔴 **落ちる**。ガードが先に `Filled` を見て完了させると、次の約定追跡の巡回では保護記録が Active でなくなり、24 時間を過ぎたレグは照会されない（ガードと約定追跡は別々の 30 秒の巡回で順序が決まらない） | **是正**（完了の前にレグの記録の追跡の起点を観測の時刻へ進める） |
| ガード `EvaluateAsync` の建玉消滅 → 取消 → 完了 | 最後の約定追跡の巡回と取消のあいだ（最大 30 秒）の部分約定が落ち得る | **是正**（取消の直後・完了の前に付け直す） |
| ガード `EvaluateAsync` の失効・建玉残 0 → 完了 | 失効の直前の部分約定が落ち得る（同上） | **是正**（失効を観測した時点で付け直す） |
| ガード `ReplaceOrCloseAsync`（再発注で保護記録の現試行が新しいレグへ移る） | 古いレグは失効済み。再発注の後は古いレグが Active から外れる | **是正**（再発注の前、失効の観測の時点で付け直す＝上と同じ 1 箇所） |
| ガード `CompleteAsClosed` / S1 の経路（`:227`、`SoftwareStopExecutor`、`OrderExecutionAppService:535`） | S1 か、S0 が既に失効した後 | 対象外 |
| `ProtectiveStopDriftAdopter.ReduceBooks` | 利用者が乖離を取り込んだとき（システム外の売買）。その後の S0 の逆指値はガードが建玉消滅で取り消す | 変えない（残余。IADR-0406 §結果） |

**(3) 増える側と減る側のプローブ（規則 11）**: 「窓」を扱う是正であるため、形を決める前に次の 3 通りを比べた。
プローブは T-10-866（下のテスト）で、ガードと約定追跡の巡回の順序を入れ替えて実測した。

| 形 | 増える側: 武装 25 時間後の S0 の約定が台帳へ届くか（ガードが先 / 約定追跡が先） | 減る側: 追跡上限を過ぎた記録の照会件数が増えないか |
| --- | --- | --- |
| A. 追跡上限を延ばす（例 7 日） | 届く / 届く（7 日まで） | 🔴 **増える**（上限内の全ての非終端の記録が 7 日間照会される） |
| B. Active な S0 のレグだけを対象外にする（issue の案 1 そのまま） | 🔴 **届かない** / 届く | 増えない（Active な S0 のレグだけ） |
| C. B ＋ ガードがレグの終端を観測したら、完了させる前にレグの記録の追跡の起点を観測の時刻へ進める（**採用**） | 届く / 届く | 増えない（Active な S0 のレグだけ） |

当初は C の代わりに「ガードが `Filled` を見ても、レグの記録が終端になるまで保護記録を完了させない（待つ）」形を実装したが、
待つあいだ約定済みの S0 の行が Active に残り、S0 と S1 が併存する群では外部要因の観測（`ProtectiveStopNetting.ReconcileShares`）が
S0 の約定で減った建玉を S1 の行から先に割り当て、2 巡回に跨がると S1 の残保護数量を黙って削る。これを避けるため C に替えた
（IADR-0406「却下した形」）。

## 設計

1. `IExecutedOrderStore.FindPendingByOrderIds(IReadOnlyCollection<string> orderIds)`: 指定した注文 ID のうち
   非終端の記録を古い順に返す。空集合なら何も読まない。追跡上限は見ない（呼び出し側が対象を絞る）。
2. `IExecutedOrderStore.RenewTracking(string orderId, DateTimeOffset trackedFrom)`: 非終端の記録の `ExecutedAt` を
   `trackedFrom` へ進める。時刻の列だけを書く（EF は変更追跡で `executed_at` だけを UPDATE する）。終端・不在・巻き戻しは何もしない。
3. `OrderFillPoller` に省略可能な `IProtectiveStopOrderStore? protectiveStops` を足す。巡回のたびに
   `protectiveStops.FindActive(batchSize)` から **S0（`!IsSoftwareStop`）かつ `StopOrderId` が空でない行**の
   `StopOrderId` を集め、`FindPendingByOrderIds` で得た記録を `FindPendingSince` の結果へ（注文 ID で重複を除いて）足す。
   以降の処理（照会・更新・発行）は既存と同一。
4. `ProtectiveStopGuard.EvaluateAsync`（S0）: `Filled`・建玉消滅での取消の直後・失効の観測の 3 箇所で、`MarkCompleted`・
   再発注より前に `store.RenewTracking(stop.StopOrderId, now)` を呼ぶ。例外は上げる（保護記録を完了させずに次の巡回でやり直す）。
5. `Program.cs`: 約定追跡に保護記録ストアを渡す。

代替案（追跡の起点を「最後に有効を確かめた時刻」にする）との比較は IADR-0406 に書く。

## 受け入れ基準

- [x] Active な S0 の保護記録の現試行の逆指値レグは、記録から追跡上限を過ぎていても照会され、約定すれば
      `OrderExecuted` が発行される（武装から 25 時間後）
- [x] 追跡上限を過ぎた記録のうち、Active な S0 のレグでないもの（エントリー・完了済みの保護記録のレグ・S1）は照会しない
- [x] ガードが約定追跡より先に `Filled` を見ても、約定は台帳へ届く（ガードは完了の前にレグの記録を窓へ戻す）
- [x] ガードの付け直しは非終端の記録の時刻だけを書き、約定追跡が書いた終端・数量を巻き戻さない
- [x] `FindPendingByOrderIds` / `RenewTracking` は EF とインメモリで同じ意味論を持つ
- [x] 本番の配線（`Program.cs`）が約定追跡へ保護記録ストアを渡す
- [x] 時刻はすべて注入時計（壁時計の sleep を使わない）

## テスト方針

T-10-860〜T-10-866（割り当て T-10-860〜T-10-869 のうち 7 件）。

- T-10-860: `FindPendingByOrderIds` の EF / インメモリ（`OrderReservationForgoneStoreTests` の 2 実装 Theory の形に倣う）
- T-10-861: `RenewTracking` の EF / インメモリ（同上）と、EF の「別のコンテキストが先に終端を書いても巻き戻さない」
- T-10-862: 約定追跡が Active な S0 のレグを追跡上限を越えて照会し、`OrderExecuted` を返す（武装 25 時間後）
- T-10-863: 追跡上限を越えた記録のうち、対象外（エントリー・完了済み保護記録のレグ・S1・保護記録ストア未構成）は照会しない
- T-10-864: ガードは `Filled` / `Expired` / `Cancelled` の観測と建玉消滅の取消で、完了の前にレグの記録を窓へ戻す（状態・数量は書かない）。終端の記録は触らない
- T-10-865: 常駐（`OrderFillPollingService`）経由で、武装 25 時間後の S0 の約定が `OrderExecuted` として発行される（Wolverine のテストハーネス）
- T-10-866: ガードと約定追跡を実ストアで組み、巡回の順序を 2 通り入れ替えて、どちらでも `OrderExecuted` が 1 回だけ出て保護記録が完了する（規則 11 のプローブ）
- 変異注入（実走）: 「S0 のレグを足さない」「ガードが付け直さない」「抽出が状態を見ない」「付け直しが終端も動かす」
  「EF の付け直しが行全体を書く」「S1 の行を除外しない」を 1 つずつ入れて赤を確かめ、テスト仕様書に記す

## 計画書との差異

なし（計画 FR-10 の「損切りを約定で数える」を実装が満たしていなかった欠落の是正）。

## 未決事項

なし（残余リスクは IADR-0406 §結果・残余リスクに記す）。
