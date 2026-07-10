---
title: IADR-0020 通知は既定で外部送信しない安全既定とし、実 Discord 送信は構成で明示有効化する
type: impl-adr
status: Accepted
related_ids: [FR-09, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/07_discord-bot-design.md
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
---

# IADR-0020: 通知は既定で外部送信しない安全既定とし、実 Discord 送信は構成で明示有効化する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-09（Discord 通知）、ADR-0001（新規サービス）
- 対象 Issue: [#15](https://github.com/endazon/ai-stock-trading/issues/15)（Slice A）
- 関連する実装仕様書: [20260710_notification-outbound](../specs/20260710_notification-outbound.md)
- 関連 IADR: [IADR-0016](IADR-0016_safe-broker-execution.md)（ブローカ実弾防止ゲートと同型の安全既定）、[IADR-0019](IADR-0019_audit-log-service.md)（イベント購読サービスの型）

## コンテキストと課題

FR-09 は取引実行・リスク統制発動・報告書確定の Discord 通知を要求する。各サービスは Discord を直接呼ばず、通知サービスが
イベントを購読して一元送信する（アーキ概要「通知・対話の一元化」）。発注機能を持つシステムの外部送信であり、誤設定や
テスト実行で**意図せず実 Discord へ送信する事故**を防ぐ必要がある。また詳細設計（07・fixed）は Gateway Bot 送信 API を主と
するが、Gateway 常駐・Vault トークン・報告書サービス（#14）依存で重い。Slice A の送信手段と安全既定を決める必要がある。

## 検討した選択肢

1. **既定で実 Discord（Webhook/Bot）へ送信する** — すぐ通知が届くが、テスト・dev・誤設定で実チャンネルへ誤送信する事故リスク。
2. **Bot Gateway 送信 API を Slice A で実装する** — 詳細設計の主方式だが、常駐 WebSocket・Discord.Net・Vault トークン・FR-14 の
   認証/コマンドと不可分で重く、CI 緑にしづらい。
3. **既定は外部送信しない no-op（ログのみ）とし、実送信は構成で明示有効化。送信手段は依存の軽い Webhook（07 が縮退用に許容）
   を Slice A の対象とする（採用）** — 安全既定で誤送信を構造的に防ぎ、CI は no-op／fake で緑にできる。Bot Gateway は FR-14 後続。

## 決定

**選択肢 3** を採用する。

- `INotificationSender` の**既定実装は `LoggingNotificationSender`（no-op＝ログのみ・外部送信しない）**。構成 `Notifications:Provider`
  が未設定（既定）または `none` の場合はこれを用いる。
- `Notifications:Provider=discord-webhook` かつ `Notifications:Discord:WebhookUrl` が設定されている場合のみ
  `DiscordWebhookNotificationSender`（実送信）を用いる。**`discord-webhook` 指定でも WebhookUrl 未設定なら no-op へフォールバックし
  警告する**（設定不備で実送信を試みない・実弾防止ゲート IADR-0016 と同型）。
- 送信手段は **Discord Webhook への HTTP POST**（`{ "content": ... }`）とする。詳細設計が縮退用に許容する方式で、Gateway 常駐・
  Vault・Discord.Net 依存を避けられる。Bot Gateway 送信 API（主方式）・スラッシュコマンド・双方向は FR-14 後続。
- 送信失敗は例外化し、MassTransit の再試行→デッドレター（`UseAiStockTradingRetry`）で可用性を担保する（NFR）。
- 通知サービスは DB を持たない（送信は fire-and-forget）。送信内容の監査は #17 AuditService が担う。

## 理由

- 安全既定（no-op）により、テスト・dev・誤設定での実 Discord 誤送信を構造的に防ぐ。発注系システムの外部送信に対する保守的方針。
- Webhook は依存が軽く CI 緑にしやすい。Slice A で FR-09（送信）の価値を先に出し、重い Bot Gateway（FR-14）を分離できる。

## 結果

- 良い影響: 誤送信事故を防ぎつつ、取引実行・リスク統制発動の通知を実装できる。CI は no-op／fake HttpMessageHandler で緑。
- 悪い影響・トレードオフ: 実 Discord 送信は構成有効化と実 API 前提の統合テストが別途必要（CI 既定では実行しない）。Bot の
  双方向（FR-14）は本スライスに含まれない。
- フォローアップ: Bot Gateway 送信 API・スラッシュコマンド・多層認証・kill switch 操作・報告書確定（FR-14・#14 連携）、
  報告書確定/kill switch イベントの購読、Vault によるトークン管理。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0016](IADR-0016_safe-broker-execution.md)（安全既定ゲートの同型）
