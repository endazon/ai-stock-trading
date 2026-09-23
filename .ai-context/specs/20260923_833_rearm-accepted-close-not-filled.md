---
title: 受理だけで完了させた S1 の保護記録を、決済が「確認できた終端かつ未約定」で終わったときに約定追跡から再武装する
type: spec
status: accepted
related_ids: [FR-10, FR-12, UC-02, ADR-0040, ADR-0016, IADR-0344, IADR-0113, IADR-0210, IADR-0118, IADR-0057, IADR-0389]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-24
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 損切り)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1 の S1)
---

# 仕様書: 受理後に取消・失効した決済で無保護になる穴を塞ぐ（#833 項目 1）

## 起点

- [#833](https://github.com/endazon/ai-stock-trading/issues/833) の **項目 1 のみ**。項目 2（拒否連発の待ち時間）・
  項目 3（古い写しの上書き＝楽観並行）・項目 4（無期限の据え置き・着地済み）は**本仕様書の射程外**である。
- 同 issue の 2026-09-23 トリアージが `develop` で引き直した実測（`git rev-parse --is-shallow-repository` → `false`）。

## 🔴 実測（コードで確認・`origin/develop` = `2d9e6bf3`）

`SoftwareStopExecutor.Settle` は決済注文の状態が `Accepted or PartiallyFilled or Filled` のとき**約定したのと同じ帳簿処理**を行う。

| 行 | 現在の挙動 |
| --- | --- |
| `SoftwareStopExecutor.cs:300` | `Accepted` を成功側の分岐へ入れる |
| `:305-317` | 受理数量ぶん `RemainingProtected` を減らし、0 になれば `State = Completed` |
| `:321-323` | 発注時点で `SoftwareStopOutcome.ClosePlaced` を発行する |

moomoo の模擬取引の注文は**当日限り**であり、0 約定のまま失効・取消され得る（#809 の調査で確認済み）。
その場合:

1. 建玉は減っていないのに `RemainingProtected = 0` / `State = Completed` になっている。
2. `Completed` の行は `FindActive` / `FindActiveSoftwareStops` のどちらにも載らないため、**ガードも到達ハンドラも二度と見ない**。
3. 約定追跡（`OrderFillPoller`）は決済レグの記録を `Cancelled` / `Expired` へ終端化して `OrderExecuted` を出すが、
   **保護記録には一切触らない**（`OrderFillPoller.cs` に `IProtectiveStopOrderStore` の参照は無い）。
4. その口座に有効な保護記録が 1 件も残らないと、`ProtectiveStopGuard` は巡回対象ゼロで早期 return し建玉を照会しないため、
   帰属不明建玉の検知（`ProtectiveStopNetting.DetectUnattributedPositions`）も走らない
   （IADR-0344 追記(9)・`ProtectiveStopGuard.cs:132-140` が自認。追随先として挙げられている #880 は未マージ）。

→ **建玉は無保護・記録は完了・Critical はゼロ**。稼働中の PoC（AAPL 707 株・S1・ライン 338.51）はこの配置に該当する。

## 射程

**「確認できた終端かつ未約定」の決済レグだけを根拠に、保護記録を再武装する。**

1. **再武装の起点は既存の約定追跡（`OrderFillPoller`）である。** 新しい常駐・新しいポーラーを作らない
   （非終端の決済レグを引き直して終端化するのはこの 1 箇所だけであり、そこが唯一「未約定で終わった」を**確認**できる点である）。
2. **再武装してよいのは `OrderStatusLifecycle.AbandonsUnfilledRemainder(status)`（`Cancelled` / `Rejected` / `Expired`）だけ**である。
   🔴 `Filled` は含めない。照会が `null`（不明）・例外は**従来どおり何も書かずに据え置く**（`OrderFillPoller` の既存の規律）。
3. **再武装量は「発注数量 − 確定した約定数量」**（未約定残）。全量未約定なら全量、部分約定なら残りだけを戻す。
4. **上限を掛ける**: 戻した後の `RemainingProtected` は**エントリーの約定数量**（記録が無ければ行の承認数量）を超えない。
5. **再武装したら必ず 1 回知らせる**: `SoftwareStopExecuted(CloseUnfilled)`（**Critical**）＋ `LogError`。
6. **不明を無音にしない**: 決済レグの照会が `null` に倒れ続ける間は、`UnresolvedCloseNotificationTracker`
   （プロセス内・1 時間間隔）で Critical ログを出す。**再武装も完了もしない。**

### 射程外（やらないこと）

- #833 項目 2（行ごとの指数バックオフ・`NextAttemptAt` の永続化）。
- #833 項目 3（`protective_stop_orders` への楽観並行の版番号。migration を伴うため別 PR）。
- #833 項目 4（据え置きの Critical。`NotifyIfStalled` として着地済み）。
- **`ClosePlaced` の発行時点を「約定してから」へ動かすこと。** issue の提案はそう書いているが、
  台帳の承認行の結線（`ProtectiveStopLedgerHandlers`）が `ClosePlaced` に依存しており、発行時点を動かすと
  在庫の押さえが決済の往復ぶん遅れる。**受理で押さえて、未約定で終わったら戻す**方を採る（IADR-0389 決定 1）。
- スキーマ変更（列の追加・migration）。本 PR は**既存の列・既存のストア API だけ**で閉じる。
- `#880`（帰属不明建玉の常駐検知）。本 PR は行を `Active` へ戻すので、戻った後はガードの巡回に載る。

## 🔴 母集合（走査したファイルと除外理由）

`grep -rn "RemainingProtected\|IsTerminal\|AbandonsUnfilledRemainder\|SoftwareCloseDecisionId" backend --include=*.cs`
（`obj` 除く）と `grep -rn "SoftwareStopOutcome" backend --include=*.cs` の和から引いた。

- **採る（変更する）**:
  - `Features/OrderExecution/PollOrderFills/OrderFillPoller.cs` — 終端化の唯一の観測点。フック。
  - `Features/OrderExecution/ExecuteSoftwareStops/SoftwareStopReArmer.cs`（新規）— 再武装の規則。
  - `Features/OrderExecution/ExecuteSoftwareStops/UnresolvedCloseNotificationTracker.cs`（新規）— 不明の Critical の間引き。
  - `Features/OrderExecution/ExecuteSoftwareStops/SoftwareStopExecutor.cs` — `Settle` の受理分岐に
    「受理は約定ではない」旨の注記のみ（**挙動は変えない**。帳簿の減算を止めると台帳の押さえが崩れる）。
  - `Hosted/OrderFillPollingService.cs` — 再武装が返したイベントの発行。
  - `Program.cs` — `SoftwareStopReArmer` / `UnresolvedCloseNotificationTracker` の登録（moomoo 構成のみ）。
  - `Shared.Contracts/Events/SoftwareStopExecuted.cs` — `CloseUnfilled` の追加（**序数は末尾**。IADR-0134 決定2）。
  - `NotificationService/Features/Notifications/NotificationFormatter.cs` — 文面（Critical）。
  - `AuditService/Domain/AuditEntryFactory.cs` — 監査の結末文。
  - `docs/tests/FR-10_risk-controls-tests.md` — テスト ID 表（T-10-700..T-10-711・T-10-730..T-10-731）。
- **除外**:
  - `GuardProtectiveStops/ProtectiveStopGuard.cs` — 再武装後の行は既存の巡回にそのまま載るので変更不要。
    **PR [#916](https://github.com/endazon/ai-stock-trading/pull/916) が同ファイルを編集中**であり、触らないことで衝突も避ける。
  - `ProtectiveStopNetting.cs` — 持ち分の規則は変えない。**PR [#918](https://github.com/endazon/ai-stock-trading/pull/918) が編集中。**
  - `Infrastructure/Persistence/*` — 新しい列・新しいストア API を足さない（既存の
    `FindActiveSoftwareStops` / `FindCompletedSoftwareStops` で決済レグから行を引ける）。
  - `RiskManagementService/Infrastructure/Steps/ProtectiveStopLedgerHandlers.cs` — `ClosePlaced` だけを見ており、
    `CloseUnfilled` では何もしない（台帳の押さえは決済レグの `OrderExecuted`（Cancelled）が既に解く。#848 / IADR-0357）。
  - `MarketMonitorService/*` — 到達検知の側は変えない。

## 決済レグから保護記録を引く方法（新しい列を足さない根拠）

決済レグの `DecisionId` は `ProtectiveStopIds.SoftwareCloseDecisionId(entryDecisionId, attempt)`＝
`SHA-256("software-stop-close:<entry:N>:<attempt>")` の先頭 16 バイトで、**決定的**である。逆関数は無いが、
候補は有限に絞れる:

1. 決済レグの記録は `Symbol` / `Market` / `Side`（＝決済方向）を持つ。エントリー方向はその反対である。
2. `FindActiveSoftwareStops(symbol, market, entrySide)` と `FindCompletedSoftwareStops(symbol, market, entrySide, 50)`
   で候補行を引く（**完了済みも引く**——受理で完了させられた行こそが本件の対象である）。
3. 各候補行について `attempt = row.Attempt` から下へ最大 50 試行ぶん再導出し、`DecisionId` が一致したら確定。
   一致しなければ**その記録は S1 の決済レグではない**（保護喪失の手仕舞い `protective-close:` は名前空間が違う）。

🔴 **一致しなかったことを異常として扱わない。** 約定追跡は全注文を見るため、一致しないのが通常である。

## 再武装の規則（二重売りが起きない根拠）

`U = 発注数量 − 確定した約定数量`（未約定残）とする。

- `U` の株数は**この決済注文では二度と約定しない**（`Cancelled` / `Rejected` / `Expired` は終端であり、
  `AbandonsUnfilledRemainder` が `Filled` を除外している）。よって「後からその注文が約定して売り過ぎる」経路は無い。
- 再武装は `RemainingProtected` を `min(現在値 + U, エントリーの約定数量)` にするだけで、**注文は 1 株も出さない。**
  上限により、同じレグを二度観測しても行の主張がエントリーの約定数量を超えない。
- 実際に売るのは従来どおり `SoftwareStopExecutor.TryCloseAsync` であり、そこは**送る前に必ず建玉を照会し**
  （`positions.GetPositionsAsync`。`null` は据え置き）、`ProtectiveStopNetting.ReconcileShares` で
  外部要因の減少を突き合わせ、`EffectiveProtectedQuantity` を上限にしか送らない。
  → 再武装した主張が実建玉より多ければ、**売る前に**外部要因の観測として削られる（IADR-0344 追記(7)）。
- 🔴 IADR-0344 追記(7) が撤去した `ProtectiveStopNetting.Restore` とは**根拠が違う**。あちらは
  **建玉照会の純額**から「自分の建玉が戻った」を推測していた（他人の建玉と区別できない）。
  本件は**自分が出した注文そのものの終端状態**から「売れなかった株数」を読む。純額を見ていないので、
  幽霊行の復活（BLK-7-2）は構造的に起き得ない。

## 受け入れ基準

| # | 基準 |
| --- | --- |
| A1 | 受理で `Completed` になった行は、決済レグが 0 約定で `Expired` になったら `Active` へ戻り `RemainingProtected` が元の株数になる |
| A2 | `Cancelled` / `Rejected` でも A1 と同じ（3 状態を等しく扱う） |
| A3 | 部分約定のまま終端したら**未約定残だけ**が戻る（`Active` のまま主張が増える） |
| A4 | `Filled` では何も起きない（再武装しない・イベントも出ない） |
| A5 | 照会が `null`（不明）では**再武装も完了もせず**、記録は非終端のまま残り、Critical ログが出る |
| A6 | 再武装のたびに `SoftwareStopExecuted(CloseUnfilled)`（Critical）が 1 回出る |
| A7 | 再武装後の主張はエントリーの約定数量を超えない（同じレグを二度観測しても増えない） |
| A8 | S1 以外の注文（エントリー・S0 の手仕舞い）の終端化では保護記録に触らない |
| A9 | 再武装は到達の記録（`TriggeredAt`）を消さない＝ガードが次の巡回で決済を撃ち直す |
| A10 | 再武装の失敗（ストア例外）は約定追跡の巡回を止めず、Critical を残す |
| A11 | A6 の `CloseUnfilled` は常駐（`OrderFillPollingService`）が**メッセージ基盤へ実際に発行する**（結果に載るだけでは満たさない） |
| A12 | 同じ銘柄に S1 の行が複数あっても、戻すのは**失効した決済レグを出した行だけ**で、他の行には触らない |

## テスト

`docs/tests/FR-10_risk-controls-tests.md` に **T-10-700..T-10-711**（＋枝番 T-10-703b）と **T-10-730..T-10-731** を追加した
（622..699 は他レーンが保持。730..733 はフレッシュ文脈の監査が挙げた 2 つの穴のために割り当てられ、730・731 を使った）。
実体は `SoftwareStopReArmerTests`（16 ケース）と `OrderFillPollingServiceTests` の T-10-730（1 ケース）。
xUnit・注入した時計のみ（壁時計の待ちを作らない）。

🔴 **変異確認（2026-09-24 実測。`OrderExecutionService.Tests` 全 725 件で実行）**:

1. 約定追跡のフック（`reArmer?.OnCloseTerminalized(...)`）を外す → **10 件が赤**（725 件中 715 件合格）。
   `SoftwareStopReArmerTests` では T-10-700 / T-10-701 ×2 / T-10-702 / T-10-705 / T-10-708 / T-10-709 / T-10-731 ×2 の
   9 件（16 件中 7 件合格）、残る 1 件は T-10-730。
   このとき T-10-703 / T-10-703b / T-10-704 / T-10-706 / T-10-707 / T-10-710 / T-10-711 は緑のまま
   —— **否定形（何も起きないこと）と、実行器を直接叩くケースはこの変異では動かない**。
   （2026-09-23 に記録した「13 件中 6 件合格」は T-10-703b を足す前の数えで、足した後は 14 件中 7 件合格だった。）
2. 判定を `AbandonsUnfilledRemainder` → `IsTerminal` へ広げる → **T-10-703b だけが赤**（725 件中 724 件合格）。
   1 巡目の実装では T-10-703（全量約定）だけを置いていたため**この変異が素通りした**ので、
   「`Filled` と申告しつつ約定数量が足りない応答」のケース（T-10-703b）を足して判定を固定した。
3. 常駐の `CloseUnfilled` の発行（`OrderFillPollingService` の `bus.PublishAsync(reArmed)`）を外す
   → **T-10-730 だけが赤**（725 件中 724 件合格）。T-10-730 を足す前は、この変異が 722 件すべて緑のまま素通りした
   （`SoftwareStopReArmerTests` は結果の `SoftwareStopEvents` までしか見ない）。
4. 決済レグの持ち主の特定を「試行番号まで一致した行」から「候補の先頭の行」へ緩める
   → **T-10-731（A が先頭の並び）が赤**。「候補の末尾の行」へ緩めると他方の並びが赤になる（いずれも 725 件中 724 件合格）。
   稼働 PoC は AAPL に S1 の行を 2 本（715 株・ライン 330.88 と 713 株・ライン 331.67）持っており、
   この変異は B の失効で A を戻し、B を無保護のまま残す。T-10-731 を足す前は素通りした。
