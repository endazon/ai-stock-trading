---
title: #613 第4弾（続）—— Domain 欠け 3 サービスの是正（AuditService・NotificationService）
type: spec
status: draft
related_ids:
  - NFR
  - IADR-0289
  - IADR-0128
  - IADR-0256
  - MSP:ADR-0030
author: endazon (with Claude Code)
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md
---

# 仕様書: #613 第4弾（続）—— Domain 欠け 3 サービスの是正

> 本仕様書は実装着手前に作成する。計画書を一次情報とし、本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 起点 ID: `NFR`（無採番。[IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) と同じ判断）
- 関連 IADR: [IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) フォローアップ3
  （`Domain/` 欠け 3 サービスの是正・独立 PR で扱うと決めた箇所）・
  [IADR-0128](../adr/IADR-0128_standard-project-layout.md)（決定6・Domain 層は外部ライブラリへ依存しない）・
  [IADR-0256](../adr/IADR-0256_domain-dependency-inspection-by-source-scan.md)（Domain 依存規律のソース走査）
- 関連仕様書: [20260903_613_vsa-three-tier-risk-management](20260903_613_vsa-three-tier-risk-management.md)
  §`Domain/` を持たない 3 サービスの実測
- 関連 issue: [#613](https://github.com/endazon/ai-stock-trading/issues/613)

## 目的・背景

前掲の作業仕様書（第1弾）の実測により、`Domain/` を持たない 3 サービス（Audit / Configuration /
Notification）のうち、`ConfigurationService` は真に Domain 不在（業務規則の正本が
`AiStockTrading.Shared.Kernel.Trading` にある）だが、`AuditService` と `NotificationService` は
**外部依存ゼロの業務規則型が `Features/` に紛れている分類漏れ**と判定された。本作業はこの 2 サービスを
是正する。

## 対象範囲

- 対象:
  - `AuditService`: `AuditEntry` / `AuditCorrelation` / `AuditEntryFactory` を `Domain/` へ移送
  - `NotificationService`: `DiscordCommandAuthorizer` / `KillSwitchConfirmation` /
    `VersionedConfirmationGuard` / `BotCommandParser` / `BotCommand` / `DiscordCommandContext` を
    `Domain/` へ移送
  - `Tests/` の対応する鏡写し（`Tests/Domain/`）
  - `ConfigurationService` は動かさない（不在の理由を本書に記録する）
- 対象外:
  - #613 第4弾（PR A）の 3 段化移送（別 PR・独立ブランチ）
  - 振る舞い・公開面・DI 登録・wire 契約の変更

## 設計

### AuditService

`AuditEntry.cs` / `AuditCorrelation.cs` / `AuditEntryFactory.cs` は前掲仕様書の実測どおり
外部依存ゼロの純型・純関数群である。`git mv` で `Domain/` へ移し、名前空間を
`AuditService.Features.AuditEvents` → `AuditService.Domain` へ変更した。

🔴 **`AuditSerialization.cs` も同時に `Domain/` へ移した（指示書の3ファイルに含まれない追加）。**
理由: `AuditEntryFactory.From(...)` は `AuditSerialization.Serialize(e)` を呼ぶ。`AuditSerialization`
自体も `System.Text.Json` と `AiStockTrading.Shared.Contracts.Events`（`AuditDetailJson.Options`）だけに
依存する外部ライブラリ非依存の型であり、唯一の参照元が `AuditEntryFactory` である。**`AiStockTrading
.Architecture.Tests` の `DomainSourceDependencyTests.Domain_の_using_は許可された名前空間だけである`
が実測で検出した**——Domain 層は許可リスト外の namespace（自サービスの `Features.*`）を `using` できない
（platform ADR-0030 §基本方針・IADR-0128 決定6）。`AuditSerialization` を `Features/AuditEvents/` に
残したまま `AuditEntryFactory` から参照すると、この規律に違反する。移した後の `AuditEntryFactory` は
`Domain` 内の同一名前空間から `using` なしで見える。

参照側（`IAuditEventStore.cs` / `EfAuditEventStore.cs` / `InMemoryAuditEventStore.cs` /
`Infrastructure/Steps/AuditEventHandlers.cs`・複数テスト）は `using AuditService.Domain;` を追加した。

### NotificationService

6 ファイルはいずれも外部依存ゼロの業務規則型（純関数・純データ型・排他ロック機構）であり、前掲仕様書の
実測どおり `git mv` で `Domain/` へ移した。

🔴 **`DiscordBotOptions.cs` も同時に `Domain/` へ移した（指示書の6ファイルに含まれない追加）。**
理由: `DiscordCommandAuthorizer.Authorize(...)` と `KillSwitchConfirmation.Verify(...)` はいずれも
`DiscordBotOptions` を引数に取る。`DiscordBotOptions` は bool/string/`IList<string>`/
`IDictionary<string,string>` のプロパティだけを持つ設定 POCO で外部ライブラリ依存が無く、
[IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) 自身の実測でも
「`DiscordBotOptions.cs` も『安全既定はすべて拒否側』という規則を型で表している」と分類漏れの
候補として言及されていた。`AiStockTrading.Architecture.Tests` の `DomainSourceDependencyTests` が
`AuditSerialization` と同じ理由（Domain → 自サービス `Features.*` の `using` 禁止）で違反を検出したため、
一緒に移した。`Program.cs`・`Infrastructure/ExternalServices/*.cs`（`DiscordBotOptionsReader` /
`DiscordBotGatewayFactory` / `DiscordNetBotGateway`）・`Features/Notifications/*CommandHandler.cs`
（5 ハンドラ）・複数テストへ `using NotificationService.Domain;` を追加した。

🔴 **コメント文言の是正**: `DiscordCommandContext.cs` の元コメントが「Discord.Net の型を…」と
ライブラリ名を実名（ドット区切り）で書いており、`DomainSourceDependencyTests` の検査 (c)
（完全修飾での迂回を塞ぐソース全文走査。コメントも対象）が `Discord.` を禁止トークンとして誤検出した
（`Directory.Packages.props` の `Discord.Net.WebSocket` 等から導出したトークン）。**実害はなく
（本ファイルは Discord.Net の型を実際には import していない）**、コメントの言い回しを
「Discord 連携の クライアント実装」へ変更し、`ドット区切りのライブラリ名を文中に書かない」形に
是正した。意味は変えていない。

### ConfigurationService（動かさない理由）

前掲仕様書の実測のとおり、業務規則の正本は `AiStockTrading.Shared.Kernel.Trading`
（`TradingAssumptions` / `VersionedAssumptions` / `TradingAssumptionsDefaults`）にあり、
`AssumptionsService.cs` / `IAssumptionsStore.cs` はそれを `using` するだけである。サービス固有なのは
「単一行＋Version の楽観排他」と「追記専用の履歴」という**永続化の関心事**であり、外部依存ゼロの純型は
`AssumptionsChangeEntry.cs` 1 本のみである。`Domain/` を新設しても中身が 1 レコードにしかならないため、
**真に Domain 不在**と判断し、動かさない。

## 受け入れ基準

- [x] `AuditService/Domain/` に `AuditEntry.cs` / `AuditCorrelation.cs` / `AuditEntryFactory.cs` /
      `AuditSerialization.cs` が置かれる
- [x] `NotificationService/Domain/` に `DiscordCommandAuthorizer.cs` / `KillSwitchConfirmation.cs` /
      `VersionedConfirmationGuard.cs` / `BotCommandParser.cs` / `BotCommand.cs` /
      `DiscordCommandContext.cs` / `DiscordBotOptions.cs` が置かれる
- [x] `ConfigurationService` は不在のまま（本書に理由を記録）
- [x] `Tests/Domain/` に対応するテストが鏡写しで置かれる（テストの名前空間は `<Svc>.Tests` のまま）
- [x] `dotnet build backend/backend.slnx` が警告ゼロで成功する
- [x] `dotnet test backend/backend.slnx --filter "Category!=Integration"` の件数が移送前と一致する
      （`AuditService.Tests` 116/116・`NotificationService.Tests` 398/398）
- [x] `AiStockTrading.Architecture.Tests` の `DomainSourceDependencyTests` が緑（87/87・移送前と一致）で、
      走査対象ファイル数の下限（`MinimumDomainSourceFiles`=100）を引き続き満たす
- [x] `dotnet format backend/backend.slnx --verify-no-changes` が緑
- [x] `node scripts/check-trace-blocks.js` / `check-doc-links.js` が緑
- [x] ルート・認可ポリシー・応答形・`Program.cs` の DI 登録・wire 契約に差分が無い

## テスト方針

**新しいテストは書かない。** 純粋な移送であり、既存テストが退行検知の手段である。

### 実測（移送前・`develop`）

| アセンブリ | 件数 |
| --- | ---: |
| `AuditService.Tests` | 116 |
| `NotificationService.Tests` | 398 |
| `AiStockTrading.Architecture.Tests` | 87 |

### 移送後の実測

| アセンブリ | 移送前 | 移送後 |
| --- | ---: | ---: |
| `AuditService.Tests` | 116 | **116** |
| `NotificationService.Tests` | 398 | **398** |
| `AiStockTrading.Architecture.Tests` | 87 | **87** |

- `dotnet build backend/backend.slnx`: 成功・警告 0・エラー 0
- `dotnet format backend/backend.slnx --verify-no-changes`: 差分なし
- `node scripts/check-trace-blocks.js` / `check-doc-links.js`: OK
- 🔴 **既知の無関係な事前失敗（本 PR の変更対象外）**: `origin/develop`（フロントエンド再編 PR #653）の
  時点で `MarketMonitorService.Tests`（2 件）・`RiskManagementService.Tests`（5 件）の
  `*ContractFixtureTests` が契約フィクスチャ JSON の配置パス変更により `FileNotFoundException` で
  失敗する。**本 PR の変更を一切含まない `origin/develop` 単体でも同一の失敗が再現する**ことを
  個別プロジェクトの `dotnet test` で確認済み（実測: PR B のブランチ差分適用前後で失敗内容・件数が
  完全に同一）。#613 の対象外であり、本 PR では是正しない。

## 計画書との差異

- 差異: 前掲仕様書の指示書が挙げた対象（Audit 3 ファイル・Notification 6 ファイル）に加え、
  `AuditSerialization.cs`・`DiscordBotOptions.cs` を追加で `Domain/` へ移した。理由は上記
  §設計 のとおり、`AiStockTrading.Architecture.Tests` の `DomainSourceDependencyTests` が実測で
  検出した「Domain → 自サービス `Features.*` の `using` 禁止」（platform ADR-0030 §基本方針・
  IADR-0128 決定6）に抵触するためである。両ファイルとも外部ライブラリ依存が無く、
  [IADR-0289](../adr/IADR-0289_three-tier-slice-transfer-rules.md) 自身が
  `DiscordBotOptions.cs` を分類漏れの候補として言及していたことと整合する。
