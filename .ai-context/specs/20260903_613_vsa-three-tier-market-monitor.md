---
title: "#613 第3弾 —— MarketMonitorService の Features/<集約>/<操作>/ 3 段化移送"
type: spec
status: done
related_ids:
  - NFR
  - IADR-0289
  - MSP:ADR-0065
  - MSP:ADR-0068
author: endazon (with Claude Code)
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md
  - planning:projects/microservices-platform/07_adr/ADR-0068_three-level-slice-split-rule.md
---

# 仕様書: #613 第3弾 —— MarketMonitorService の 3 段化移送

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（構成是正・保守性の非機能作業）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 起点 ID: `NFR`（無採番。`.claude/rules/traceability.md` 無採番許容ケース 2 ——
  ソースツリーの割り方であり計画の非機能要件表に当たる番号が無い。第1弾
  [IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) と同じ判断）
- 関連 ADR: platform `ADR-0065` 決定 2（`Features/<集約>/<操作>/` の 3 段を規範とする）・決定 3（`Tests/` は本体の鏡写し）・
  platform `ADR-0068`（3 段目へ下ろすのは操作の処理・登録表は 2 段目に残す）
- 計画書リンク: <https://github.com/endazon/project-planning/blob/main/projects/microservices-platform/07_adr/ADR-0068_three-level-slice-split-rule.md>

## 目的・背景

#613 第1弾（[IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md)・PR #652）で移送規則を確定し
`RiskManagementService` を移送した。本作業（第3弾）は、[作業仕様書 20260903_613](20260903_613_vsa-three-tier-risk-management.md)
§残る10サービスの割り当て表の `MarketMonitorService` 行を指示書として実施する。

`MarketMonitorService` は `Features/MarketMonitor/` に 12 ファイル・9 端点を持つが、3 段目（操作）は 0 である。
指示書の割り当て表は 9 操作フォルダを提案しているが、**実装は指示書の粗い事前分析ではなく IADR-0289 の
決定 1〜6 を直接適用する**（第1弾の `RiskManagementService` と同じ判定基準に揃えるため。指示書は全 10 サービスを
横断する粗い事前分析であり、個々のファイルの内訳〔DTO の要求記録が単一操作専属か複数操作共有か〕までは
検証していない）。

## 対象範囲

- 対象:
  - `MarketMonitorService` の `Features/MarketMonitor/<操作>/` 3 段化（9 操作）
  - `MarketMonitorService` の `Tests/` を本体の樹形の鏡写しへ再配置
- 対象外:
  - 他サービスの移送（別 PR）
  - `Hosted/` の移動（IADR-0276 決定 2 で現状維持と確定済み）
  - `Infrastructure/`（`Persistence` / `ExternalServices` / `Steps`）の移動
  - 2 段目（集約）の切り直し
  - 振る舞い・公開面（ルート・認可・応答形）・DI 登録・wire 契約の変更

## 設計

### 移送規則（IADR-0289 の踏襲。変更なし）

第1弾と同一の規則を適用する。詳細は [IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) 決定 1〜6 を参照。
要点のみ再掲する。

1. 操作（3 段目）＝ 登録表に登録された 1 端点。登録表（`MonitorSettingsEndpoints.cs`）の `MapGroup` ／ タグ ／
   グループ単位の認可 ／ `Program.cs` から呼ぶメソッド名（`MapMonitorSettingsEndpoints`）／登録の順序は変えない。
2. ファイルの行き先は「1 つの操作にしか使われないか」だけで決める。`Program.cs` からの DI 登録は参照元として数えない。
3. 複数操作が使う共通部分（ヘルパ・DTO）は 2 段目に残す。
4. 名前空間はフォルダに合わせる（`MarketMonitorService.Features.MarketMonitor.<操作>`）。3 段目は 2 段目の入れ子であり、
   下ろしたファイルは 2 段目の共有型を `using` なしで見られる。
5. `Tests/` は本体の樹形をそのまま写す。テストの名前空間は `MarketMonitorService.Tests` のまま据え置く。

### 実測: 9 端点と参照元（移送前）

`Features/MarketMonitor/MonitorSettingsEndpoints.cs`（147 行・登録表）に登録された 9 端点。

| # | 操作フォルダ | 端点 | 一緒に下ろすファイル |
| --- | --- | --- | --- |
| 1 | `GetMonitorSettings` | GET `/settings` | （なし。`IMonitoredSymbolStore` を直接使用） |
| 2 | `ReplaceMonitorSettings` | PUT `/settings` | `MonitorSettingsUpdateRequest`（1 操作専属。実測: grep で他の参照ゼロ） |
| 3 | `UpdateMovementThreshold` | PUT `/settings/movement-threshold` | `MovementThresholdUpdateRequest`（1 操作専属） |
| 4 | `UpdateCooldown` | PUT `/settings/cooldown` | `CooldownUpdateRequest`（1 操作専属） |
| 5 | `GetMonitorSettingsHistory` | GET `/settings/history` | （なし） |
| 6 | `GetWatchlist` | GET `/watchlist`（OwnerOrService） | （なし） |
| 7 | `AddWatchlistSymbol` | POST `/watchlist` | （なし。`WatchlistChangeRequest`/`MarketOf`/`ActorOf` は 2 段目） |
| 8 | `RemoveWatchlistSymbol` | DELETE `/watchlist`（`[FromBody]` 明示） | （なし。同上） |
| 9 | `GetWatchlistHistory` | GET `/watchlist/history` | （なし） |

指示書（20260903_613_vsa-three-tier-risk-management.md）は `MonitorSettingsEndpoints.cs` を
「登録表（9 操作＋4 DTO＋`ActorOf`/`MarketOf`）」と一括りに記していたが、実測で 4 DTO の使用元を数えると
内訳が割れた（第1弾 `RiskManagementService` の `LimitsUpdateRequest`〔単一操作専属で `UpdateRiskLimits/Endpoint.cs`
へ下ろした〕と同じ判定を適用する）:

| 型 | 使用元（grep 実測） | 判定 |
| --- | --- | --- |
| `MonitorSettingsUpdateRequest` | `ReplaceMonitorSettings` の 1 箇所のみ | 決定2: 単一操作専属 → 3 段目（`ReplaceMonitorSettings/Endpoint.cs`）へ下ろす |
| `MovementThresholdUpdateRequest` | `UpdateMovementThreshold` の 1 箇所のみ | 同上 → `UpdateMovementThreshold/Endpoint.cs` |
| `CooldownUpdateRequest` | `UpdateCooldown` の 1 箇所のみ | 同上 → `UpdateCooldown/Endpoint.cs` |
| `WatchlistChangeRequest` | `AddWatchlistSymbol`・`RemoveWatchlistSymbol`・`MarketOf` の 3 箇所 | 決定3: 複数操作共有 → 2 段目に残す（第1弾の `KillSwitchRequest`/`PauseRequest` と同型。新規ファイル `Features/MarketMonitor/WatchlistChangeRequest.cs`） |
| `ActorOf` | 5 操作（Replace/UpdateMovementThreshold/UpdateCooldown/Add/Remove） | 決定3: 複数操作共有 → 登録表内に `internal static` のまま残す（qualified 参照 `MonitorSettingsEndpoints.ActorOf`） |
| `MarketOf` | 2 操作（Add/Remove） | 同上 → 登録表内に残す（`MonitorSettingsEndpoints.MarketOf`） |

### 2 段目に残すもの（変更なし。指示書と一致）

`MonitorSettingsService.cs`（4 操作: Replace/UpdateMovementThreshold/UpdateCooldown/GetHistory が使用）・
`MonitorWatchlistService.cs`（4 操作: GetWatchlist/Add/Remove/GetHistory が使用）・
`IMonitoredSymbolStore.cs`／`IMonitorSettingsChangeLog.cs`／`MonitorSettingsChangeEntry.cs`（複数操作＋Persistence）・
`MarketMonitorAppService.cs`／`MonitorRoundResult.cs`／`ICooldownStore.cs`／`IPriceBaselineStore.cs`／
`IPositionStore.cs`／`IMarketSchedule.cs`（`Hosted/MonitorPollingService.cs`・`Infrastructure/Steps/`・
`Infrastructure/ExternalServices/` から使用）。

### `Tests/` の再配置

被テスト型の置き場をそのまま写す。**MarketMonitorService の既存エンドポイントテストは、いずれも複数操作を
横断してアサートしている**（例: `MonitorWatchlistEndpointsTests` は GET/POST/DELETE/history の 4 操作を 1 ファイルで
検証）。第1弾の `RiskControlEndpointsTests`（複数操作を横断＝集約直下の `Tests/Features/RiskManagement/` に留めた）
と同じ扱いとし、**単一操作に対応するテストが実在しない限り `Tests/Features/MarketMonitor/<操作>/` は作らない**
（空フォルダを作らない）。実測で単一操作専属のテストファイルは 0 件だった。

| 移送先 | ファイル |
| --- | --- |
| `Tests/`（直下・据え置き） | `MonitorContractFixtureTests.cs`（フロント契約。既存の main tree と対応する `Features/Domain/Infrastructure/Hosted` の折り返しが無い）・`MonitorWorkerWebApplicationFactory.cs`・`TestAuthHandler.cs`・`TestDoubles.cs`（土台）・`CollectionIntervalNotConfigurableTests.cs`（`Program.cs` の実ルート表を横断検査する構造テスト）・`PositionStoreSelectionTests.cs`（`WebApplicationFactory<Program>` で DI 選択を検査する配線テスト。第1弾の `MarketDataWiringTests.cs`/`OrderActivityWiringTests.cs` と同型） |
| `Tests/Domain/` | `MonitorSettingsBoundsTests.cs`・`PriceMovementEvaluatorTests.cs`・`StopLossEvaluatorTests.cs` |
| `Tests/Features/MarketMonitor/`（集約直下・複数操作横断のため据え置き） | `MarketMonitorServiceTests.cs`（`MarketMonitorAppService` は Hosted 専属で 2 段目残置）・`MonitorSettingsEndpointsTests.cs`（GET/PUT /settings + history の 3 操作横断）・`MonitorSettingsServiceTests.cs`（4 操作共有）・`MonitorCollectionSettingsEndpointsTests.cs`（movement-threshold + cooldown の 2 操作横断）・`MonitorWatchlistEndpointsTests.cs`（GET/POST/DELETE + history の 4 操作横断）・`MonitorWatchlistServiceTests.cs`（4 操作共有） |
| `Tests/Infrastructure/ExternalServices/` | `HttpPositionStoreTests.cs` |
| `Tests/Infrastructure/Persistence/` | `EfStoreTests.cs`・`WatchlistConfigSeedTests.cs`（`EfMonitoredSymbolStore` の永続化・シード再構成を実体で検証） |
| `Tests/Infrastructure/Steps/` | `ConsumerEndpointNameTests.cs`・`TradeDecisionMadeBaselineConsumerTests.cs`（`TradeDecisionMadeBaselineHandler` 購読の検証） |
| `Tests/Hosted/` | `MonitorPollingServiceTests.cs` |

## 受け入れ基準

- [x] `Features/MarketMonitor/` に操作ディレクトリが 9 個でき、各操作が `Endpoint.cs` を持つ
- [x] `Tests/` が `Features/` ／ `Domain/` ／ `Infrastructure/` ／ `Hosted/` の鏡写しになっている（プロジェクトは 1 本のまま）
- [x] `dotnet build backend/backend.slnx` が警告ゼロで成功する
- [x] `dotnet test backend/backend.slnx --filter "Category!=Integration"` のテスト件数が移送前と一致する
- [x] `AiStockTrading.Architecture.Tests` が緑
- [x] `node scripts/check-trace-blocks.js` / `check-test-traceability.js` / `check-doc-links.js` /
      `check-adr-index-sync.js` が緑（`check-test-traceability.js` は第1弾と同じ既知の Windows ローカル
      限定偽陽性が残るのみ。§実測 参照）
- [x] `dotnet format --verify-no-changes` が緑
- [x] ルート・認可ポリシー・応答形・`Program.cs` の DI 登録・wire 契約に差分が無い

## テスト方針

**新しいテストは書かない。** 本作業は純粋な移送であり、既存テストが退行検知の手段である（IADR-0289 決定5・
第1弾と同じ判断）。検証は次の 3 点で行う。

1. テスト件数の前後比較（アセンブリ単位。とくに `MarketMonitorService.Tests`）
2. `MonitorSettingsEndpointsTests` / `MonitorWatchlistEndpointsTests` / `MonitorCollectionSettingsEndpointsTests`
   が無改修で緑（ルート・認可・応答形が動いていないことの証拠）
3. `ConsumerEndpointNameTests` / `MonitorContractFixtureTests` が緑（キュー分離・公開面の固定）

### 実測（移送前・`develop` `b8367987`。#652 マージ直後）

| アセンブリ | 件数 |
| --- | ---: |
| `MarketMonitorService.Tests` | 130 |
| `AiStockTrading.Architecture.Tests` | 87 |
| 全アセンブリ合計（`Category!=Integration`） | 5444 |

### 移送後の実測（2026-09-03）

| 観点 | 移送前 | 移送後 |
| --- | ---: | ---: |
| `MarketMonitorService.Tests` | 130 | **130** |
| `AiStockTrading.Architecture.Tests` | 87 | **87** |
| 全アセンブリ合計（`Category!=Integration`） | 5444 | **5444** |
| `Features/MarketMonitor/` の操作ディレクトリ | 0 | **9** |

- `dotnet build backend/backend.slnx`: 成功・警告 0・エラー 0
- `dotnet format backend/backend.slnx --verify-no-changes`: 差分なし
- `node scripts/check-trace-blocks.js` / `check-doc-links.js` /
  `check-adr-index-sync.js --range=origin/develop..HEAD`: いずれも OK
- `node scripts/check-test-traceability.js`: **第1弾（`RiskManagementService`）と同じ Windows ローカル限定の
  [T1] 偽陽性**（`serviceTestDirs()` の `fs.existsSync` が大文字小文字を区別しない Windows で `Tests/` を
  `tests/` としても数える）。移送前の `b8367987`（第1弾マージ直後）で同一コマンドを実行しても同じ 1 件が出ることを
  確認済みであり、**本 PR が持ち込んだ違反ではない**。CI（Linux）では発生しない。

### 追随が要った参照側

3 段目は 2 段目の入れ子であるため、下ろしたファイル自身の `using` は 1 行も増えていない。

| 追随した側 | 件数 |
| --- | ---: |
| `MonitorSettingsEndpoints.cs`（登録表が呼ぶ 9 操作） | 9 行 |
| `Program.cs` | 0 行（`MapMonitorSettingsEndpoints()` 呼び出し以外に移送対象への直接参照が無いため） |
| テスト | 0 ファイル（既存の全テストは HTTP 経由でエンドポイントを叩くのみで、移送した型を直接 `using` していない） |

## 計画書との差異

- 差異: なし。platform `ADR-0065` 決定 2・決定 3 と `ADR-0068` 決定 1〜5 の形をそのまま採る。

## 未決事項

- なし（第1弾 [IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) フォローアップ1〜3 は
  MarketMonitorService には該当しない —— MarketMonitorService は HTTP 端点を持つ 6 サービスの 1 つであり、
  `Domain/` も実在する）。
