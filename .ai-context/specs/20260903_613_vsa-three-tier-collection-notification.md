---
title: #613 第6弾 Features/<集約>/<操作>/ の 3 段化 —— InformationCollectionService・NotificationService の移送
type: spec
status: draft
related_ids:
  - NFR
  - IADR-0259
  - IADR-0276
  - IADR-0289
  - MSP:ADR-0065
  - MSP:ADR-0068
  - MSP:ADR-0077
author: endazon (with Claude Code)
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md
  - planning:projects/microservices-platform/07_adr/ADR-0068_three-level-slice-split-rule.md
  - planning:projects/microservices-platform/07_adr/ADR-0077_operation-semantics-in-three-level-slice.md
---

# 仕様書: #613 第6弾 —— InformationCollectionService・NotificationService の 3 段化移送

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（構成是正・保守性の非機能作業）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 起点 ID: `NFR`（無採番。`.claude/rules/traceability.md` 無採番許容ケース 2 ——
  ソースツリーの割り方であり計画の非機能要件表に当たる番号が無い。
  [IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) と同じ判断）
- 関連 ADR: platform `ADR-0065` 決定 2・決定 3／platform `ADR-0068` 決定 1〜5／
  **platform `ADR-0077`（「操作」は契機の形で決めない。分界は入口の配線と操作の処理）**
- 移送規則の正本: [IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) 決定 1〜6
  （**本 PR で新規 IADR は作らない**。第 5 弾が同 IADR へ `ADR-0077` 追随の追記を入れるため、
  本 PR は IADR-0289 を編集せず、判断は本仕様書に記録して第 5 弾へ申し送る）
- 第 1 弾の指示書: [20260903_613_vsa-three-tier-risk-management](20260903_613_vsa-three-tier-risk-management.md)
  §残る 10 サービスの割り当て表（`InformationCollectionService` / `NotificationService` の行）
- 計画書リンク: <https://github.com/endazon/project-planning/blob/main/projects/microservices-platform/07_adr/ADR-0077_operation-semantics-in-three-level-slice.md>

## 目的・背景

第 1 弾（PR #652）で移送規則が確定し、第 2 弾以降で HTTP 端点を持つサービスへ順に当ててきた。
[IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) §追記 1 の暫定解釈（操作＝登録表の HTTP 端点）は
**計画側の `ADR-0077` が置き換えた**。同 ADR 決定 1 は「操作とは外部からの 1 つの契機に応えて行う 1 つの
ユースケースであり、契機の形（HTTP・イベント購読・スケジュール・チャットコマンド）では決めない」と定める。

本作業（第 6 弾）は、その裁定によって移送対象へ戻った 5 サービスのうち、**HTTP 端点をほとんど／まったく
持たない 2 サービス** —— `InformationCollectionService`（契機＝スケジュール＋HTTP 2 本）と
`NotificationService`（契機＝イベント購読 22 本＋Discord スラッシュコマンド 7 本）—— を移送する。

実測（移送前・`origin/develop` `947329ea`）:

| 観点 | InformationCollection | Notification |
| --- | ---: | ---: |
| `Features/<集約>/` の `.cs` | 8 | 14 |
| `Features/<集約>/` の操作ディレクトリ | 0 | 0 |
| HTTP 端点（`Program.cs` 直書き） | 2 | 0 |
| イベント購読ハンドラ | 0 | 22 |
| Discord スラッシュコマンド | 0 | 7 |
| `Tests/` の `.cs` | 31 | 29 |
| テスト件数 | 462 | 398 |

> **`Features/Notifications/` は 21 → 14 になっている。** 第 1 弾の割り当て表は移送前の実測（21 件）を
> 載せているが、その後 PR #659（`Domain/` 欠けの是正）が `BotCommandParser` / `BotCommand` /
> `DiscordCommandAuthorizer` / `DiscordCommandContext` / `DiscordBotOptions` / `KillSwitchConfirmation` /
> `VersionedConfirmationGuard` の 7 本を `Domain/` へ移した。**本 PR はそれらを動かさない。**

## 対象範囲

- 対象:
  - `InformationCollectionService` の `Features/InformationCollection/<操作>/` 3 段化（2 操作）
  - `NotificationService` の `Features/Notifications/<操作>/` 3 段化（5 操作）
  - 両サービスの `Tests/` を本体の樹形の鏡写しへ再配置
- 対象外:
  - 他サービスの移送（第 5 弾が並行、残りは後続 PR）
  - `Hosted/`・`Infrastructure/`（`Steps` ＝ Wolverine ハンドラ・`ExternalServices` ＝ Discord ゲートウェイ／
    HTTP アダプタ）の**移動と名前空間の変更**（`ADR-0077` 決定 2「入口の配線は現在の置き場に残す」・
    [IADR-0276](../adr/IADR-0276_claude-md-vsa-correction-and-hosted-placement.md) 決定 2）
  - `Domain/`（#659 で切り出したばかりの 7 本）の移動
  - 2 段目（集約）の切り直し
  - 振る舞い・公開面（ルート・認可・応答形・Discord のコマンド名と登録順）・DI 登録・wire 契約
    （`Shared.Contracts` の型・メッセージ URN・キュー名）の変更
  - 新規 IADR の起草・`.ai-context/adr/README.md` の更新（規則の正本は IADR-0289。
    **`ADR-0077` 追随の追記は第 5 弾が入れる**ため本 PR は同 IADR に触れない）

## 設計

### 契機の棚卸しと操作の割り当て

`ADR-0077` 決定 1・2 に従い、**契機（trigger）ではなくユースケース（操作）で束ね**、
**入口の配線は現在の置き場に残し、操作の処理だけを 3 段目へ下ろす**。

#### InformationCollectionService（集約 `InformationCollection`・操作 2）

| # | 操作フォルダ | 契機 | 入口の配線（動かさない） | 3 段目へ下ろすファイル |
| ---: | --- | --- | --- | --- |
| 1 | `RunCollectionCycle` | ① スケジュール（`Collection:PollIntervalSeconds` の定時巡回）<br>② HTTP `POST /internal/collection/run-once`（OwnerOrService・K8s CronJob） | `Hosted/CollectionPollingService.cs`（`ExecuteAsync` ループ＋`RunOnceAsync`）／`Program.cs` の `MapPost` | `InformationCollectionAppService.cs`・`CollectionResult.cs` |
| 2 | `ActivateGeneralWebCollection` | HTTP `POST /internal/collection/general-web-activation`（OwnerOnly） | `Program.cs` の `MapPost` ＋ `.RequireAuthorization` | `Endpoint.cs`（ラムダ本体を切り出す） |

🔴 **操作 1 は契機が 2 つあるが 1 つの操作である**（`ADR-0077` 決定 1・基盤の `DataSources/Sync/` と同型）。
run-once 端点は `CollectionPollingService.RunOnceAsync` を呼ぶだけであり、定時巡回とまったく同じ処理へ入る。

🔴 **操作 1 の `Endpoint.cs` は作らない。** run-once のラムダ本体は `poller.RunOnceAsync(ct)` の 1 行であり、
処理の実体は入口の配線（`Hosted/`）にある。これを `Features/` へ切り出すと
**`Features/` が `Hosted/` を参照する**形になり、`ADR-0065` 決定 7 の参照方向の規律を今より読みにくくする
（[IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) 決定 2 が案 2 を退けたのと同じ理由）。
ルート・認可・登録順は `Program.cs` に据え置く。

##### 2 段目に残るもの（根拠つき・全件）

| ファイル | 残す根拠 |
| --- | --- |
| `ISourceFetcher.cs`・`IInformationSource.cs`・`SourceFetch.cs` | 実装が `Infrastructure/ExternalServices/`（6 コネクタ＋`SourceFetchRunner`＋`NoSourcesFetcher`）にあり、`Program.cs` が DI で選ぶ |
| `IKnowledgeBaseSink.cs` | 実装が `Infrastructure/ExternalServices/` 3 本 |
| `ICostControlGate.cs` | 呼び出し元が `Hosted/CollectionPollingService.RunOnceAsync`（入口の配線）＋実装 2 本が `Infrastructure/ExternalServices/` |
| `RawInformationItem.cs` | **他アセンブリ**（`AiStockTrading.Shared.Infrastructure` の `FinnhubQuoteClient`）からも参照される |

#### NotificationService（集約 `Notifications`・操作 5）

| # | 操作フォルダ | 契機 | 入口の配線（動かさない） | 3 段目へ下ろすファイル |
| ---: | --- | --- | --- | --- |
| 1 | `OperateKillSwitch` | Discord `/killswitch`（起動・解除。確認ボタン → 確認フレーズのモーダル） | `Infrastructure/ExternalServices/DiscordNetBotGateway.cs`（コマンド登録・ディスパッチ・モーダル） | `KillSwitchCommandHandler.cs`（`KillSwitchCommandResult` を同居） |
| 2 | `OperateTradingPause` | Discord `/pause`・`/resume`・`/status` | 同上 | `PauseCommandHandler.cs`（`PauseCommandResult` を同居） |
| 3 | `OperateStageGate` | Discord `/stage`（status / withdrawal / promote / demote） | 同上 | `StageGateCommandHandler.cs`（`StageGateCommandResult` を同居） |
| 4 | `ClearGoodFaithViolations` | Discord `/gfv`（clear） | 同上 | `GoodFaithViolationCommandHandler.cs`（`GoodFaithViolationCommandResult` を同居） |
| 5 | `ReviewReport` | Discord `/report`（show / approve / request-changes） | 同上 | `ReportCommandHandler.cs`（`ReportCommandResult` を同居） |

🔴 **`/pause`・`/resume`・`/status` は 1 つの操作へ束ねる。** 3 コマンドを 1 本の
`PauseCommandHandler.HandleAsync` が扱っており、**分けるには型を割る＝純粋な移送でなくなる**。
`ADR-0077` 決定 1 の「契機が 2 つある操作は 1 つの操作である」に沿う。`/stage`・`/gfv`・`/report` の
サブコマンドも同じ理由で 1 操作に束ねる。**Discord のスラッシュコマンド名・登録順は 1 バイトも変えない。**

🔴 **22 種のイベント購読には操作フォルダを作らない。** 判定（`ADR-0068` 決定 2・`ADR-0077` 決定 2）を
当てると、購読側の処理の実体は `NotificationFormatter`（22 の `From(...)` 多重定義）と
`NotificationMessage` / `INotificationSender` であり、**いずれも 22 操作すべてが使う共通部分**である
（`ADR-0068` 決定 3 により 2 段目に残る）。
**`NotificationFormatter` を操作ごとに割ることはできない** —— C# の `partial` は
複数の名前空間にまたがれないため、フォルダごとに割ると型を割る（改名する）ことになり、
[IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) 決定 4（名前空間はフォルダに合わせる）と
「純粋な移送」の両方を同時には満たせない。私的ヘルパ（`ReasonLabel` / `StaleWarning` / `FormatDuration` /
`Ratio` / `Describe` ×2 / `SettledCash`）も複数の `From` が共有している。

##### 2 段目に残るもの（根拠つき・全件）

| ファイル | 残す根拠 |
| --- | --- |
| `NotificationFormatter.cs` | 22 のイベント購読操作すべてが使う共通部分（上記） |
| `NotificationMessage.cs` | 同上＋`INotificationSender` の引数型 |
| `INotificationSender.cs` | 22 ハンドラ＋実装 3 本が `Infrastructure/ExternalServices/` |
| `IDiscordBotGateway.cs` | `Infrastructure/Steps/DiscordBotHostedService.cs`（常駐）＋実装 2 本が `Infrastructure/ExternalServices/` |
| `IKillSwitchController.cs`・`IPauseController.cs`・`IStageGateController.cs`・`IGoodFaithViolationController.cs`・`IReportReviewController.cs` | 実装（`Http*Controller`）が `Infrastructure/ExternalServices/` にあり `Program.cs` が DI で登録する。3 段目へ下ろすと **`Infrastructure` が `Features/<集約>/<操作>/` を参照する**形になり、[IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) 決定 2 が案 2 として退けた形になる |

### 🔴 本 PR で要った判断（第 5 弾へ申し送り。IADR-0289 追記候補）

[IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) 決定 2 は
「**`Infrastructure/`・`Hosted/`・他サービスから使われるものは 2 段目に残す** —— 呼び出し元が `Features/` の
操作ではないためである」と書いている。**`ADR-0077` の下ではこの一文をそのまま当ててはならない。**

- `ADR-0077` 決定 2 は、購読ハンドラ（`Infrastructure/Steps/`）・常駐ジョブ（`Hosted/`）・
  Discord のコマンド登録／ディスパッチ（`Infrastructure/ExternalServices/DiscordNetBotGateway.cs`）を
  **「その操作の入口の配線」**と位置づけた。**入口の配線は操作の一部である。**
- したがって「呼び出し元が入口の配線であること」は、もはや「操作専属でない」ことの根拠にならない。
  **1 つの操作の入口の配線からしか使われないファイルは、`ADR-0068` 決定 2 の意味で 1 操作専属であり、
  3 段目へ下ろす。**
- そう読まないと、`InformationCollectionAppService`（`Hosted/` から使われる）も 5 つの Discord
  コマンドハンドラ（`DiscordNetBotGateway` から使われる）も 2 段目に固定され、
  **`ADR-0077` が移送対象へ戻した 5 サービス 75 ファイルが 1 件も動かない** —— 同 ADR 決定 3 (c) が
  暫定解釈 B を退けた理由（75 ファイルを 2 段のまま残す）が、そのまま再現する。
- **一方、「実装が `Infrastructure/` にあるポート」は 2 段目に残す。** 下ろすと参照方向が
  `Infrastructure` → 3 段目になり、[IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) 決定 2 の
  案 2 が退けた形になるためである。**入口の配線から「使われる」ことと、`Infrastructure` の
  アダプタに「実装される」ことを区別する。**

第 5 弾（`TradeDecision` / `OrderExecution` / `Backtest`）も同じ形（購読ハンドラ・常駐ジョブ経由）で
移送するため、**この読みは弾をまたいで一致している必要がある。** 本 PR は IADR-0289 を編集せず、
第 5 弾の追記へ本節を申し送る。

### `Tests/` の鏡写し（[IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) 決定 5）

**テストの名前空間は `<Svc>.Tests` のまま据え置く**（決定 5）。フォルダだけを本体の樹形へ揃える。

#### InformationCollectionService.Tests

| 行き先 | 件数 | 内容 |
| --- | ---: | --- |
| `Tests/Domain/` | 6 | `DegradationEvaluatorTests`・`FinnhubQuotaCalculatorTests`・`GeneralWebActivationPolicyTests`・`InformationSourceCatalogTests`・`PromptSafetySanitizerTests`・`SourceAllowlistTests` |
| `Tests/Features/InformationCollection/RunCollectionCycle/` | 2 | `InformationCollectionServiceTests`（`InformationCollectionAppService`）・`RunOnceAuthorizationTests`（run-once 端点の認可） |
| `Tests/Features/InformationCollection/ActivateGeneralWebCollection/` | 1 | `GeneralWebActivationEndpointTests` |
| `Tests/Hosted/` | 2 | `CollectionPollingServiceTests`・`DegradationStateTrackerTests` |
| `Tests/Infrastructure/ExternalServices/` | 10 | 公式ソースコネクタ 6（`Boj`/`Edinet`/`Finnhub`/`Fred`/`News`/`SecEdgar`）・`SourceFetchRunnerTests`・`InformationSourceFactoryTests`・`HttpCostControlGateTests`・`KnowledgeBaseWriterSinkTests` |
| `Tests/`（直下・据え置き） | 9 | `Program.cs` の配線／構造テスト 6（`CostControlGateSelectionTests`・`InformationSourceSelectionTests`・`KnowledgeBaseSinkSelectionTests`・`CollectionIntervalNotConfigurableTests`・`UnauthenticatedEndpointsNotAllowedTests`・`HealthEndpointTests`）・テスト土台 3（`InformationCollectionWorkerWebApplicationFactory`・`TestAuthHandler`・`TestDoubles`） |

#### NotificationService.Tests

| 行き先 | 件数 | 内容 |
| --- | ---: | --- |
| `Tests/Domain/`（**既存・動かさない**） | 4 | `BotCommandParserTests`・`DiscordCommandAuthorizerTests`・`KillSwitchConfirmationTests`・`VersionedConfirmationGuardTests` |
| `Tests/Features/Notifications/OperateKillSwitch/` | 1 | `KillSwitchCommandHandlerTests` |
| `Tests/Features/Notifications/OperateTradingPause/` | 1 | `PauseCommandHandlerTests` |
| `Tests/Features/Notifications/OperateStageGate/` | 2 | `StageGateCommandHandlerTests`・`StagePromoteWarningTests` |
| `Tests/Features/Notifications/ClearGoodFaithViolations/` | 1 | `GoodFaithViolationCommandHandlerTests` |
| `Tests/Features/Notifications/ReviewReport/` | 1 | `ReportCommandHandlerTests` |
| `Tests/Features/Notifications/` | 3 | `NotificationFormatterTests`・`NotificationTemplateGoldenTests`（ゴールデンは**テスト本体の `Dictionary` 内**。外部ファイルは無い）・`DiscordSettingsAreReadOnlyTests`（5 操作すべてを横断する否定形） |
| `Tests/Infrastructure/Steps/` | 3 | `NotificationConsumersTests`・`NotificationConsumerCoverageTests`・`LlmGovernanceNotificationTests` |
| `Tests/Infrastructure/ExternalServices/` | 10 | `DiscordBotGatewayFactoryTests`・`DiscordBotOptionsReaderTests`・`DiscordWebhookHttpClientTests`・`DiscordWebhookNotificationSenderTests`・`NotificationSenderFactoryTests`・`SecretRedactionTests`・`HttpKillSwitchControllerTests`・`HttpPauseControllerTests`・`HttpStageGateControllerTests`・`HttpReportReviewControllerTests` |
| `Tests/`（直下・据え置き） | 3 | `HealthEndpointTests`・テスト土台 2（`NotificationWorkerWebApplicationFactory`・`RecordingNotificationSender`） |

## 受け入れ基準

- [x] `dotnet build backend/backend.slnx` が成功し、**警告 0・エラー 0**
- [x] `dotnet test backend/backend.slnx --filter "Category!=Integration"` の**テスト件数がアセンブリ単位で移送前と同数**
      （移送前: `InformationCollectionService.Tests` 462・`NotificationService.Tests` 398・全 20 アセンブリ合計 5447）
- [x] `Features/InformationCollection/` に操作ディレクトリ 2 個、`Features/Notifications/` に 5 個
- [x] `AiStockTrading.Architecture.Tests` が緑（`Domain/` ソース走査件数が下限以上）
- [x] `dotnet format backend/backend.slnx --verify-no-changes` が差分なし
- [x] `node scripts/check-trace-blocks.js` / `check-test-traceability.js` / `check-doc-links.js` /
      `check-adr-index-sync.js` / `check-cross-repo-refs.js` が OK
- [x] 公開面に差分が無い —— HTTP 端点（verb + path + 認可 + 登録順）・Discord のスラッシュコマンド名と
      登録順・Wolverine のハンドラ発見（キュー名＝`ai-stock-trading.notification-service.<イベント型名>`）・
      メッセージ URN・`Shared.Contracts`

## テスト方針

**テストは 1 件も追加・削除・改変しない**（純粋な移送）。移送が振る舞いを変えていないことは、
既存テストが無改修で緑であること自体で示す。とくに次が公開面の固定として効く。

1. `NotificationConsumerCoverageTests`（**Wolverine のハンドラ発見**をリフレクションで網羅検査する。
   ハンドラ型・名前空間を動かしていないことの証拠）
2. `NotificationConsumersTests` / `LlmGovernanceNotificationTests`（購読 → 整形 → 送信の経路）
3. `DiscordBotGatewayFactoryTests` / `DiscordSettingsAreReadOnlyTests`（Discord コマンドの窓口と読み取り専用性）
4. `RunOnceAuthorizationTests` / `GeneralWebActivationEndpointTests` / `UnauthenticatedEndpointsNotAllowedTests` /
   `CollectionIntervalNotConfigurableTests`（HTTP 端点の集合・認可）

## 計画書との差異

- 差異: なし。platform `ADR-0065` 決定 2・決定 3、`ADR-0068` 決定 1〜5、`ADR-0077` 決定 1〜3 の形をそのまま採る。
  `Hosted/` の扱いは [IADR-0276](../adr/IADR-0276_claude-md-vsa-correction-and-hosted-placement.md) 決定 2（現状維持）に従う
  （`ADR-0077` §残るもの は「`Hosted/` の置き場は別に裁定が要る」と明記しており、本 PR では動かさない）。

## 移送後の実測（2026-09-03）

移送前の基準は `origin/develop` `947329ea`。

| 観点 | 移送前 | 移送後 |
| --- | ---: | ---: |
| `InformationCollectionService.Tests` | 462 | **462** |
| `NotificationService.Tests` | 398 | **398** |
| `AiStockTrading.Architecture.Tests` | 87 | **87** |
| 全アセンブリ合計（`Category!=Integration`・20 アセンブリ） | 5447 | **5447** |
| `Features/InformationCollection/` の操作ディレクトリ | 0 | **2** |
| `Features/Notifications/` の操作ディレクトリ | 0 | **5** |
| `Features/InformationCollection/` 直下の `.cs`（2 段目） | 8 | **6** |
| `Features/Notifications/` 直下の `.cs`（2 段目） | 14 | **9** |
| `InformationCollectionService/Tests/` 直下の `.cs` | 31 | **9** |
| `NotificationService/Tests/` 直下の `.cs` | 29 | **3** |

**アセンブリ別の件数は 20 アセンブリすべてで移送前と一致した。**

- `dotnet build backend/backend.slnx`: 成功・警告 0・エラー 0
- `dotnet format backend/backend.slnx --verify-no-changes`: 差分なし
- `node scripts/check-trace-blocks.js`（走査 41 件）／`check-doc-links.js`（640 件）／
  `check-cross-repo-refs.js`（2061 件）／`check-adr-index-sync.js --range=origin/develop..HEAD`: いずれも OK
- `node scripts/check-consumer-endpoint-names.js`: OK（11 サービス・本番 `.cs` 780 件。
  キュー名の規則 `<ServiceName>.<メッセージ型名>` は不変）
- `node scripts/check-test-traceability.js`: **Windows ローカルでのみ [T1] が偽陽性になる**
  （第 1 弾の仕様書 §移送後の実測 に記録済みの既知事象。`fs.existsSync(<Svc>/tests)` が
  Windows の大文字小文字を区別しないパスで実在する `Tests/` を旧樹形として数える。
  CI〔Linux〕では発生しない。**本 PR が持ち込んだ違反ではない**）
- **フレーク 1 件**: 全量実行の 1 巡目で
  `ReportService.Tests.HttpOpenDUptimeSourceTests.タイムアウトは未供給へ倒す` が失敗した。
  **本 PR は `ReportService` を 1 バイトも触っていない**時間依存のテストであり、
  単独で再実行して緑（7/7）を確認した。合計件数は 749 で移送前と一致する。

### PR 直前の `origin/develop` 取り込み後（`229a413b`）

マージ後にもう一度全量を実行し、**20 アセンブリすべてが緑（失敗 0）**であることを確認した。
合計は **5470**（＝ 5447 ＋ develop 側が本 PR と独立に増やした 23 件。
`InformationCollectionService.Tests` 462 → 468 ＝ #668 の 6 件、`AiStockTrading.Shared.Infrastructure.Tests`
258 → 274、`AiStockTrading.Shared.Contracts.Tests` 350 → 351）。
**`NotificationService.Tests` は 398 のまま**であり、本 PR の移送は前後で件数を動かしていない。

取り込みに伴う追随は 1 件だけである —— #668 が `Tests/` 直下へ追加した
`InformationSourceFactoryDailyVolumeTests` を、被テスト型（`Infrastructure/ExternalServices/
InformationSourceFactory`）に合わせて `Tests/Infrastructure/ExternalServices/` へ移した
（鏡写しの規範を新規テストにも当てる。名前空間は据え置きで件数に影響しない）。

### 追随が要った参照側（[IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) 決定 4 の効き方の実測）

3 段目は 2 段目の入れ子であるため、**下ろしたファイル自身の `using` は 1 行も増えていない**。

| サービス | 追随した側 | 件数 |
| --- | --- | ---: |
| InformationCollection | `Program.cs`（`using` 1・`AppSvc` エイリアス 1・端点呼び出し 1） | 3 行 |
| | `Hosted/CollectionPollingService.cs`（`AppSvc` エイリアス） | 1 行 |
| | テスト（`CollectionPollingServiceTests` の別名・`InformationCollectionServiceTests` の `using`） | 2 ファイル |
| Notification | `Program.cs`・`Infrastructure/ExternalServices/DiscordNetBotGateway.cs`・`DiscordBotGatewayFactory.cs` | 3 ファイル × 5 行 |
| | テスト（3 段目の型を触るもの） | 9 ファイル |

## 未決事項

- **イベント購読 22 本に操作フォルダが生まれない。** 本仕様書 §NotificationService のとおり、
  処理の実体（`NotificationFormatter`）が 22 操作の共通部分であり、`ADR-0068` 決定 3 に照らして
  2 段目に残るためである。**将来 `NotificationFormatter` を操作ごとに割るなら型の改名を伴い、
  純粋な移送ではなくなる**（別 issue で扱う）。
- **`Features/` の 2 段目に残るポートの扱い。** 本 PR は「実装が `Infrastructure/` にあるポートは
  2 段目」を採ったが、これは第 1 弾からの一貫であって `ADR-0077` が直接に定めたものではない。
  第 5 弾と読みを揃える（§本 PR で要った判断）。
