---
title: 判断由来の決済の指値が発注から N 分未約定なら系が取り消すだけにする（既定は無効・1 建玉 1 日 1 回）。moomoo の失効（Disabled）を失注（Expired）として拒否と区別して表示する（計画 ADR-0067・#1339）
type: spec
status: done
related_ids: [FR-05, FR-10, FR-11, FR-19, UC-01, UC-06, ADR-0003, ADR-0007, ADR-0040, ADR-0050, ADR-0051, ADR-0067, IADR-0040, IADR-0117, IADR-0357, IADR-0461, IADR-0515, IADR-0535, IADR-0536, IADR-0539]
author: claude (Claude Code)
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0067_decision-exit-limit-unfilled-cancel-only.md (決定 1〜7)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§5「判断由来の決済の指値の未約定時の取消（N）」「同 回数の上限」「発注パターン禁止」)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-05 失注の定義・FR-19)
---

# 仕様書: 判断由来の決済の指値の未約定時の取消と、失効の「失注」表示（計画 ADR-0067・#1339）

> 本仕様書は実装着手前に作成した（着手 2026-10-16）。基点は AST `origin/develop` `84f7612d`。
> 計画は project-planning `origin/main` `a44caed`（ADR-0067 を足したコミット）で読んだ。

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **FR-05**（注文状態の追跡・失注の定義）、**FR-10**（手仕舞いは止めない）、**FR-19**（相場操縦とみなされ得る発注パターン。取消も検知の適用範囲）、FR-11（観測）、UC-01 / UC-06。
- 計画 ADR: **ADR-0067**（決定 1〜7）。ADR-0050 決定 1 の補完。ADR-0051 決定 3（自動の利確は採らない＝置き直し・成行を採らない理由）、ADR-0040 決定 4（GTC の実弾確認＝#1214）、ADR-0003・ADR-0007。
- 起点 issue: **#1339**（同件の検索: open issue 一覧・直近 issue に ADR-0067 を起点にした実装 issue は無い。#1339 は「取消置換が無く」を題に含み、残りの論点を planning#797 の裁定待ちで保留していた。本件はその残り＝ADR-0067 のフォローアップ 1・2 を実装するので、新規に起票せず #1339 を起点にする）。
- 関連: #1214（保護逆指値の GTC の実弾確認。**有効化はその後**）、#1341（IADR-0536。DAY・解放・WRN）、#1340（IADR-0535）。
- 新規 IADR: **IADR-0539**（develop の最大は IADR-0538。open PR に IADR を足すものは無い。欠番可・最大＋1）。

## 計画の要求（受け入れ基準の元）

| # | ADR-0067 | 要求 |
| --- | --- | --- |
| R1 | 決定 1 | 判断（LLM）由来の決済の指値が、発注から N 分たっても約定しなければ、系が取り消すだけにする。置き直し・訂正・成行への切り替えはしない。取消の後は何も出さない（次の巡回に任せる） |
| R2 | 決定 1 | 取り消した決済は、ブローカーが終端を確かめた時点で処理中から外れる（IADR-0536 決定 3「迷ったら解放しない」を変えない） |
| R3 | 決定 1 | 一部約定の注文の残りの扱いを実装が定め、IADR に記録する |
| R4 | 決定 2 | N の既定は 15 分（分で書く）。回数の上限は 1 建玉 1 日 1 回（市場の現地取引日）。上限到達後は取り消さず DAY の失効に任せる |
| R5 | 決定 2 | **既定は無効** |
| R6 | 決定 2 | 有効時の N は相場操縦の検知の窓（IADR-0040 の既定 5 分）より長い。N の上端・回数の上下端を IADR に記録する |
| R7 | 決定 3 | 取消も検知（FR-19）の適用範囲に含める |
| R8 | 決定 4 | 適用は判断由来の決済だけ。利用者の手仕舞い・維持率割れの自動縮小には効かない |
| R9 | 決定 5 | moomoo の `Disabled`（失効）を FR-05 の「失注」とし、拒否と区別して表示する |
| R10 | 決定 6 | 実施は #1214 の実弾確認の後。既定無効で実装し、有効化は PoC 確認の後（PR・IADR に明記） |
| R11 | 決定 7 | 統制と実現手段（3 点セット）を機能仕様書へ書く |
| R12 | CLAUDE.md | 既定値を `TradingDefaults` のテストで固定する |

## 原因・現状（コードで確かめた事実）

1. 判断由来の決済は `OrderApprovalOrigin.TradeDecision` を持つ承認経路の決済で、常に指値で出る（`OrderIntent.MarketOrder` を立てるのは利用者の手仕舞い `PositionCloseService` だけ。全文検索で確認）。記録 `ExecutionRecord.ApprovalOrigin` に出どころが残る（IADR-0515）。
2. 取消の口は `OrderAmendmentService.CancelAsync`（`IBrokerAdapter.CancelOrderAsync` → 照会で確認 → 確認できたときだけ `OrderCancelled`）にある（IADR-0357）。取消は注文のライフサイクル台帳（`order_lifecycle_events`）に残り、`OrderCancelled` はリスク管理の注文アクティビティ射影（`OrderCancelledActivityHandler`）へ入る＝相場操縦の検知の母集団（R7）。
3. 検知の窓は発注時刻で切る（`EfOrderActivitySource`。`PlacedAt >= asOf - 5 分`）。窓の値は `TradingDefaults.CreateManipulationDetectionSettings().LookbackWindow`（5 分）で、実行時の変更経路は無い（`ManipulationDetectionSettings` の注記）。
4. 発注執行（OrderExecutionService）はリスク管理（RiskManagementService）を参照できない。両者が共有できるのは `Shared.Contracts` と `Shared.Kernel`（発注執行は現在 Kernel を参照していないが、他の 7 サービスが参照しており依存規則上も許される）。
5. 失効: `MMApiMoomooTradeClient.MapState` は OpenD の `Disabled`（22）を `Failed` へ写し、`MoomooBrokerAdapter.MapState` が `Failed` を `OrderStatus.Rejected` へ写す。`OrderStatus.Expired` は既にあり、終端・未約定の終端（在庫解放の引き金）の集合にも含まれる（下の母集合）。
6. 表示: 注文状態を出す画面（SC）は無い（フロントの全文検索で `OrderStatus` を表示する箇所は無い）。表示は Discord 通知（`NotificationFormatter.From(OrderExecuted)` / `From(PositionCloseAbandoned)`）、監査の要約（`AuditEntryFactory.From(OrderExecuted)`）、約定追跡のログが列挙名（`Rejected` 等）をそのまま出す。

## 母集合（規則 9・10）

### `Disabled` を `Expired` へ写し替えて挙動が変わる箇所

誤りの側の文字列 `OrderStatus.Rejected` / `OrderStatus.Expired` / `MoomooOrderState.` / `"Rejected"` / `Rejected =>` で `backend/` と `frontend/` を走査した（試験・移行を除く）。

| 箇所 | `Rejected` と `Expired` の扱い | 写し替えの影響 |
| --- | --- | --- |
| `OrderStatusLifecycle`（発注執行・リスク管理）の `IsTerminal` / `AbandonsUnfilledRemainder` | どちらも含む | 無し（在庫解放・再武装・手仕舞いの未約定残の通知は同じに動く） |
| `PaperBrokerAdapter.IsTerminal`・`ProtectiveStopWaiverSettlement.IsTerminal` | どちらも含む | 無し |
| `OrderActivityRecord.IsCancelledWithoutFill`（検知の母集団） | `Expired` を含み `Rejected` を含まない | **そのままだと変わる**（失効が母集団に入る）。PR のレビュー（🟡）を受け、`Expired` を外して `Cancelled` だけを数える形へ直した（失効は取消ではない。写し替えの前と同じ母集団。IADR-0539 決定 6・T-19-324） |
| `EfOrderActivityStore` / `InMemoryOrderActivityStore` の `Rejected` | 見送り・拒否の記録 | 無し（`Disabled` の経路ではない） |
| `MoomooBrokerAdapter`:192・218、`OrderExecutionAppService`:935 | 発注時の拒否 | 無し（発注の応答。`Disabled` は来ない） |
| `MMApiMoomooTradeClient.IsTerminal`・`MoomooBrokerAdapter.IsTerminal` | `Failed` を含む | **新しい `Expired` を足す**（足さないと終端が届かない） |
| `NotificationFormatter`・`AuditEntryFactory` の `{e.Status}` | 列挙名を出す | 表示に日本語の区別を足す（R9） |
| 文字列 `"Rejected"` / `Rejected =>` | 別の型（`WatchlistApplyStatus` 等）だけ | 無し |

除外: `SubmitFailed`（3）・`Failed`（21）・`Deleted`（23）は ADR-0067 決定 5 が「本 ADR では決めない」とするので `Failed`→`Rejected` のまま残す。

### この変更で新たに誤りになる自分の記述（規則 10）

- `IMoomooTradeClient.MoomooOrderState.Failed` の注記「OpenD の SubmitFailed / Failed / Disabled / Deleted」→ Disabled を外す。
- `MMApiMoomooTradeClient.MapState` の注記・`MMApiMoomooTradeClientMappingTests` の `failed` 集合（22 を含む）→ 22 を外し `Expired` の試験を足す。
- `PendingCloseRecheckAndReportTests` T-10-2657 の注記「失効（Rejected）」→ 試験の値は Rejected のまま意味は通る（照会の応答を直接与えている）ので直さない。docs/tests の T-10-2654 の「拒否（失効）」も同じ（照会の応答の分類であり、写像の記述ではない）。

## 方針

### 1. 取消（R1〜R8）

- 新しい部品 `DecisionCloseUnfilledCanceller`（発注執行・`Features/OrderExecution/CancelUnfilledDecisionCloses/`）。約定追跡の常駐（`OrderFillPollingService.PollOnceAsync`）が巡回ごとに呼ぶ。新しい常駐は足さない。
- **無効なら何もしない**（ストアを読まない・ブローカーへ何も送らない）。
- 対象（`IExecutedOrderStore.FindUnfilledDecisionCloses(placedAtOrBefore, limit)`）: `PositionEffect=Close` かつ `ApprovalOrigin=TradeDecision` かつ 非終端（Accepted / PartiallyFilled）かつ 発注（`ExecutedAt`）≦ 今 − N かつ **まだ取消を求めていない**（新しい列 `UnfilledCancelRequestedAt` が null）。古い順・上限つき。
  - `ApprovalOrigin=Unknown`（旧いメッセージ）・null（保護の機構）・`OwnerClose`・`MaintenanceMarginReduction` は対象外（R8。`Unknown` は「分からない」なので取り消す側へ倒さない——取消は系の能動的な操作である）。
- 回数の上限: 同じ建玉（銘柄・市場・決済の方向）の判断由来の決済について、**今の市場の現地取引日**に取消を求めた件数（`UnfilledCancelRequestedAt` がその日の範囲にある記録）が上限以上なら、取り消さない（DAY の失効に任せる）。INF を注文ごとに 1 回残す（常駐のプロセス内で畳む）。
- 手順: ①印を書く（`MarkUnfilledCancelRequested`。非終端・未印の記録だけに書ける）→ ②`OrderAmendmentService.CancelAsync` → ③確認できたら `OrderCancelled` を発行する。**印を先に書く**: 取消の送信の後に落ちても、再起動後に同じ注文をもう一度取り消さず、回数にも数えられる（少ない側へ倒す）。取消の失敗（例外）でも印は残す（再試行しない＝取消の反復を作らない。注文は DAY で失効する）。
- 取消の後の解放は既存の約定追跡が行う（照会で終端を観測して `OrderExecuted`。IADR-0536 決定 3 を変えない。R2）。
- **一部約定（R3）**: 一部約定のまま N 分たった決済も取り消す（残りだけが取り消され、約定した分は残る＝moomoo の `Cancelled_Part`）。残りが生きている間は同じく判断が止まるため。
- 構成（`DecisionCloseUnfilledCancel` 節）: `Enabled`（既定 false）・`UnfilledMinutes`（既定 15）・`MaxCancelsPerPositionPerDay`（既定 1）。**有効なときだけ**範囲を検証し、外れたら起動で止める（`ValidateOnStart`）。
  - N: **検知の窓（5 分）より長く**（6 以上）、**390 以下**（米国の通常取引時間 6.5 時間。これより長い N は DAY の失効より後になり意味が無い）。
  - 回数: **1 以上 3 以下**。
  - 検知の窓は発注執行から読めない（事実 4）。窓の値を `Shared.Kernel` の定数（`DecisionCloseUnfilledCancelDefaults.ManipulationLookbackWindowMinutes`）に置き、リスク管理の `TradingDefaults` もその定数から窓を作る（1 か所に置く）。窓を変えると N の下端も一緒に動く。
- 既定値は `Shared.Kernel` の `DecisionCloseUnfilledCancelDefaults` に置き、`TradingDefaults`（リスク管理）が同じ値を公開し、`TradingDefaultsTests` が計画の値で固定する（R12）。

### 2. 失注（R9）

- `MoomooOrderState.Expired`（末尾に足す）。`MMApiMoomooTradeClient.MapState(22)` → `Expired`。`MoomooBrokerAdapter.MapState(Expired)` → `OrderStatus.Expired`。両方の `IsTerminal` に足す。
- 表示: `Shared.Contracts` に `OrderStatusDisplay.Describe(status)`（`Expired（失注: 期限切れで失効）`・`Rejected（拒否: 証券会社が受理せず）` など、列挙名に日本語の区別を添える）を置き、通知（取引実行・手仕舞いが約定せず終了）・監査の要約・約定追跡のログ（解放・未約定の WRN）で使う。列挙名は残す（ログの grep・既存の運用手順を壊さない）。
- 画面は対象外（注文状態を出す画面が無い。事実 6）。

### 3. 既定無効と有効化の順序（R10）

- 配備（Helm values）にも有効化を書かない。IADR-0539 と PR に「有効化は #1214 の実弾確認と PoC の確認の後、利用者が決める」と書く。

## 窓（規則 11）

### 取消の窓（発注＝前の端・終端の観測＝後の端）

プローブ: **P+**（発注から N 分未満で非終端＝取り消さない）、**P−**（発注から N 分以上で非終端＝取り消す）、**P0**（発注から N 分以上だが終端＝取り消さない）。

| 形 | P+ | P− | P0 |
| --- | --- | --- | --- |
| (a) 前の端だけ（発注から N 分以上なら状態を問わず取り消す） | ✓ | ✓ | ✗（終端の注文へ取消を送る。変異 MT-C2 で赤） |
| (b) 後の端だけ（非終端なら経過を問わず取り消す） | ✗（発注直後に取り消す。変異 MT-C1 で赤） | ✓ | ✓ |
| **(c) 両端** | ✓ | ✓ | ✓ |

### 回数の上限の窓（市場の現地取引日）

プローブ: **D+**（同じ現地取引日に 2 本目＝取り消さない）、**D−**（翌現地取引日の 1 本目＝取り消す）、**Dz**（UTC では翌日だが米国東部では同じ日＝取り消さない）。

| 形 | D+ | D− | Dz |
| --- | --- | --- | --- |
| (a) 上限を見ない | ✗（変異 MT-C4） | ✓ | ✗ |
| (b) UTC の暦日で数える | ✓ | ✓ | ✗（変異 MT-C5） |
| (c) 過去 24 時間で数える | ✓ | ✗（前日の取消が 24 時間以内に残る） | ✓ |
| **(d) 市場の現地取引日** | ✓ | ✓ | ✓ |

## 受け入れ基準（試験への写像）

| # | 基準 | 試験 |
| --- | --- | --- |
| A1 | 無効（既定）なら何もしない（ストアを読まない・取消を送らない） | T-10-2670 |
| A2 | 有効で、判断由来の決済が発注から N 分たって非終端なら取り消す。N 分未満・終端は取り消さない。一部約定も取り消す | T-10-2671 |
| A3 | 1 建玉 1 日 1 回。同じ現地取引日の 2 本目は取り消さない。翌日は取り消す。UTC の日付ではなく市場の現地取引日で数える | T-10-2672 |
| A4 | 利用者の手仕舞い・自動縮小・出どころ不明・出どころなし・新規建ては取り消さない | T-10-2673 |
| A5 | 印を先に書く。取消の失敗でも印が残り、同じ注文を二度取り消さない。確認できた取消だけ `OrderCancelled` を出す | T-10-2674 |
| A6 | 有効時の N ≦ 検知の窓（5 分）・N > 390・回数 0 / 4 は構成エラー。無効なら検証しない。起動（`ValidateOnStart`）で止まる | T-10-2675 |
| A7 | 常駐が巡回ごとに取消を呼び、確認できた `OrderCancelled` を発行する。置き直し・成行は送らない | T-10-2676 |
| A8 | DB・インメモリのストアで、対象の問い合わせ・印・回数の数え方が一致する | T-10-2677 |
| A9 | moomoo の `Disabled`（22）は `Expired`（失注）へ写り、拒否（`Rejected`）と区別される。`SubmitFailed`・`Failed`・`Deleted` は拒否のまま | T-10-2678 |
| A10 | 通知・監査の要約で、失注と拒否が日本語で区別して表示される | T-10-2679 |
| A12 | 失注は検知の「約定なしの取消」に数えない（母集団を変えない）。系の取消（Cancelled）は数える | T-19-324 |
| A13 | 回数の上限で取り消さなかった決済は次の巡回から候補に戻さず、件数の上限を占めない | T-10-2680 |
| A14 | 前の現地取引日に発注した非終端の決済は候補に入れず、回数の枠を消費しない | T-10-2681 |
| A15 | 上限で取り消さなかった注文の記憶は、取引日が替わったら消える | T-10-2682 |
| A11 | 既定値（無効・15 分・1 回）と検知の窓（5 分）を `TradingDefaults` で固定し、既定の N は窓より長い | `TradingDefaultsTests` |

## 変異（実測: 赤になった試験）

`dotnet test --filter "DecisionCloseUnfilled|MMApiMoomooTradeClientMapping"`（29 件）で、各変異を 1 つずつ入れて実測した（2026-10-16）。

| 変異 | 内容 | 赤になった試験 |
| --- | --- | --- |
| MT-C1 | 経過を見ない（後の端だけ。インメモリの問い合わせ） | T-10-2671・T-10-2677（インメモリ） |
| MT-C2 | 状態を見ない（前の端だけ。DB・インメモリの問い合わせ） | T-10-2677（両方）。T-10-2671 は印の書き込みが終端を拒むので緑（多重の防御） |
| MT-C3 | 有効・無効を見ない | T-10-2670・T-10-2676 |
| MT-C4 | 回数の上限を見ない | T-10-2672・T-10-2674 |
| MT-C5 | UTC の暦日で数える | T-10-2672（2 件） |
| MT-C6 | 出どころを見ない（承認経路の決済すべて） | T-10-2673・T-10-2677（インメモリ） |
| MT-C7 | 印を書かずに取り消す | T-10-2671・T-10-2672・T-10-2674 |
| MT-C8 | N の下端を窓と同じ値まで許す（`<=` を `<` に） | T-10-2675（検証・起動の 2 件） |
| MT-C9 | `Disabled` を `Failed` のまま | T-10-2678 |
| MT-C10 | 上限で取り消さなかった注文を問い合わせで除かない（インメモリ） | T-10-2677（インメモリ）・T-10-2680 |
| MT-C11 | 失注も検知の約定なしの取消に数える | T-19-324 |
| MT-C12 | 当日の始まりの下限を見ない（インメモリの問い合わせと部品の両方） | T-10-2672・T-10-2677（インメモリ）・T-10-2681・T-10-2682 |
| MT-C13 | 上限の記憶を日替わりで消さない | T-10-2682 |

## 影響範囲

- コード: 発注執行（新部品・オプション・ストア 2 実装・行・移行・常駐・Program・moomoo の写像）、`Shared.Kernel`（既定値）、`Shared.Contracts`（表示）、リスク管理（`TradingDefaults` の窓を定数から）、通知・監査（表示）。
- 文書: `docs/functional/FR-10_risk-controls.md`・`docs/functional/FR-19_trading-guard.md`・`docs/tests/FR-10_risk-controls-tests.md`・`docs/tests/FR-19_trading-guards-tests.md`。`.ai-context/adr/IADR-0539`・索引。`.claude/rules/traceability.repo.md` の計画 ADR のレンジ（`ADR-0001..0066` → `0067`。計画リポの実物で ADR-0067 が増えた）。

## 残余

1. 有効化は #1214 の実弾確認と PoC の確認の後（利用者が決める）。それまで本統制は配備で働かない（ADR-0067 決定 7 の「無い」のまま。暫定手段は DAY の失効と 15 分の WRN）。
2. SIMULATE での取消は配線済み（`MMApiMoomooTradeClient.CancelOrderAsync`）だが、本経路での実測は有効化の PoC で確かめる。
3. 取消を送った後に注文が約定し切ることがある（取消と約定の競合）。そのときは約定として記録される（約定追跡の既存の経路）。回数には数える。
