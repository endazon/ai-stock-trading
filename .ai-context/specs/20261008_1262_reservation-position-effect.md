---
title: 突合で確定した決済の記録を送った時の建て・決済の別で組み直す —— 予約の行に PositionEffect を持たせる（#1262）
type: spec
status: accepted
related_ids: [FR-10, UC-06, ADR-0050, IADR-0515, IADR-0486, IADR-0466, IADR-0461, IADR-0389, IADR-0362, IADR-0092, IADR-0057]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0050_decision-close-nets-in-flight-closes-and-stop-line-exit-only-without-mechanical-stop.md (決定 1)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 手仕舞いと損切りは止めない)
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md (UC-06 利用者の手仕舞い・維持率割れの自動縮小)
---

# 突合で確定した決済の記録を送った時の建て・決済の別で組み直す（#1262）

## 起点

- [#1262](https://github.com/endazon/ai-stock-trading/issues/1262)（#1253 / PR #1261 の残余。IADR-0515 追記(1) の残余 1 つ目）。
- 計画 ADR-0050 決定 1・FR-10・UC-06。計画の裁定は要らない（IADR-0515 の範囲の実装判断。同追記が「別の件」とした決済の向きの復元を、予約の行に残す形で行う）。
- 前例: IADR-0486 決定 6（`StopFloorSource`）・IADR-0515 追記(1)（`ApprovalOrigin`。マイグレーション `20261008045915_AddReservationApprovalOrigin`）。

## 現況（origin/develop 275df0d4 で確認）

| # | 事実 | 場所 |
| --- | --- | --- |
| 1 | 本番の照会は発注意図を組み直すとき `PositionEffect` を常に `Open` にする（moomoo の注文は建て・決済の別を返さない） | `MoomooReservationBrokerProbe.ToBrokerOrder` |
| 2 | 突合の `BuildRecord` は照会が返した発注意図の `PositionEffect` をそのまま記録へ写す | `OrderReservationReconciler.BuildRecord` |
| 3 | 予約の行 `order_dispatch_reservations` に建て・決済の別の列は無い | `OrderDispatchReservationRow.cs`・スナップショット |
| 4 | 処理中の決済の読み出し（`FindPendingCloses`）は `Close` の記録しか返さない | `IExecutedOrderStore`・EF・インメモリ |
| 5 | S1 の決済レグの再武装（`SoftwareStopReArmer`）は `Close` 以外の記録を候補にしない | `OnCloseTerminalized`・`OnCloseUnresolved` |
| 6 | 通常の経路（送信結果が分かった発注）は、承認の経路は `intent.PositionEffect`、保護の機構（S0/S3 の逆指値レグ・保護喪失の成行・S1 の決済・常駐ガードの逆指値と成行）は `Close` で記録する | `OrderExecutionAppService`・`SoftwareStopExecutor`・`ProtectiveStopGuard` の `store.Save` |

したがって、送信結果が不明のまま突合で確定した決済（利用者の手仕舞い・維持率割れの自動縮小・判断の手仕舞い・保護の機構の決済）は
`Open` の記録として残り、S1 の取消・差し引き・判断の手仕舞いの差し引き・S1 の再武装のいずれにも載らない（#1253 の是正が moomoo の経路で効かない）。

## 設計（IADR-0515 追記(2)）

1. **予約の行**: `OrderDispatchReservationRow.PositionEffect`（`PositionEffect?`）と列 `order_dispatch_reservations.PositionEffect`（integer NULL）。
   マイグレーション `20261008053053_AddReservationPositionEffect` を `dotnet ef migrations add` で生成し、**列の追加だけ**。🔴 **既存行は null のまま埋めない**。
2. **ポート**: `IOrderReservationStore.TryReserve` に省略可能な末尾引数 `positionEffect`（既定 null）、`OrderDispatchReservation.PositionEffect`（既定 null）。
   EF・インメモリの両方が保存し、`Find` / `FindStalledReserved` で読み戻す。`MarkCompleted` は列に触れない。
3. **書き手（6 か所すべて）**: 予約を取る経路は、通常の経路が記録に書くのと同じ値を渡す。
   承認の相 1 は `intent.PositionEffect`、保護の機構の 5 か所は `PositionEffect.Close`。🔴 突合で確定した記録が通常の経路の記録と同じ形になる
   （読み出し側の除外の規則〔S0/S3 の保護レグを引かない等〕は通常の経路の記録で既に検証済みであり、新しい規則を足さない）。
4. **突合**: `BuildRecord` に `positionEffect` を足し、予約の行の値が在ればそれを、無ければ照会が返した値を記録の `PositionEffect` に書く
   （`reservation.PositionEffect ?? order.Intent.PositionEffect`）。照会ごとに直さず、突合の 1 か所で直す（試験の照会・将来の照会も同じ規則に乗る）。
   `MoomooReservationBrokerProbe` は `Open` の近似のままにし、注記だけを改める。phase-4 自己修復・競合の経路は既存の記録を使う（変えない）。
5. **列を足す前の行（受け入れ基準 3）**: 🔴 **null は照会の値（本番では `Open`）のまま＝是正前と同じ**。推測で `Close` にしない。
   - `Close` に倒すと、実際はエントリー（新規建て）だった行が決済として記録され、`FindPendingCloses` に載る。売り建てでは買いのエントリーが
     「処理中の買い戻し」になり、S1 の取消の段が**エントリーを取り消し得る**・判断の手仕舞いが差し引き過ぎる。`FindRecentOpens`（保護喪失の成行の見分け）と
     床の遡及（`SoftwareStopFloorRetrofitter`。Open の記録だけが対象）からエントリーが消える。誤りの害がエントリー側へ広がる。
   - `Open` のまま（是正前と同じ）の害は、列を足す前に滞留していた決済の予約が S1 に取り消されも差し引かれもしないこと（#1262 以前と同じ）に限られ、
     突合で確定した 1 件は既存の Critical の所見（IADR-0362 の ProbeTerminalized）で人に知らされる。対象は「配備の時点で滞留中の予約」だけ
     （滞留の閾値の下限 1 時間を過ぎた Reserved。新しい予約は列を持つ）で、時間とともに 0 になる。
   - 保護記録の有無から推測する案（突合の保護レグの判定と同じ手がかり）は採らない: 保護記録はエントリーの相 1 の直後に書くので、その書き込みが落ちた
     エントリーは「保護記録が無い＝決済」と読まれる（上の害）。

窓（規則 11）は該当しない: 建て・決済の別は予約を取る時点で 1 回だけ書かれ、以後変わらない静的な属性であり、時間差で増える側・減る側を持たない。

## 母集合（規則 9・10）

- `git grep -n "PositionEffect.Open" -- backend/Services/OrderExecutionService ':!backend/Services/OrderExecutionService/Tests/*'`（17 行）:
  - `OrderExecutionAppService.cs` 9 行（250/260/360/374/503/515/531/541＝送る前の承認の発注意図の判定。予約・突合を通らない。1201＝保護記録から組み直すエントリーの写し）→ 対象外。
  - `IExecutedOrderStore.cs:55`・`EfExecutedOrderStore.cs:99`・`InMemoryExecutedOrderStore.cs:93`（`FindRecentOpens`）→ **是正で突合で確定した決済が消える側**。
    是正前は突合で確定した決済（売り）が Open として載り、売り建ての S1 の見分けに余分な導出 ID を足していた（害は無い）。是正後は通常の経路と同じ。
  - `SoftwareStopFloorRetrofitter.cs:127`（Open の約定済みの記録だけを床の遡及の対象にする）→ 是正前は突合で確定した決済が Open に見えたが、
    遡及は S1 の行（エントリーの DecisionId）から記録を引くので決済の記録には届かない。是正後も変わらない。
  - `IndeterminateDispatchFaultInjection.cs:180/255`（送る時の要求の判定）→ 予約・突合を通らない。対象外。
  - `MoomooReservationBrokerProbe.cs:69` → **本件の発生源**。値は変えず、突合が予約の行の値で上書きする旨を注記する。
- `TryReserve` の実装（`git grep -n "bool TryReserve(" -- backend`）: 本番 2（`EfOrderReservationStore`・`InMemoryOrderReservationStore`）＋試験の包み 4
  （`OrderExecutionServiceTests`・`StopLossMethodCoexistenceTests`・`StopLegScriptedBroker`・`OrderReservationRetentionServiceTests`）＝ 6。すべての署名を揃える
  （試験の包みは引数をそのまま内側へ渡す。`ReservationStoreTestExtensions` の 2 引数の拡張は既定値で足りる）。
- `TryReserve(` の呼び出し（試験以外）: 6 か所。
  | 場所 | 予約する DecisionId | 通常の経路の記録 | 渡す値 |
  | --- | --- | --- | --- |
  | `OrderExecutionAppService.cs:351`（承認の相 1） | 承認 | `intent.PositionEffect`（相 4） | `intent.PositionEffect` |
  | `OrderExecutionAppService.cs:905`（S0 の逆指値レグ） | `StopDecisionId` | `Close`（971） | `Close` |
  | `OrderExecutionAppService.cs:1279`（保護喪失の成行） | `CloseDecisionId` | `Close`（1315） | `Close` |
  | `SoftwareStopExecutor.cs:467`（S1 の決済） | `SoftwareCloseDecisionId` | `Close`（503） | `Close` |
  | `ProtectiveStopGuard.cs:451`（常駐ガードの逆指値） | `StopDecisionId` | `Close`（505） | `Close` |
  | `ProtectiveStopGuard.cs:549`（常駐ガードの成行） | `CloseDecisionId` | `Close`（586） | `Close` |
- 決済の記録を数える箇所（`git grep -n "FindPendingCloses\|PositionEffect.Close\|PositionEffect != PositionEffect.Close" -- backend/Services/OrderExecutionService ':!*/Tests/*'`）:
  | 箇所 | 用途 | 是正で変わること |
  | --- | --- | --- |
  | `OrderExecutionAppService.CountInFlightClosesAsync`（:832。ADR-0050 決定 1・IADR-0461） | 判断の手仕舞いの差し引き | 突合で確定した決済（利用者・自動縮小・S1・保護喪失の成行）が数えられる。Active な S0/S3 の逆指値レグは通常の経路と同じく除く |
  | `SoftwareStopExecutor` 取消の段（:646。IADR-0466 / IADR-0515 決定 3） | S1 の決済の前の取消 | 突合で確定した判断の手仕舞いは取り消され、利用者・自動縮小は猶予の内なら残す（出どころは #1253 で運ばれる） |
  | `SoftwareStopExecutor.NetOwnerClosesAsync`（:739。IADR-0515 決定 4） | S1 の差し引き | 突合で確定した利用者・自動縮小の決済が差し引かれる |
  | `SoftwareStopFloorRetrofitter`（:192） | 床の遡及の前の処理中の決済の確認 | 突合で確定した決済が数えられる（通常の経路と同じ） |
  | `SoftwareStopReArmer`（:59/:155） | S1 の決済レグの再武装・不明の通知 | 突合で確定した S1 の決済が取り消された・不明のとき、再武装・通知の対象になる（是正前は Open のため黙って外れていた） |
  | `OrderExecutionAppService.cs:136`（承認の発注意図の判定） | 送る前 | 対象外 |
  他サービス（RiskManagement・Report・TradeDecision）の `PositionEffect.Open` はそれぞれの承認・台帳の行であり、`executed_orders` を読まない（`OrderExecuted` は建て・決済の別を運ばない）→ 対象外。
- 本変更で新たに誤りになる自分の記述:
  - IADR-0515 追記(1) の残余「本番の照会は … Open で近似するため突合で確定した決済は S1 の読み出しに載らない」→ 凍結記録なので本文は書き換えず、日付つき追記(2) で解消を記す。索引行も同じく追記で改める。
  - `IReconciledEntryProtection.cs:9`・`OrderReservationReconciler.cs:28` の「予約は PositionEffect を持たず／予約・プローブの PositionEffect は当てにならない」→
    保護レグを張るかの判別は引き続き保護記録で行う（本件で変えない）。予約が持つようになった事実と食い違うので注記を改める（判別の規則は変えない）。
  - `MoomooReservationBrokerProbe.ToBrokerOrder` の注記「ローカル永続の報告用途に限られ」→ 記録の `PositionEffect` は処理中の決済の読み出しに使われる。注記を改める。
  - `docs/tests/FR-10_risk-controls-tests.md` の #1253 の節の残余（照会の近似）→ 新しい節から解消を参照する。
  - 試験 `ReconciledEntryProtectionTests` の注記「PositionEffect=Open 固定」→ 照会の値としては正しい（照会は変えない）。変更なし。

## 受け入れ基準 → 試験

| # | 受け入れ基準（#1262） | 試験 |
| --- | --- | --- |
| 3 前提 | 予約の行は建て・決済の別を保存して読み戻す（EF〔別のコンテキスト〕・インメモリ。`Find`・`FindStalledReserved`。確定で消えない。省略は null） | T-10-2456 |
| 1 前提 | 6 つの書き手が通常の経路と同じ値を予約の行に残す（承認の相 1＝発注意図の値、保護の機構＝Close） | T-10-2457 |
| 1 | 送信結果が不明のまま突合で確定した決済（利用者の手仕舞い・自動縮小・判断の手仕舞い）の記録は、**本番の照会の写像**（`MoomooReservationBrokerProbe` が Open を返す）を通しても `Close` であり、`FindPendingCloses` に載る（EF・インメモリ） | T-10-2458 |
| 2 | 端から端まで（本番の照会の写像）: 突合で確定した利用者の手仕舞い・自動縮小は S1 に取り消されず差し引かれ、判断の手仕舞いは取り消される | T-10-2459 |
| 3 | 否定形: 列を足す前の予約の行（null）は照会の値（Open）のまま＝是正前と同じで、S1 はそれを取り消しも差し引きもしない。エントリーの予約（Open）は Open のまま | T-10-2460 |

試験の置き場所: `ReservationPositionEffectTests`（T-10-2456〜T-10-2458・T-10-2460 の記録）・`SoftwareStopDecisionCloseYieldTests`（T-10-2459・T-10-2460 の到達・T-10-2457 の S1）・
T-10-2457 の他の保護の機構 4 経路は既存の試験（`OrderExecutionServiceIndeterminateStopTests`・`OrderExecutionServiceProtectiveStopTests`・
`ProtectiveStopGuardIndeterminateStopTests`・`ProtectiveStopGuardIndeterminateCloseTests`）に注記つきの表明を足す。本番の照会の写像は偽の moomoo の口
（`FoundOrderMoomooClient`。照会に注文のスナップショットを返す）を与えた `MoomooReservationBrokerProbe` そのものを通す。

変異（1 本ずつ当てて戻した。実測）:

| 変異 | 内容 | 赤 |
| --- | --- | --- |
| P1 | 🔴 突合が照会の値で記録を書く（`BuildRecord` の `reservedPositionEffect ?? intent.PositionEffect` → `intent.PositionEffect`。是正前） | 9（T-10-2458 ×5・T-10-2459 ×3・T-10-2460 ×1） |
| P2 | 承認の相 1 で渡さない | 4（T-10-2457） |
| P3 | EF の `TryReserve` で列に書かない | 4（T-10-2456 EF・T-10-2458 EF ×3） |
| P4 | S1 の決済で渡さない | 1（T-10-2457） |
| P5 | 常駐ガードの逆指値で渡さない | 1（`ProtectiveStopGuardIndeterminateStopTests`） |
| P6 | 常駐ガードの成行で渡さない | 2（`ProtectiveStopGuardIndeterminateCloseTests`） |
| P7 | 承認直後の S0 の逆指値で渡さない | 1（`OrderExecutionServiceIndeterminateStopTests`） |
| P8 | 保護喪失の成行で渡さない | 2（`OrderExecutionServiceProtectiveStopTests`） |

## 検証

- `dotnet build backend/backend.slnx`（警告 0）・`dotnet format --verify-no-changes`（触ったプロジェクト）
- `dotnet ef migrations has-pending-model-changes`（スナップショットとモデルの一致）
- `dotnet test`: OrderExecutionService.Tests・Architecture.Tests（後で bin/obj を消す）
- `node scripts/scripts.test.js`・check-trace-blocks・check-adr-index-sync・check-adr-index-addendum-loss・check-cross-repo-refs・check-commit-messages・
  check-test-traceability・check-plan-id-qualification・gen-knowledge-graph --check・check-reading-budget

## 残余

- 🔴 列を足す前の予約の行（配備の時点で滞留中の Reserved）は null で、突合で確定した決済は従来どおり Open として残る（上の設計 5。是正前と同じ）。
- 突合で確定した S0/S3 の逆指値レグも `Close` になり、Active でない行の古い試行のレグが証券会社に生きていれば判断の手仕舞いの差し引き（IADR-0461）に数えられる。
  通常の経路で送った逆指値レグと同じ扱いであり、本件で新しく生じる型ではない（少なく売る側。次の判断で残りを売る）。
- 試験の採番: T-10-2456〜T-10-2460（着手時の最大 T-10-2455 の次）。
