---
title: 定時サイクルのハンドラの実行時間の上限を導いて明示し、打ち切り・再配送で判断を二重に出さない（定時判断の DecisionId を決定的にする）（#1169）
type: spec
status: accepted
related_ids: [FR-02, FR-10, NFR-02, NFR-13, UC-01, ADR-0003, ADR-0013, IADR-0490, IADR-0023, IADR-0129, IADR-0057, IADR-0407, IADR-0463, IADR-0483, IADR-0379]
author: claude (Claude Code)
created: 2026-10-06
updated: 2026-10-06
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-02, NFR-02, NFR-13)
  - planning:projects/ai-stock-trading/04_workflows/01_scheduled-trading-cycle.md
---

# 仕様書: 定時サイクルの実行時間の上限と、再配送で判断を二重に出さない DecisionId（#1169）

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-02**（定時トリガーで「情報収集 → 生成 AI の判断 → 発注」の取引サイクルを実行できる。UC-01）
- 非機能: NFR-02（定時取引サイクル 1 回の所要 10 分以内）・NFR-13（LLM 費用の月次統制。二重呼び出しは費用に効く）
- 統制の前提: FR-10（重複発注をしない。下流の DecisionId の冪等）
- 関連 ADR: ADR-0003（リスク管理の権威と直列の配置）・ADR-0013（Wolverine）
- 関連 IADR: IADR-0023・IADR-0129・IADR-0057・IADR-0407・IADR-0463・IADR-0483・**IADR-0490（本件の決定）**
- 起票: [#1169](https://github.com/endazon/ai-stock-trading/issues/1169)
- ブランチ: `fix/FR-02-1169-decision-cycle-timeout`（定時サイクルは FR-02。計画の要求表で確認）
- 基点コミット: `origin/develop` `58fe8c24`

## 目的・背景

稼働 PoC（2026-10-05）で、保有上限の引き上げにより監視 6 銘柄がすべて LLM へ回り、定時サイクル（`InformationCollectedHandler`。1 通で全銘柄を順に判断）が
**60,015 ms で打ち切られて再配送**された。1 回目の判断は捨てられ、再試行が LLM を二重に呼び、判断の時刻が 1 分ずれた。
ハンドラの注記は「再配送すると DecisionId が新規採番のため下流の冪等をすり抜け重複発注し得る」としていたが、サイクル全体の打ち切りは銘柄ごとの try/catch では
捕まらない。本件は ①打ち切りの崖を消し、②打ち切り・再配送で判断を二重に出さないことを仕組みと試験で保証する。

## 調査（変更前のコードと WolverineFx 6.24.5 の実物。行番号は基点コミット／タグ `V6.24.5`）

### 1. 実行時間の上限はどこで決まるか

| 事実 | 出典 |
| --- | --- |
| 版は WolverineFx 6.24.5 | `Directory.Packages.props:31-33` |
| 既定の上限は **60 秒** | Wolverine `src/Wolverine/WolverineOptions.cs:505` `DefaultExecutionTimeout { get; set; } = 60.Seconds();` |
| チェーンごとの上書き `ExecutionTimeoutInSeconds`（int 秒）。無ければ既定 | `Runtime/Handlers/HandlerChain.cs:219`・`:783-791`（`DetermineMessageTimeout`） |
| 属性 `[MessageTimeout(秒)]` はチェーンの同じ値を書くだけ | `Attributes/MessageTimeoutAttribute.cs:8-17` |
| 受信の実行器は組み立て時にチェーンの上限を読む | `Runtime/Handlers/Executor.cs:425`（`Build`） |
| 実行時に `new CancellationTokenSource(_timeout)` を作り、ハンドラの token に連結する | `Executor.cs:237-238` |
| 例外（取り消しを含む）で `ClearAllAsync()`（溜めた発行を捨てる）→ 失敗の規則（再試行） | `Executor.cs:258-266` |
| ポリシー（`IHandlerPolicy`）は `HandlerGraph.Compile` で読み込み方式に依らず適用される | `Runtime/Handlers/HandlerGraph.cs:397`・`:561-575` |
| 本サービスは上限を明示していない（`UseWolverine` は共通配線 1 行だけ） | AST `backend/Services/TradeDecisionService/Program.cs:569-572`（変更前） |
| 共通の再試行は 2s/10s/30s の 3 回 → `<queue>_error` | AST `backend/TestSupport/AiStockTrading.TestSupport.PlatformShim/Foundation/Extensions/WolverineExtensions.cs:38-39`・`:216-218` |
| RabbitMQ のキューの既定は Inline（受信を 1 通ずつ処理し、成功後に ack） | Wolverine `src/Transports/RabbitMQ/Wolverine.RabbitMQ/Internal/RabbitMqQueue.cs:43` |

観測（14:44:23 開始 → 60,015 ms で `TaskCanceledException`、14:45:26 に再試行）は「既定 60 秒 ＋ 再試行 2 秒」とそのまま一致する。

### 2. `TradeDecisionMade` の発行は outbox か

| 事実 | 出典 |
| --- | --- |
| ハンドラは引数の `IMessageBus`（＝受信の `MessageContext`）で `PublishAsync` する | AST `InformationCollectedHandler.cs:86`（変更前） |
| 受信の文脈は `Transaction = this` | Wolverine `Runtime/MessageContext.cs:912`（`ReadEnvelope`） |
| `Transaction` があれば発行は `_outstanding` に溜めるだけで送らない | `Runtime/MessageBus.cs:355-365`・`MessageContext.cs:565-572` |
| 成功後に「送信 → 受信の完了（ack）」の順 | `Runtime/MessageSucceededContinuation.cs:22-24` |
| 失敗した試行は `ClearAllAsync` で溜めた発行を捨てる | `MessageContext.cs:647-668`・`Executor.cs:265` |
| 永続の outbox（EF/Postgres）は無い。本サービスは DB を持たない | AST `Program.cs:31-33`（「判断はステートレス（DB なし）」） |
| 最終の失敗などの報告口はランタイムの `MessageBus`（受信の文脈の外）から即時に出す。費用の報告口は DI スコープの `IMessageBus` を使う（受信の文脈と同じ実体かは本件で確かめていない） | AST `Program.cs:415`・`:424`・`:118-122` |

結論: **打ち切られた試行の判断は送られない（良い側）**。今回重複しなかったのは仕組みどおり。ただし**永続ではない**ので、
**送信した後・ack の前にプロセスが落ちる**と RabbitMQ が同じ `InformationCollected` を再配送し、サイクル全体を判断し直す。

### 3. DecisionId は再配送をまたいで決定的か・下流の重複排除

| 事実 | 出典 |
| --- | --- |
| 判断の DecisionId は毎回 `Guid.NewGuid()`（決済・新規建ての 2 か所） | AST `Features/TradeDecision/DecideTrade/TradeDecisionAppService.cs:618-619`・`:728-729` |
| 起点イベントは巡回ごとに一意の `EventId` を持つ | AST `Shared/AiStockTrading.Shared.Contracts/Events/InformationCollected.cs:14-19`・`Services/InformationCollectionService/Hosted/CollectionPollingService.cs:150-152` |
| リスク管理: 承認済みの新規建ての**同じ DecisionId** の再配送は再審査も発行もしない | AST `Services/RiskManagementService/Features/RiskManagement/OrderScreeningService.cs:48-58`・`Infrastructure/Steps/TradeDecisionMadeHandler.cs:35-40`（IADR-0407） |
| 台帳の承認は DecisionId で冪等 | AST `Services/RiskManagementService/Infrastructure/Persistence/EfPortfolioLedgerStore.cs:21` |
| 発注執行: 同じ DecisionId の発注結果があれば再発注しない（3 相の予約） | AST `Services/OrderExecutionService/Features/OrderExecution/DispatchApprovedOrder/OrderExecutionAppService.cs:90-110`（IADR-0057） |
| 監査台帳は受信の封筒 ID で冪等（DecisionId ではない） | AST `Services/AuditService/Infrastructure/Steps/AuditEventHandlers.cs:14` |

結論: 下流の冪等はすべて **DecisionId** で引くが、判断側が毎回新規に採番するため、**ack 前の落ちの再配送では効かない**。
DecisionId を (巡回, 市場, 銘柄) で決定的にすれば、下流の既存の冪等がそのまま止める。

### 4. 1 銘柄の上限と監視銘柄数

| 事実 | 出典 |
| --- | --- |
| LLM の timeout は `LlmGateway:TimeoutSeconds`（未設定・不正・非正値は 30 秒）。REST の HttpClient・gRPC の deadline の両方 | AST `Program.cs:79`・`:143`・`:170-173` |
| 稼働の構成は空＝30 秒 | AST `deploy/helm/ai-stock-trading/values-local.yaml:361`・`values.yaml:774` |
| 1 判断の LLM 呼び出し＝一次スクリーニング（有効なら 1）＋ 二次（`VoteCount` 回を**順に**） | AST `Features/TradeDecision/DecideTrade/DecisionOrchestrator.cs:36`・`:70-76` |
| 構成の既定はスクリーニング有効・1 票（稼働も `Decision__EnableScreening=true`） | AST `Infrastructure/ExternalServices/DecisionOptionsLoader.cs:8-24`・`values-local.yaml:369` |
| LLM 以外の照会の上限: reports 5 秒・risk 5 秒・order-execution 8 秒・monitor 5 秒・marketdata 5 秒 | AST `Program.cs:196`・`:219`・`:287`・`:363`・`:460` |
| **判断サービスに監視銘柄数の上限は無い**（権威は市場監視。追加は Finnhub の巡回予算で拒否されるだけ） | AST `Services/MarketMonitorService/Domain/WatchlistCycleFit.cs:19-43` |
| 稼働の監視銘柄は 6 | #1169 の観測 |

最悪の 1 銘柄 ＝ 30 秒 × 2 ＋ 照会（和で 28 秒程度。為替・知識ベースは明示の上限なし）。

## 対象範囲

- 変更: `InformationCollectedHandler`（銘柄ごとの締め切り・決定的な DecisionId・前提超過の警告）、`Program.cs`（予算の登録・ポリシーの登録・起動時の 1 行）
- 新規: `Features/TradeDecision/ScheduledCycleBudget.cs`・`Features/TradeDecision/ScheduledDecisionIds.cs`・`Infrastructure/Steps/ScheduledCycleTimeoutPolicy.cs`
- 注記の追随: `Shared/AiStockTrading.Shared.Contracts/Events/TradeDecisionMade.cs`（DecisionId の採番の注記）
- 文書: `docs/operations/operations.md`（上限の構成・ログ・障害対応）・`docs/api/events-and-ports.md`（DecisionId の定時の導出）・`docs/tests/FR-10_risk-controls-tests.md`（T-10-2240〜2249）
- 範囲外: 価格変動の判断のハンドラの上限（1 通 1 銘柄。残余リスクへ）・helm の値（既定で足りる。配備メモへ）

## 母集合（規則 9・10。`origin/develop` `58fe8c24`）

誤りの側の文字列で全文書を走査した（`git grep -n -E "新規採番|都度新規|DecisionId は.*採番|DefaultExecutionTimeout|既定 60 秒|60 秒で打ち切" -- ':!.ai-context/specs' ':!CHANGELOG.md'`）。

| ヒット | 扱い |
| --- | --- |
| `InformationCollectedHandler.cs`（旧 62-64 行「DecisionId は都度新規採番のため…重複発注し得る」） | **追随**（日付つき追記で解消を書く。原文の趣旨は残す） |
| `Shared/.../Events/TradeDecisionMade.cs:8`「DecisionId は判断サービスが新規採番する」 | **追随**（定時は決定的に導く、と追記。起点と繋がらない趣旨は変わらない） |
| `.ai-context/adr/IADR-0307_*.md:41`「`Guid.NewGuid()` で新規採番」 | 除外（凍結記録。当時の事実） |
| `AuditService/.../AuditEventHandlers.cs:14`（MessageId の新規採番） | 除外（別の ID） |
| 「既定 60 秒」の他のヒット（市場監視の巡回・報告書の改訂の timeout ほか 20 件） | 除外（別の 60 秒。Wolverine の上限ではない） |
| `docs/api/events-and-ports.md:45`（`TradeDecisionMade` の行。採番の記述なし） | **追記**（定時の DecisionId の導出を 1 文） |

規則 10（この変更で新たに誤りになる自分の記述）: ハンドラの新しい注記は「Wolverine は失敗した試行の発行を捨てる」と書く——これは**ハンドラ引数の `IMessageBus` での発行**についてだけ真である。
ランタイムの `MessageBus` から出す報告口（最終の失敗ほか）は捨てられない。IADR-0490 §結果に書き分けた。

## 設計

IADR-0490 決定 1・2 のとおり。

- `ScheduledCycleBudget.Derive(llmTimeout, llmCalls, maxWatched)`: 1 銘柄 ＝ `llmTimeout × llmCalls + 30s`、サイクル ＝ `1 銘柄 × maxWatched + 60s`（秒は切り上げ）。
  `LlmCallsPerDecision` ＝ `(EnableScreening ? 1 : 0) + VoteCount`。`ParseMaxWatchedSymbols`（`TradeCycle:MaxWatchedSymbols`。不正は 10）。
- DI に singleton で登録（解決時に構成を読む）。ポリシーとハンドラが同じ値を読む。
- ハンドラ: 監視銘柄 > 前提なら警告 1 行。銘柄ごとに `CreateLinkedTokenSource(handler token)` ＋ `CancelAfter(PerSymbol)`。catch の条件はハンドラの token のまま。
  判断が出たら `decision with { DecisionId = ScheduledDecisionIds.For(EventId, Symbol, Market) }`（空の EventId は導かず警告）。
- 選ばなかった形（銘柄ごとのメッセージへの分割）は IADR-0490 §検討した選択肢。

## 受け入れ基準

1. 定時サイクルのハンドラの上限が、本番の組み立てで構成から導いた値になる（既定 60 秒のままではない）。前提の監視銘柄数・LLM の timeout・呼び出し回数を変えると導き直される。他のハンドラの上限は変えない。
2. サイクルが上限で打ち切られて再試行されても、判断は銘柄ごとに 1 件だけ発行され、DecisionId は (巡回, 市場, 銘柄) で決まる値である。
3. 同じ起点イベントが発行の後に再配送されても、判断は同じ DecisionId で出る（下流の DecisionId の冪等が止める形）。次の巡回は別の DecisionId。
4. 1 銘柄が締め切りを超えたら、その銘柄の失敗として分離して最終の失敗を 1 件報告し、他の銘柄は発行し、サイクルは再試行しない。
5. 起点の EventId が空なら決定的な DecisionId を使わない（毎回新規）。
6. 監視銘柄が前提を超えたら警告を出し、判断は止めない。
7. 既存の定時・価格変動・最終の失敗の試験が変わらず通る。

## 試験（T-10-2240〜T-10-2249。置き場所は `docs/tests/FR-10_risk-controls-tests.md` の同名の節）

| T-ID | 受け入れ基準 | 試験 |
| --- | --- | --- |
| T-10-2240 | 1 | `ScheduledCycleBudgetTests.T_10_2240_*`（導出 6 ケース・切り上げ） |
| T-10-2241 | 1 | `ScheduledCycleBudgetTests.T_10_2241_*`（呼び出し回数 4 ケース・前提の読み取り 7 ケース） |
| T-10-2242 | 1 | `ScheduledCycleBudgetTests.T_10_2242_*`（非正の入力） |
| T-10-2243 | 1 | `ScheduledCycleTimeoutCompositionTests.T_10_2243_*`（本番の組み立て 4 ケース・他のハンドラ） |
| T-10-2244 | 2・3・5 | `ScheduledCycleBudgetTests.T_10_2244_*`（決定的・区別・空・版 8 の既知の値） |
| T-10-2245 | 3 | `ScheduledCycleRedeliveryTests.T_10_2245_*` |
| T-10-2246 | 2 | `ScheduledCycleRedeliveryTests.T_10_2246_*`（実行器を通す。上限 2 秒で打ち切り → 再試行） |
| T-10-2247 | 4 | `ScheduledCycleRedeliveryTests.T_10_2247_*` |
| T-10-2248 | 5 | `ScheduledCycleRedeliveryTests.T_10_2248_*` |
| T-10-2249 | 6 | `ScheduledCycleRedeliveryTests.T_10_2249_*` |
| （既存） | 7 | `InformationCollectedConsumerTests`・`PriceMovementDetectedConsumerTests`・`TradeDecisionFailureRecordTests`（必須依存の登録だけ追加） |

試験の書き方（IADR-0379）: 遅い上流は「応答しない上流（`Task.Delay(Timeout.InfiniteTimeSpan, ct)`）＋ 打ち切りの観測」で書き、壁時計どうしの競争にしない。
`InvokeMessageAndWaitAsync` は実行時間の上限を掛けない経路なので、T-10-2245〜2249 はローカルキューへ流して受信の実行器を通す。

下流の冪等そのもの（同じ DecisionId の再配送で承認・発注が 1 回）は既存の試験が固定している: リスク管理 `ApprovedDecisionReplayRegressionTests`（T-10-870〜873）・
発注執行 `OrderApprovedConsumerTests.同一OrderApprovedが再配送されても二重発注しない`。本件の試験はその前提（再配送で同じ DecisionId）を固定する。

## 変異（自前。13 件。`TradeDecisionService.Tests` の `ScheduledCycle|InformationCollectedConsumer` で実行）

| ID | 変異 | 結果（殺した試験） |
| --- | --- | --- |
| M1 | サイクルの上限から「× 監視銘柄数の前提」を落とす | 殺した（T-10-2240・T-10-2243） |
| M2 | 秒の切り上げを切り捨てにする | 殺した（T-10-2240（端数）） |
| M3 | LLM 呼び出し回数から一次スクリーニングを落とす | 殺した（T-10-2241・T-10-2243） |
| M4 | 監視銘柄数の前提の 0 を受け入れる | 殺した（T-10-2241） |
| M5 | 本番の組み立てが上限のポリシーを登録しない | 殺した（T-10-2243） |
| M6 | ポリシーが全ハンドラへ上限を掛ける | 殺した（T-10-2243（他のハンドラ）） |
| M7 | ハンドラが決定的な DecisionId を使わない（新規採番のまま） | 殺した（T-10-2245・T-10-2246） |
| M8 | DecisionId の名前から銘柄を落とす | 殺した（T-10-2244（2 本）） |
| M9 | 空の起点イベントからも導く | 殺した（T-10-2244・T-10-2248） |
| M10 | 銘柄の締め切りを掛けない | 殺した（T-10-2247） |
| M11 | 銘柄の catch がサイクルの打ち切りも握り潰す | 殺した（T-10-2246） |
| M12 | 前提ちょうどでも警告する | 殺した（T-10-2249） |
| M13 | DecisionId の名前から起点イベントを落とす | 殺した（T-10-2244（2 本）・T-10-2245） |

13 件すべて殺した（生存 0）。実行は 1 件ごとに書き換え → `dotnet test` → 原状へ戻す。

## 残余リスク

IADR-0490 §結果のとおり。要点:

- 監視銘柄が前提（既定 10）を超えると崖は残る（警告のみ・アラートなし）。
- 最悪の場合のサイクル（既定 960 秒）は NFR-02 の 10 分を超え得る（上限は上界であって目標ではない）。
- 再配送の判断の内容が 1 回目と違っても同じ DecisionId（下流は先着を保つ）。監査台帳は封筒 ID で冪等なので、ack 前の落ちの窓では同じ DecisionId の行が 2 つ残り得る。
- 価格変動の判断のハンドラは既定 60 秒のまま（1 銘柄の最悪 90 秒で打ち切られ得る）。

## 配備メモ（PoC）

- 変わるのは **trade-decision-service のイメージだけ**（契約・helm・DB の変更なし。`TradeDecisionMade.cs` は注記のみ）。
- 既定のままで上限は 960 秒（LLM 30 秒・二段・前提 10 銘柄）。起動ログに
  `定時サイクルの実行時間の上限: 00:16:00（1 銘柄の締め切り 00:01:30 × 監視銘柄数の前提 10 ＋ 余裕 00:01:00）` が 1 行出る。
- 監視銘柄を 10 より増やすときは `TradeCycle__MaxWatchedSymbols` を trade-decision の env に足す。超えている間は各サイクルで
  `監視銘柄 … 件が定時サイクルの上限の前提 … 件を超えています` の警告が出る。
- 確認: `kubectl -n ai-stock-trading logs deploy/trade-decision-service --since=3h | grep -E "Failed to process message.*InformationCollected|定時サイクルの実行時間の上限|上限の前提"`
  に `Failed to process message` が出ないこと。定時判断の `DecisionId` は同じ巡回の再試行で同じ値になる。

## ［2026-10-06 追記 / #1169］別文脈の監査（条件付き GO・🔴 なし）への対応

監査の指摘（head `b1a25eda`）を新しいコミットで直した（amend・force push はしない）。

| 指摘 | 対応 | 試験 |
| --- | --- | --- |
| 🟡1 滞留した古い起点を順に判断する（受信は Inline・`ListenerCount = 1`・`PreFetch = 100`。Wolverine `RabbitMqQueue.cs:43`・`:81-97`、`Endpoint.cs:424` で確認）。上限 960 秒は巡回間隔より長く、待ちが RabbitMQ の `consumer_timeout`（既定 30 分）を超えると処理中のサイクルもやり直しになる | ハンドラの先頭で、収集の完了からの経過が鮮度の上限（`NewsStatusValidFor` を `NewsCollectionStatusStore.Clamp` の範囲〔1 分〜2 時間〕へクランプ。宣言なしは NFR-02 の 10 分）を**超えたら**、警告 1 行を出して何もせず正常に終える（発行なし・ニュースの状態も記録しない・再試行しない）。IADR-0490 決定 3・運用仕様書に記録 | T-10-2250（純関数）・T-10-2251（捨てる／境界ちょうどは判断／宣言なし。受信は 1 回で完了） |
| 🟡2 開場の判定がサイクル開始の時刻 1 回 | 銘柄ごとに `clock.UtcNow` を読み直す（IADR-0490 決定 4） | T-10-2252（判断の最中に引けを越える時計） |
| 🟡3 1 銘柄の締め切りの値が固定されていない（変異 A3 生存） | T-10-2247 の前提を 100 銘柄にし、サイクルの上限（300 秒）を試験の待ちの予算（30 秒）より遥かに長くした（締め切りだけが応答しない LLM を止められる） | T-10-2247 |
| 🟡4 DecisionId の置き換えが買いでしか試されていない（変異 A6 生存） | 売りの新規建て（売り建ての建て増し）と決済の判断でも、再配送で同じ DecisionId になることを足した | T-10-2245（3 形） |
| 🟢 空の EventId の警告が表明されていない（変異 A7 生存） | 判断ごとに警告 1 行を表明 | T-10-2248 |

捨てた件数の計上は警告のログだけにした（業務メトリクスの見送りの理由は契約の語彙であり、判断 1 回の見送りとサイクル 1 回の破棄は単位が違うため足さない）。

追加の変異（9 件・すべて殺した）:

| ID | 変異 | 結果 |
| --- | --- | --- |
| A3 | 銘柄の締め切りをサイクルの上限へ差し替える | 殺した（T-10-2247） |
| A6 | 決定的な DecisionId を買いのときだけ使う | 殺した（T-10-2245（売り・決済）） |
| A7 | 空の起点イベントの警告を出さない | 殺した（T-10-2248） |
| S1 | 古い起点を捨てない | 殺した（T-10-2251） |
| S2 | 鮮度の境界を「以上」にする | 殺した（T-10-2250・T-10-2251） |
| S3 | 宣言が無いときの上限を 30 分にする | 殺した（T-10-2250・T-10-2251） |
| S4 | 宣言をクランプしない | 殺した（T-10-2250） |
| S5 | 古い起点を捨てるとき例外で終える（再試行させる） | 殺した（T-10-2251） |
| C1 | 開場の判定にサイクル開始の時刻を使う | 殺した（T-10-2252） |

A7 は初回の変異の作りが誤っていた（文言の先頭だけを消したため、表明している部分文字列が残り生存と出た）。呼び出しごと止める形に作り直して殺したことを確かめた。
