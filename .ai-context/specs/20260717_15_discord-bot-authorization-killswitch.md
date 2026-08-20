---
title: Discord Bot 基盤・多層認証・kill switch・版番号付き冪等確定（FR-14）— Issue #15
type: spec
status: review
related_ids:
  - FR-09
  - FR-14
  - UC-06
  - ADR-0003
  - IADR-0020
  - IADR-0051
  - IADR-0062
author: claude
created: 2026-07-17
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/07_discord-bot-design.md (fixed)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-09 / FR-14)
related_specs:
  - "../adr/IADR-0062_discord-bot-gateway-and-authorization.md（本 PR の設計判断）"
  - "20260710_notification-outbound.md（#15 Slice A: アウトバウンド通知・IADR-0020）"
  - "../adr/IADR-0051_service-to-service-auth.md（s2s トークン基盤の再利用元）"
---

# 仕様書: Discord Bot 基盤・多層認証・kill switch（Issue #15）

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-14**（Discord からの対話・kill switch 起動）／**FR-09**（通知・既存）
- ユースケース: **UC-06**（統制操作＝kill switch）
- 詳細設計: `06_technical/07_discord-bot-design.md`（**fixed**）
- Issue: [#15](https://github.com/endazon/ai-stock-trading/issues/15)

## 目的・背景

#15 のうち**アウトバウンド通知（FR-09）は PR #65 で完了済み**（イベント購読 → Discord Webhook 送信・
既定で外部送信オフ・IADR-0020）。本 PR は残る **FR-14 の双方向 Bot** を実装する。

Bot は発注機能を持つシステムへの操作窓口であり、`/killswitch` は全取引の即時停止という最大の副作用を持つ。
したがって**誤爆防止の多層認証**と**安全既定（既定オフ・未設定は拒否）**を最優先とする。

## スコープ

### 本 PR の対象

| 項目 | 内容 |
| --- | --- |
| Bot 基盤 | Gateway（WebSocket）常駐・**最小 Intents（Guilds のみ）**・スラッシュコマンド登録。**既定 no-op** |
| 多層認証 | DM 拒否／サーバー・チャンネル固定／ユーザーID許可リスト／Keycloak マッピング／確認ステップ |
| kill switch | `/killswitch`・`/killswitch off`。確認ボタン＋**確認フレーズ**。Risk の既存 HTTP を**呼ぶだけ** |
| 版番号付き冪等確定 | `対象ID＋版番号` の楽観ロック機構（**純粋機構のみ**・全数テスト） |

### 対象外（明示）

| 項目 | 理由 |
| --- | --- |
| `/report show` / `/report approve` の結線 | **#14（ReportReviewStateMachine）と交差**するため。機構（`VersionedConfirmationGuard`）のみ提供 |
| 自然文リプライの AI 分析サービス中継 | MessageContent Intent が要る（#14 交差）。IADR-0062 決定2 |
| `/status` / `/pause` / `/resume` | Risk に pause 相当のエンドポイントが無く別途設計が要る |
| 実 Discord Gateway 依存テスト | 外部 SaaS への WebSocket は CI で張れない。後続 E2E へ分離（IADR-0062 理由3） |
| Risk / Report / Configuration / `Shared.Contracts` / `TradingDefaults` の変更 | 本 PR は `NotificationService/**` に閉じる（kill switch は既存 HTTP を呼ぶだけ） |

## 設計

設計判断の根拠は **[IADR-0062](../adr/IADR-0062_discord-bot-gateway-and-authorization.md)** に記す。

### レイヤ構成（Discord.Net を Application に漏らさない）

```
Application（純粋・外部 SDK 非依存）
  Ports/IDiscordBotGateway.cs        … Gateway 常駐の抽象
  Ports/IKillSwitchController.cs     … Risk の kill switch HTTP の抽象
  State/DiscordBotOptions.cs         … 多層認証の設定（既定＝全拒否）
  State/DiscordCommandContext.cs     … 着信コマンドの文脈（Guild/Channel/User/DM 種別）
  Services/DiscordCommandAuthorizer  … ★多層認証（純関数）
  Services/BotCommandParser          … ★コマンド解析（純関数）
  Services/KillSwitchConfirmation    … ★確認フレーズ照合（純関数）
  Services/VersionedConfirmationGuard… ★版番号付き冪等（純粋機構）
  Services/KillSwitchCommandHandler  … 上記を束ねる（IKillSwitchController 経由で Risk 呼び出し）

Worker（外部依存）
  Composable/Adapters/DiscordNetBotGateway.cs   … Discord.Net 実装
  Composable/Adapters/NullDiscordBotGateway.cs  … ★既定（接続しない）
  Composable/Adapters/HttpKillSwitchController.cs … Risk への HTTP（owner トークン付与）
  Composable/Adapters/DiscordBotGatewayFactory.cs … 安全既定の選択（IADR-0020 と同型）
```

### 多層認証の評価順（すべて fail-safe＝拒否が既定）

| 層 | 条件 | 不成立時 |
| --- | --- | --- |
| 1 | **DM ではない** | 拒否（詳細設計07「DM は不使用」） |
| 2 | GuildId が設定値と一致（**設定が空なら拒否**） | 拒否・ログのみ |
| 3 | ChannelId が設定値と一致（**設定が空なら拒否**） | 拒否・ログのみ |
| 4 | Discord ユーザーIDが**許可リストに存在**（**空なら拒否**） | 拒否・ログのみ |
| 5 | Keycloak マッピングが存在し actor を特定できる | 拒否（actor 不明の操作はさせない） |

**「設定が空＝全許可」にしない**ことが本仕様の要。設定漏れが全開放にならないようにする。

### kill switch の資格情報（IADR-0062 決定4）

Risk の `POST /risk-controls/kill-switch/engage|disengage` は **`OwnerOnly`**（`trading-owner`）であり、
IADR-0051 の s2s トークン（`trading-service`）では **403**。Bot は**専用の owner マップ Keycloak クライアント**の
client_credentials で呼ぶ。PlatformShim の `ClientCredentialsTokenProvider` / `ServiceAuthOptions` は `public` の
ため**無改修で再利用**する（`Notifications:Discord:OwnerAuth` セクション。`ServiceAuth` とは別系統）。

### 冪等性

- **kill switch 起動**: 起動済みなら Risk が現状態を返すのみ（副作用なし）。詳細設計07 と一致。
- **版番号付き確定**: `VersionedConfirmationGuard` が `対象ID＋版番号` で判定する。
  - 未確定の最新版 → `Accepted`
  - 同一 `対象ID＋版番号` の再要求 → `AlreadyConfirmed`（副作用なし）
  - 確定済みより**古い版** → `Stale`（「最新ドラフトを確認してください」）

## 受け入れ基準（本 PR 分・テストへの写像）

| # | 基準 | テスト |
| --- | --- | --- |
| 1 | DM からの操作は拒否される | `DiscordCommandAuthorizerTests` |
| 2 | 指定サーバー/チャンネル以外の着信は拒否される | 同上 |
| 3 | 許可リスト外のユーザーは操作できない（**本人以外は操作不可**） | 同上 |
| 4 | **設定が空のとき全拒否**（fail-safe） | 同上 |
| 5 | Keycloak マッピングが無いユーザーは拒否される | 同上 |
| 6 | 確認フレーズ不一致では kill switch が起動しない | `KillSwitchConfirmationTests` |
| 7 | **確認フレーズ未設定なら起動しない** | 同上 |
| 8 | `/killswitch` / `/killswitch off` が解析される・未知コマンドは拒否 | `BotCommandParserTests` |
| 9 | 起動済みへの再起動は副作用なし（冪等） | `KillSwitchCommandHandlerTests` |
| 10 | 同一 `対象ID＋版番号` の再確定は `AlreadyConfirmed`・古い版は `Stale` | `VersionedConfirmationGuardTests` |
| 11 | **既定で Bot は接続しない**（no-op）／設定不備でも接続しない | `DiscordBotGatewayFactoryTests` |
| 12 | Risk へ owner トークン付きで engage/disengage が送られる | `HttpKillSwitchControllerTests` |

## 検証

- `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx` 緑
- `dotnet format` 通過・警告ゼロ
- **実 Discord Gateway 依存の検証は本 PR に含めない**（IADR-0062 フォローアップ・後続 E2E）

## 設定キー（PR 末尾の単一コミットに集約）

すべて**既定オフ**。値が揃った時のみ有効化される。

| キー | 既定 | 意味 |
| --- | --- | --- |
| `Notifications:Discord:Bot:Enabled` | `false` | Bot Gateway 常駐の有効化 |
| `Notifications:Discord:Bot:Token` | 空 | Bot トークン（秘匿・Vault 化は後続） |
| `Notifications:Discord:Bot:GuildId` | 空 | 専用サーバー（空＝全拒否） |
| `Notifications:Discord:Bot:ChannelId` | 空 | 専用チャンネル（空＝全拒否） |
| `Notifications:Discord:Bot:AllowedUserIds` | 空 | 許可ユーザーID（空＝全拒否） |
| `Notifications:Discord:Bot:UserMapping:<discordUserId>` | 空 | Keycloak 利用者名への対応付け |
| `Notifications:Discord:Bot:KillSwitchConfirmationPhrase` | 空 | 確認フレーズ（空＝起動拒否） |
| `Notifications:Discord:OwnerAuth:*` | 空 | owner マップ機密クライアント（client_credentials） |
| `RiskManagement:BaseUrl` | 空 | Risk の同期照会先 |
