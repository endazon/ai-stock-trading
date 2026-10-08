---
title: 突合で確定した発注の記録に承認の出どころを運ぶ —— 予約の行に ApprovalOrigin を持たせる（#1253）
type: spec
status: accepted
related_ids: [FR-10, UC-06, ADR-0050, IADR-0515, IADR-0486, IADR-0466, IADR-0057, IADR-0074, IADR-0092]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0050_decision-close-nets-in-flight-closes-and-stop-line-exit-only-without-mechanical-stop.md (決定 1)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 手仕舞いと損切りは止めない)
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md (UC-06 利用者の手仕舞い・維持率割れの自動縮小)
---

# 突合で確定した発注の記録に承認の出どころを運ぶ（#1253）

## 起点

- [#1253](https://github.com/endazon/ai-stock-trading/issues/1253)（IADR-0515 の残余。#1222 / PR #1252 の切り出し）。
- 計画 ADR-0050 決定 1・FR-10・UC-06。計画の裁定は要らない（IADR-0515 の範囲の実装判断。同 IADR が「採らなかった案」に置いた予約の行への列の追加を、残余の解消として採る）。
- 前例: IADR-0486 決定 6（`StopFloorSource` を予約の行に残し、突合が組み直す記録へ写す。マイグレーション `20261003024142_AddReservationStopFloorSource`・
  試験 `StopFloorMarkerPersistenceTests` T-10-2203 / T-10-2204）。

## 現況（origin/develop bdd56434 で確認）

| # | 事実 | 場所 |
| --- | --- | --- |
| 1 | 承認の経路の発注は相 4 で `ExecutionRecord.ApprovalOrigin` を書く（`Unknown` は null） | `OrderExecutionAppService.cs`（相 4 の `store.Save`） |
| 2 | 予約（相 1）は `TryReserve(decisionId, now, broker.Provider, intent.StopFloorSource)` で、出どころを残さない | 同 `TryReserve` の呼び出し |
| 3 | 突合の `BuildRecord` はブローカーの注文から記録を組み直し、予約の行の `StopFloorSource` だけを写す。出どころは null | `OrderReservationReconciler.cs`（`BuildRecord`） |
| 4 | 予約の行 `order_dispatch_reservations` に出どころの列は無い | `OrderDispatchReservationRow.cs`・スナップショット |
| 5 | `IOrderReservationStore` の実装は本番 2（EF・インメモリ）＋試験の包み 4（`OrderReservationRetentionServiceTests`・`StopLossMethodCoexistenceTests`・`OrderExecutionServiceTests`・`StopLegScriptedBroker`） | `grep -rn "bool TryReserve(" backend` |

## 設計（IADR-0515 追記(1)）

1. **予約の行**: `OrderDispatchReservationRow.ApprovalOrigin`（`OrderApprovalOrigin?`）と列 `order_dispatch_reservations.ApprovalOrigin`（integer NULL）。
   マイグレーション `20261008045915_AddReservationApprovalOrigin` は `dotnet ef migrations add` で生成し、**列の追加だけ**。🔴 **既存行は null のまま埋めない**（分からない＝取り消す側）。
2. **ポート**: `IOrderReservationStore.TryReserve` に省略可能な末尾引数 `approvalOrigin`（既定 null）、`OrderDispatchReservation` に `ApprovalOrigin`（既定 null）。
   EF・インメモリの両方が保存し、`Find` / `FindStalledReserved` で読み戻す。`MarkCompleted` は列に触れない（EF は行の更新・インメモリは `with`）。
3. **書き手**: 承認の経路の相 1（`OrderExecutionAppService`）だけが渡す。値は相 4 と同じ式（`Unknown` は null）。保護の機構の予約（S0/S3 の逆指値レグ・保護喪失の成行・S1 の決済）は渡さない＝null。
4. **突合**: `BuildRecord` に `approvalOrigin` を足し、予約の行の値を組み直した記録の `ApprovalOrigin` へ写す。予約の行が null なら null のまま（推測しない）。
   phase-4 自己修復（記録が既に在る）・競合（通常フローが確定済み）は既存の記録をそのまま使う（変えない）。

窓（規則 11）は該当しない: 出どころは予約を取る時点で 1 回だけ書かれ、以後変わらない静的な属性であり、時間差で増える側・減る側を持たない。

## 母集合（規則 9・10）

- 前例の語 `StopFloorSource` で `backend/Services/OrderExecutionService` を走査（`grep -rln StopFloorSource`）: 予約の経路は
  行・ポート・EF・インメモリ・相 1・突合・マイグレーション 2 本（Designer 含む）・スナップショット・試験 1 本。本件は同じ箇所に同じ形で足す
  （記録側 `executed_orders` は #1222 で済み）。
- `bool TryReserve(` の実装（上の現況 5）: 6 つすべての署名を揃えた。試験の包みは引数をそのまま内側へ渡す。
- `TryReserve(` の呼び出し（テスト以外）: 6 か所（承認の相 1・S0 の逆指値・保護喪失の成行・S1 の決済・常駐ガードの逆指値・常駐ガードの成行）。出どころを渡すのは承認の相 1 だけ。
- 本変更で新たに誤りになる自分の記述: IADR-0515 の「採らなかった案（予約の行にも列を足す）」と残余「突合で確定した記録は分からない」→ 凍結記録なので本文は書き換えず日付つき追記で改める。
  索引行（README）の「却下: …予約の行への列の追加」「残余: 突合で確定した記録…」も同じく追記で改める。テスト仕様書の #1222 の節の残余も新しい節から参照する。
  `IOrderReservationStore.TryReserve` の `stopFloorSource` の説明「省略は null（分からない＝決済・保護レグ）」は本件でも正しい（出どころとは別の引数）。

## 受け入れ基準 → 試験

| # | 受け入れ基準（#1253） | 試験 |
| --- | --- | --- |
| 1 | 利用者の手仕舞い（自動縮小・判断も同様）の送信結果が不明で予約が Reserved のまま、突合が発注済みと確定 → 組み直した記録の `ApprovalOrigin` は予約の出どころ | T-10-2453（EF・インメモリ） |
| 1' | 端から端まで: 突合で確定した利用者の手仕舞い・自動縮小は S1 に取り消されず差し引かれる | T-10-2454 |
| 2 | 否定形: 出どころの無い予約（列を足す前の行）は null のまま。S1 は従来どおり取り消してから送る。猶予を過ぎた突合の記録も通常の経路と同じく取り消す | T-10-2453（null）・T-10-2455 |
| 3 | EF・インメモリの両方の予約ストアで同じく保存・読み戻しできる。承認の相 1 が予約の行に書く（`Unknown` は null） | T-10-2452 |

変異（1 本ずつ当てて戻した）: `BuildRecord` へ null を渡す → T-10-2453 の 4 件・T-10-2454 の 2 件・T-10-2455 の 1 件が赤。
相 1 で出どころを渡さない → T-10-2452 の 3 件が赤。EF の `TryReserve` で列に書かない → T-10-2452（EF）・T-10-2453（EF）の 3 件が赤。

## 検証

- `dotnet build backend/backend.slnx`（警告 0）・`dotnet format --verify-no-changes`
- `dotnet ef migrations has-pending-model-changes`（スナップショットとモデルの一致）
- `dotnet test`: OrderExecutionService.Tests（1 本ずつ・後で bin/obj を消す）
- `node scripts/scripts.test.js`・check-trace-blocks・gen-knowledge-graph --check・check-test-traceability・check-adr-index-sync・
  check-adr-index-addendum-loss・check-cross-repo-refs・check-commit-messages

## 残余

- 🔴 **本番の照会（`MoomooReservationBrokerProbe`）が組み直す発注意図は `PositionEffect.Open` で近似される**（`ToBrokerOrder`。moomoo の注文から一意に復元できない。照会は IADR-0092）。
  突合で確定した利用者の手仕舞いの記録は Open として残り、S1 の処理中の決済の読み出し（`FindPendingCloses`＝Close だけ）に**載らない**。
  したがって本件の是正（出どころが記録に在る）は、照会が Close を復元できるときに効く。現状の本番の照会では、突合で確定した決済は S1 に取り消されもしないが
  差し引かれもしない（#1222 以前の「取り消される」とも違う形）。照会で `PositionEffect` を復元する（または予約の行に決済の向きを残す）のは別の件。
- 🔴 猶予（`NettedCloseGrace`＝2 分）の起点は記録の時刻（`ExecutedAt`＝証券会社が答えた発注の時刻、答えなければ突合の時刻）である。突合は滞留の閾値
  （下限 1 時間）より古い予約しか扱わないため、証券会社が発注の時刻を答える限り、突合で確定した記録は猶予を過ぎており、通常の経路と同じく取り消される
  （T-10-2455）。差し引かれるのは発注の時刻が分からず突合の時刻で記録したときに限られる（T-10-2454）。
- 列を足す前の予約の行・切り替え前のリスク管理が出した承認は null のまま（分からない＝取り消す側。是正前と同じ）。
- 試験の採番: T-10-2452〜T-10-2455（着手時の最大 T-10-2451 の次）。
