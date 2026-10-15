---
title: 据え置いた「未発注」判定の毎時の警告を畳み、発注の記録が無いまま残る S1 を識別する（#856）
type: spec
status: done
related_ids: [FR-05, FR-10, NFR-09, ADR-0040, ADR-0045, IADR-0362, IADR-0365, IADR-0488, IADR-0538]
author: claude (Claude Code)
created: 2026-10-15
updated: 2026-10-15
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-05 発注・FR-10 逆指値なしの建玉を持たない)
  - planning:projects/ai-stock-trading/07_adr/ADR-0045_reservation-release-criterion-per-trading-env.md (決定 1 (b) held-not-placed の観測)
---

# 仕様書: 据え置き警告の畳み込みと「幽霊の S1」の識別（#856）

> 本仕様書は実装着手前に作成した（着手 2026-10-15）。基点は AST `origin/develop` `adec2031`。
> `git rev-parse --is-shallow-repository` → `false`。

## 起点

- #856 の 2026-10-14 のコメント「BeforeSend の後始末の手順を見直した」。2026-10-05 の故障注入（`BeforeSend`）で残った
  AAPL の予約（`order_dispatch_reservations`・DecisionId `4908458f-7859-49a3-89bc-a743ec07d1bc`）と、
  S1 の記録（`protective_stop_orders`・`Active`・`残保護=未確定`）が 10 日間残り、巡回のたびに出力が続いている。
- 後始末（SQL 2 行・予約は `Forgone` 推奨か DELETE か）は**利用者の判断待ち**。本件は、その判断に依らずコードで安全に直せることと、
  runbook の欠落 3（S1 の記録を閉じる手順が無い）を扱う。

## 実測: 何が・どんな条件で・いつまで出るか（`adec2031` のコード）

| 出力 | レベル | 頻度 | 出る条件 | いつまで |
| --- | --- | --- | --- | --- |
| `発注予約リコンサイル: 照会は「未発注」と答えましたが…据え置きます（DecisionId=4908458f-… 取引環境=MoomooSimulate）` | **Warning** | 巡回ごと（配備 `IntervalHours=1`＝**毎時**。`Hosted/OrderReservationReconciliationService.cs:308-315`） | 予約が `Reserved`・`ReservedAt` が滞留閾値（配備 2 時間）より前・照会が `NotPlaced`・その取引環境の門（`ReleaseOnNotPlaced:Simulate`）が閉 | **永久**。予約が `Reserved` を出るか（人の SQL）、門が開くか、照会が `NotPlaced` 以外を返すまで。`Reserved` は保持期間のパージの対象外 |
| `発注予約リコンサイル: 滞留 1 件を走査（… 据え置き〔未発注だが門が閉〕 1 …）` | Information | 毎時 | 同上（`Scanned > 0`） | 同上 |
| 計器 `ast_order_reservation_reconciliations_total{outcome="held-not-placed",provider="MoomooSimulate"}` | — | 毎時 +1 | 同上 | 同上 |
| `ソフトウェア逆指値（S1）の保護は継続中: Active N 件 …: AAPL/UnitedStates Buy 残保護=未確定（エントリー約定待ち） トリガー=327.83 未到達 EntryDecisionId=4908458f-…` | Information | 5 分ごと（`SoftwareStopSummaryInterval`） | S1 の行が `Active` | **永久**。ガードは記録の無い未確定の S1 を `StillActive` とする（`ProtectiveStopGuard.cs` の「未確定＝これから約定し得る」）。閉じるのは損切りラインに到達したときだけ |

- 保護逆指値ガードの巡回の Warning（`保護逆指値ガード: Active N 件を評価…`）は `StillActive` だけでは出ない（この件では出ていない）。
- 🔴 計器と DecisionId つきのログは、門を開ける判断の記録 (b)（運用仕様書「解放の門を開けるときの記録」）の入力である。**消してはならない**。

## 方針（計画の裁定が要らない範囲）

### 1. 据え置き警告の畳み込み（(a)）

- 同じ予約（DecisionId）・同じ取引環境について、`HeldNotPlaced` が**途切れずに続く**あいだ、Warning は**初回と、再通知の間隔（既定 24 時間）ごと**にだけ出す。
- 畳んだ巡回でも **DecisionId つきの Information の行を必ず出す**（観測 (b) の grep と、後始末の確認手順が使う）。計器は今までどおり毎巡回数える。
- **状態が変わったら畳まない**: 取引環境が変わった・その巡回で `HeldNotPlaced` 以外（`Indeterminate`・`Failed`・`Released`）になった・
  全件を走査した巡回で載らなかった（確定・人の SQL で `Reserved` を出た）——いずれも記憶を消し、次の `HeldNotPlaced` を初回として Warning にする。
- バッチが満杯（`Scanned >= EffectiveBatchSize`）の巡回では、載らなかった予約の記憶を消さない（走査されなかっただけかもしれない）。
- 記憶はプロセス内（再起動で消える＝再起動後の初回は Warning。安全側）。
- 再通知の間隔は `Reconciliation:HeldNotPlacedReminderHours`（既定 24・下限 1・上限 168）。0 以下は 1 へ。
- **Critical（突合で「発注済み」と確定）とその直後の保護の結果の行は対象外**（1 件ごとに 1 回しか出ない経路であり、畳まない）。

### 2. 発注の記録が無いまま残る S1（「幽霊の疑い」）の識別（(b)）

- 定義: S1・`Active`・`RemainingProtected` が NULL（未確定）・`TriggeredAt` が NULL・作成から一定時間（既定 24 時間）経過・
  **エントリーの発注記録（`executed_orders`）が無い**。
- 5 分ごとの要約の該当行に ` 発注記録なし（幽霊の疑い）` を付ける。
- 別に Warning を 1 行出す（銘柄・トリガー・EntryDecisionId・作成時刻と、後始末の手順〔発注経路の Runbook〕への案内）。
  これも**初回と再通知の間隔（既定 24 時間）ごと**だけにする。該当しなくなったら記憶を消す。
- 🔴 **行を閉じる・消すことはしない**（観測のみ）。閉じるのは利用者の判断と SQL（runbook）。
- 発注記録の照会は候補（未確定・未到達・経過済み）にだけ行う（間隔に 1 回・通常 0 件）。
- 構成は `ProtectiveStopGuard:UnrecordedSoftwareStopAge`（既定 24 時間）・`ProtectiveStopGuard:UnrecordedSoftwareStopReminderInterval`（既定 24 時間）。

### 3. runbook（`docs/operations/broker-execution-paths-runbook.md`）

- 新節「発注の記録が無いまま残った S1 の記録を閉じる」（欠落 3）: 読み取りで確認 → 該当 1 行だけを閉じる → 確認 → 戻し方。
  予約は `Forgone`（推奨）と DELETE の両案を並べ、どちらを選ぶかは利用者の判断と明記する。
- 手順 10（`BeforeSend` の後始末）を新節へつなぐ。手順 8 の期待（「巡回ごとに Warning」）を、畳み込みに合わせて直す。
- `docs/operations/operations.md` の「監視すべきログ」の Warning の項を、畳み込みに合わせて直す。

## 母集合（規則 9〜11）

- **誤りになる側の文字列で走査した**（`照会は「未発注」` / `据え置きます（` / `巡回ごとに出る` / `残保護=未確定` / `毎巡回 Warning`。
  `.ai-context/specs` と `CHANGELOG.md` は凍結・生成物として除外）:
  - `Hosted/OrderReservationReconciliationService.cs:308-315`（本体。直す）
  - `docs/operations/broker-execution-paths-runbook.md:213`（対比表。初回の Warning は今までどおり出るので真のまま）・`:310`（「巡回ごとに出る」。直す）
  - `docs/operations/operations.md:468`（Warning の説明。畳み込みを足す）
  - `SoftwareStopLivenessReporterTests.cs:82`（既存の未確定の表明。発注記録の照会を渡さない呼び出しでは印が付かないので真のまま）
  - ほかはソフトウェア逆指値の決済・建玉照会の別の文言（対象外）
- **IADR の言い直し**: IADR-0362 決定 1「常駐が警告でログする（無音にしない）」・同 :137 付近の「毎巡回で出す」（`OrderReservationReconciliationService.cs:302` のコメント）。
  凍結記録の本文は書き換えず、IADR-0538 で「無音にしない（Information は毎巡回・Warning は初回と 24 時間ごと）」へ改めたと記録し、コードのコメントを直す。
- **窓（規則 11）**: 窓は「同じ予約の `HeldNotPlaced` が続く区間」。増える側＝新しい予約が据え置かれる（初回 Warning が要る）／
  減る側＝据え置きが解ける（確定・人の SQL・不確定へ移る）。3 つの形を試験で比べる:

  | 形 | 新しい予約の初回 Warning | 解けた後の再発で Warning | 24 時間後の再通知 |
  | --- | --- | --- | --- |
  | 後の端だけ（最後に警告した時刻だけを見る） | 期待どおり | **空く**（解けても記憶が残り、再発が畳まれる） | 期待どおり |
  | 前の端だけ（初めて見た DecisionId かだけを見る） | 期待どおり | 期待どおり（消せば） | **空く**（二度と Warning にならない） |
  | 両端（記憶の消去＋最後に警告した時刻） | 期待どおり | 期待どおり | 期待どおり ← **採用** |

## 受け入れ基準 → 試験

| ID | 受け入れ基準 | 試験 |
| --- | --- | --- |
| T-10-2664 | 同じ予約の据え置きが続く巡回では、Warning は初回だけ。2 巡回目以降は DecisionId つきの Information。24 時間たつと再び Warning | `HeldNotPlacedWarningThrottleTests` |
| T-10-2665 | 状態が変わったら畳まない（取引環境の変化・不確定を挟む・全件走査で載らなかった）。バッチ満杯では記憶を消さない | 同上 |
| T-10-2666 | 本番の合成（常駐 → scope → リコンサイラ）で 2 巡回回すと、Warning 1 行・継続の Information 1 行。計器は 2 回とも数える | `OrderReservationReconciliationServiceTests` |
| T-10-2667 | 発注記録が無く 24 時間たった未確定・未到達の S1 は要約に印が付き、Warning（後始末の案内つき）が初回と 24 時間ごとにだけ出る。記録がある・若い・到達済み・残保護が確定済みには付かない。該当しなくなったら記憶を消す | `SoftwareStopLivenessReporterTests` |
| T-10-2668 | 常駐ガードが発注記録のストアを渡して幽霊の疑いを出す（本番の組み立て） | `ProtectiveStopGuardServiceTests` |
| T-10-2669 | 再通知の間隔の既定と範囲（24 時間・1〜168） | `HeldNotPlacedWarningThrottleTests` |

変異（赤になることを確かめる）: 畳まない／記憶を消さない（後の端だけ）／時刻を見ない（前の端だけ）／発注記録の有無を見ない／経過を見ない。

## 範囲外

- 予約・S1 の行を自動で閉じる・消すこと（取引の安全・利用者の判断）。
- 解放の門を開けること、#856 のコメント §6 の欠陥 1・2（解放時に保護の記録が閉じない・古い承認の再投入）。
