---
title: IADR-0344 S1 ソフトウェア逆指値 — 保護記録に機構列を足して永続化し、発注執行が損切り到達を購読して到達を先に記録し、予約つき固定 DecisionId の成行で 1 回だけ決済する
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-12, FR-11, FR-09, UC-02, ADR-0040, ADR-0016, IADR-0014, IADR-0030, IADR-0035, IADR-0057, IADR-0113, IADR-0118, IADR-0129, IADR-0210, IADR-0342]
author: claude (Claude Code)
created: 2026-09-18
updated: 2026-09-18
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1 の S1・§結果 悪い影響「S1 は二重決済の経路を SIMULATE に戻す」)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 の 3 文〔口座種別の軸〕)
  - planning:projects/ai-stock-trading/04_workflows/02_event-driven-trading.md (§損切りの実行機構)
---

# IADR-0344: S1 ソフトウェア逆指値

- 状態: Accepted
- 日付: 2026-09-18
- 決定者: claude（起票 #820。利用者レビューは PR で受ける）

## 起点・関連

- 関連する計画書 ID: **ADR-0040 決定 1 の S1**（既存の損切り検知を購読し成行で決済する）、FR-10（口座種別の軸）、FR-12、UC-02、
  ADR-0016 決定 2(b)（空売りは常に S0。改めない）
- 対象 Issue: #820（取り込み: #826 項目 2・3）
- 関連する実装仕様書: [20260918_820_s1-software-stop](../specs/20260918_820_s1-software-stop.md)
- 関連 IADR: [IADR-0342](IADR-0342_simulate-stop-loss-method-selection.md)（選択機構。S1 のフォールバックを本 IADR で外す）、
  [IADR-0210](IADR-0210_broker-side-stop-loss-unification.md)（S0・保護記録・ガード）、
  [IADR-0057](IADR-0057_order-dispatch-idempotency.md)（予約）、[IADR-0113](IADR-0113_moomoo-fill-polling.md)（約定追跡）、
  [IADR-0118](IADR-0118_broker-position-reconciliation.md)（建玉照会の null＝不明）、
  [IADR-0014](IADR-0014_market-monitor-events-and-boundary.md) / [IADR-0030](IADR-0030_position-store-sync-api.md) /
  [IADR-0035](IADR-0035_stop-loss-authoritative.md)（到達検知と損切りラインの供給元）、[IADR-0129](IADR-0129_wolverine-messaging-topology.md)（購読キュー）

## コンテキストと課題

moomoo の模擬取引は指値・成行しか受け付けず（`OrderType_Stop` は拒否・当日限り）、S0 では SIMULATE に建玉が残らない。
S2 は建玉を残すが損切りが起きない。S1 は「市場監視の到達検知（60 秒周期の `StopLossTriggered`）を発注執行が購読して成行で決済する」
ことで、ペーパーでも損切り決済までを観測可能にする。ADR-0040 は S1 が 2026-07-31 の裁定で消した**二重決済の経路を SIMULATE に戻す**
ことを悪い影響として明記しており、実装の中心課題は**二重決済を作らないこと**である。

決めるべきは (a) ソフトウェア逆指値をどこに永続化するか、(b) 市場監視は S1 の損切りラインを知っているか、(c) 到達をどう冪等に決済へ変えるか
（市場監視は価格が戻るまで毎巡回再発火する・メッセージは再配送される・ガードと並行し得る）、(d) 部分約定・未約定の扱い、
(e) ガードと手法混在、(f) 監査・通知・台帳、(g) 再起動耐性、である。

## 検討した選択肢

1. **(a) 新テーブル `software_stop_orders`** — 意味は分かれるが、ガードの巡回・EF ストア・インメモリ実装・テストの足場を二重に持つ。
   手法混在の按分では両テーブルを突き合わせることになる。**却下**。
2. **(a) 既存 `protective_stop_orders` に機構列を足す**（採用）— 1 エントリー＝高々 1 保護の主キーがそのまま効き、ガードは同じ巡回で両方を見られる。
3. **(b) 市場監視へ手法と損切りラインを別経路で供給する** — 不要と判明した。市場監視は台帳（リスク管理 `GET /risk-controls/open-positions`）の
   建玉を評価し、損切りラインは承認 Intent の `StopLossPrice`（IADR-0035）である。S1 の承認も `StopLossPrice` を必ず持つ（無ければ見送り）。**変更しない**。
4. **(c) リスク管理が到達を受けて Close の `OrderApproved` を出す**（旧 IADR-0015 の形）— 保護記録（手法）を持つのは発注執行だけであり、
   S0 / S2 の建玉にまで決済を出す危険がある。発注執行の購読で閉じる。**却下**。
5. **(e) 損切り到達の通知に建玉ごとの手法を載せる**（台帳の承認行に手法を保存 → open-positions → 市場監視 → `StopLossTriggered`）—
   3 サービスとリスク管理の migration を要し、台帳は銘柄単位で最新エントリーの値しか持たない（混在時に誤る）。**本 IADR では採らない**（決定 7）。

## 決定

1. **永続化**: `protective_stop_orders` に `Mechanism`（`StopLossExecutionMethod`・非 null・既定 0＝S0）・`TriggeredAt`（null）・`TriggeredPrice`（null）を足す
   （migration `AddSoftwareStopColumns`。既存行は S0 として読まれる）。S1 の行は `StopOrderId=""`、`StopDecisionId=ProtectiveStopIds.SoftwareStopId(entry)`、
   `TriggerPrice`＝承認の `StopLossPrice`、`Quantity`＝承認数量、`Attempt`＝**送った決済の試行数**（0 始まり）。
   `IProtectiveStopOrderStore.FindActiveSoftwareStops(symbol, market, entrySide)` を足す。
2. **解決**: `StopLossMethodPolicy` は S1 を `StopLossMethodDisposition.SoftwareStop` へ解決する（S0→S0／非 SIMULATE→拒否／空売り→S0／S2→免除 の順序は不変。
   S3・未知だけが従来のフォールバック）。
3. **発注時**（`OrderExecutionAppService`）: S1 はブローカーの逆指値能力（成行決済に使う `IProtectiveOrderBroker`）と保護記録ストアを要し、
   欠ければ見送る（`StopOrderUnsupported`）。🔴 **エントリーを送る前に行を Active で保存する**（行が無ければのみ）——送った後に保存すると
   「建玉はあるのにソフトウェア逆指値が無い」窓ができる。送った結果が生きていれば（Accepted / PartiallyFilled / Filled）`SoftwareStopArmed` を発行し、
   終端失敗・見送り（接続断）なら行を Completed にする。送信後の不明（例外）は行を Active のまま残す（ガードが扱う）。
4. **到達**（発注執行の新ハンドラ `StopLossTriggeredHandler` → `SoftwareStopExecutor.OnTriggeredAsync`）: 銘柄・市場・建玉方向が一致する Active な S1 行のうち、
   **行の作成が検知時刻以前**で、**検知時の価格が行自身の損切りラインに達している**（買い建て: 価格 ≦ ライン）ものだけを対象にする。
   🔴 **決済の前に到達を永続化する**（`TriggeredAt` / `TriggeredPrice`。既に記録済みなら上書きしない）。以降の決済が据え置きになっても、
   次の到達を待たずにガードが再試行する。**一度到達したら価格が戻っても決済する**（FR-10: 損切りラインは到達で発動する。遅れて届いた到達も同じ）。
   行自身のラインで判定するのは、台帳の損切りラインが銘柄単位で最新エントリーの値に丸められるため（同一銘柄の別エントリーを巻き込まない）。
5. **決済**（`SoftwareStopExecutor.TryCloseAsync`。ハンドラとガードが共有）:
   1. エントリーの記録（`executed_orders`）があり**未終端**ならブローカーで取り消し、再照会して**終端になるまで決済しない**（据え置き）。
      約定 0 で終端 → 行を Completed にし、自分が取り消したなら `SoftwareStopExecuted(EntryCancelled)` を出す。記録が無い（送信後不明）なら行の数量を上限に扱う。
   2. 建玉を照会し（null＝不明は据え置き）、**決済数量＝min(エントリーの約定数量, 建玉残)**。建玉残は同じ銘柄・方向の建玉から
      **手法の異なる Active 行の数量を差し引いた値**（決定 6）。0 以下なら決済せず Completed（手動決済等で既に無い）。
   3. `attempt = Attempt + 1`、`CloseDecisionId = ProtectiveStopIds.SoftwareCloseDecisionId(entry, attempt)`。
      🔴 **二重決済の防止は 3 重**: (i) 同じ DecisionId の発注記録があれば再送せずその結果で扱う、(ii) 送る前に予約表で DecisionId を確保し
      （IADR-0057 の一意制約）、確保できなければ送らない（ハンドラとガードの並行・再配送）、(iii) 行が Completed なら候補にならない。
   4. 成行を送る（参照価格＝検知時の価格）。接続断（`BrokerUnavailableException`）は予約を解放して据え置き。その他の例外は予約を残して据え置き
      （届いたか不明＝同じ DecisionId では再送しない。滞留予約はリコンサイルの領分）。
   5. 受理（Accepted / PartiallyFilled / Filled）→ 記録を保存し予約を確定、行を Completed、`SoftwareStopExecuted(ClosePlaced)`（決済 Intent 同伴）。
      約定は既存の約定追跡（IADR-0113）が `OrderExecuted` で台帳へ届ける。
   6. 拒否（Rejected / Cancelled / Expired）→ 記録を保存し `Attempt` を進める。**到達 1 回につき 3 試行で打ち切り**、到達の記録を外して
      `SoftwareStopExecuted(CloseRejected)`（Critical・手動決済を促す）。次の到達で再開する（市場監視は価格が戻るまで再発火する）。
      moomoo の模擬取引は時間外の成行を受け付けない可能性があり、無制限に送ると拒否注文を積み続けるため。
6. **ガード**（`ProtectiveStopGuard`）: **S1 行はブローカーの注文照会をしない**。到達済みなら決定 5 を再試行、未到達なら
   「エントリーが約定 0 で終端」または「建玉残（決定 5-2 の按分）が 0 以下」で Completed、それ以外は据え置く。
   **S0 行の建玉残もソフトウェア逆指値の約定数量（記録が無ければ 0）を差し引いて判定する**（#826 項目 3 の S1 側）——差し引かないと
   S1 の建玉が S0 の逆指値を生かし続け、建玉消滅後に残った逆指値が反対建玉を生む。S1 が無い構成（実弾・S0 のみ）では差し引く量が 0 で**挙動は不変**。
   S2 は記録を持たないため按分に入らない（残余リスク）。
7. **通知文**（#826 項目 2）: 損切り到達を検知する市場監視は建玉ごとの手法を知らない（選択肢 5 を採らない）。`StopLossTriggered` の通知と
   リスク管理の検知ログは「S0＝ブローカー側の逆指値が決済／S1＝システムが成行で決済（別途通知）／S2＝誰も決済しない（手動で決済）」を
   **列挙**する文へ改める。S1 の決済・拒否は `SoftwareStopExecuted` の通知が建玉を特定して伝え、S2 は免除の通知が建玉ごとに明示済みである。
8. **契約**: 新イベント 2 種（既存イベントの形は変えない）。
   - `SoftwareStopArmed`（EntryDecisionId, Symbol, Market, Side, ProductType, Quantity, StopLossPrice, Provider, OccurredAt）— 監査・通知（Warning:
     「システム停止中は決済されない」を明記）。
   - `SoftwareStopExecuted`（EntryDecisionId, Symbol, Market, Outcome, Quantity, StopLossPrice, TriggeredPrice, Attempt, CloseDecisionId?, CloseOrderId?,
     CloseIntent?, OccurredAt）・`SoftwareStopOutcome`（`ClosePlaced=0` / `EntryCancelled=1` / `CloseRejected=2`）— 監査・通知
     （ClosePlaced / EntryCancelled は Warning、CloseRejected は Critical）・リスク管理の台帳結線（ClosePlaced のとき `AppendApproval`。冪等は DecisionId）。
9. **購読と再起動耐性**: 発注執行は `StopLossTriggered` を `ai-stock-trading.order-execution-service.StopLossTriggered` で購読する（IADR-0129 の共通ヘルパ。
   Wolverine 6.24.5 の RabbitMQ キューは既定で durable）。停止中に発行された到達はキューに残り再開後に届く。行は DB にあり、再起動後の到達・ガードの巡回が
   そのまま決済する。ハンドラは Wolverine の規約発見とビルド時 codegen に載り、依存（`SoftwareStopExecutor`）は構成を問わず登録する
   （建玉照会は moomoo 構成でだけ解決され、内蔵 paper では据え置きになる。S1 は内蔵 paper で選べない＝行ができない）。
   宣言 `pipeline.json` に段 `execute-software-stop` を足す（変換段: 到達 → 決済）。

## 理由

- **到達の永続化を決済より先に置く**のは、損切りの発動を「その時点で決済できたか」に依存させないためである。据え置き（接続断・建玉不明・取消待ち）を
  ガードの巡回（既定 30 秒）で拾えば、価格が戻って到達が途絶えても決済が失われない。
- **予約表の再利用**は、IADR-0057 が発注の at-most-once を既に保証している仕組みであり、新しい排他（行ロック・楽観並行）を足さずに
  ハンドラとガードの並行・再配送を塞げる。
- **未終端のエントリーを取り消してから決済する**のは、決済後に残りが約定すると無保護の建玉が生まれるため。終端を待つ間の遅延はガードの巡回 1 回分である。
- **数量を建玉と約定数量の小さい方にする**のは、手動決済・一部決済の後に全量を売ると反対建玉（空売り）を作るためである。

## 結果・残余リスク

- 良い影響: SIMULATE＋S1 で建玉が残り、損切りライン到達で成行決済まで観測できる。S0・S2・実弾の挙動は不変（S0 のガードは S1 行が無ければ差分 0）。
- **システム停止中は決済されない**（S1 の本質。ブローカー側に保護が無い）。`SoftwareStopArmed` の通知が明記する。
- **到達の遅延**: 市場監視 60 秒周期＋キュー＋（据え置き時）ガード 30 秒。損切りラインから価格が離れて約定し得る（成行）。
- **moomoo アダプタは送信後の SDK 例外を Rejected に丸める**（既存の挙動）。実際には届いていた場合、次の試行（別 DecisionId）が重なり得る。
  次の試行の前に建玉を照会し直すため、先の注文が約定済みなら数量 0 で止まるが、**未約定のまま滞留していると二重に出る**。SIMULATE 限定で頻度は低い。
- **即時約定で返った決済は約定追跡に載らない**（非終端だけを追跡する。S0 の保護喪失の手仕舞いと同じ既存の穴）。moomoo は発注応答を Submitted で返すため現状は起きない。
- **同一銘柄の S1 と S2 の混在**: S2 は記録を持たないため按分できず、S1 側の建玉が手動決済で先に消えていると S1 の決済が S2 の建玉を売り得る。
- **損切り到達の通知は建玉ごとの手法を示さない**（列挙文。決定 7）。手法の表示は #823。
- 市場監視は平日判定だけで時間外も到達を出す。時間外の成行拒否は決定 5-6 の打ち切りで抑える。

## ［2026-09-18 追記 / #820 の監査］同じ銘柄の複数行への配分と、エントリー記録が無い行の扱い

監査（フレッシュな文脈・PR #830）が 2 件の売り過ぎを実測した。**挙動を直した。決定 1〜9 の方針は変えない。**

1. **決定 5-2 を改める（配分）**: 行ごとに「建玉残」を上限にしていたため、同じ銘柄・方向の S1 行が同じ建玉を二重に
   主張した（実測: 10 株の行 2 件＋手動売却 5 株 → 20 株の決済 vs 保有 15 株＝反対建玉）。**同じ銘柄・市場・方向の
   Active な S1 行へ、記録の作成時刻（＝エントリーの発注順）→ EntryDecisionId の順に建玉残を配分する**
   （`ProtectiveStopNetting.AllocateSoftwareStops`）。配分は決定的で、ハンドラとガードのどちらから呼んでも同じになる。
   - 🔴 **順序に到達時刻を混ぜない**。1 回の到達を処理する途中で行へ到達を書き込むと順序が変わり、どの行にも
     持ち分が回らなくなる（実測で 2 件とも据え置きになった）。
   - 🔴 **発注済みで未約定の決済数量を差し引く**。建玉照会は決済が約定するまで減らないため、差し引かないと
     次の行へ同じ建玉をもう一度配分する（実測で 20 株になった）。窓は 1 日（当日有効の注文しか出さない）。
   - **持ち分が 0 の行は完了させず据え置く**。他行の決済で建玉が減れば次の巡回で持ち分が生まれる。完了させると
     保護のない建玉が残る。建玉そのものが無い（方向の純額が 0）ときだけ従来どおり完了する。
2. **決定 5-7 を足す（孤立行）**: エントリーの発注記録が無い行は、従来は**行の数量で決済していた**ため、同じ銘柄の
   別のエントリーの建玉を売った（実測: 孤立行＋実在の 10 株で 20 株）。**記録が無いあいだは 1 株も決済しない。**
   `SoftwareStopExecutor.DefaultOrphanGrace`（15 分）を過ぎても記録が無ければ、決済を出さずに行を閉じて
   `SoftwareStopExecuted(EntryMissing=3)` を発行する（通知・監査は Critical）。閉じないと毎巡回この行で決済を試み続ける。
   猶予は、送信と記録のあいだのクラッシュ窓・予約のリコンサイルが記録を補える長さに合わせた。
3. **記録済みの決済を配分より先に確定する**: 同じ試行の決済記録がある（クラッシュ窓）ときは、建玉照会も配分も行わず
   記録の結果で行を確定する。差し引きを先に効かせると自分の決済で持ち分 0 になり、確定できなくなる（実測）。

残る監査指摘（**本追記では直していない**）: 決済が受理後に取消・失効した場合の再武装、拒否の連発に対する待ち時間、
古い写しからの上書き（楽観並行）、無期限の据え置きの Critical 化。**#833 で扱う。**
