---
title: 通知の /status が、口座を照会できていない間（MaxDailyOrderAmount=null）に JsonException で失敗しないよう、受け手の稼働状態の射影を送り手と揃えて null 許容にし「不明」と表示する（#990）
type: spec
status: accepted
related_ids: [FR-14, FR-10, UC-07, ADR-0009, ADR-0041, IADR-0408, IADR-0354, IADR-0075, IADR-0162]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-14 Discord Bot の /status / FR-10 リスク統制)
  - planning:projects/ai-stock-trading/07_adr/ADR-0041_out-of-system-trade-adoption-and-capital-baseline.md (基準資金は口座照会へ寄せる)
---

# 仕様書: 通知の /status を資金未供給の間も使えるようにする（#990）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-14（Discord Bot の `/status`）、FR-10（リスク統制の稼働状態）
- ユースケース（UC）: UC-07（稼働状態の確認）
- 画面（SC）: なし（Discord の `/status` 応答）
- 関連 ADR: ADR-0009（3 統制の表示）、ADR-0041 決定2（基準資金は口座照会に由来し、照会できない間は未供給）
- 関連 IADR: IADR-0354（資金・上限の null は未供給）、IADR-0408（送り手の本物の型による契約テスト＝決定3。本件はその追記）、
  IADR-0075（通知の pause/status アダプタ）、IADR-0162（供給が無い値を 0 と表示しない）
- 関連 issue: #990（本件）、#957 C / PR #989（発見元。作業中に develop へマージされたため、本件は f5fae563 へ載せ直した）、#980（契約テストの形の先例。マージ済み）

## 現物で確認した（是正前・`origin/develop` ddf2d835。載せ直し後 f5fae563 でも同じ）

| 事実 | 出典 |
| --- | --- |
| 送り手 `RiskStatusView.MaxDailyOrderAmount` は `decimal?`。`snapshot.Capital` が null（口座を照会できていない）なら null を返す | `RiskManagementService/Features/RiskManagement/GetRiskStatus/RiskStatusView.cs`・`RiskStatusService.cs` |
| 受け手 `HttpPauseController.RiskStatusView.MaxDailyOrderAmount` は `decimal`（非 null） | `NotificationService/Infrastructure/ExternalServices/HttpPauseController.cs` |
| 値型の非 null 項目へ JSON の `null` が来ると `System.Text.Json` は `JsonException` を投げ、`GetStatusAsync` は「稼働状態の照会に失敗しました（JsonException）」を返す | PR #989 の作業中の実測（issue 本文）。本件の T-10-941 を是正前のコードへ当てて再現する（下記「検証」） |
| 通知のテストプロジェクトはリスク管理を extern alias `RiskManagementWorker` で参照済み（#980）。稼働状態の非 null の契約テスト（T-10-934）は PR #989 が `RiskControlOperationReadContractTests` に置いた | `NotificationService.Tests.csproj`・同テストファイル |
| 画面（SC-03）側の受け手は `maxDailyOrderAmount: number \| null` で既に null 許容 | `frontend/src/lib/risk/contracts.ts` |

## 決定

- 受け手の射影 `RiskStatusView.MaxDailyOrderAmount` を `decimal?` にする（送り手と同じ）。
- 表示は null のとき **「上限 不明（口座を照会できていません）」** とし、**0 と表示しない**（「上限 0」と「上限が分からない」は別の事実。IADR-0354・IADR-0162 と同じ規律）。
  非 null のときの表示（`日次発注 {発注額}/{上限} 円`）は変えない。
- 項目の**欠落**（送り手が項目を出さない版）も null として同じく「不明」になる（0 へ倒さない）。
- 新しい決定は無く、IADR-0408 決定3（送り手の本物の型で直列化して受け手に読ませる）の適用と、IADR-0354 の表示規律の通知への適用である。
  **新しい IADR は作らず、IADR-0408 へ日付つき追記**を置く（索引行も追随）。

## 受け入れ基準

1. 送り手の本物の型 `RiskStatusView` を資金・上限 null（口座を照会できていない・新規建て停止中）で web 既定に直列化した応答を読ませると、
   `GetStatusAsync` は `Succeeded=true` を返し、本文に 3 統制・段階・当日損益・ポジションの行と「上限 不明（口座を照会できていません）」を含む。
   上限を 0 と表示しない（T-10-941）。
2. 同じ型で上限が非 null のときは `{発注額:N0}/{上限:N0} 円` を表示し「不明」を含まない（T-10-942）。
3. 手書きの JSON で `maxDailyOrderAmount` を欠落させた応答も成功し、上限を「不明」と表示する（0 としない。T-10-943）。
4. 既存の `HttpPauseControllerTests`（非 null の手書き JSON）は緑のまま。

テスト ID: 割り当て範囲 **T-10-941〜T-10-949** のうち 941〜943 を使う（`git grep` で origin/* 全ブランチに未使用を確認。
949 は PR #989 の作業仕様書の本文に「未使用」として現れるだけ）。T-10-930〜940 は #989 が使用済みのため使わない。
置き場: T-10-941・942 は #989 がマージ済みになったため同じ受け手の契約テストの `RiskControlOperationReadContractTests` へ、
T-10-943（手書きの JSON）は `HttpPauseControllerTests` へ置く（新しいテストファイルは作らない）。

## 母集合（規則 9〜11）

### 規則 9: 他の受け手 DTO に「送り手が null 許容・受け手が非 null」のずれが無いか

**引き方**: 誤りの側（受け手の非 null の値型項目）から引く。受け手の母集合は
`git ls-files 'backend/Services/*' | grep -v /Tests/ | grep '/ExternalServices/Http[A-Z][^/]*\.cs$'`（25 本）と、
`git grep -ln 'DeserializeAsync\|JsonDocument.Parse\|ReadFromJsonAsync\|GetFromJsonAsync' -- 'backend/Services/*' ':!*/Tests/*'`（上と同じ集合＋外部 API・永続化）で取り、
各アダプタの `ReadFromJsonAsync<T>` の型 `T`（入れ子を含む）の項目を 1 つずつ送り手の本物の型と突き合わせた。

- 値型の非 null 項目へ null が来ると**例外**（本件の型）。参照型（`string` 等）の非 null 注釈は `System.Text.Json` の既定
  （`RespectNullableAnnotations=false`）では null を黙って通すため、**送り手が null 許容の参照型**も同じ表で見た（下流の NullReferenceException の型）。

| 受け手（サービス・アダプタ） | 受け手の DTO | 送り手の本物の型 | 受け手の非 null 項目（値型／参照型） | 送り手の同項目 | 判定 |
| --- | --- | --- | --- | --- | --- |
| 通知 `HttpPauseController.GetStatusAsync` | `RiskStatusView` | リスク管理 `GetRiskStatus.RiskStatusView` | `MaxDailyOrderAmount decimal` | **`decimal?`（口座照会不能で null）** | **ずれ（本件で是正）** |
| 同上 | 同上 | 同上 | `KillSwitchEngaged`/`DailyLossLockoutActive`/`TradingPaused`/`NewEntriesBlocked bool`・`Stage int`・`DailyRealizedPnl`/`UnrealizedPnl`/`DailyPnl`/`DailyOrderedAmount`/`DrawdownRatio`/`MaxDrawdownRatio decimal`・`OpenPositionCount`/`MaxOpenPositions int` | いずれも非 null（`Stage` は `TradingStage`） | 一致 |
| 同上（受けない項目） | — | 同上 | `Capital`・`MaxOrderAmount`（`decimal?`）は受け手が受けない | — | 対象外（読まない） |
| 通知 `HttpPauseController.Pause/Resume` | `PauseStateView(bool Paused)` | `PauseState.Paused bool` | `Paused` | `bool` | 一致 |
| 通知 `HttpKillSwitchController` | `KillSwitchStateView(bool Engaged)` | `KillSwitchState.Engaged bool` | `Engaged` | `bool` | 一致 |
| 通知 `HttpGoodFaithViolationController` | `ClearResultView` | 匿名型（値は `GoodFaithViolationClearingOutcome`） | `RemainingCount int` | `int` | 一致 |
| 通知 `HttpStageGateController` | `StageGateStatusView` ほか入れ子 5 型 | `StageGateStatus`・`StageSettings`・`StageTransition`・`PromotionAssessment`・`WithdrawalAssessment`・`Stage1GateCriteria`・`StageTransitionResult` | `CurrentStage int`・`StageSettingsView(int,int,decimal)`・`StageTransitionView`（`int`×4・`string ApprovedBy`/`Reason`・`DateTimeOffset`）・`Eligible bool`・`Triggered`/`HaltNewEntries bool`・`Accepted bool`・`Stage1GateCriteriaView`（`int`×3・`bool`）・`CurrentSettings`/`Promotion`/`Withdrawal`（参照） | いずれも非 null（`TargetStage`・`Reason`・`ProposedStage`・`Transition`・`ResultingSettings` は送り手 null 許容で受け手も null 許容） | 一致 |
| 通知 `HttpReportReviewController`（照会・差し戻し・確定） | `ReviewView(int Version, …)` | 報告書 `ReportReviewView`／`ReportReview`（`int Version`） | `Version int` | `int` | 一致 |
| 通知 `HttpReportReviewController`（入力補完） | `ReportListItem(string?, JsonElement?)` | 報告書 `ReportPeriodKeyItem` | 無し（全項目 null 許容） | — | 一致 |
| 判断 `HttpSizingContextProvider` | `SizingContextDto` | リスク管理 `SizingContextView` | 無し（全項目 null 許容。IADR-0408） | — | 一致 |
| 判断 `HttpHeldPositionProvider` | `OpenPositionDto`・`WorkingEntryOrderDto` | リスク管理 `OpenPositionView`・`WorkingEntryOrderView` | 無し（全項目 null 許容。IADR-0390） | — | 一致 |
| 判断 `HttpWatchlistProvider` | `WatchedSymbol(string, Market)` | 市場監視 `MonitoredSymbol(string, Market)` | `Symbol`（参照）・`Market` | 非 null | 一致 |
| 判断 `HttpDailyPolicyProvider` | `ConfirmedDailyPolicyDto(DateOnly, string, int)` | 報告書 `ConfirmedDailyPolicy(DateOnly, string, int)` | 全項目 | 非 null | 一致 |
| 判断・費用統制 `HttpAssumptionsClient` | `VersionedAssumptions` | 同じ共有型（`Shared.Kernel`） | — | 同一の型 | 対象外（同一型） |
| 情報収集 `HttpCostControlGate` | `CostStateDto(bool?, decimal?)` | 費用統制の状態 | 無し（全項目 null 許容） | — | 一致 |
| 市場監視 `HttpPositionStore` | `OpenPositionDto` | リスク管理 `OpenPositionView` | 無し（全項目 null 許容。IADR-0399） | — | 一致 |
| 報告書 `HttpOpenPositionSource` | `OpenPositionDto` | 同上 | 無し（全項目 null 許容。IADR-0408） | — | 一致 |
| 報告書 `HttpPeriodFillSource` | `LedgerFillDto` | リスク管理 `LedgerFill` | `Symbol`・`Market`・`Side`・`PositionEffect`・`Quantity`・`Price`・`ExecutedAt`・`FxRateToBase decimal`・`DecisionId Guid` | いずれも非 null（`Provider`・`FxRateBaseToDisplay` は双方 null 許容） | 一致 |
| 報告書 `HttpPeriodDriftAdoptionSource` | `DriftAdoptionDto` | リスク管理 `DriftAdoptionView` | `AdoptionId`・`Symbol`・`Market`・`Side`・`int`×3・`ObservedAt`・`AdoptedAt` | 非 null（`Actor`・`Reason` は送り手非 null・受け手 null 許容＝安全側） | 一致 |
| 報告書 `HttpBuyInInferenceRecordSource` | `BuyInInferenceQueryDto`・`BuyInInferenceRecordDto` | 匿名型＋`BuyInInferenceRecord` | `Id`・`Symbol`・`Market`・`int`×5・`InferredOn`・`ObservedAt`・`InferredAt` | 非 null（`BanUntil` は双方 null 許容。外側 3 項目は受け手 null 許容） | 一致 |
| 報告書 `HttpOpenDUptimeSource` | `SessionUptimeDto`・`SessionUptimeDayDto` | `SessionUptimeView`・`OpenDSessionUptimeDay` | `Stage1CumulativeCountedDays int`・`SessionDateEasternTime DateOnly`・`UptimeRatio decimal` | 非 null | 一致 |
| 報告書 `HttpStageProgressSource` | `StageGateDto(TradingStage?)` | `StageGateStatus` | 無し | — | 一致 |
| 報告書 監査台帳 4 本（`HttpBorrowFeeRecordSource`・`HttpFxSourceStatusSource`・`HttpLlmUsageRecordSource`・`HttpTradeRationaleSource`） | `AuditEntryDto(Guid, string, string)` | 監査 `AuditEntry` | `Id`・`EventType`・`Detail` | 非 null | 一致 |
| 同上の `Detail` の中身 | 共有契約のイベント型（`Shared.Contracts.Events`） | 同じ共有型 | — | 同一の型 | 対象外（同一型） |

**除外したものと理由**:

- `HttpReportNarrativeDrafter`・`HttpLlmCompletionClient`: 名前の `Http…` は名残で、共有契約（`Shared.Contracts.Llm`）の型でやり取りする（同一型）。
- 情報収集の外部 API（BOJ・EDINET・Finnhub・FRED・SEC EDGAR）: 他サービスではなく外部の供給元。本件の「サービス間の受け手」の射程外。
- 永続化の読み戻し（`AssumptionsSerialization`・`MonitorSettingsSerialization`・`RiskSettingsSerialization`・`EfBrokerPositionObservationStore`・監査の `ProtectiveStopWaiverSettlement`）:
  自サービスが書いたものを自サービスが読む。サービス間の受け手ではない。
- `OpendAuthGateway` の `OpendAuthEndpoints`: 上流サイドカーの写像を 1 ファイルに閉じた別系統（IADR-0321）。未供給は `NotSupplied` へ倒す設計。
- 画面（`frontend/`）の受け手: 依頼の射程（通知・判断・報告書・市場監視・情報収集）外。`maxDailyOrderAmount` は既に `number | null`。
- メッセージング（Wolverine）の受け手: 送り手と同じ共有契約の型を使う（同一型）。

**結果**: ずれは本件の 1 件（通知の稼働状態の `MaxDailyOrderAmount`）だけ。同じ PR で直す他の箇所は無い。

### 規則 10: この変更で新たに誤りになる自分の記述

走査語 `MaxDailyOrderAmount`・`maxDailyOrderAmount`・`稼働状態の照会`・`JsonException`（`docs/`・`.ai-context/adr/`・`frontend/src`）。

- 変えるもの: `HttpPauseController` の射影の型と `Format` の表示（本件）。
- `docs/operations/capital-baseline-seed-runbook.md`・`docs/operations/operations.md` の「`/status` の資金が『取得できていません』」:
  SC-03（統制状態の画面と `GET /risk-controls/status`）の記述であり、Discord の `/status` は資金を表示しない。本件で誤りにならない。変えない。
- PR #989（作業中にマージ済み）の IADR-0408 の追記「見つけた欠陥（本件では直していない）」と索引行の「（未是正）」: 凍結記録であり本文は変えない。
  **その直後に本件の日付つき追記を置き、「上の追記の……を是正した」と書いて解消する**（本体・索引行の両方）。
- 同じく #989 の作業仕様書 `20260925_957_cross-service-read-contracts-c`: 凍結記録。変えない。
- テスト仕様書 FR-10（`docs/`＝生きた文書）の #957 の節の残余リスク「見つけた欠陥（本書では直していない）」: **本件で誤りになる**。
  「本節の作業では直していない。次節で是正した」へ書き換え、本件の節をその直後に置いた（目次にも 1 行足した）。
- 導出値: テスト数（`NotificationService.Tests` の件数）は「検証」で計算し直す。

### 規則 11: 窓（時間差）

受け手だけの変更で、受け手は「数値」「null」「項目の欠落」のいずれも受ける（増える側＝新しい受け手×旧い送り手〔常に数値を返していた版〕、
減る側＝旧い受け手×新しい送り手〔null を返す現行版〕の両方で、新しい受け手は成功する）。送り手の変更は無く、
配備順に依る窓は生じない。旧い受け手が残る間は従来どおり失敗する（是正前の状態のまま＝悪化はしない）。

## 射程外（見送り）

- 送り手の `RiskStatusView` の変更（null の意味は IADR-0354 で確定済み）。
- Discord の `/status` に資金（`Capital`）や 1 注文あたりの上限（`MaxOrderAmount`）を足すこと（本件の求めに無い）。

## 検証

（2026-09-25・`origin/develop` f5fae563 へ載せ直した後の作業ツリーで実行）

- `dotnet build backend/backend.slnx -v q`: 0 警告 0 エラー。
- `dotnet test backend/Services/NotificationService/Tests/NotificationService.Tests.csproj`: 601/601 合格（載せ直し前の develop ddf2d835 の上では 597/597。
  #989 の T-10-932〜935 の 4 件が増えた分）。`RiskControlOperationReadContractTests` と `HttpPauseControllerTests` は計 16 件。
- `dotnet format backend/backend.slnx --verify-no-changes`: 差分なし（exit 0）。
- 変異注入（上の 16 件に対して。1 つずつ入れて実行し、実行ごとに変異前の内容へ書き戻した）:
  - 受け手の上限を非 null に戻す（是正前の develop の受け手）: 2 件赤（T-10-941 は「稼働状態の照会に失敗しました（JsonException）」＝issue の事象を再現、T-10-943 は上限を 0 と表示）。
  - 型は null 許容のまま null を 0 と表示する: 2 件赤（T-10-941 / T-10-943）。
  - T-10-942 はどちらの変異でも緑（非 null の表示が変わらないことの固定であり、変異の対象外）。
- 文書・検査器: `check-trace-blocks`・`check-doc-links`・`check-cross-repo-refs`・`check-plan-id-qualification`・`check-test-traceability`
  （テスト ID の重複の増加なし）・`check-adr-index-addendum-loss`・`check-adr-index-sync --range=origin/develop..HEAD`・`check-commit-messages`・
  `check-reading-budget`・`gen-knowledge-graph --check`: いずれも exit 0。
