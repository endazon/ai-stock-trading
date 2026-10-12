---
title: 新規建ての審査を直列化し、承認した新規建てを同じトランザクションで予約する（#1319）
type: spec
status: done
related_ids: [FR-10, FR-20, UC-01, UC-02, ADR-0003, ADR-0009, ADR-0016, IADR-0346, IADR-0407, IADR-0057, IADR-0163, IADR-0342, IADR-0148, IADR-0529]
author: claude (Claude Code)
created: 2026-10-13
updated: 2026-10-13
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 保有建玉数の上限・1 日あたりの発注金額上限)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§5 保有建玉数 3・Stage の発注可能額)
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md (決定 9 保有建玉数)
  - planning:projects/ai-stock-trading/07_adr/ADR-0009_pause-resume-and-lockout-states.md (手仕舞いは止めない)
---

# 仕様書: 新規建ての審査を直列化し、承認した新規建てを同じトランザクションで予約する（#1319）

> 本仕様書は実装着手前に作成した（着手 2026-10-13）。基点は AST `origin/develop` `bd59e52a`。

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **FR-10**（保有建玉数の上限 3・1 日あたりの発注金額上限）、FR-20（段階資金）、UC-01 / UC-02（定時判断 → 審査）。
- 計画 ADR: ADR-0003（統制は最終防衛線）、ADR-0016 決定 9（保有建玉数）、ADR-0009（手仕舞い・損切りは止めない）。
- 起票: #1319（PoC 2026-10-12 米国セッションで、上限 3 なのに 6 建玉まで新規建てが承認された疑い）。
- 関連 IADR: IADR-0346（未終端の新規建ての算入。残余リスクの評価が誤り＝本件で是正）、IADR-0407（承認済みの判断の再配送は再審査しない。**変えない**）、IADR-0057（発注執行の DecisionId 予約。**変えない**）、IADR-0163 決定 2（不在が統制の無効を意味する依存は必須）。
- 新規 IADR: **IADR-0529**（本件の決定）。

## 問題

1 回の定時サイクルの判断 N 件は、Wolverine がハンドラの成功後にまとめて発行するため、リスク管理へ**同時に**届く。
審査は 1 件ごとに snapshot を組むが、承認は台帳に何も書かない（承認行は `OrderApproved` が RabbitMQ を往復した後に
`OrderApprovedLedgerHandler` が書く）。したがって N 件はどれも**同じ古い snapshot**で判定され、保有建玉数・日次発注累計・
段階資金の上限をすり抜ける。IADR-0346 は残余リスクで「承認直後の数秒は次の審査から見えないが、定時判断の周期（5 分）に比べて
無視できる」と評価したが、**次の判断は 5 分後ではなく同じ瞬間に届く**ため、この評価は誤りだった。

## 母集合（規則 9・10。着手前に引いた）

### 同じ snapshot / 同じ注文源を読む統制（規則 9: 「承認しても台帳に何も書かない」側の語で走査）

走査: `grep -rn "snapshotBuilder.Build()\|IWorkingEntryOrderSource" backend/Services/RiskManagementService --include=*.cs`（テスト・obj を除く）。

| 読み手 | 読む値 | 本件の扱い |
| --- | --- | --- |
| `OrderScreeningService.ScreenAsync`（審査） | `OpenPositionCount`・`DailyOrderedAmount`・`InvestedCapital`（ほか状態系） | **直列化の対象**。予約を含めて数える |
| `ShortSellContextSupplier`（空売り文脈・審査の中で呼ばれる） | 注文源（未終端の新規建て）→ 1 銘柄の空売り建玉・空売り合計・建玉総額 | 注文源へ予約を足すので**自動で予約を含む**。審査の中（ロックの内側）で呼ばれる |
| `EntryBlockersService`（#1113 の新規建ての可否の口） | snapshot | 読むだけ（承認しない）。注文源経由で予約を含む＝判断側も先に承認された分を見る |
| `SizingContextService`（サイジングの残枠） | snapshot | 同上（残枠が予約の分だけ減る） |
| `RiskStatusService`（画面の状態） | snapshot | 同上 |
| `WorkingEntryOrdersService`（`GET /risk-controls/working-entry-orders`。判断の入力） | 注文源 | 同上（予約を未約定の新規建てとして返す） |

- 承認を出す経路は `OrderScreeningService`（判断由来。新規建て・手仕舞い）・`PositionCloseService`（owner の手仕舞い）・
  `MaintenanceMarginReductionService`（維持率割れの自動縮小）の 3 つ（`grep -rn "new OrderApproved(" …`）。**新規建てを承認するのは審査だけ**であり、
  他の 2 つは決済（Close）なので直列化・予約の対象外（ADR-0009）。
- `SymbolsTradedToday`（同日再エントリー）は IADR-0346 決定 4 により未約定を算入しない。予約も同じく算入しない（変えない）。
- 状態系（kill switch・一時停止・ロックアウト・縮退・口座種別・基準資金）は累計ではないため、同時到着ですり抜けない（対象外）。
- 日次損失・DD・連敗は約定（実現・含み）からのみ導くため対象外。

### この変更で新たに誤りになる自分の記述（規則 10）

- `IADR-0346` の残余リスク 2 行（`.ai-context/adr/IADR-0346_…md:103-104`）→ **日付つき追記**で誤りと置き換え先を書く（本文は凍結）。
- `IADR-0407` 決定 2「承認を発行し直さない」・棄却案「承認行から `OrderApproved` を組み直して発行し直す」→ 本件は**承認行ではなく予約から**組み直す（予約が承認時点の損切りの実行機構を持つ）。承認行がある場合の決定（再発行しない）は変えないため、IADR-0407 の本文は誤りにならない。IADR-0529 に関係を書く。
- テスト T-10-873（「射影の前に届いた再配送は通常の審査を受ける」）→ 予約があるので**再審査せず同じ承認を発行し直す**形に変わる。テストの期待（承認される・同じ DecisionId）は変わらないが、根拠のコメントとテスト仕様書の行を直す。
- 機能仕様書 FR-10「約定前の新規建て注文も算入する」の表 → 予約の行を足す。
- 走査: `grep -rn "直列化は本決定の範囲外\|並行審査\|互いの未約定\|定時判断の周期（5 分）に比べて" --include=*.md --include=*.cs .` → IADR-0346 の 2 行のみ（他は無関係の「直列化」）。

## やること

1. **予約テーブル `entry_reservations`**（主キー `DecisionId`）と EF マイグレーション（`dotnet ef migrations add AddEntryReservations`）。
   列: 銘柄・市場・方向・数量（承認行と同じ＝Intent の数量）・価格・`FxRateToBase`・承認数量・承認時刻・損切りの実行機構。
2. **ポート `IEntryReservationStore`**（Features）:
   - `RunSerializedAsync(body)`: 新規建ての審査を直列化して body を実行する。EF 実装は **`BEGIN` → `pg_advisory_xact_lock(定数)` → body → `COMMIT`**（body が投げればロールバック）。関係 DB でないプロバイダ（テストの EF InMemory）はプロセス内のセマフォへ縮退する。InMemory 実装はインスタンスのセマフォ。
   - `Find(decisionId)` / `Reserve(reservation)`。
3. **注文源へ予約を足す**: `EfWorkingEntryOrderSource` / `InMemoryWorkingEntryOrderSource` は「承認行の未終端」∪「**承認行が無い**予約のうち注文アクティビティが終端でないもの」を返す。承認行が来たら承認行だけで数える（二重計上しない）。当日の判定・残数量は従来どおり純関数が行う。
4. **審査**（`OrderScreeningService.ScreenAsync`）: 新規建ては全体を `RunSerializedAsync` の中で行う。
   1. 承認行がある → `ApprovedReplay`（IADR-0407。変えない）。
   2. 予約がある（承認行はまだ無い）→ **再審査せず、予約から同じ `OrderApproved` を組み直す**（`ApprovedRepublish`）。
   3. どちらも無い → 従来の審査。承認なら同じトランザクションで予約を書く。
   - 手仕舞い（Close）はロックを取らず、予約も書かない（従来どおり）。
5. **ハンドラ**（`TradeDecisionMadeHandler`）: `ApprovedRepublish` は観測を記録し（DecisionId で冪等）、審査メトリクスは刻まず、`OrderApproved` を発行する。
6. **配線**: `Program.cs` に `EfEntryReservationStore` を scoped で登録し、`OrderScreeningService` の**必須引数**にする（IADR-0163 決定 2）。注文源も予約を読む。
7. **IADR-0529** を起こす。IADR-0346 に日付つき追記（残余リスクの評価の誤りと置き換え先）。
8. 文書の追随: 機能仕様書 FR-10（統制の入力）・テスト仕様書 FR-10（新 T-ID と T-10-873 の行）・データ仕様（`entry_reservations`）。

## 受け入れ基準（#1319 の 9 項目 → テスト）

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 台帳に何も反映されない状態で新規建てを N 件続けて審査（N > 上限）→ 上限 − 既存建玉数まで承認、残りは `MaxPositionsExceeded` | T-10-2568 |
| 2 | 同じ条件で日次発注累計・段階資金も先に承認した分を含めて判定 | T-10-2569（日次）・T-10-2570（段階資金） |
| 3 | 並行に審査しても 1・2 が成り立つ | T-10-2571（InMemory・並行）・T-10-2579（**実 PostgreSQL・並行**） |
| 4 | 承認済みの判断の再配送（承認行あり）は再審査されず、拒否へも反転しない | 既存 T-10-870〜T-10-875（無改変で緑）・T-10-2572 |
| 5 | 承認後に `OrderApproved` の発行が失敗して再配送されても `OrderApproved` が発行される | T-10-2573（審査）・T-10-2574（ハンドラ） |
| 6 | 予約が承認行へ引き継がれた後に二重計上しない | T-10-2575（審査）・T-10-2577（注文源 EF/InMemory） |
| 7 | 取消・失効・見送りの後は枠が返る | T-10-2576 |
| 8 | 手仕舞いは直列化・予約で遅延も拒否もされない | T-10-2578 |
| 9 | IADR-0346 の残余リスクを後継で是正（本文は凍結） | IADR-0529・IADR-0346 の追記（文書） |
| — | 予約の冪等・臨界区間の例外で予約が残らない | T-10-2580・T-10-2581 |

## 検証

- `dotnet build backend/backend.slnx`（警告 0）
- `dotnet test backend/Services/RiskManagementService/Tests`（全件）
- 実 PostgreSQL の並行テスト（`AiStockTrading.IntegrationTests` の `EntryScreeningSerializationE2ETests`）をローカルの PostgreSQL 16 で `E2E_POSTGRES_CONNECTION` を与えて実走する
- `dotnet format backend/backend.slnx --verify-no-changes`
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
- 文書系の検査器（`check-trace-blocks` / `gen-knowledge-graph --check` / `check-doc-links` / `check-adr-index-sync` / `check-commit-messages` / `check-plan-id-qualification`）

## 実測

- `dotnet test backend/Services/RiskManagementService/Tests`: 全件緑（実装前 2,326 件 → 2,347 件）。`dotnet build backend/backend.slnx --no-incremental` は警告 0。
- 実 PostgreSQL 16（ローカル `initdb`・`E2E_POSTGRES_CONNECTION` で注入）で `EntryScreeningSerializationE2ETests` 4 件緑。既存の `ApprovedOrderTerminalConcurrencyE2ETests` 3 件も緑（同じ DB・移行に `AddEntryReservations` を含む）。
- 実 DB で見つかった食い違い: 予約から組み直した承認の承認時刻が、最初の発行と 100ns 単位でずれた（PostgreSQL の timestamp はマイクロ秒）。承認時刻をマイクロ秒へ切り詰めて揃えた（IADR-0529 決定 2）。
- 並行テストを `Task.Run` で書いた最初の形は、直列化を外す変異でも緑だった（スレッドプールの注入待ちで審査が自然に順番になる）。専用スレッド（`TaskCreationOptions.LongRunning`）へ改めて、変異で赤になることを確かめた。
- 母集合の取りこぼし（規則 10 で後から見つけた追随先）: `scripts/scripts.repo.test.js` の「cutover manifest は EF ModelSnapshot の全テーブルと一致する」が赤になった。新しい表 `entry_reservations` を `scripts/cutover-count-reconcile.sh` の manifest へ `state`（統制の状態・自動パージの対象外・切替で保全）として足した。あわせて、当初入れていた「7 日より古い予約を消す」掃除をやめた（state の表を自動で消すと保全の閉世界に反する）。運用手順書（ブローカーの発注経路）の「手で書き換えない表」にも足した。
- 採番の衝突: 起草時の最大＋1 は IADR-0528 だったが、作業中に develop へ IADR-0528（Stage 0 の as-of 入力。#1321）が入った。欠番受容の規則（`traceability.repo.md`）に従い、その時点の最大＋1 の **IADR-0529** へ改番した。最初の 2 コミットの件名・本文に残る「IADR-0528」は改番前の番号である。
- 変異（テスト仕様書の表と同じ）:

| 変異 | 赤になったテスト |
| --- | --- |
| M1 直列化しない | 並行 3（InMemory）・手仕舞いの待ち 1・実 DB の並行 2 |
| M2 予約を書かない | 新しいテスト 15・T-10-873・実 DB 3 |
| M3 予約から組み直さない | T-10-2573・T-10-2574・T-10-873・実 DB の再配送 1 |
| M4 注文源が承認行のある予約も返す | T-10-2577（EF） |
