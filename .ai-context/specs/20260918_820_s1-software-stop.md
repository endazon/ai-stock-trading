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
