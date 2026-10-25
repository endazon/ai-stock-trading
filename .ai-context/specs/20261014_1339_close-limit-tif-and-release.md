---
title: 判断由来の決済の指値に有効期限 DAY を明示し、追跡を打ち切った決済はブローカーの終端を確かめてから解放し、未約定の決済を定期的にログへ出す（#1339・最小是正）
type: spec
status: done
related_ids: [FR-05, FR-10, FR-11, UC-02, UC-06, ADR-0003, ADR-0040, ADR-0050, IADR-0113, IADR-0117, IADR-0210, IADR-0461, IADR-0481, IADR-0515, IADR-0536]
author: claude (Claude Code)
created: 2026-10-14
updated: 2026-10-14
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-05 発注執行・FR-10 手仕舞いは止めない)
  - planning:projects/ai-stock-trading/07_adr/ADR-0050_decision-close-nets-in-flight-closes-and-stop-line-exit-only-without-mechanical-stop.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定 4 の GTC の確認は #1214 の領分。本件では触らない)
---

# 仕様書: 判断由来の決済の指値の有効期限・追跡の打ち切り後の解放・未約定の決済の定期ログ（#1339）

> 本仕様書は実装着手前に作成した（着手 2026-10-14）。基点は AST `origin/develop` `03753282`。
> 利用者の裁定（2026-10-14）: **最小是正のみ**。取消と置き直し・成行への切り替えは #1214 の実弾確認とあわせて後で決める。

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **FR-05**（発注執行）、**FR-10**（手仕舞いは止めない）、FR-11（監査・観測）、UC-02 / UC-06。
- 計画 ADR: ADR-0003（承認済みの注文だけを発注）、ADR-0050（判断の決済と処理中の決済）。ADR-0040 決定 4（保護逆指値の GTC）は #1214 の領分で、本件は保護逆指値の有効期限を**変えない**。
- 起票: #1339（PoC 2026-10-13 米国セッション。AAPL の利確 13 株・指値 347.93 が約定せず閉場まで残った）。
- 関連: #1214（保護逆指値の GTC）、#1340（判断由来の決済が処理中の銘柄を LLM の前に見送る。未マージ。本件は develop の上で作る）。
- 新規 IADR: **IADR-0536**（develop の最大は IADR-0534、open PR #1340 が IADR-0535 を使う）。

## 原因（コードで確かめた事実）

1. 判断由来の決済は承認経路（`OrderExecutionAppService.ExecuteAsync` の相 3）で `IClientOrderIdBroker.PlaceOrderAsync` → `MoomooBrokerAdapter.PlaceCoreAsync`（既定 `MoomooOrderKind.Limit`）→ `MMApiMoomooTradeClient.PlaceOrderAsync` と流れる。`TrdPlaceOrder.C2S` に `SetTimeInForce` を呼んでいない（SDK 10.8.6808 は `TimeInForce_DAY` / `GTC` / `GTD` / `IOC` を持つ）。有効期限は moomoo の既定（DAY とみられる・未確認）に任されている。
2. 約定追跡（`OrderFillPoller`）は発注から追跡上限（既定 24 時間）の間だけ照会する。上限を過ぎると打ち切りを監査へ残し（IADR-0481 決定 3）、`MarkTrackingAbandoned` は印の列だけを書く。**記録の状態は非終端のまま残り、以後だれも照会しない。**
3. `FindPendingCloses` は追跡上限を見ず、非終端の決済をすべて返す。ただし決済のゲート（`CountInFlightClosesAsync`）は、そのうち**ブローカーが生きていると答えた注文だけ**を数える（IADR-0461 決定 4。照会が null・例外・終端なら数えない）。S1 の取消（IADR-0466）・損切り幅の遡及（IADR-0472）も同じ見分け方である。
   - したがって「塞がれ続ける」のは、ブローカーが注文を生きていると答え続ける間である。有効期限が既定に任されている（1）ことが、その期間を決める。
   - 一方、追跡を打ち切った決済の記録は、ブローカーが後で終端を返しても**非終端のまま**である。終端の `OrderExecuted` が出ないので、リスク管理の台帳の終端・手仕舞いの未約定残の通知（IADR-0357）・監査が届かない。照会の口（#1340）も毎回この記録をブローカーへ問い合わせる。
4. 約定追跡は変化があったときしかログを出さない（`OrderFillPollingService.PollOnceAsync` の INF は更新・不明・失敗があるときだけ）。未約定のまま残っている決済は運用者から見えない。
5. `MMApiMoomooTradeClient.QueryOrderAsync` は当日の注文一覧（`GetOrderList`）だけを見る。日をまたいだ注文が一覧から消えると null（不明）になる。これは本件で変えない（下の残余）。

## 方針（最小是正）

1. **有効期限 DAY の明示**: `MoomooOrderRequest` に省略可能な `TimeInForce`（`MoomooTimeInForce.Day` だけを持つ）を足す。`MoomooBrokerAdapter.PlaceCoreAsync` が **`Kind=Limit` かつ `PositionEffect=Close`** のときだけ `Day` を載せ、`MMApiMoomooTradeClient` が `SetTimeInForce(TimeInForce_DAY)` を呼ぶ。null のときは呼ばない（従来どおり）。
   - 対象外（変えない）: 保護逆指値 `Stop`（S0。#1214 の対象）・S3 の `StopLimit` / `TrailingStop`（`PlaceWithRejectionDetailAsync` を直接呼ぶ）・成行 `Market`（S1 の決済・成行の手仕舞い）・新規建ての指値（`Open`）。
   - 承認経路の指値の決済は、取引判断の決済のほか利用者の手仕舞い・維持率割れの自動縮小（成行でないもの）も同じ口を通る。アダプタは出どころを持たないので、これらにも DAY が載る。moomoo の既定が DAY なら挙動は変わらない（IADR-0536 決定 1）。
2. **追跡を打ち切った決済の解放**: 約定追跡の常駐が、一定間隔（既定 10 分）で「追跡を打ち切った・非終端・承認経路（`ApprovalOrigin` あり）の決済」をブローカーへ照会し直す。**ブローカーが終端（約定・取消・拒否＝失効を含む）と答えたときだけ**、通常の追跡と同じ処理（記録の終端化・`OrderExecuted` の発行）で解放する。null・例外・非終端・不明（`Unknown` は非終端へ写る）は解放しない。
3. **迷ったときの倒し方**: **解放しない**。根拠は IADR-0536 決定 3（解放してブローカー側で生きていれば、台帳が在庫を返し、後から約定しても追跡されず、同じ建玉へ 2 本目の決済が並び得る＝売り越し。解放しない害は、決済のゲートがブローカーの「生きている」を要件にしているため「止まり続ける」には直結しない）。
4. **未約定の決済の定期ログ**: 約定追跡の常駐が、一定間隔（既定 15 分）で「非終端・承認経路の決済のうち発注から 15 分以上たったもの」を 1 件ごとに **WRN** で出す（判断 ID・注文 ID・銘柄・方向・約定/数量・状態・経過分・追跡中か打ち切り済みか）。0 件なら出さない。通知は足さない（任意とされた。連打の設計を増やさない）。

## 窓（規則 11）

窓の両端は **発注（前の端。記録の `ExecutedAt`＝発注時刻。非終端の進捗では動かない）** と **終端の観測（または追跡の打ち切り）（後の端）** である。

### 解放の形

プローブ:

- **P+（増える側＝解放してはならない）**: 追跡を打ち切った後も、ブローカーが注文を生きている（Accepted / 一部約定）と答える（例: 既定が GTC だった・場が開いている）。期待＝解放しない。
- **P−（減る側＝解放すべき）**: 追跡を打ち切った後に、ブローカーが失効（Rejected へ写る `Disabled`）・取消を答える。期待＝解放する（終端の `OrderExecuted` を 1 回）。
- **P0（不明）**: 照会が null（当日一覧に無い）・例外。期待＝解放しない。

| 形 | 条件 | P+ | P− | P0 |
| --- | --- | --- | --- | --- |
| (a) 前の端だけ | 発注から一定時間（例: 追跡上限）を過ぎたら解放 | ✗（生きている注文を解放する） | ✓ | ✗（不明を解放する） |
| (b) 後の端だけ | 打ち切りを問わず、ブローカーが終端と答えたら解放（＝全期間を照会し続ける） | ✓ | ✓ | ✓ |
| (c) 両端 | 追跡を打ち切った（前の端から上限を過ぎた）**かつ** ブローカーが終端と答えた | ✓ | ✓ | ✓ |

(b) と (c) は 3 プローブでは区別できない。違いは照会の量である。(b) は追跡上限を過ぎた**全ての**非終端の記録（Active な S0 の逆指値レグ・S1 の決済レグを含む）を照会し続けることになり、追跡上限（IADR-0113）を置いた理由と打ち切りの記録（IADR-0481）を無意味にする。(c) は追跡の窓の内側を通常の追跡に任せ、外側を承認経路の決済に絞って低頻度で照会する。**(c) を採る。** 実装後に変異で実測する（下の「実測」）。

### 定期ログの形

- 前の端だけ（発注から 15 分以上）: 終端した記録も出してしまう → 状態（後の端）も見る。
- 後の端だけ（非終端なら出す）: 発注直後の決済まで毎回出す（指値が数秒で約定する平常を警告にする）。
- 両端（非終端 かつ 発注から 15 分以上）を採る。

## 母集合（規則 9・10。着手前に引いた）

### 規則 9: 誤りの側の語で走査

`git grep -n "SetTimeInForce\|TimeInForce\|MoomooOrderRequest(\|FindPendingCloses\|MarkTrackingAbandoned\|FindTrackingExpired" -- backend docs .ai-context/adr`

| 場所 | 扱い |
| --- | --- |
| `MMApiMoomooTradeClient.PlaceOrderAsync`（`SetTimeInForce` 無し） | **是正の本体**（`TimeInForce` があれば `SetTimeInForce`） |
| `IMoomooTradeClient.cs` の `MoomooOrderRequest` | 省略可能な `TimeInForce` を末尾へ足す（既存の呼び出しは無改変） |
| `MoomooBrokerAdapter.PlaceCoreAsync` / `PlaceWithRejectionDetailAsync` | `Limit`＋`Close` のときだけ `Day` を載せる。S3 の口は渡さない（null） |
| `IndeterminateDispatchFaultInjection`（`_inner.PlaceOrderAsync(request)`） | 要求をそのまま渡す。**変えない**（TIF は保たれる） |
| `ScriptedPositionsMoomooClient` ほか試験の偽物 | 要求を受けるだけ。**変えない** |
| `OrderFillPoller.CollectAbandonedAsync` / `OrderFillPollingService`（打ち切り） | 打ち切りは変えない。打ち切った**後**の照会し直しを足す |
| `IExecutedOrderStore` / `EfExecutedOrderStore` / `InMemoryExecutedOrderStore` | 問い合わせを 2 つ足す（打ち切った承認経路の決済・発注から一定時間たった承認経路の決済）。列は足さない（移行なし） |
| `OrderExecutionAppService.CountInFlightClosesAsync`（`FindPendingCloses`） | **変えない**（ブローカーの「生きている」を要件にしている。#1340 が `InFlightCloses.cs` へ移す） |
| `SoftwareStopExecutor` / `SoftwareStopFloorRetrofitter`（`FindPendingCloses`） | **変えない**（同じ見分け方） |
| `FillPollingOptions` | 間隔 2 つを足す |
| `docs/functional/FR-10_risk-controls.md`・`docs/tests/FR-10_risk-controls-tests.md` | 節と試験 ID を足す |
| `.ai-context/specs/` の過去の仕様書 | 凍結記録。変えない |

### 規則 10: この変更で新たに誤りになる自分の記述

- `MoomooOrderRequest` の注記「既定はマーケタブルリミット」→ 有効期限の項を足す。
- `IExecutedOrderStore.MarkTrackingAbandoned` の注記「状態・数量・価格・起点には触れない」→ 正しいまま（解放は `UpdateOutcome` で行い、印は触らない）。
- `OrderFillPoller` の冒頭注記「追跡上限を過ぎて非終端のまま残った注文は、追跡の打ち切りを監査へ残す」→ 承認経路の決済だけは打ち切りの後も低頻度で照会し直す、を足す。
- `FillPollingOptions.MaxTrackingHours` の注記「以降は滞留として人手／リコンサイルの領分に戻す」→ 承認経路の決済の例外を足す。
- `OrderFillPollingService` の注記「照会不達・不明は据え置きます」→ 変わらない。
- 導出値: 既定の照会し直しは 10 分ごと・バッチ上限は既存の `BatchSize`（200）。対象は保有建玉の数で頭打ち。

### 衝突（#1340）

- #1340 は `OrderExecutionAppService.cs`（`CountInFlightClosesAsync` を `InFlightCloses.cs` へ移す）・`Program.cs` を変える。本件はどちらも変えない。
- 文書の衝突はあり得る: `.ai-context/adr/README.md` の末尾の行（#1340 が IADR-0535 を足す）、`docs/functional/FR-10_risk-controls.md`・`docs/tests/FR-10_risk-controls-tests.md` の `updated:` と trace ブロックの `iadrs:`。いずれも行の足し合わせで解ける。
- 試験 ID は #1340 の T-10-2642〜T-10-2651 を避け、**T-10-2652〜**を使う。

## 受け入れ基準 → テスト

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 承認経路の指値の決済（`Limit`・`Close`）は `TimeInForce=Day` で送る。保護逆指値（`Stop`）・S3（`StopLimit`/`TrailingStop`）・成行・新規建ての指値は `TimeInForce` を載せない（null） | T-10-2652 |
| 2 | `MMApiMoomooTradeClient` は `TimeInForce=Day` のとき `TrdPlaceOrder.C2S.TimeInForce=TimeInForce_DAY` を送り、null のときは欄を立てない（保護逆指値の要求は従来どおり） | T-10-2653 |
| 3 | 追跡を打ち切った承認経路の決済を照会し直し、ブローカーが終端（失効＝Rejected・取消・約定）と答えたら記録を終端化し、`OrderExecuted` を 1 回出す（解放） | T-10-2654 |
| 4 | 🔴 ブローカーが非終端（Accepted・一部約定）・null・例外と答えたら解放しない（記録は非終端のまま。一部約定は進捗だけ記録する） | T-10-2655 |
| 5 | 照会し直しの対象の境界: 追跡中（打ち切っていない）・出どころの無い決済（S1 の決済・保護レグ）・新規建て・終端済みは照会しない | T-10-2656 |
| 6 | 常駐が照会し直しを間隔ごとに 1 回だけ行い、解放を INF で残す | T-10-2657 |
| 7 | 未約定の承認経路の決済を間隔ごとに WRN で出す（発注から 15 分以上・非終端のみ。0 件なら出さない。間隔の内側では繰り返さない） | T-10-2658 |
| 8 | EF のストアの問い合わせ 2 つがインメモリと同じ集合を返す | T-10-2659 |

## 実測（変異）

変異ごとに、新しい試験（T-10-2652〜T-10-2659）を実行した。すべての変異で 1 件以上が赤になった（スクリプト `scratchpad/ast1339/mutate.py`。各変異の後に元へ戻す）。窓の表の (a) は MT5、(b) は MT8 にあたる。

| 変異 | 内容 | 赤になる試験 |
| --- | --- | --- |
| MT1 | 新規建ての指値にも DAY を載せる | T-10-2652（陰性） |
| MT2 | 決済なら種別を問わず DAY（保護逆指値にも載る） | T-10-2652（陰性） |
| MT3 | DAY を載せない | T-10-2652（陽性 3 件） |
| MT4 | `SetTimeInForce` を呼ばない | T-10-2653 |
| MT5 | 🔴 前の端だけ（打ち切ったら照会せずに解放） | T-10-2654・T-10-2655（4 件すべて）・T-10-2656・T-10-2657 |
| MT6 | 🔴 照会が不明（null）でも解放する | T-10-2655（null）・T-10-2656 |
| MT7 | 出どころを見ない（保護の機構のレグも照会する） | T-10-2656・T-10-2659（インメモリ） |
| MT8 | 後の端だけ（打ち切りの印を問わず照会する） | T-10-2656・T-10-2659（インメモリ） |
| MT9 | 照会し直しの間隔を見ない | T-10-2657 |
| MT10 | 定期ログの間隔を見ない | T-10-2658 |
| MT11 | 定期ログで発注からの経過を見ない | T-10-2658 |
| MT12 | EF の問い合わせで打ち切りの印を見ない | T-10-2659（EF） |
| MT13 | EF の問い合わせで状態を見ない（定期ログ） | T-10-2659（EF） |


## 残余

- `QueryOrderAsync` は当日の注文一覧だけを見る。失効した注文が翌日の一覧に現れなければ照会は null になり、**解放されない**（迷ったら解放しない）。このとき決済のゲートは null を数えないので、決済は止まらない。記録は非終端のまま残り、定期ログに「打ち切り済み」と出続ける。履歴の照会を足すかは PoC（10-14 の開場後の確認）の結果で決める。
- OpenD の状態が不明（`TIMEOUT` 等 → 非終端）のまま返り続けると、ゲートはそれを生きているとして数える（IADR-0461 の既存の残余）。
- 取消と置き直し・成行への切り替えはしない（裁定）。指値が当日中に約定しなければ、翌日に判断し直すまで決済は出ない。
