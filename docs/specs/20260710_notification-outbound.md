---
title: 通知サービス Slice A（イベント購読→テンプレート整形→Discord アウトバウンド通知・安全既定）
type: spec
status: done
related_ids: [FR-09, UC-01, UC-02, UC-06, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/07_discord-bot-design.md
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
---

# 仕様書: 通知サービス Slice A（Discord アウトバウンド通知）

> Issue [#15](https://github.com/endazon/ai-stock-trading/issues/15)（FR-09/FR-14・Must）の **Slice A（FR-09 送信のみ）**。
> 取引パイプラインのイベントを購読し、種別ごとのテンプレートで整形して Discord へ**一方向通知**する。送信は CI 安全な
> no-op 既定とし、実 Discord 送信は構成で明示的に有効化する（実弾防止と同型の安全既定・IADR-0016 の思想）。
> 双方向 Bot（FR-14: Gateway 接続・スラッシュコマンド・kill switch 操作・報告書確定）は後続スライス。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-09（報告書確定・取引実行・エラー・リスク統制発動の Discord 通知。Must）
- ユースケース（UC）: UC-01/02（取引実行の通知）、UC-06（リスク統制発動の通知）
- 技術検討: `07_discord-bot-design.md`（fixed。通知は一方向・Bot 送信 API が主／**縮退用に通知専用 Webhook を予備として許容**・
  DM 不使用・専用チャンネル）、`01_architecture-overview.md`（「通知・対話の一元化」＝各サービスは Discord を直接呼ばずイベント発行のみ）
- ADR: ADR-0001（platform 再利用・新規サービス）
- 関連 IADR: 本作業で新規 [IADR-0020](../adr/IADR-0020_notification-safe-outbound.md)
- 対象 Issue: #15（Slice A）

## 目的・背景

各サービスは Discord を直接呼ばず、通知サービスがイベントを購読して種別ごとのテンプレートで送信する（アーキ概要「通知・
対話の一元化」）。発注機能を持つシステムのため、**実 Discord 送信は既定で無効**（外部へ何も送らない no-op）とし、構成で
明示的に有効化した場合のみ実送信する（誤送信・情報漏えいの安全既定）。詳細設計（07）は Gateway Bot 送信 API を主とするが、
縮退用 Webhook を予備として許容しており、本スライスは**依存の軽い Webhook 送信**を実装対象とする（Bot Gateway は FR-14 後続）。

## 対象範囲

### 新規サービス `NotificationService`（Application + Worker。Domain 層なし・DB なし＝送信は fire-and-forget）

- **購読対象イベント（現存し FR-09 に該当するもの）**:
  - `OrderExecuted`（取引実行）
  - `OrderRejected`（リスク統制発動＝発注拒否・理由つき）
  - `StopLossTriggered`（リスク統制発動＝損切りライン到達）
- **テンプレート整形**（`NotificationFormatter`・純関数）: イベント→`NotificationMessage(Title, Content, Severity)` に写像する。
- **送信ポート** `INotificationSender`:
  - `LoggingNotificationSender`（**既定・no-op＝ログ出力のみ**。外部送信しない。CI/dev の安全既定）。
  - `DiscordWebhookNotificationSender`（実送信。`HttpClient` で Discord Webhook へ POST。エラーは例外化し MassTransit 再送に委ねる）。
  - `NotificationSenderFactory`（構成 `Notifications:Provider`＝`none`（既定）／`discord-webhook` で選択。`discord-webhook` 指定でも
    `Notifications:Discord:WebhookUrl` 未設定なら**安全側に no-op へフォールバックし警告**＝設定不備で実送信を試みない）。
- **配信の可用性（NFR）**: 送信失敗は例外化し、MassTransit の再試行→デッドレター（`UseAiStockTradingRetry`）で回復性を担保する。
- **Worker ホスト**: Serilog/OTel・ヘルスチェック（liveness）・MassTransit RabbitMQ 消費者＋再試行。DB・認可エンドポイントなし。
  実行時基盤は test-support shim（本番非使用・IADR-0013）。

## 受け入れ基準

CI で緑にする範囲（ユニット＋MassTransit テストハーネス＋WebApplicationFactory＋fake HttpMessageHandler）:
- [x] `OrderExecuted`／`OrderRejected`／`StopLossTriggered` を購読すると、種別ごとに整形された通知が送信される（fake sender で検証）。
- [x] `OrderRejected` の通知に拒否理由（`RejectionReason`）が含まれる。
- [x] 既定（`Notifications:Provider` 未設定）では `LoggingNotificationSender`＝**外部送信しない**（安全既定）。
- [x] `discord-webhook` 指定かつ WebhookUrl 未設定なら no-op へフォールバックし警告する（設定不備で実送信しない）。
- [x] `DiscordWebhookNotificationSender` は Webhook へ `content` を POST し、非 2xx 応答で例外化する（fake HttpMessageHandler）。`content` は Discord の 2000 文字上限に切り詰める。
- [x] Worker が起動しヘルス `/health/live` が応答する（既定の安全 sender で）。
- [x] 既存テストを緑に保つ。

実 API 前提（CI 既定では実行しない）:
- [ ] 実 Discord Webhook への送信 E2E（トークン/URL は Vault・実 API）。

## 対象外（後続）

- 双方向 Discord Bot（FR-14: Discord.Net Gateway 接続・スラッシュコマンド・多層認証・kill switch 起動・報告書確定）。Gateway 常駐・
  Vault トークン・報告書サービス（#14）依存のため別スライス。
- 報告書確定通知（報告書サービス #14 未実装のため）。kill switch 起動通知（RiskManagement が kill switch イベントを発行するようになれば購読・後続）。
- 通知の永続ログ（監査は #17 の AuditService が担う）。テンプレートの Embed 装飾・多言語。

## テスト方針

- `NotificationFormatter` は純関数として各イベントの整形（種別・銘柄・拒否理由）を単体検証。
- `NotificationSenderFactory` は構成による選択と安全フォールバック（未設定→no-op・警告）を検証。
- `DiscordWebhookNotificationSender` は fake `HttpMessageHandler` で POST 本文・非 2xx 例外化を検証（実ネットワーク不使用）。
- Consumer は MassTransit `ITestHarness`＋fake sender で送信呼び出しを検証。
- Worker 起動は `NotificationWorkerWebApplicationFactory`（既定安全 sender）で確認。

## 関連仕様

- 連携元（イベント発行）: [20260710_order-execution](20260710_order-execution.md)、[20260710_risk-management-worker](20260710_risk-management-worker.md)、[20260710_stop-loss-execution](20260710_stop-loss-execution.md)
- 実装ADR: [IADR-0020](../adr/IADR-0020_notification-safe-outbound.md)。安全既定の思想は [IADR-0016](../adr/IADR-0016_safe-broker-execution.md)（ブローカ実弾防止ゲート）と同型。

## 未決事項

- Bot Gateway 送信 API（主方式）と Webhook（縮退）の使い分け・トークン管理（Vault）は FR-14 スライスで確定する。
- kill switch 起動・pause/resume の通知は RiskManagement のイベント発行（後続）に合わせて購読を追加する。
