---
title: 作業仕様書 — 発注・注文管理の再実装（損切りのブローカー側逆指値への一本化・注文状態「拒否」の分離維持・OpenD 切断時の見送り）
type: work
status: review
related_ids: [FR-05, FR-10, UC-01, UC-02, ADR-0002, ADR-0016, ADR-0019, ADR-0024, IADR-0015, IADR-0057, IADR-0092, IADR-0113, IADR-0117, IADR-0118, IADR-0129, IADR-0150, IADR-0210, IADR-0211]
author: endazon (with Claude Code)
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-05 / FR-10)
  - planning:projects/ai-stock-trading/04_workflows/02_event-driven-trading.md (逆指値一本化・逆指値が成立しない場合の扱い)
  - planning:projects/ai-stock-trading/06_technical/03_moomoo-integration.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md (OpenD 常駐・SPOF)
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md (決定2(b)・決定10)
  - planning:projects/ai-stock-trading/07_adr/ADR-0024_opend-unattended-restart-conditional.md
related_specs:
  - ../adr/IADR-0210_broker-side-stop-loss-unification.md
  - ../adr/IADR-0211_opend-unavailable-forgo-without-queueing.md
  - ../adr/IADR-0015_stop-loss-mechanical-close.md
  - ../adr/IADR-0057_order-dispatch-idempotency.md
  - ../adr/IADR-0092_reservation-broker-probe-moomoo.md
  - ../adr/IADR-0113_moomoo-fill-polling.md
  - ../adr/IADR-0118_broker-position-reconciliation.md
  - ../../docs/functional/FR-10_risk-controls.md
  - ../../docs/tests/FR-10_risk-controls-tests.md
  - ../../docs/DEFINITION_OF_DONE.md
---

# 作業仕様書: 発注・注文管理の再実装 — 逆指値一本化・「拒否」の分離・OpenD 切断の見送り（#331）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-05**（発注・注文状態の追跡。「拒否」＝証券会社が受理しなかった状態）／
  **FR-10**（損切りの実行機構＝ブローカー側逆指値への一本化。システム側は検知・記録・通知のみで決済注文を発行しない。
  逆指値が未受理・失効・受け付けない銘柄/時間帯では建玉を持たない）
- ユースケース（UC）: UC-01, UC-02
- 業務フロー: 04_workflows/02（fixed・逆指値一本化反映済み。「逆指値が成立しない場合の扱い」の 4 分岐表）
- 関連 ADR: ADR-0002（moomoo OpenAPI・OpenD 常駐・SPOF・INDEX 決定 33「再起動中は発注不可」）／
  ADR-0016 決定 2(b)（逆指値の同時発注必須は建玉方向を問わない）・決定 10（`StopOrderRequired`）／
  ADR-0019・ADR-0024（OpenD 無人再起動は条件付き成立。SPOF であること自体は変わらない）
- 実装 ADR: **[IADR-0210](../adr/IADR-0210_broker-side-stop-loss-unification.md)（本作業・逆指値一本化の実装形）**／
  **[IADR-0211](../adr/IADR-0211_opend-unavailable-forgo-without-queueing.md)（本作業・OpenD 切断時の見送り）**／
  前提: IADR-0015（旧・損切り機械執行。本作業で Superseded）・IADR-0057（発注 3 相の冪等化）・
  IADR-0092（remark=DecisionId 伝播）・IADR-0113（約定追跡ポーリング）・IADR-0118（建玉突合）
- 起点 issue: #331（親 #344 フェーズ 2）。旧 #292 / #304（建玉突合・owner 決済）は #331 へ吸収済み

## 目的・背景

計画大改定（planning PR #144）で確定した 2 点を実装へ反映する。

1. **損切りの実行機構をブローカー側の逆指値へ一本化する**（FR-10・利用者裁定 planning#88）。
   現行実装は逆で、市場監視の `StopLossTriggered` をリスク管理が購読し **Close の `OrderApproved` を発行**している
   （`StopLossTriggeredHandler` → `StopLossExecutionService.BuildCloseApproval`。IADR-0015）。
   ブローカー側逆指値の発注はコードのどこにも存在しない（moomoo クライアントは `OrderType_Normal`＝指値のみ）。
   二重決済（システム決済とブローカー逆指値の併存）を解消し、「建玉あり ⇒ 有効な逆指値あり」を成立させる。
2. **注文状態「拒否」**（FR-05・planning#60）。`OrderStatus.Rejected` と事前拒否（`OrderRejected` イベント）は
   既に別型で存在するが、**moomoo アダプタが OpenD 不達（発注が届いていない）まで `Rejected` へ丸めており**、
   「拒否 = 証券会社が受理しなかった状態」の集計を汚染している。OpenD 切断（SPOF・ADR-0002/0024）は
   「拒否」ではなく**見送り**（キューイングせず破棄＋通知）として分離する（issue #331 スコープ 3）。

## ギャップ分析（現状 → 要求 → 差分）

**11 サービスは実装済みであり、本作業はゼロからの再実装ではなく差分実装である。**

| # | 要求（issue #331 / 計画） | 現状（コードを実読した結果） | 差分（本 PR で実装） |
| --- | --- | --- | --- |
| 1 | 損切りは建玉と同時にブローカーへ発注する逆指値で行う | 逆指値の発注は**存在しない**。`OrderIntent.StopLossPrice` は台帳保存とソフト監視（`StopLossEvaluator`）にのみ使われる | エントリー（Open）発注と同一トランザクション内で保護逆指値（反対売買・Close・トリガー=StopLossPrice）をブローカーへ発注する（`IProtectiveOrderBroker` 能力ポート。moomoo=`OrderType_Stop`+`AuxPrice`、paper=滞留 Accepted）。逆指値レグは `ExecutionRecord` として保存し既存の約定追跡ポーリング（IADR-0113）で追跡、`ProtectiveStopPlaced` でリスク管理台帳へ承認行を結線する |
| 2 | 到達検知は記録・通知のみ。**決済注文を発行しない** | `StopLossTriggeredHandler`（リスク管理）が **Close の `OrderApproved` を発行している**（旧計画・IADR-0015） | ハンドラから発行を除去し記録（ログ）のみへ。`StopLossExecutionService` は削除（一本化の実装そのものであり #346 の旧実装保全とは別件。監査台帳・約定履歴は不変）。否定形テストで固定 |
| 3 | 逆指値が未受理ならエントリー取消／約定済みなら即時手仕舞い。失効なら再発注、不可なら手仕舞い。**逆指値なしの建玉を持たない** | 該当機構なし | エントリー時: 逆指値未受理→未約定なら取消・約定済みなら成行手仕舞い（`ProtectiveStopCoverageLost`）。滞留中: `ProtectiveStopGuard`（常駐）が逆指値の失効を検知し再発注、不可なら成行手仕舞い。建玉消滅時は残存逆指値を取り消す（反対建玉の防止） |
| 4 | 注文状態「拒否」＝証券会社が受理しなかった状態。事前拒否と別状態・別集計 | `OrderStatus.Rejected`（ブローカー拒否・終端）と `OrderRejected`＋`RejectionReason`（事前拒否）は**既に別型・別監査エントリ**。ただし **OpenD 不達・SDK 例外も `Rejected` へ丸めている**（`MoomooBrokerAdapter.PlaceCoreAsync` の catch） | 発注前に確実に未達と判明する失敗（接続確立の失敗）を `BrokerUnavailableException` として分離し、`Rejected` へ丸めない。分離の構造テスト（別状態・統制違反観測へ不混入）を追加 |
| 5 | OpenD 切断時は**キューイングせず見送り＋通知** | 例外→`Rejected` 終端（「拒否」に計上・通知は約定 Warning として流れる）。Wolverine の共通再試行がキューで再送する | 確実に未発注の切断は予約（IADR-0057）を解放し `OrderDispatchForgone` を発行して**正常終了**（ハンドラが例外を投げない＝再試行・キュー滞留なし）。通知（Warning・「発注は再試行されない」明記）・監査記録を追加 |
| 6 | 建玉のブローカー実ポジション突合・owner 決済経路（旧 #292/#304 の吸収） | **実装済み**。突合=`BrokerPositionSnapshotService`＋`PositionReconciliationDrift`（IADR-0118）、owner 決済=`PositionCloseService`＋`PositionCloseRequested`（IADR-0117）。テスト・通知・監査も存在 | **差分なし**（本表で確認結果を記録）。逆指値ガードが建玉消滅時に残存逆指値を取り消す点だけが新規の接続（差分 3） |

## 対象範囲

| 含む | 含まない |
| --- | --- |
| Shared.Contracts: `IProtectiveOrderBroker`（能力ポート）・`BrokerUnavailableException`・新イベント 3 種（`ProtectiveStopPlaced` / `ProtectiveStopCoverageLost` / `OrderDispatchForgone`） | moomoo 実環境（OpenD 実機）での逆指値受理・時間帯・SIMULATE 対応可否の実測（**#342 PoC 待ち**。後述） |
| OrderExecutionService: エントリー＋逆指値の同時発注・未受理時の建玉解消・`ProtectiveStopGuard`（失効検知・再発注・残存取消）・逆指値レグの永続化（EF 新テーブル）・OpenD 切断の見送り | 実弾解禁（`LiveTradingGate` は不変・SIMULATE 限定のまま） |
| RiskManagementService: `StopLossTriggeredHandler` の決済発行除去・逆指値レグの台帳承認結線・分離の構造テスト | 取引判断側の StopLossPrice 算出（既存・IADR-0035） |
| Notification / Audit: 新イベント 3 種の通知・監査記録 | Discord 実送信の配線（既存ポートのまま） |
| docs/functional/FR-10・docs/tests/FR-10（必須範囲の生きた文書の追随）・docs/data 2 箇所の記述是正 | 旧実装のデータ（監査台帳・約定履歴・承認行）の削除・変換（7 年保持・#346） |
| 市場監視のソフト検知（`StopLossTriggered`）は**存置**（検知・記録・通知のみの経路として計画どおり） | 報告書サービスの `TradeTrigger.StopLoss` 分類（台帳経由で従来どおり動く。表示文言の見直しは対象外） |

## 設計（要点。詳細は IADR-0210 / IADR-0211）

### 1. 逆指値の同時発注（エントリー経路）

`OrderExecutionService.ExecuteAsync` の 3 相（予約→発注→確定・IADR-0057）を保ったまま、Open 注文に保護レグを追加する。

- **事前検証（予約の前）**: Open かつ `StopLossPrice is null` → 見送り（`OrderDispatchForgone`・理由 `StopLossPriceMissing`）。
  Open かつブローカーが `IProtectiveOrderBroker` を実装しない → 見送り（理由 `StopOrderUnsupported`）。
  いずれも**建玉を作らない側（fail-closed）**であり、FR-10「逆指値なしの建玉を持たない」の入口。
- **エントリー発注後（終端失敗でない場合）**: 反対売買・Close・同数量・トリガー=StopLossPrice の逆指値を発注。
  - 受理 → 逆指値レグを `ExecutionRecord`（StopDecisionId は EntryDecisionId から決定的に導出）と
    `ProtectiveStopOrder`（新テーブル）へ保存し、`ProtectiveStopPlaced` を発行。
    リスク管理が同イベントで台帳へ承認行を追加 → 逆指値が約定（＝ブローカー側で損切り成立）すると
    既存ポーリングの `OrderExecuted` が台帳の建玉を減らす（新しい経路を増やさない）。
  - 未受理・例外 → **建玉解消**: エントリー未約定なら取消、約定済みなら成行手仕舞い（`IProtectiveOrderBroker.PlaceMarketOrderAsync`）。
    `ProtectiveStopCoverageLost`（Critical 通知・監査記録・Close レグは台帳へ結線）。解消も失敗したら Remediation=None で Critical。
- 逆指値・手仕舞いレグは**リスク管理のスクリーニングを通さない**（Close は統制で止めない・FR-10 共通不変条件。
  旧 `StopLossExecutionService` が `OrderApproved` を直接発行していたのと同じ規律で、発行主体が発注執行へ移る）。

### 2. 逆指値の失効検知・残存取消（`ProtectiveStopGuard`）

Active な `ProtectiveStopOrder` を定期巡回（moomoo 構成のみ配線。ブローカー注文照会＋建玉照会が前提）:

| 逆指値の状態 | ブローカー建玉（entry 方向の残） | 動作 |
| --- | --- | --- |
| 非終端 | あり | 何もしない（正常） |
| 非終端 | なし | **逆指値を取り消す**（決済済み建玉に残る注文が反対建玉を生む事故の防止・04_workflows/02 補足） |
| Filled | — | 完了（ブローカー側で損切り成立。台帳へはポーリング経由で既に届く） |
| Cancelled/Rejected/Expired | あり | **再発注**（新しい試行番号で決定的 DecisionId を導出）。不可なら**成行手仕舞い**（CoverageLost） |
| Cancelled/Rejected/Expired | なし | 完了 |

照会不能（注文 null・建玉 null）は**据え置き**（不明を「無い」と取り違えない。IADR-0118 と同じ規律）。

### 3. OpenD 切断の見送り（キューイングしない）

- `MMApiMoomooTradeClient` の**接続確立**（`EnsureConnectedAsync`）の失敗＝注文がブローカーへ届き得ない失敗を
  `BrokerUnavailableException` に分類。送信後のタイムアウト等（届いたか不明）は従来どおり（予約が守る）。
- `MoomooBrokerAdapter` は同例外を `Rejected` に**丸めず**伝播。
- `OrderExecutionService` は同例外を捕捉し、予約を解放（確実に未発注のため安全）して
  `OrderDispatchForgone`（理由 `BrokerUnavailable`）を返す。ハンドラは**例外を投げず正常終了**する
  —— Wolverine の再試行・error キュー滞留（＝キューイング）を発生させない。再発注は次の取引判断からのみ。

### 4. 「拒否」と事前拒否・見送りの別集計

| 事象 | 型 | 監査台帳の EventType | 統制違反観測 |
| --- | --- | --- | --- |
| 事前拒否（リスク管理） | `OrderRejected` + `RejectionReason` | `OrderRejected` | 記録される（クラス A/B/C 分類） |
| 拒否（証券会社） | `OrderExecuted`（`Status=Rejected`） | `OrderExecuted` | **到達しない**（構造テストで固定） |
| 見送り（OpenD 切断ほか） | `OrderDispatchForgone` | `OrderDispatchForgone` | **到達しない**（同上） |

## 受け入れ基準（issue #331 の 4 項目 → テスト写像）

- [ ] **「建玉あり ⇒ 有効な逆指値あり」の不変条件**（同時発注・未受理時の建玉解消/不成立の全分岐）
  - `OrderExecutionServiceProtectiveStopTests`（Application.Tests）: 同時発注・未受理×未約定=取消・未受理×約定済=成行手仕舞い・
    解消失敗=None 通知・StopLossPrice なし/能力なし=見送り（建玉を作らない）・**プロパティベース**（擬似乱数で
    ブローカー挙動を振り、「約定済み建玉が残る ⇒ Active な逆指値レグがある」を全ケース検証）
  - `ProtectiveStopGuardTests`: 失効→再発注・再発注不可→手仕舞い・建玉消滅→残存取消・照会不能→据え置き（境界値表）
- [ ] **システムが決済注文を発行しない（否定形）**
  - `StopLossTriggeredHandlerTests`（RiskManagement.Infrastructure.Tests）: `StopLossTriggered` を処理しても
    `OrderApproved`（および一切のメッセージ）を発行しない
- [ ] **「拒否」と「事前拒否」の分離（別状態・別集計）**
  - `MoomooBrokerAdapterTests`: ブローカー拒否→`Rejected` ／ OpenD 不達→`BrokerUnavailableException`（`Rejected` にしない・否定形）
  - `RejectionSeparationTests`（RiskManagement.Infrastructure.Tests）: `OrderExecuted` / `OrderDispatchForgone` を扱う
    ハンドラが統制違反観測ストアへ依存しない（構造・否定形）
  - `AuditEntryFactoryTests`: 3 者が異なる EventType で記録される
- [ ] **OpenD 切断時にキューイングせず見送り＋通知**
  - `OrderExecutionServiceTests`: `BrokerUnavailableException` → 予約解放・`ExecutionRecord` を残さない・Forgone を返す
  - `OrderApprovedHandlerTests`（Infrastructure.Tests）: 見送り時に例外を投げず（＝再試行させず）`OrderDispatchForgone` のみ発行
  - `NotificationFormatterTests` / `AuditEntryFactoryTests`: 見送りの通知（Warning）・監査記録

統制系（FR-10）の 3 点セット: 境界値テーブル＝ガードの状態×建玉の分岐表／プロパティベース＝不変条件の擬似乱数検証／
否定形＝決済注文の不発行・Rejected への不混入・統制違反観測への不到達。

## テスト方針

- 単体（xUnit v3 + AwesomeAssertions）。ブローカーはフェイク（`IProtectiveOrderBroker` 実装可否・受理/拒否/例外を注入）。
- 逆指値レグの永続化は InMemory ストア＋ EF ストア（Sqlite/InMemory の既存流儀に従う）。
- 新イベント 3 種: `EventMessageTypeNameTests` へ識別子固定・後方互換基準（`UPDATE_EVENT_BASELINE=1`）再生成・
  監査カバレッジ（`AuditConsumerCoverageTests`）を green にするハンドラ追加。
- 既存テストの改廃: `StopLossExecutionServiceTests` は削除（対象クラス削除）。`StopLossTriggeredConsumerTests` は
  否定形テストへ置き換え。`MoomooBrokerAdapterTests` ほかは新分岐を追加。

### ［2026-08-28 追記 / #331］カバレッジ床割れとテスト追補

**事実**: 初回 push の CI（`build-and-test`）が**カバレッジ床割れで失敗した**。実測
`78.78%`（14326/18185 行・レポート 51 件）に対し `coverage-floor.json` の `lineRateFloor` は `0.79`。
本 PR は 69 ファイル・+3591/−356 で、**新規コードのうち常駐・配線・契約の層にテストが当たっていなかった**
（床は回帰防止のラチェットであり、下げない）。

**分母が増えた内訳（実測）**: 手書きマイグレーション `20260827232000_AddProtectiveStopOrders.cs` の
**32 行**が丸ごと分母へ入る（`exclude` は `*.Designer.cs` / `*ModelSnapshot.cs` だけで、手書きの
`Up`/`Down` は IADR-0143 決定2 により除外しない）。この 32 行は本追補でも被覆していない
（EF マイグレーションの実行検査は既存のどのマイグレーションでも行っていない。同じ扱いを踏襲する）。

**被覆が無かった新規コード（cobertura の実データで特定。推測ではない）**:

| 箇所 | 未被覆 | なぜ意味があるか |
| --- | --- | --- |
| `ProtectiveStopGuardService`（常駐） | 42/42 行（0%） | 巡回結果の**発行**・無効化・失敗時の再試行が全て無検証だった |
| `ProtectiveStopGuard` の fail-safe 分岐 | 10 行 | 対象ゼロ・1 件失敗・再発注の送信例外 |
| 監査ハンドラ 3 種 | 12 行 | 相関（DecisionId）で注文チェーンへ載ることが無検証 |
| 通知ハンドラ 3 種 | 6 行 | 購読→送信の配線が無検証 |
| `OrderApprovedHandler` の保護喪失発行 | 5 行 | `ProtectiveStopCoverageLost` の発行経路 |
| `NotificationFormatter` の対処・理由の分岐 | 4 行 | 3 つの見送り理由・`EntryCancelled` の文面 |
| 新イベント 3 種のレコード（契約） | 23 行 | wire / 監査 payload の往復（null が意味を持つ） |
| `BrokerUnavailableException`（原因つき） | 4 行 | 原因例外の保持 |
| `MoomooBrokerAdapter.GetOrderAsync` の fail-safe | 3 行 | 照会不能を `null` に倒す（状態を捏造しない） |

**追加したテスト（すべて安全・統制に直結する経路。カバレッジ稼ぎの空テストは置かない）**:

- `ProtectiveStopGuardServiceTests`（新規・8 件）: 再発注／保護喪失の**イベント発行**、維持だけの巡回では
  発行しない（否定形）、無効化時は一度も巡回せず**警告を残す**、間隔ごとの巡回、巡回失敗後の再試行、
  停止要求のキャンセルをエラーとして記録しない、設定の既定値。
  常駐の `ExecuteAsync` は `StartAsync` とは別タスクで走るため、**固定待ちではなくシグナル**で待つ
  （時間依存のちらつきを作らない。この事実は `BrokerPositionSnapshotService` の既存テストでは
  観測されておらず、同サービスの `ExecuteAsync` は現在も未被覆である）。
- `ProtectiveStopGuardTests`（+3 件）: 巡回対象ゼロなら建玉照会もしない（否定形）、1 件の評価が例外でも
  残りを処理し失敗として数える、再発注の**送信例外**でも手仕舞いへ倒れる。
- `AuditEventConsumersTests`（+3 件）: 見送りが**拒否とは別 EventType**で注文チェーンに載る、
  保護逆指値の発注がエントリーと同じ相関で載る、保護喪失が相関・銘柄つきで残る。
- `NotificationConsumersTests`（+3 件）: 見送り通知が「再試行されない」ことを伝える、保護逆指値の発注は
  Info、保護喪失は解消内容つきの Critical。
- `NotificationFormatterTests`（+2 件）: `EntryCancelled` は「建玉は生じていません」と読める、
  見送りの 3 理由が日本語で読み分けられる（`Theory`）。
- `OrderApprovedConsumerTests`（+1 件）: 逆指値未受理 → 建玉解消 → `ProtectiveStopCoverageLost` の発行と
  手仕舞いレグの記録。
- `MoomooBrokerAdapterTests`（+2 件）: 状態照会の例外は `null` に倒す（否定形）、接続不可の原因例外は保たれる。
- `ProtectiveStopEventPayloadTests`（新規・6 件）: 新イベント 3 種の **JSON 往復**（監査 payload が唯一の
  一次証跡であり、`CloseDecisionId` / `CloseIntent` の **null が意味を持つ**）と、接続不可分類の原因例外保持。
  `EventBackwardCompatibilityTests` はプロパティの**型名**しか見ない（同テストの「既知の限界」）ため、
  その外側を押さえる。

**結果（実測。CI と同じ全量 1 走（`Category!=Integration`・Release・レポート 51 件）で測った値）**:
`78.78%`（14326/18185）→ `79.43%`（14445/18185）。床 `79.00%` に対し **0.43 ポイントの余裕**を持たせた
（床ぎりぎりだと測定誤差で再び落ちるため。実際、CI の初回実測 14327 行に対しローカルは 14326 行で
**1 行ぶれている**）。**床は変更していない。**

## 実環境依存で本 PR では検証できない範囲（#342 PoC 待ち）

| 事項 | 本 PR での扱い |
| --- | --- |
| moomoo SIMULATE が `OrderType_Stop` を受理するか（計画 03_moomoo-integration は「模擬取引は指値・成行のみ」と記載） | 受理されない場合、本実装は**設計どおり建玉を作らない**（未受理→取消/手仕舞い）。Stage 1 で建玉が一切作れない事態は #342 の PoC 項目で確認し、必要なら計画へ環流する |
| 逆指値を受け付けない銘柄・時間帯の実範囲 | 未受理分岐が一律に受ける（銘柄・時間帯の事前判定は供給元がなく実装しない） |
| `AuxPrice`（トリガー価格）の protobuf 実挙動・約定時刻 | 写像は SDK 非依存部を単体テスト、protobuf 依存部は live 検証（既存 mapping テストの方針と同じ） |
| OpenD 切断→再接続の実挙動（`OnDisconnect` 後の InitConnect） | 接続確立失敗の例外分類のみ実装。実機の切断パターンは PoC で確認 |

## 母集合の引き直し（traceability.repo.md 規則 9・10）

**誤りの側（旧機構の記述）から引いた。** 走査軸と結果・除外は次のとおり（生の出力に対して判断。加工しない）。

- 軸 1: `機械執行|決済注文を機械的に|LLM を迂回して決済`（全ファイル・パス除外のみ）→ 20 件。
  対応: `StopLossTriggeredHandler` / `StopLossExecutionService` / RiskManagement `Program.cs` / `StopLossTriggered`（イベントコメント）／
  docs/functional/FR-10（決済経路表）／docs/data/audit-events.md・risk-management-aggregates.md（相関の記述）を本 PR で是正。
- 軸 2: `StopLossExecutionService`（クラス名）→ 22 ファイル。上記に加えテスト 2 本（削除・置換）、
  `PositionCloseService` / `MaintenanceMarginReductionService` / `PortfolioProjection` / `OrderApprovedLedgerHandler` のコメント（比較参照）を追随。
- 軸 3: `BuildCloseApproval` → 3 ファイル（軸 2 に包含）。
- 軸 4: `StopLoss`（ReportService 配下）→ `TradeTrigger.StopLoss` と表示文言。
- **除外とその理由**:
  - `.ai-context/specs/`・`.ai-context/adr/`（IADR-0015 ほか）: **凍結記録**。本文は書き換えず、IADR-0015 には
    `Superseded by IADR-0210` の追記のみ行う（.ai-context/README.md の凍結原則）。
  - `CHANGELOG.md`: 生成物。履歴行（旧機構の実装記録）は当時の事実であり是正対象でない。
  - `docs/tests/FR-10_risk-guard-core-tests.md` T-10-16: 再実装前の写像表。対象テストの削除に伴い日付付き注記を追記（写像表自体は史実として保持）。
  - ReportService の表示文言「損切りライン到達による機械執行」: 集計は台帳（約定）由来で本変更の影響を受けず、
    報告書テンプレート（planning 04_report-templates）の文言確定は #338 の射程。**本 PR では変更しない**。
  - `OrderIntent` コメント「機械執行の Close」: 維持率自動縮小（UC-06）の機械執行 Close は本 PR 後も存在し記述は正のまま。
- 規則 10（是正後の語で引き直す）: 実装完了後に `ProtectiveStop|OrderDispatchForgone|BrokerUnavailable` で再走査し、
  新設した語の参照切れ（イベント未登録・ハンドラ漏れ）が無いことを確認する（検証手順に含める）。

## 計画書との差異

- **差異 1（実装位置）**: 04_workflows/02 のシーケンス図は「逆指値の未受理・失効の検知」を市場監視、
  「再発注指示」をリスク管理に置くが、本実装は両方を**発注執行サービス**（`ProtectiveStopGuard`）に置く。
  ブローカー注文の状態・建玉の照会経路（OpenD 接続）を持つのが発注執行だけであり、検知と対処を同一サービスに
  置くほうが分岐（不明の据え置き・再発注の冪等化）を単純に保てるため。**業務規則（検知する・再発注する・
  不可なら手仕舞う・逆指値なしの建玉を持たない）は計画どおり**であり、担いのサービス配置のみの差異。
  根拠は IADR-0210 決定 4 に記録し、計画側シーケンス図への環流候補として報告する。
- 差異 2: なし（FR-05 / FR-10 の規則そのものはすべて計画どおり）。

## 未決事項

- moomoo SIMULATE の逆指値受理可否（上表・#342）。受理されない場合の Stage 1 検証手順は PoC 結果を待って裁定。
- 逆指値のトリガー到達後の約定価格（成行ストップのスリッページ）の記録粒度は既存の `SlippageCalculator`
  （参照価格＝トリガー価格）で近似する。実測後に見直す。
