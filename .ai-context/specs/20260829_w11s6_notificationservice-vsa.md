---
title: NotificationService を単一プロジェクト＋VSA 樹形へ移送する（W11 段 4-6）
type: spec
status: approved
related_ids: [NFR, IADR-0259, IADR-0263, IADR-0264, IADR-0265]
author: endazon (with Claude Code)
created: 2026-08-29
updated: 2026-08-29
plan_refs: []
---

# 仕様書: NotificationService の単一プロジェクト＋VSA 移送（W11 段 4-6）

> **11 サービス移送波の 6 本目**である。1 本目（AuditService・
> [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)）・
> 2 本目（ConfigurationService・[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md)）・
> 3 本目（CostControlService・[20260829_w11s4c](20260829_w11s4c_costcontrolservice-vsa.md)）・
> 4 本目（BacktestService・[20260829_w11s4d](20260829_w11s4d_backtestservice-vsa.md)）・
> 5 本目（MarketMonitorService・隣接作業ツリー `/home/user/wt/w11s5` 読み取り専用。
> `.ai-context/specs/20260829_w11s5_marketmonitorservice-vsa.md`。**着手時点で develop へ
> マージ済み**〔#590・後述「母集合の引き直し」参照〕）で確定した判断の型をそのまま適用する。
> **新しい判断軸は生じなかった。**NotificationService は AuditService（1 本目）と同型
> （**`Domain/` を持たない・集約は 1 つ**）であり、AuditService の決定 1〜5 をそのまま適用した。

## 起点

- 起点 ID: **`NFR`（無採番）**。構造移送＝メタ作業であり、`.claude/rules/traceability.md`
  「起点 ID の種別」の無採番許容ケース **2** に当たる（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)
  が確定済みの判断を継承する。環流はしない）。
- 上流: [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)（1 本目の 5 決定。
  **`Domain/` を持たないサービスの型**）・[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md)・
  [IADR-0265](../adr/IADR-0265_domain-project-count-checker-dynamic-lower-bound.md)（検査の下限の動的化）・
  [IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md)（樹形・写像方針表）

## 着手前に読んだもの

- `CLAUDE.md` / `.claude/rules/traceability.md` / `.claude/rules/traceability.repo.md` /
  `docs/DEFINITION_OF_DONE.md`
- [IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) /
  [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) /
  [IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) /
  [IADR-0265](../adr/IADR-0265_domain-project-count-checker-dynamic-lower-bound.md) /
  [IADR-0258](../adr/IADR-0258_structure-aware-checkers-dual-layout.md)
- [20260829_w11s4a](20260829_w11s4a_auditservice-vsa.md)（**同型・最重要**）/
  [20260829_w11s4b](20260829_w11s4b_configurationservice-vsa.md) /
  [20260829_w11s4c](20260829_w11s4c_costcontrolservice-vsa.md) /
  [20260829_w11s4d](20260829_w11s4d_backtestservice-vsa.md)（手順・落とし穴の申し送り）
- **隣接作業ツリー `/home/user/wt/w11s5`（読み取り専用・5 本目 MarketMonitorService）**の
  `.ai-context/specs/20260829_w11s5_marketmonitorservice-vsa.md`（直前の申し送り。
  空ディレクトリの偽陽性・パイプでの終了コード隠蔽・IADR リンク是正など）
- 基盤の実物（読み取り専用）: `/home/user/microservices-platform/src/platform/backend/Services/NotificationService/`
  （**同名サービスの参照実装**。`Features/Notifications/` の集約名・`Common/Observability/` `Common/Options/`
  の実例を確認したが、本サービスには対応物が無いため後述判断で不採用とした）

## 対象範囲

- 対象: `backend/Services/NotificationService/`（6 プロジェクト → 2 プロジェクトへ統合）、
  `backend/backend.slnx`、`docker-compose.yml`、`scripts/k8s-local-images.sh`、
  `docs/security/security.md`（旧パスの是正 1 箇所）
- 対象外: 他サービス（次の PR 以降）、`backend/Shared/` `backend/TestSupport/`（据え置き集合）、
  `.ai-context/adr/IADR-0198_fx-expired-visibility.md` ・`.ai-context/adr/README.md`
  （凍結記録・当時の引用。後述「母集合の引き直し」参照）、
  `.ai-context/specs/`（point-in-time の記録。同上）

## 着手前の母集合の引き直し（`.claude/rules/traceability.repo.md` 規則1〜10）

**母集合は記憶で挙げず、誤りになる側の文字列で全追跡ファイルを走査して引いた**（規則1・2・9・10）。
走査した語は `NotificationService\.(Api|Application|Infrastructure)\b` /
`NotificationService/(src|tests)` の 2 本 ＋ `NotificationService` 単体（設定・パス文脈）。

| 項目 | 実測 |
| --- | --- |
| 移送前の .cs（src + tests） | **68**（src 39・tests 29。内訳: Program.cs 1・Application/Ports 7・Application/Services 10・Application/State 4・Infrastructure/Composable/Adapters 15・Infrastructure/Composable/Steps 2・Api.Tests 2・Application.Tests 13・Infrastructure.Tests 14） |
| 移送前の csproj | **6**（src 3・tests 3） |
| migration | **0 本**（`find ... -iname "*migration*"` 0 件） |
| `DbContext` | **0 件**（`grep -rl DbContext` 0 件。`dotnet ef migrations has-pending-model-changes` は
  「`Microsoft.EntityFrameworkCore.Design` を参照していない」で exit 1・対象外の実測証跡） |
| `BackgroundService`（リテラル型） | **0 件**。`DiscordBotHostedService` は `IHostedService` を**直接実装**
  （`: BackgroundService` ではない）——`Hosted/` は作らない（後述判断5） |
| Wolverine ハンドラ | `NotificationHandlers.cs`（4 型: `OrderExecutedNotificationHandler` /
  `OrderRejectedNotificationHandler` / `StopLossTriggeredNotificationHandler` /
  `AssumptionsChangedNotificationHandler`） → `Infrastructure/Steps/` |
| `internal` 型のうち Tests が直接参照するもの | **12 型 + 1 メンバ**（後述「`internal`→`public` の判断」） |
| `list-test-projects.js --count` | **移送前 40**（クリーンな作業ツリーで実測。タスク文の「37」は
  前提の実測値が古い——本ブランチの base 時点で既に AuditService/ConfigurationService/CostControlService/
  BacktestService の 4 本が移送済みであるため、無採番の初期値からの差分が積み上がっている。
  🔴 **実際には develop が MarketMonitorService（#590）の移送も既に取り込んでおり、本ブランチの
  base（5ec764a）だけがそれより 1 世代古い**——後述「想定外」1 参照） |
| `NotificationService` を参照する他サービス・横断テストの `ProjectReference` / `extern alias` | **0 件**
  （`backend/Services/*/*.csproj` の全文走査・`AiStockTrading.IntegrationTests.csproj` の
  `extern alias` は `RiskManagementWorker` / `ReportWorker` / `CostControlWorker` の 3 つのみで
  Notification は該当しない。実測で確認済み） |
| `deploy/helm/.../pipeline.json` の NotificationService 関連 consumer 参照 | 0 件（対象外） |
| `docker-compose.yml` / `scripts/k8s-local-images.sh` の build args | 各 1 箇所（`SERVICE_PROJECT` /
  `SERVICE_DLL`。両方とも本 PR で追随した） |
| `docs/` 配下の NotificationService パス参照 | **1 件**（`docs/security/security.md`。
  `DiscordCommandAuthorizer` の実装ファイルパスをインラインコードで引用。**trace ブロック規約の対象外**
  ——計画 ID/IADR/仕様書名ではなく実装ファイルパスの言及であるため。旧パスを新パスへ是正した） |
| `.ai-context/adr/` 配下の同パターン参照 | **2 件**（`README.md` の IADR-0198 索引要約の引用文・
  `IADR-0198_fx-expired-visibility.md` 本文。**いずれも「`NotificationService.Api/Program.cs` が
  当時こう書いていた」という凍結された事実の引用**であり、書き換えると当時の記述と食い違う。
  据え置いた——[IADR-0261](../adr/IADR-0261_namespace-alignment-to-platform.md) が同種の
  「移設の由来を述べるコメント」を書き換えなかったのと同じ判断） |
| `.ai-context/specs/` 配下の同パターン参照 | 実測 **5 件**（5 ファイル）。**いずれも point-in-time の記録**
  （`.claude/rules/traceability.repo.md` 除外規定）であり未更新。内訳: `20260808_466_stage-promote-warning-and-audit.md`・
  `20260803_354_wolverine-migration.md`・`20260828_335_347_llm-allocation-and-cost-governance.md`・
  `20260720_223_killswitch-disengage-phrase.md`・`20260718_165_stage-gate-discord-bot.md`（当時の
  実装パス・クラス名の記述） |
| `Tests/NotificationConsumerCoverageTests.cs` 本文コメント内の旧パス言及 | **1 件**（`NotificationService.Api/Program.cs`
  が「10 種」と書いていた、という**移送対象のソースファイル自身に残る歴史的コメント**）。
  [IADR-0261](../adr/IADR-0261_namespace-alignment-to-platform.md) が「移設の由来を述べるコメントは
  書き換えない」とした判断をそのまま適用し、**据え置いた**（当時 `NotificationService.Api/Program.cs` に
  書かれていた事実の引用であり、新パスへ直すと事実に反する） |
| コード本文中の部分修飾名（`grep -rnE '\b(Application|Api|Infrastructure|Domain)\.[A-Z]'`。
  `namespace`/`using` 宣言行を除く） | **0 件**（全 Tests ファイルが明示的な `using` を持ち、
  暗黙の親名前空間解決に依存していなかった。4・5 本目が踏んだ「using 欠落」「完全修飾名の部分参照」の
  いずれも発生しなかった） |

### 想定外の発見

1. 🔴 **origin/develop が本ブランチの base（5ec764a）より 1 世代進んでいた。** 着手後の検査で
   `git log HEAD..origin/develop` に 5 本目 MarketMonitorService の移送コミット（`35b330a`・#590）が
   見つかった。本 PR の作業自体（NotificationService のファイル移送）とは領域が重ならないため
   実装への影響は無いが、`scripts/check-adr-index-sync.js`（既定の走査範囲 `origin/develop..HEAD`）が
   「IADR-0090 の索引行が古い」と誤検出した——実際には**5 本目が既に是正済み**であり、本ブランチが
   その是正を含まない古い base 上にあることが原因である。**対処**: コミット後に
   `git rebase origin/develop` で base を最新へ揃える（後述「検証」節で再検査した結果を記録する）。
   同じ事象は残り 5 本でも起こり得る——**着手前に `git log HEAD..origin/develop --oneline` を必ず
   確認すること**を申し送りに追加する。

## 目標樹形（実施結果）

```
backend/Services/NotificationService/
├── NotificationService.csproj
├── Program.cs
├── appsettings.json / appsettings.Development.json
├── Features/Notifications/        (21 ファイル: Ports 7 + Services 10 + State 4)
├── Infrastructure/
│   ├── ExternalServices/          (15 ファイル: 旧 Composable/Adapters)
│   └── Steps/                     (2 ファイル: 旧 Composable/Steps。DiscordBotHostedService・NotificationHandlers)
└── Tests/
    └── NotificationService.Tests.csproj  (29 ファイル)
```

`Domain/` `Common/` `Hosted/` `Infrastructure/Persistence/` `Infrastructure/Messaging/` は
**実体が無いため作らなかった**（母集合の実測どおり）。

## 設計（判断とその理由）

### 判断1: 集約は 1 つ（`Notifications`）とし、`_Shared/` は作らない
（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定1 の適用。新しい判断ではない）

Discord への一方向通知（イベント購読 → 整形 → 送信）と Discord Bot 経由の双方向操作（kill switch・
一時停止・段階ゲート・GFV 解除・報告書レビュー）は、いずれも `IDiscordBotGateway` を介した単一の
窓口に属する不可分な概念であり、操作フォルダの兄弟を作る決定（3 段目のスライス分割）は採らない
（[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) 決定1）。したがって集約は 1 つとし、
`Features/Notifications/` 直下に平らに置いた。

**集約名は「サービス名から `Service` を落とす」機械的規則（`CostControl`・`Backtest`・`MarketMonitor`）
ではなく `Notifications`（複数形）とした。** 理由は 2 点:

1. **本サービス自身の構成キーが複数形である**（`Notifications:Provider` / `Notifications:Discord:Bot`
   ―― `DiscordBotOptions.SectionName = "Notifications:Discord:Bot"`）。
2. **基盤（MSP）の同名サービスの参照実装が `Features/Notifications/`（複数形）である**——本サービスは
   AuditService や CostControlService と異なり**基盤に文字どおり同名の対応物を持つ**唯一のケースであり、
   参照実装の名前へ揃えることが最も強い先例である。

型のクラス名接頭辞は単数形（`NotificationFormatter` 等）だが、**フォルダ／集約名と型の命名規則は
独立**であり矛盾しない（AuditService の集約名 `AuditEvents` も型名 `AuditEntry` と単数/複数が
一致していない先例と同型）。

### 判断2: `Domain/` が無い制約下で、Application 層（Ports/Services/State）は `Features/Notifications/` 直下へ
（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定2 の適用。新しい判断ではない）

NotificationService は現状 `Domain/` を持たない。写像方針表の既定（「複数から使う業務ルールは
`Domain/Services/`」）は「Domain を持つ、または持つべきだと別途判断された」場合にのみ適用するため
（同決定2）、**フォルダ移送そのものを理由に `Domain/` を新設しない**。旧 `Application/{Ports,Services,State}`
の 21 ファイルは移送で層を変えず、そのまま `Features/Notifications/` 直下へ平らに置いた。

| 元のプロジェクト | 型（代表） | 置き場 |
| --- | --- | --- |
| `Application/Ports/`（7） | `IDiscordBotGateway` / `IKillSwitchController` / `IPauseController` /
  `IStageGateController` / `IGoodFaithViolationController` / `IReportReviewController` /
  `INotificationSender` | `Features/Notifications/` |
| `Application/Services/`（10） | `BotCommandParser` / `DiscordCommandAuthorizer` /
  `KillSwitchCommandHandler` / `PauseCommandHandler` / `StageGateCommandHandler` /
  `GoodFaithViolationCommandHandler` / `ReportCommandHandler` / `NotificationFormatter` /
  `KillSwitchConfirmation` / `VersionedConfirmationGuard` | `Features/Notifications/` |
| `Application/State/`（4） | `BotCommand` / `DiscordBotOptions` / `DiscordCommandContext` /
  `NotificationMessage` | `Features/Notifications/` |

### 判断3: `Infrastructure/Composable/Adapters/` の 15 ファイルは、内容に関わらず一括で `Infrastructure/ExternalServices/` へ
（BacktestService「判断3」（`BarDataOptions.cs` の扱い）と同型の適用。新しい判断ではない）

BacktestService の移送（[20260829_w11s4d](20260829_w11s4d_backtestservice-vsa.md) 判断3）は、
「構成 DTO」（`BarDataOptions.cs`）であっても**元が `Infrastructure/Composable/Adapters/` にあった
以上は Infrastructure へ**という判断を確立している（**「移送で型の層を変えない」を Application/
Infrastructure の区分にも適用する**）。本サービスも同じ判断を適用し、`DiscordBotOptionsReader.cs`
（`IConfiguration` から `DiscordBotOptions` を読む静的クラス。それ自体は I/O を持たない）を含む
15 ファイルすべてを内容の技術的性質で個別判定せず、**元のプロジェクト（Infrastructure）をそのまま
引き継いだ**。

内訳（すべて `Infrastructure/ExternalServices/`）: `DiscordBotGatewayFactory` / `DiscordBotOptionsReader` /
`DiscordNetBotGateway` / `DiscordOwnerAuthExtensions` / `DiscordWebhookHttpClientExtensions` /
`DiscordWebhookNotificationSender` / `HttpGoodFaithViolationController` / `HttpKillSwitchController` /
`HttpPauseController` / `HttpReportReviewController` / `HttpStageGateController` /
`LoggingNotificationSender` / `NotificationSenderFactory` / `NullDiscordBotGateway` /
`RedactedUriHttpClientLogger`。

`IClock` / `SystemClock` のような**集約を跨ぐ技術プリミティブは本サービスに存在しない**ため
（`grep -rl "IClock\|SystemClock\|TimeProvider"` 0 件）、`Common/Abstractions/` は作らなかった
（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定3 は「該当する型が
あれば」の適用であり、無ければ作らない——決定2 と同じ「実体の無い区分を先回りしない」作法）。

### 判断4: Wolverine ハンドラは `Infrastructure/Steps/`
（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定5 の適用。新しい判断ではない）

`NotificationHandlers.cs`（4 型の購読専用ハンドラ）は、名前空間が既に `NotificationService.Infrastructure.Steps`
の形であり（[IADR-0261](../adr/IADR-0261_namespace-alignment-to-platform.md) により先行整合済み）、
フォルダを合わせるだけで済んだ。

### 判断5: `DiscordBotHostedService` は `Hosted/` へ動かさず `Infrastructure/Steps/` に留める（本サービス固有の判断）

[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) 決定1 は `Hosted/` を
「🔴 AST 固有: **`BackgroundService`**（ルート直下）」と定義しており、対象は**リテラルに
`BackgroundService` を継承する型**である。`DiscordBotHostedService` は `IHostedService` を
**直接実装**しており（`StartAsync`/`StopAsync` を自前で書く形。`ExecuteAsync` のオーバーライドを
持つ `BackgroundService` 派生ではない）、タスク文の実測前提（「BackgroundService 0 件」）とも
整合する。

加えて、**名前空間が移送前から既に `NotificationService.Infrastructure.Steps`**
（[IADR-0261](../adr/IADR-0261_namespace-alignment-to-platform.md) の名前空間整合波で
`NotificationHandlers.cs` と同じフォルダ `Composable/Steps/` にあったため同じ変換規則が
適用された）——「移送では元の層に対応するフォルダへ素直に置く」という 2〜5 本目共通の作法
（[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定3 の
🔴 注記）をここにも適用し、`Hosted/` へ新たに動かさず `Infrastructure/Steps/` へ置いた。
**`Hosted/` フォルダは作らない**（実体が無い区分を先回りしないという 1〜5 本目共通の作法）。

### 判断6: `internal` → `public` は「Tests が直接参照する型・メンバー」に限る
（[IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md) 決定4 の適用）

移送前から `internal` だった 15 型のうち、Tests から**コンストラクタ呼び出し・`typeof`/ジェネリック
型引数・静的メンバー呼び出し**で直接参照されていたものだけを `public` にした（DI 経由のインターフェース
越しの解決は対象外）。**先に `grep` で Tests からの直接参照を型ごとに洗い出してから**可視性を変えた
（1 本目の申し送り2 を踏襲）。

| 型 | 直接参照の根拠 |
| --- | --- |
| `DiscordBotGatewayFactory` | `DiscordBotGatewayFactoryTests.cs` / `SecretRedactionTests.cs` が
  `DiscordBotGatewayFactory.Create(...)` を静的呼び出し |
| `DiscordBotOptionsReader` | `DiscordBotOptionsReaderTests.cs` が `DiscordBotOptionsReader.Read(...)` を
  静的呼び出し |
| `DiscordNetBotGateway` | `DiscordBotGatewayFactoryTests.cs` が `.Should().BeOfType<DiscordNetBotGateway>()` |
| `DiscordWebhookHttpClientExtensions` | `DiscordWebhookHttpClientTests.cs` が
  `DiscordWebhookHttpClientExtensions.ClientName` を静的参照 |
| `DiscordWebhookNotificationSender` | `SecretRedactionTests.cs` / `DiscordWebhookHttpClientTests.cs` /
  `NotificationSenderFactoryTests.cs` / `DiscordWebhookNotificationSenderTests.cs` が
  `new DiscordWebhookNotificationSender(...)` |
| `HttpKillSwitchController` | `HttpKillSwitchControllerTests.cs` が `new HttpKillSwitchController(...)` /
  `NullLogger<HttpKillSwitchController>` |
| `HttpPauseController` | `HttpPauseControllerTests.cs` が `new HttpPauseController(...)` /
  `NullLogger<HttpPauseController>` |
| `HttpReportReviewController` | `HttpReportReviewControllerTests.cs` が `new HttpReportReviewController(...)` /
  `NullLogger<HttpReportReviewController>` |
| `HttpStageGateController`（＋ネスト定数 `BelowStatisticalBasisWarning`） | `HttpStageGateControllerTests.cs` が
  `new HttpStageGateController(...)` / `NullLogger<HttpStageGateController>` /
  `HttpStageGateController.BelowStatisticalBasisWarning`（値の直接参照） |
| `LoggingNotificationSender` | `NotificationSenderFactoryTests.cs` が
  `.Should().BeOfType<LoggingNotificationSender>()` |
| `NotificationSenderFactory` | `NotificationSenderFactoryTests.cs` / `SecretRedactionTests.cs` が
  `NotificationSenderFactory.Create(...)` を静的呼び出し |
| `NullDiscordBotGateway` | `DiscordBotGatewayFactoryTests.cs` が
  `.Should().BeOfType<NullDiscordBotGateway>()` |

`DiscordBotHostedService` / `DiscordOwnerAuthExtensions` / `HttpGoodFaithViolationController`
（`IGoodFaithViolationController` のスタブ経由でのみテストされ、実装型は直接参照されない）/
`RedactedUriHttpClientLogger` は Tests から直接参照されないため `internal` のまま据え置いた。
`HttpStageGateController` / `HttpPauseController` のネストした `private`/`internal` 補助メンバ
（`Format*` 静的メソッド・`*View` レコード）は、`BelowStatisticalBasisWarning` を除き Tests から
直接参照されないため据え置いた（クラス自体が `public` でもネストした `internal` メンバは
別アセンブリから見えないが、直接参照が無いため実害は無い）。`InternalsVisibleTo` は新設していない
（旧 3 csproj にあった計 3 エントリはすべて削除した）。

## Tests 統合（3 → 1）で変えていないことの証跡

**中身は 1 行も変えていない**（`git mv` のみ・変更は namespace 宣言・using の書き換えに限定。
自己参照になった `using NotificationService.Application.{Ports,Services,State};` は削除し、
複数の旧 using が同じ新 namespace へ収束する場合は 1 行へ集約した）。

### テスト件数の突合（移送前後を実測。削っていないことの証跡）

移送前は各旧テストプロジェクトを個別に `dotnet test` して実測した（本 PR 着手直後・クリーンな
作業ツリーで測定。旧プロジェクトがまだ存在する段階で先に測定したため `git stash` は使っていない
——1 本目・4 本目・5 本目の申し送りを踏襲）。

| テストアセンブリ | 移送前 | 移送後 |
| --- | ---: | ---: |
| `NotificationService.Api.Tests` | 1 | — |
| `NotificationService.Application.Tests` | 284 | — |
| `NotificationService.Infrastructure.Tests` | 113 | — |
| **`NotificationService.Tests`** | — | **398** |
| 合計 | **398** | **398** |

1 + 284 + 113 = 398 = 移送後の合格件数と**完全一致**。減った件・増えた件は 0。

### `[Fact]`/`[Theory]` 属性の総数（直前の PR で有効だった裏取りの軸）

`grep -rhoE '^\s*\[(Fact|Theory)' Tests/*.cs` = **235**（`[Fact]` 203・`[Theory]` 32）。
移送前の同じ走査（旧 3 テストプロジェクト合計）も **235**（`[Fact]` 203・`[Theory]` 32）で完全一致。

## `list-test-projects.js --count` の突合

- 移送前: **40**
- 移送後: **38**
- 差分: **-2**（旧 3 テストプロジェクト → 新 1 テストプロジェクトの差分と一致）

## `has-pending-model-changes`（対象外の実測証跡）

```
$ dotnet ef migrations has-pending-model-changes \
    --project backend/Services/NotificationService/NotificationService.csproj \
    --startup-project backend/Services/NotificationService/NotificationService.csproj
Build started...
Build succeeded.
Your startup project 'NotificationService' doesn't reference Microsoft.EntityFrameworkCore.Design.
This package is required for the Entity Framework Core Tools to work. Ensure your startup
project is correct, install the package, and try again.
（exit code 1）
```

`NotificationService.csproj` は `Microsoft.EntityFrameworkCore.Design` を参照しない
（`DbContext` 0 件・`grep -c "DbContext\|migration" NotificationService.csproj` = 0）。
**本サービスは対象外**であり、黙って省略せず本節に実測で記録する（DoD の指示どおり）。

## `DomainLayerDependencyTests` の下限（[IADR-0265](../adr/IADR-0265_domain-project-count-checker-dynamic-lower-bound.md)。
手で触っていない）

`RepositoryLayout.cs` / `DomainLayerDependencyTests.cs` は本 PR で 1 行も変更していない。
NotificationService は元々 `Domain/` を持たない（移送前の
`UnmigratedServicesWithDomainProjectCount` に数えられていなかった）ため、本移送は同カウントの
実測値を**動かさない**（AuditService・ConfigurationService と同型。母集合が「`.Domain` 接尾辞
ディレクトリを持つ未移送サービス」であるため、元から対象外のサービスは移送してもカウントが
変化しない——[IADR-0264](../adr/IADR-0264_configurationservice-vsa-and-client-abolition.md) 決定5 の
実測と同じ構造）。

## IADR を作らない判断

**本 PR では新しい IADR（`IADR-0266`）を作らない。** [IADR-0263](../adr/IADR-0263_auditservice-vsa-migration-first-of-eleven.md)・
BacktestService「判断3」（[20260829_w11s4d](20260829_w11s4d_backtestservice-vsa.md)）・
[IADR-0259](../adr/IADR-0259_single-project-vsa-structure.md) の写像方針表を参照するだけで、
本 PR の判断1〜6 すべてが機械的に導けたためである。

- 判断1（集約は 1 つ・`_Shared/` 不要）は IADR-0263 決定1 の**そのままの適用**。集約名の選定
  （`Notifications`）は新しい設計軸ではなく、既存の「実態に合わせて命名する」運用（AuditService の
  `AuditEvents`・ConfigurationService の `Assumptions`）の**踏襲**である。
- 判断2（`Domain/` 無し→ Features 直下）は IADR-0263 決定2 の**そのままの適用**（1 本目と全く同型。
  NotificationService は 6 本目にして 2 例目の「Domain 無し」サービスである）。
- 判断3（`Infrastructure/Composable/Adapters/` は内容に関わらず一括で `ExternalServices/`）は
  BacktestService「判断3」の**そのままの適用**。
- 判断4（Wolverine ハンドラ→ `Steps/`）は IADR-0263 決定5 の**そのままの適用**。
- 判断5（`IHostedService` 直接実装は `Hosted/` へ動かさない）は
  IADR-0259 決定1 の`Hosted/`定義（`BackgroundService` 限定）の**文字どおりの適用**であり、
  例外を作る判断ではない。
- 判断6（`internal`→`public`）は IADR-0263 決定4 の**そのままの適用**。

## 受け入れ基準

- [x] `dotnet build backend/backend.slnx` が 0 warning / 0 error で通る
      （`dotnet build-server shutdown` → `bin`/`obj` 全消去 → フルビルドで確認済み）
- [x] `dotnet test backend/backend.slnx` の失敗が `AiStockTrading.IntegrationTests` の 8 件のみ
      （Docker 不在の環境制約）
- [x] `dotnet format backend/backend.slnx --verify-no-changes` が通る（exit 0）
- [x] `dotnet ef migrations has-pending-model-changes` は対象外（DbContext 0 件。実測証跡を上記に記録）
- [x] `list-test-projects.js --count` が移送前より 2 少ない（40 → 38）
- [x] `coverage-floor.json` の床（79.00%）を割らない（実測は「検証（再測定）」節）
- [x] 検査器一式（`scripts/README.md` 掲載分）が緑（`check-adr-index-sync.js` の一時的な赤は
      base の世代遅れが原因であり、rebase 後に解消したことを「検証（再測定）」節で確認する）
- [x] `node --test scripts/scripts.test.js scripts/scripts.repo.test.js` が緑

## 計画書との差異

- 差異: なし。本件は構造移送のみで振る舞いを変えていない（IADR-0259 決定7）。

## 残り 5 本のサービスへの申し送り（本 PR で踏んだ落とし穴・再利用可能な手順）

1. 🔴 **着手前に `git log HEAD..origin/develop --oneline` を必ず確認すること。** 並行移送では
   自分の作業ツリーの base が先行 PR のマージによって古くなることがある（本 PR では 5 本目
   MarketMonitorService の #590 が着手後に develop へ入っていた）。構造依存の検査器
   （`check-adr-index-sync.js` 等、既定で `origin/develop..HEAD` を走査範囲にするもの）は、
   base が古いままだと**他 PR が既に是正した差分を「自分が壊した」ように誤検出する**。
   コミット後、push 前に `git rebase origin/develop` で base を揃え、検査器を再実行すること。
2. **`Domain/` を持たないサービスでは、AuditService（1 本目）の決定1・2 がそのまま流用できる。**
   本 PR（6 本目）で 2 例目として確認できた——「集約1つ・`_Shared/` 不要・Application 層は
   `Features/<集約>/` 直下」という型は、Domain 無しサービス共通の型として扱ってよい。
3. **`Infrastructure/Composable/Adapters/` 配下は、技術的性質（I/O の有無）で個別判定せず、
   元のプロジェクトをそのまま `Infrastructure/<region>/` へ引き継ぐのが最も速く、かつ
   「移送で層を変えない」の原則に忠実である。** 本 PR は 15 ファイルすべてを一律
   `ExternalServices/` へ移し、個別の I/O 有無判定に時間を使わなかった（BacktestService
   「判断3」の先例をそのまま適用した結果）。
4. 🔴 **`Hosted/` は「`BackgroundService` を継承する型」限定で読む。** `IHostedService` を
   直接実装する型（本 PR の `DiscordBotHostedService`）は対象外——名前空間が既に
   `Infrastructure.Steps` の形で先行整合されているなら、素直にそこへ置けばよい。
   「常駐サービスっぽいから `Hosted/`」と早合点しないこと。
5. **明示的な `using` を書く既存のコードベースでは、名前空間フラット化の「using 欠落」
   「完全修飾名の部分参照」は起きない。** 本 PR（6 本目）は 4・5 本目と異なり、
   1 回目のビルドから 0 Warning / 0 Error だった（フルクリーンビルドでも再現）。
   ただし**フルクリーンビルド手順は省略しないこと**——今回問題が無かったのは事後確認の結果であり、
   事前に「大丈夫だろう」と判断してよい根拠にはならない。
6. **集約名は機械的な「サービス名から `Service` を落とす」だけでなく、構成キー・基盤の同名
   参照実装の実例を確認してから決めること。** 本 PR は `Notifications`（複数形）を選んだ——
   構成セクション名（`Notifications:...`）と基盤 `NotificationService` の `Features/Notifications/`
   の 2 つの実例が一致したため。機械的規則と実例が食い違う場合は実例を優先する
   （AuditService の `AuditEvents`・ConfigurationService の `Assumptions` と同じ判断の型）。

## 検証手順そのものの落とし穴（本 PR で親が実測。残り 5 本へ申し送る）

移送の中身とは無関係に、**検証のやり方で偽の赤が 2 種類出た**。どちらも「移送が壊れている」
という顔をするので、切り分け方を残す。

### 1. 🔴 `bin`/`obj` を全消去した直後の `dotnet build` は MSBuild が obj の生成で競合する

キャッシュ消去後にいきなり `dotnet build backend/backend.slnx` を叩くと、
`Could not find a part of the path '.../obj/Debug/net10.0/*.GeneratedMSBuildEditorConfig.editorconfig'`
のような**「今まさに自分が作るはずのファイルが無い」エラー**が 9〜24 件出る（実測。4 コアの
負荷が高い状態で再現）。**ソースの問題ではない**——同じツリーで増分ビルドすると 0 error になる。

**対処**: 消去後は **`dotnet restore` を先に通してから `dotnet build --no-restore`** する。
restore が obj の骨格を先に作るので競合しない。実測でこの順なら **0 Warning / 0 Error**。

> ⚠️ 4 本目の申し送り「1 回目のビルドは嘘をつく（キャッシュを消して全容を出す）」は**有効なまま**である。
> 消すこと自体は正しい。**消した後に restore を挟む**という一手が足りていなかった。

### 2. 🔴🔴 中断したカバレッジ収集が残す部分的なレポートは、床割れの偽陽性になる

`dotnet test --collect:"XPlat Code Coverage"` を途中で止めると、**走り終えた分のレポートだけが
`cov/` に残る**。この状態で `check-coverage.js` を掛けると、**走らなかったテストプロジェクトの
被覆が丸ごと欠けた分母**で計算され、実測より低く出る。本 PR では **レポート 24 件で 78.04%（床割れ）→
35 件で 82.27%（合格）** と、**同じツリーで 4.2 ポイント**動いた。

🔴 **レポート件数がテストプロジェクト数と一致しているかを必ず先に見ること。**
CI も同じ不変条件（`want=$(wc -l < shard.txt)` と `got=$(find cov -name coverage.cobertura.xml | wc -l)`
の突合）を持っており、**「走らなかった」と「二重に走った」を両方捕まえる**ために置かれている。
ローカルで `--root` を指すときはこの門が無いので、**自分で数える**。

**正しい収集手順（CI と同じ）**:
```bash
rm -rf cov                                   # 🔴 古いレポートを必ず消す
dotnet test backend/backend.slnx --configuration Release \
  --filter "Category!=Integration" \
  --collect:"XPlat Code Coverage" --results-directory "$PWD/cov"
find cov -name coverage.cobertura.xml | wc -l    # = テストプロジェクト数であること
node scripts/check-coverage.js --root cov
```
**Release である**こと・**`Category!=Integration`** であること・**`cov/` を作り直す**ことの 3 点が要る。

### 3. 背景で走らせた検証を止めるときは、プロセスが本当に消えたか確かめる

中断した `dotnet test` が生きたまま `bin`/`obj` を消すと、**走行中のビルドと消去が競合**して
上の 1 と見分けのつかないエラーになる。`pgrep -c dotnet` が 0 になるまで確認してから消すこと
（`dotnet build-server shutdown` だけでは MSBuild のワーカーノードが残る）。
