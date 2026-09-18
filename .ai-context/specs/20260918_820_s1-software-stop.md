---
title: S1 ソフトウェア逆指値（SIMULATE 限定）— 発注執行が損切り到達を購読し、固定の決済 DecisionId で成行決済する（二重決済なし・再起動耐性）
type: spec
status: accepted
related_ids: [FR-10, FR-12, FR-11, FR-09, UC-02, ADR-0040, ADR-0016, IADR-0014, IADR-0030, IADR-0035, IADR-0057, IADR-0113, IADR-0118, IADR-0129, IADR-0210, IADR-0342, IADR-0344]
author: endazon (with Claude Code)
created: 2026-09-18
updated: 2026-09-18
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1 の S1・§結果「S1 は二重決済の経路を SIMULATE に戻す」)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 の 3 文〔口座種別の軸〕)
  - planning:projects/ai-stock-trading/04_workflows/02_event-driven-trading.md (§損切りの実行機構)
---

# 仕様書: S1 ソフトウェア逆指値（#820）

## 起点

- #820（enhancement）。計画 ADR-0040 決定 1 の **S1**（既存の損切り検知〔市場監視の到達検知〕を購読し、成行で決済する）。
- 前提: #819（PR #825・IADR-0342。選択機構・実弾での拒否・S2）。S1 は現状 `StopLossMethodPolicy` で S0 へフォールバックしている。
- 取り込む後続: #826 項目 2（損切り到達の通知文が「ブローカーの逆指値が決済する」と断定している）・項目 3（同一銘柄に手法が混在したときのガードの数量合算）を S1 に関わる範囲で扱う。
- 新規 IADR: **IADR-0344**。追記: IADR-0342（S1 がフォールバックしなくなった）・IADR-0210（保護記録のモデルに機構列を足した）。

## 射程

| 含む | 含まない（別 issue） |
| --- | --- |
| S1 選択時の新規買い（moomoo SIMULATE）でブローカーへ保護レグを出さず、ソフトウェア逆指値を永続化する | S3（#821） |
| 発注執行が `StopLossTriggered` を購読し、到達したソフトウェア逆指値を固定 DecisionId の成行で決済する | SC-02 / SC-03 / 日報の手法表示（#823） |
| 二重決済の防止（再配送・毎巡回の再発火・ハンドラとガードの競合） | 市場監視の判定ロジック・監視間隔の変更 |
| 部分約定・未約定エントリーの扱い（残りを取り消してから約定分だけ決済） | 実弾（`TrdEnv=real`）での S1（選べない。IADR-0342 のまま） |
| `ProtectiveStopGuard` がソフトウェア逆指値をブローカー照会しない・建玉消滅で解消する・手法混在時の数量の按分 | 空売りの S1（ADR-0016 決定 2(b) により常に S0） |
| 監査・通知・台帳結線（新イベント 2 種）と損切り到達通知文の手法別の記述 | 損切り到達通知そのものの重複抑止（毎巡回 Critical が出る既存挙動） |
| 再起動耐性（DB から復元・到達の記録を先に永続化してガードが再試行） | |

## 設計（詳細は IADR-0344）

1. **永続化**: 既存 `protective_stop_orders` に列 `Mechanism`（`StopLossExecutionMethod`・既定 0＝S0）・`TriggeredAt`・`TriggeredPrice` を足す（EF migration `AddSoftwareStopColumns`）。
   S1 の行は `StopOrderId=""`・`StopDecisionId=ProtectiveStopIds.SoftwareStopId(entry)`・`TriggerPrice=承認の StopLossPrice`・`Attempt`＝送った決済の試行数（0 始まり）。
2. **発注時**（`OrderExecutionAppService`）: `StopLossMethodPolicy` が S1（SIMULATE・新規買い）を `SoftwareStop` へ解決する。**エントリーを送る前に**行を Active で保存し（窓を作らない）、
   エントリーが生きていれば `SoftwareStopArmed` を発行、終端失敗・見送りなら行を Completed にする。
3. **到達時**（新 `StopLossTriggeredHandler` → `SoftwareStopExecutor`）: 銘柄・市場・建玉方向が一致する Active な S1 行のうち、
   (a) 行の作成が到達検知より前、(b) 検知時の価格が**行自身の損切りライン**に達している、ものを対象に、**先に到達を永続化**（`TriggeredAt`）してから決済を試みる。
4. **決済の手順**: エントリーが未終端なら取り消して終端を待つ（待てなければ次回へ）→ 約定数量と建玉（手法の異なる Active 行の数量を差し引く）の小さい方を
   `ProtectiveStopIds.SoftwareCloseDecisionId(entry, attempt)` の成行で決済。送信前に予約表（IADR-0057）で DecisionId を確保し、既存の記録があれば再送しない。
   受理（Accepted / PartiallyFilled / Filled）で行を Completed にし `SoftwareStopExecuted(ClosePlaced)` を発行（リスク管理が台帳の承認行へ結線）。
   約定 0 のまま取り消したら `SoftwareStopExecuted(EntryCancelled)`。拒否は試行を進め、到達ごとに 3 回で打ち切って `CloseRejected`（Critical）を出し到達の記録を外す（次の到達で再開）。
   接続断・建玉照会不能・取消待ちは**据え置き**（イベントなし・到達の記録は残す）。
5. **ガード**: S1 行はブローカーの注文照会をしない。到達済みなら決済を再試行し、未到達なら「エントリーが約定 0 で終端」か「建玉が残っていない」で Completed。
   S0 行の建玉残はソフトウェア逆指値の約定数量を差し引いて判定する（混在時にソフトウェア側の建玉が S0 の逆指値を生かし続けない）。
6. **通知文**: `StopLossTriggered` は検知側が建玉ごとの手法を知らないため、「S0＝ブローカーの逆指値／S1＝システムが成行で決済（別途通知）／S2＝誰も決済しない（手動）」を列挙する文へ改める。
   リスク管理の検知ログも同様。
7. **市場監視は変更しない**: 損切りラインは台帳の承認 Intent 由来（IADR-0035）で、S1 の承認も `StopLossPrice` を持つため、市場監視は既に S1 の建玉を評価できる。
8. **パイプライン宣言**: `pipeline.json` に段 `execute-software-stop`（order-execution・入力 `StopLossTriggered`・出力 `SoftwareStopExecuted`）を足す。

## 母集合（着手前の走査・2026-09-18・`git grep -c -F`・CHANGELOG.md と `.ai-context/specs/` を除く）

| 検索語 | 件数（ファイル） | 扱い |
| --- | --- | --- |
| `StopLossTriggered` | 43 | 本番コード: 契約（**型は変えない**）・市場監視（**変えない**。設計 7）・リスク管理 `StopLossTriggeredHandler`（ログ文言を手法別に）・通知 `NotificationFormatter`（文言）・監査（**変えない**）。発注執行に購読を新設。テスト: 通知の文言テストとゴールデンを更新、リスク管理の購読テストは文言に依存しない（変更なし）。docs: `api/events-and-ports.md`（行の説明が旧機構「決済を発行」のまま→是正）・`functional/FR-10_risk-controls.md`（S1 を追記）・`data/audit-events.md`（新イベント 2 種）・`tests/FR-10_risk-controls-tests.md`（AC 追記）。`operations/wolverine-queue-cleanup-runbook.md` は移行時点の旧キュー一覧（歴史記録）のため変更しない。`tech/system-architecture.md` の図は MON→RSK の検知経路で誤りではないため変更しない。`deploy/.../pipeline.json` は段を追加（設計 8）、`files/README.md` は段の一覧を持たないため変更しない。`.ai-context/adr/` 10 件は IADR-0342 / IADR-0210 / 索引のみ追記（他は凍結・当時の記述） |
| `ProtectiveStop`（型名の前方一致） | 本番コード 14 ファイル（発注執行）＋契約・監査・通知・リスク管理 | `ProtectiveStopOrder` / 行 / EF ストア / インメモリストア / `IProtectiveStopOrderStore`（機構列と `FindActiveSoftwareStops`）・`ProtectiveStopGuard`（S1 分岐と数量の按分）・`ProtectiveStopIds`（S1 の ID 2 種）・`OrderExecutionAppService`（S1 分岐）・DbContext（列）を変更。`ProtectiveStopPlaced` / `ProtectiveStopCoverageLost` / `ProtectiveStopWaived` の**意味と形は変えない** |
| `StopLossMethodPolicy` | 3 | 本体・テスト・`OrderExecutionAppService`。S1 のフォールバックを外す |
| `NotImplementedFallbackToBrokerStop` | 3 | S3・未知だけに残す（名前は据え置き） |
| `CloseDecisionId` | 12 | 既存の保護喪失の手仕舞い ID（`protective-close:`）は**変えない**。S1 は別の名前空間（`software-stop-close:`）で導出し衝突させない |
| `決済はブローカー` | 5 | 通知文・リスク管理ログ・ゴールデンを手法別へ。`FR-10_risk-controls.md` の記述は S0 の説明として正しく、S1 を並べて追記。IADR-0342 は凍結 |
| `#820` | 4 | `OrderExecutionAppService`（ログ文言）・`StopLossExecutionMethod`（「未実装」の注釈）を是正。IADR-0342 / 索引は追記で扱う |
| `protective_stop_orders` | migration・DbContext・IADR・仕様書・`scripts` の cutover manifest | テーブルは増えない（列の追加のみ）。manifest はテーブル単位のため変更不要（`scripts.repo.test.js` が ModelSnapshot との整合を検査する） |

**除外とその理由**

- `.ai-context/specs/` の既存仕様書: 凍結記録。
- フロントエンド: 契約フィクスチャ（HTTP 応答）の形を変えない（イベントのみ）。
- `docs/api/openapi.yaml`: HTTP 経路の追加なし。

## 受け入れ基準 → テスト

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | SIMULATE＋S1 の新規買いは保護レグを出さず建玉を保持し、ソフトウェア逆指値が永続化される | `OrderExecutionServiceSoftwareStopTests`（逆指値 0 回・行 Active・S1・損切りライン・`SoftwareStopArmed`／終端失敗は Completed／再配送で重ねない）／`StopLossMethodPolicyTests`（S1→SoftwareStop・空売りと非 SIMULATE は従来） |
| 2 | 損切りライン到達で成行決済される（約定数量だけ・固定 DecisionId） | `SoftwareStopExecutorTests.到達したソフトウェア逆指値は固定のDecisionIdで成行決済され完了する` ほか |
| 3 | 二重決済が起きない（再配送・毎巡回の再発火・ガードとの競合・送信済みの記録） | `SoftwareStopExecutorTests`（2 回目の到達で発注 0・予約が取れなければ発注しない・記録済みなら再送しない） |
| 4 | 部分約定・未約定のエントリーは残りを取り消し、約定分だけ決済する | `SoftwareStopExecutorTests`（未約定は取消のみで EntryCancelled・部分約定は取消後に約定分だけ・取消が終端にならなければ据え置き） |
| 5 | 到達していない行・到達より後に建てた行は決済しない | `SoftwareStopExecutorTests` |
| 6 | 再起動耐性: 到達の記録を先に永続化し、決済できなかった到達はガードが再試行する | `SoftwareStopExecutorTests.接続断では到達を記録して据え置き`／`ProtectiveStopGuardSoftwareStopTests.到達済みのソフトウェア逆指値はガードが決済を再試行する` |
| 7 | ガードはソフトウェア逆指値をブローカー照会せず、建玉が無くなれば解消する。S0 の挙動は不変 | `ProtectiveStopGuardSoftwareStopTests`（注文照会 0 回・建玉消滅で Completed・未約定エントリーは据え置き）／既存 `ProtectiveStopGuardTests` 全緑 |
| 8 | 手法混在: S0 の建玉残はソフトウェア逆指値の約定数量を差し引いて判定し、S1 の決済は S0 の数量を食わない | `ProtectiveStopGuardSoftwareStopTests` / `SoftwareStopExecutorTests` の混在ケース |
| 9 | 決済の拒否を 3 回で打ち切り Critical を出す | `SoftwareStopExecutorTests` |
| 10 | 通知文が手法ごとに正しい／監査に配置と決済が残る／台帳に決済レグが結線される | `NotificationFormatterTests`・`NotificationTemplateGoldenTests`・`NotificationConsumersTests`／`AuditEntryFactoryTests`・`AuditEventConsumersTests`／`ProtectiveStopLedgerHandlersTests` |
| 11 | 発注執行が `StopLossTriggered` を購読し発行まで通る | `StopLossTriggeredConsumerTests`（発注執行） |
| 12 | 永続化の往復（列の追加） | `EfProtectiveStopOrderStoreTests` |
| 13 | 契約の後方互換（新イベントの型名・スキーマ基線） | `EventMessageTypeNameTests`・`EventBackwardCompatibilityTests`（基線に追加） |
| 14 | **同じ銘柄の複数の S1 行は建玉を配分し、合計が保有数量を超えない**（#820 の監査・売り過ぎの防止） | `SoftwareStopExecutorTests.同じ銘柄の複数のソフトウェア逆指値は建玉を配分し合計で保有数量を超えない`（T-10-351） |
| 15 | 持ち分が無い行は建玉が残っていても完了させず据え置く | `SoftwareStopExecutorTests.持ち分が無い行は建玉が残っていても完了させず据え置く`（T-10-352） |
| 16 | 配分はハンドラとガードで同じ（呼ぶ順に依らない） | `SoftwareStopExecutorTests.配分は到達時刻と作成時刻で決まりハンドラとガードで同じになる`（T-10-353） |
| 17 | **エントリーの発注記録が無い行は 1 株も決済しない**（猶予内は据え置き） | `SoftwareStopExecutorTests.エントリーの発注記録が無い行は決済せず猶予内は据え置く`（T-10-354） |
| 18 | 猶予を過ぎた孤立行は決済せず閉じ、Critical で人手へ知らせる | `SoftwareStopExecutorTests.エントリーの発注記録が無い行は猶予を過ぎるとEntryMissingで閉じる`（T-10-355） |
| 19 | 孤立行は他の行の建玉を食わない | `SoftwareStopExecutorTests.孤立行の決済は他の行の建玉を食わない`（T-10-356） |
| 20 | **S0 と S1 が同数でも S1 の決済が出る**（S0 のレグ記録で二重に差し引かない。2 巡目監査 B1） | `SoftwareStopExecutorTests.S0とS1が同数でもS1の決済は出る_S0のレグ記録で二重に差し引かない`（T-10-357） |
| 21 | **ガード 1 巡回で先の行の決済が即時約定しても、決済合計が保有数量を超えない**（2 巡目監査 B2） | `SoftwareStopExecutorTests.ガード巡回で先の行の決済が即時約定しても合計が建玉を超えない`（T-10-358） |
| 22 | 建玉へ反映済みの古い決済は持ち分を削らない（B2 の是正が逆側へ倒れない） | `SoftwareStopExecutorTests.建玉へ反映済みの古い決済は差し引かない`（T-10-359） |
| 23 | **S0 の逆指値が約定・取消された巡回でも、建玉が残る限り S1 の行を完了させない**（3 巡目監査 B3） | `ProtectiveStopGuardSoftwareStopTests.S0の逆指値が約定した巡回でもS1の行は建玉が残る限り完了しない`（T-10-368）／`同.S0分を手仕舞った巡回でもS1の行は完了しない`（T-10-369） |
| 24 | **再発注済み S0 の前試行のレグ記録で数量を二重に引かない**（3 巡目監査 B4） | `SoftwareStopExecutorTests.再発注済みS0の古いレグ記録があってもS1の持ち分は二重に削られない`（T-10-370） |

## ［2026-09-18 追記 / #820 の 4 巡目監査］持ち分の決め方を作り直す（配分 → 残保護数量）

4 巡目の監査がブロッキング 4 件（BLK-1〜4）を出した。**4 巡連続で「是正のたびに別の欠陥が出て」おり、
個々のバグではなく設計の問題である**ため、持ち分の決め方ごと作り直す。

### 何が根本原因か

ブローカーの建玉照会は**銘柄単位の純額**であり、どの建玉がどの保護記録のものかを区別しない。
それを**毎巡回ゼロから計算し直して**持ち分を決めていたため、規則をどう変えても
「売り過ぎ（反対建玉）」か「損切りが黙って出ない」のどちらかへ倒れた。

| 巡 | 是正 | その是正が作った欠陥 |
| --- | --- | --- |
| 1 | 行ごとの「建玉残」上限をやめ、行へ配分する | B1（S0 のレグ記録で二重差し引き → S0 と同数なら決済が 1 株も出ない）・B2（即時約定した決済を取りこぼして売り過ぎ） |
| 2 | 差し引きを「建玉照会が映していない決済」へ | B3（差し引きが対称で、S0 と S1 が同数だと双方から見て残 0 → 建玉が残ったまま保護が外れる）・B4（再発注済み S0 の前試行レグが除外漏れ） |
| 3 | 保護を外す判定を方向の純額だけに | BLK-1（自分の建玉を失った S1 行が不死化し、同銘柄の別エントリーの生きた S0 逆指値を毎巡回・恒久的に黙って取り消す） |

### 作り直しの設計（**保護記録が「自分の残保護数量」を状態として持つ**）

1. **列を足す**: `protective_stop_orders` に `RemainingProtected`（int?・null＝未確定）と
   `StalledNotifiedAt`（DateTimeOffset?）を足す（migration `AddProtectiveStopRemainingProtected`。
   **既存行の初期値は `Quantity`**）。派生プロパティ `ProtectedQuantity` は
   **S0 は `RemainingProtected ?? Quantity`**（ブローカーに実在する逆指値が覆う数量）、
   **S1 は `RemainingProtected ?? 0`**（確定するまで 1 株も主張しない）。
2. **確定**: エントリーの発注記録が**終端**になった時点で、その行の残保護数量＝**エントリーの約定数量**。
   未終端・記録なしの行は**未確定のまま**（主張 0・後述 4 の割り当て対象にもしない）。
3. **自分の決済で減らす**: 受理された決済の数量だけ減らす。決済レグの `SoftwareCloseDecisionId`
   （エントリー ＋ 試行番号から決定的）で突き合わせ、**試行番号を進める保存と同じ 1 回**で減算する
   （再入では別の試行番号になるため二重に減らない）。
4. **外部要因の減少を一度だけ確定的に割り当てる**: 銘柄・市場・方向ごとに
   **S1 の予算＝方向の純額 − Active な S0 行の `ProtectedQuantity`** を求め、
   **確定済み S1 行の残保護数量の合計が予算を超えている分**を、**作成時刻 → EntryDecisionId** の順に
   **古い行から**削って**保存する**（＝削ったことの記録。次の巡回で引き直さない）。
   **S0 行は削らない**——S0 の主張はブローカーに実在する逆指値の数量であり、帳簿を削っても注文は縮まないため、
   削ると S0 の逆指値が建玉より大きくなって反対建玉を生む。
5. **決済数量は残保護数量そのもの**。配分（`ProtectiveStopNetting.AllocateSoftwareStops`）と
   「建玉照会がまだ映していない決済」の差し引き（`UnreflectedCloseQuantity` /
   `IExecutedOrderStore.FindClosesSince` / `SnapshotLagAllowance`）は**撤去する**。
6. **行を完了させるのは残保護数量が 0 になったときだけ**（純額・他手法の主張では完了させない）。
7. **部分的にしか決済できないときは完了させない**——受理された決済の数量が残保護数量に満たなければ
   行は Active のまま（`TriggeredAt` も残す）で、残りを次の巡回・次の到達で決済する。
8. **ガードは S0 行 → 到達済み S1 行 → 未到達 S1 行の順**に評価する。
   S0 を先に評価するのは、S1 の予算が「**この巡回の後も生きている S0**」の数量を引くべきだからである
   （約定・取消で役目を終えた S0 の数量を引くと、B3 と同じく S1 の保護が消える）。
   到達済みを未到達より先にするのは BLK-4（未到達の行が先に持ち分を取る）の再発防止である。
9. **到達済みなのに決済できない行は、猶予（`DefaultSettlementGrace`＝15 分）を過ぎたら Critical**
   （`SoftwareStopOutcome.CloseStalled=4`）を**1 行につき 1 回**出す（`StalledNotifiedAt` で記録）。
   行は Active のまま再試行を続ける（無音の失敗を残さない）。
10. **S0 側は S1 の残保護数量を差し引く**（`ProtectiveStopNetting.RemainingPositionFor`）。
    発注記録の照会をやめたため、完了済み S0 行の取消済みレグ記録で S1 の持ち分が食われる問題（BLK-2）は
    構造的に消える。**S1 行が無い構成では差し引く量が 0 で、S0 の挙動は従来と 1 バイトも変わらない。**

### 母集合（作り直しの走査・2026-09-18・`git grep -n`）

| 検索語 | 扱い |
| --- | --- |
| `AllocateSoftwareStops` | 本体（`ProtectiveStopNetting`）・呼び出し 1 箇所（`SoftwareStopExecutor`）・テスト。**撤去** |
| `FindClosesSince` | `IExecutedOrderStore`・EF 実装・インメモリ実装・`SoftwareStopExecutor`・テスト。**撤去**（本 PR で新設したもので、外部利用は無い） |
| `RemainingPositionFor` | `ProtectiveStopNetting`（S0 の建玉残）・`ProtectiveStopGuard`（S0 経路と静的ヘルパ）・テスト。**引数から `IExecutedOrderStore` を外す** |
| `snapshotTakenAt` / `SnapshotLagAllowance` | `ProtectiveStopGuard`・`SoftwareStopExecutor`・テスト。**撤去**（照会時刻に依存しない） |
| `SoftwareStopOutcome` | 契約・監査 `AuditEntryFactory`・通知 `NotificationFormatter`・リスク管理 `ProtectiveStopLedgerHandlers`（`ClosePlaced` だけを見るため変更なし）・テスト。**`CloseStalled=4` を末尾へ追加**（序数は動かさない） |
| `ProtectiveStopOrder(` の生成箇所 | `OrderExecutionAppService`（S1 の新規行・S0 の同時発注）・`ProtectiveStopGuard`（S0 の再発注）・各テスト。**位置引数を末尾に足すため既存の生成箇所は無改修** |
| `protective_stop_orders` | migration・DbContext・`ProtectiveStopOrderRow`・EF ストア・cutover manifest（テーブル単位のため無改修） |

**除外とその理由**

- `.ai-context/specs/` の他の仕様書・`.ai-context/adr/` の凍結記録: 当時の記述であり書き換えない（IADR-0344 には日付つき追記で残す）。
- フロントエンド・`docs/api/openapi.yaml`: HTTP 応答の形を変えない。
- `docs/operations/`・`tech/`: 機構の説明を持たないため変更なし。

### 受け入れ基準の変更・追加

**変更**: 受け入れ基準 14〜24 のうち配分方式を前提にした文言は、残保護数量方式へ読み替える
（T-10-351〜359・368〜370 は新しい意味で固定し直す。配分順で持ち分が決まる主張は、
「**古い行から削る**」という一度きりの割り当てへ置き換わる）。

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 25 | **残保護数量はエントリーの約定確定で決まり、決済で減り、0 になったときだけ行が完了する** | `SoftwareStopExecutorTests.残保護数量は約定確定で決まり決済で減り0で完了する`（T-10-371） |
| — | 既存 T-10-352 の読み替え（配分で持ち分 0 → **割り当てで残保護数量 0 になった行は完了する**） | `SoftwareStopExecutorTests.建玉の減少を割り当てられて残保護数量が0になった行は完了する`（T-10-352） |
| 26 | **部分的にしか決済できなければ行を完了させず、残りを次の巡回で決済する**（BLK-3） | `SoftwareStopBlockingRegressionTests.部分的にしか決済できない行は完了させず残りを次の巡回で決済する`（T-10-372） |
| 27 | **自分の建玉を失った S1 行は一度だけ確定的に削られて完了し、同じ銘柄の生きた S0 逆指値を取り消させない**（BLK-1） | `SoftwareStopBlockingRegressionTests.建玉を失ったS1の行は完了し生きているS0の逆指値を取り消させない`（T-10-373） |
| 28 | **完了済み S0 行の取消済みレグ記録があっても S1 の持ち分は削られない**（BLK-2） | `SoftwareStopBlockingRegressionTests.完了済みS0の取消済みレグ記録があってもS1の持ち分は削られない`（T-10-374） |
| 29 | **到達済みの行を未到達の行より先に処理する**（BLK-4） | `SoftwareStopBlockingRegressionTests.到達済みの行を未到達の行より先に処理する`（T-10-375） |
| 30 | **到達済みなのに決済できない状態が猶予を過ぎたら Critical を 1 回出し、行は再試行を続ける** | `SoftwareStopExecutorTests.到達済みで決済できない状態が猶予を過ぎたらCriticalを一度だけ出す`（T-10-376） |
| 31 | **外部要因の減少は一度だけ割り当てて記録し、次の巡回で引き直さない**（持ち分が巡回ごとに揺れない） | `SoftwareStopExecutorTests.外部要因の減少は一度だけ割り当てて記録し次の巡回で揺れない`（T-10-377） |
| 32 | **永続化の往復（残保護数量・据え置き通知の記録）** | `EfProtectiveStopOrderStoreTests.残保護数量と据え置き通知の記録が往復する`（T-10-378） |
