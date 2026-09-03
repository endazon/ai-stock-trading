---
title: #613 第4弾 —— AuditService・ConfigurationService・CostControlService の 3 段化移送
type: spec
status: draft
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

# 仕様書: #613 第4弾 —— AuditService・ConfigurationService・CostControlService の 3 段化移送

> 本仕様書は実装着手前に作成する。計画書を一次情報とし、本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 起点 ID: `NFR`（無採番。[IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) と同じ判断——
  ソースツリーの割り方＝規約整備のメタ作業であり、計画側の非機能要件表に当たる番号が無い）
- 関連 IADR: [IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md)（移送規則の確定。決定 1〜6 を本作業でも適用する）
- 関連仕様書: [20260903_613_vsa-three-tier-risk-management](20260903_613_vsa-three-tier-risk-management.md)
  §残る 10 サービスの割り当て表・§`Domain/` を持たない 3 サービスの実測（本作業の指示書）
- 関連 issue: [#613](https://github.com/endazon/ai-stock-trading/issues/613)（第4弾。3 サービスの移送を扱う。
  `Domain/` 欠けの是正は別 PR に分離する——IADR-0289 フォローアップ 3）

## 目的・背景

前掲の作業仕様書（第1弾）が確定した移送規則（IADR-0289）を、HTTP 端点を持つ残り 5 サービスのうち
3 サービス（`AuditService` / `ConfigurationService` / `CostControlService`）へ適用する。3 サービスは
いずれも登録表（`*Endpoints.cs`）の中に処理本体（ラムダ）が書かれており、既存ファイルの `git mv` だけで
3 段目へ下ろせるのは `CostControlService` の `MonthlyCostUsage.cs` 1 件のみである（実測は前掲仕様書）。

## 対象範囲

- 対象:
  - `AuditService`: `Features/AuditEvents/` を 3 操作へ 3 段化
  - `ConfigurationService`: `Features/Assumptions/` を 3 操作へ 3 段化
  - `CostControlService`: `Features/CostControl/` を 4 操作へ 3 段化（`MonthlyCostUsage.cs` を `GetCostUsage/` へ同居）
  - 3 サービスの `Tests/` を本体の樹形の鏡写しへ再配置
- 対象外:
  - `Domain/` を持たない 3 サービスの是正（別 PR。IADR-0289 フォローアップ 3）
  - 2 段目（集約）の切り直し
  - `Hosted/`／`Infrastructure/` の移動
  - 振る舞い・公開面（ルート・認可・応答形）・DI 登録・wire 契約の変更

## 設計

移送規則は [IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) 決定 1〜6 をそのまま適用する
（本 PR では新たな決定を追加しない）。

### AuditService（`Features/AuditEvents/` 3 操作）

| # | 操作フォルダ | 端点 | 認可 |
| --- | --- | --- | --- |
| 1 | `GetAuditEventsByCorrelation` | GET `/audit/events/{correlationId:guid}` | OwnerOnly |
| 2 | `GetRecentAuditEvents` | GET `/audit/events` | OwnerOnly |
| 3 | `GetAuditEventsByType` | GET `/audit/events/by-type` | OwnerOrService |

3 操作とも既存の独立ファイルは無く、`AuditQueryEndpoints.cs` のラムダを `Endpoint.cs` へ切り出した
（IADR-0289 決定 3）。`DefaultLimit`/`MaxLimit` 定数は `GetRecentAuditEvents` 専属のため同居させた。
`IAuditEventStore` / `AuditEntry` / `AuditEntryFactory` / `AuditCorrelation` / `AuditSerialization` は
複数操作・`Infrastructure/Steps/AuditEventHandlers.cs` から使われるため 2 段目に残す
（前掲仕様書の実測どおり）。

### ConfigurationService（`Features/Assumptions/` 3 操作）

| # | 操作フォルダ | 端点 | 認可 |
| --- | --- | --- | --- |
| 1 | `GetAssumptions` | GET `/assumptions` | OwnerOrService |
| 2 | `GetAssumptionsHistory` | GET `/assumptions/history` | OwnerOnly |
| 3 | `UpdateAssumptions` | PUT `/assumptions`（`AssumptionsChanged` 発行） | OwnerOnly |

`UpdateAssumptionsRequest`・`ActorOf` は `UpdateAssumptions` 専属のため同居させた。`AssumptionsService`・
`IAssumptionsStore`・`IAssumptionsChangeLog`・`AssumptionsChangeEntry` は 3 操作すべて・Persistence 実装が
使うため 2 段目に残す。

### CostControlService（`Features/CostControl/` 4 操作）

| # | 操作フォルダ | 端点 | 認可 | 同居 |
| --- | --- | --- | --- | --- |
| 1 | `RecordCost` | POST `/costs/record`（`CostThresholdReached` 発行） | OwnerOnly | `RecordCostRequest` |
| 2 | `GetCostState` | GET `/costs/state` | OwnerOrService | （なし） |
| 3 | `GetCostReview` | GET `/costs/review` | OwnerOrService | （なし） |
| 4 | `GetCostUsage` | GET `/costs/usage` | OwnerOrService | `MonthlyCostUsage.cs`（`git mv`） |

`CostControlAppService`（4 操作＋`Infrastructure/Steps/LlmCostIncurredHandler.cs` が使用）と
`RecordCostResult`（戻り値を同ハンドラが読む）は 2 段目に残す（前掲仕様書の実測どおり）。

🔴 **`MonthlyCostUsage` は `GetCostUsage` の名前空間（3 段目）へ移すため、2 段目の
`CostControlAppService.GetUsageAsync` から見るには `using` が要る。** IADR-0289 決定 4 の
「3 段目は 2 段目の入れ子」は 3 段目から 2 段目への解決を無条件化するものであり、逆方向
（2 段目が 3 段目を参照する）はこの限りではない。本件は
「`MonthlyCostUsage.cs` を `GetCostUsage` に同居させる」という前掲仕様書の割り当て表の指示を
優先し、`CostControlAppService.cs` に 1 行 `using` を追加した。

### Tests の鏡写し（3 サービス共通の判断）

- 単一操作に閉じたテスト（該当なし。3 サービスとも `*EndpointsTests.cs` は複数操作を横断して検証する）は
  当該操作フォルダへ、複数操作・グループ全体を検証するテストは `Tests/Features/<集約>/` （2 段目相当）へ
  置く。これは第1弾の `RiskControlEndpointsTests.cs` / `StageGateEndpointsTests.cs` の置き場（2 段目）と
  同じ判断である。
- `InMemory<Store>` を直接使う「ストア契約」テスト（例: `AuditEventStorePeriodQueryTests.cs`）は、
  **被テスト型（`InMemoryAuditEventStore`）の置き場**に従い `Tests/Infrastructure/Persistence/` へ置いた
  （型がある場所を機械的に辿れることを優先し、テストの意図での分類はしない）。
- Program.cs 配線テスト（`HealthEndpointTests` / `IntrospectionEndpointTests` / `CostControlWiringTests`）と
  テスト土台（`*WorkerWebApplicationFactory` / `TestAuthHandler` / `AssumptionsTestDoubles` の一部）は
  `Tests/` 直下に残す。ただし `AssumptionsTestDoubles.cs`（CostControlService）は
  `CachedAssumptionsProviderTests.cs` 専属の利用であったため `Tests/Infrastructure/ExternalServices/` へ
  移した（土台でも参照元が 1 箇所に閉じるものは被参照側へ寄せる）。

## 受け入れ基準

- [x] `Features/AuditEvents/`（3）・`Features/Assumptions/`（3）・`Features/CostControl/`（4）に
      操作ディレクトリが生成され、各操作が `Endpoint.cs` を持つ
- [x] `Tests/` が `Features/` ／ `Infrastructure/` ／ `Hosted/` ／ `Domain/` の鏡写しになっている
      （プロジェクトは 1 本のまま）
- [x] `dotnet build backend/backend.slnx` が警告ゼロで成功する
- [x] `dotnet test backend/backend.slnx --filter "Category!=Integration"` の件数が移送前と一致する
- [x] `AiStockTrading.Architecture.Tests` が緑で、件数が移送前と一致する
- [x] `dotnet format backend/backend.slnx --verify-no-changes` が緑
- [x] `node scripts/check-trace-blocks.js` / `check-doc-links.js` / `check-adr-index-sync.js` が緑
      （`check-test-traceability.js` は Windows ローカル限定の既知の偽陽性 [T1] のみ。第1弾と同じ事象で
      本移送の前後で変わらない）
- [x] ルート・認可ポリシー・応答形・`Program.cs` の DI 登録・wire 契約に差分が無い

## テスト方針

**新しいテストは書かない。** 純粋な移送であり、既存テストが退行検知の手段である（IADR-0289 と同じ方針）。

### 実測（移送前・`develop` `b8367987`）

| アセンブリ | 件数 |
| --- | ---: |
| `AuditService.Tests` | 116 |
| `ConfigurationService.Tests` | 18 |
| `CostControlService.Tests` | 123 |
| `AiStockTrading.Architecture.Tests` | 87 |
| 全アセンブリ合計（`Category!=Integration`） | 5444 |

### 移送後の実測

| アセンブリ | 移送前 | 移送後 |
| --- | ---: | ---: |
| `AuditService.Tests` | 116 | **116** |
| `ConfigurationService.Tests` | 18 | **18** |
| `CostControlService.Tests` | 123 | **123** |
| `AiStockTrading.Architecture.Tests` | 87 | **87** |
| 全アセンブリ合計（`Category!=Integration`） | 5444 | **5444** |

- `dotnet build backend/backend.slnx`: 成功・警告 0・エラー 0
- `dotnet format backend/backend.slnx --verify-no-changes`: 差分なし
- `node scripts/check-trace-blocks.js` / `check-doc-links.js`: OK
- `node scripts/check-adr-index-sync.js`: skip（浅いクローンで検査範囲を決められない。第1弾と同じ事象）
- `node scripts/check-test-traceability.js`: [T1] のみ（Windows ローカル限定の既知の偽陽性。
  第1弾の仕様書が記録した事象と同一原因——`fs.existsSync(<Svc>/tests)` の大文字小文字非区別）

## 計画書との差異

- 差異: なし。[IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) の決定 1〜6 をそのまま適用する。
