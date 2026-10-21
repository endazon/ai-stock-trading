---
title: IADR-0568 全 Worker はブローカ（基盤の RabbitMQ）へサービスごとのユーザー（ast-<サービス名>）で接続し、パスワードは ESO が同期する Secret から非 optional に読む。既定のユーザーの接続文字列は廃止して描画で止める
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0013, ADR-0001, IADR-0094, IADR-0341, IADR-0566]
author: claude (Claude Code)
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0013_messaging-follow-wolverine-kafka.md
related_specs:
  - ../specs/20261022_GHSA-pvmh_rabbitmq-per-service-credentials.md
---

# IADR-0568: ブローカへのサービスごとの資格情報（移行の段 2）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-22
- 決定者: オーナー裁定 2026-10-22（Refs GHSA-pvmh-cvmf-pvcp。順序は基盤 → AST）。chart の形は実装が決めた。

## 起点・関連

- 起点: Refs GHSA-pvmh-cvmf-pvcp（最小権限・既定の資格情報を使わない）。
- 前提: 基盤の段 1（MSP の IADR-0573。ユーザーと権限・Vault の種・ブローカへの到達の制限。基盤のリポジトリでマージ済みであること）。
- 関連: IADR-0094 / [IADR-0341](IADR-0341_screen-only-eso-wiring-local-profile.md)（ESO の受け口）、計画 ADR-0013（Wolverine + RabbitMQ）、ADR-0001（基盤の再利用）。

## コンテキスト

- AST の全 Worker（11 件）は、全サービス共通の接続文字列（`global.rabbitmqConnectionString`。資格情報を値に含む）でブローカへ接続していた。
- 基盤の段 1 で、ブローカにサービスごとのユーザー `ast-<サービス名>` ができ、パスワードは Vault `ai-stock-trading/rabbitmq-users`（プロパティ名 = ユーザー名）に乱数で入った。

## 決定

1. **接続の形**: `global.rabbitmq.{host, port, credentialsSecretName, usernamePrefix}`。各 Worker は
   `RABBITMQ_PASSWORD`（Secret `credentialsSecretName` のキー `<usernamePrefix><サービス名>`）を env に入れ、
   接続文字列は `amqp://<user>:$(RABBITMQ_PASSWORD)@host:port` を k8s の補間で組む（env の値に平文を描かない。基盤の chart と同じ形）。
2. **非 optional**: 値が無いまま別の資格情報へ戻る経路を作らない。Secret が無い・キーが無いときは新しい Pod が起動せず、
   ロールアウトは旧 Pod を残す（replicas 1・既定の RollingUpdate）。
3. **旧キーは廃止**: `global.rabbitmqConnectionString` を与えたら描画で止める。設定点の欠落も止める。
4. **供給**: ESO 所有（`externalSecrets.enabled`）では ExternalSecret `ast-rabbitmq-users` が Vault を dataFrom.extract で同期する。
   従来経路（ESO なし）では起動器は作らず、無ければ helm の前で止める（開発用の既定値を持たない）。
5. **回したら作り直す**: Reloader の注釈（経路B）に全 Worker で `ast-rabbitmq-users` を足す。基盤が値を回すと
   （Vault → 基盤の reconcile-users.sh でブローカへ反映 → ESO 同期）、Reloader が Worker を作り直して新しい値で接続する。

## 結果

- AST の Worker は既定のユーザーを使わなくなる。1 つの値が漏れても、取り消しは 1 サービスで済む。
- 既定のユーザーの無効化は基盤の段 3（基盤のサービスの切り替えの後）。

## 残る制約

1. `backend/Services/*/appsettings.Development.json` と単体試験・docker-compose の接続文字列は、単体起動の専用ブローカ向けであり本変更の対象外
   （k8s の Worker は `ASPNETCORE_ENVIRONMENT=Production` で Development の設定を読まない）。
2. 値を回す順序（ブローカ → Secret）の間は、作り直された Pod が一時的に認証に失敗し得る。手順は基盤の reconcile-users.sh を先に回す。
