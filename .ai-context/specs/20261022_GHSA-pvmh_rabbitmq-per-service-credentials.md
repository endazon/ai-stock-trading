---
title: 全 Worker をブローカのサービスごとの資格情報（ESO 供給）へ切り替え、既定のユーザーの接続文字列を外す（移行の段 2・Refs GHSA-pvmh-cvmf-pvcp）
type: spec
status: done
related_ids: [NFR, ADR-0013, IADR-0568, IADR-0341, IADR-0566]
author: claude (Claude Code)
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0013_messaging-follow-wolverine-kafka.md
---

# 仕様書: ブローカのサービスごとの資格情報（段 2）

> 本仕様書は実装着手前に作成した（着手 2026-10-22。AST `origin/develop` `2f2f608e`）。

## 起点

- Refs GHSA-pvmh-cvmf-pvcp（オーナー裁定 2026-10-22。順序は基盤 → AST。AST の PR は基盤の PR のマージ後に出す）。非機能 `NFR`。
- 前提: 基盤の段 1（MSP の IADR-0573。ユーザー `ast-<サービス名>` と Vault `ai-stock-trading/rabbitmq-users` の種）。
- IADR: **IADR-0568**。計画との矛盾: なし（ADR-0013 は Wolverine + RabbitMQ を定め、資格情報の形は定めない）。

## 範囲

1. chart: `global.rabbitmq.*`（旧 `global.rabbitmqConnectionString` は廃止・描画で止める）、`templates/deployment.yaml` の env、Reloader の注釈
2. chart: ExternalSecret `ast-rabbitmq-users`（`externalSecrets.enabled` のとき）
3. 起動器: ESO の所有対象に追加、従来経路では Secret が無ければ止める
4. 試験: helm.yml（新ステップと #795 ステップの期待の更新）、`k8s-local-deploy.test.sh`（T-pvmh-01・02）
5. chart README

## 母集合（規則 9・10）

走査語: `rabbitmqConnectionString`・`amqp://guest`・`RabbitMq__ConnectionString`（`.ai-context/specs/`・`CHANGELOG.md` を除く）。

| # | 箇所 | 扱い |
| --- | --- | --- |
| 1 | `deploy/helm/ai-stock-trading/{values.yaml, templates/deployment.yaml}` | 置き換える |
| 2 | `backend/Services/*/appsettings.Development.json`・単体試験・`docker-compose.yml`・`scripts/e2e-local-infra.sh` | **対象外**（単体起動の専用ブローカ。k8s の Worker は Production で Development を読まない） |
| 3 | `scripts/fixtures/helm-release-drift/*`・`scripts/scripts.repo.test.js` の接続文字列 | 対象外（伏せ字の試験の入力） |

## 受け入れ基準

- [x] 既定・経路B の描画の全 Worker（11 件）が `ast-<サービス名>` のユーザーで接続し、パスワードは `ast-rabbitmq-users` のキー `ast-<サービス名>`（非 optional）。`RABBITMQ_PASSWORD` が接続文字列より先
- [x] 描画に `guest` が現れない。旧キー・Secret 名の欠落は描画で止まる
- [x] ESO 所有では ExternalSecret `ast-rabbitmq-users` を描く。Reloader の注釈に入る
- [x] 従来経路で Secret が無ければ起動器が ast-secrets に触れる前に止まる
- [x] 変異: キーから接頭辞を外す・接続文字列を guest に戻す → helm の検査がそれぞれ赤

## 配備前の手順（稼働中の PoC。値は書かない。配備は閉場後）

1. 基盤の段 1（MSP#2056）が配備済みであること: `bash deploy/local/rabbitmq-setup/reconcile-users.sh --check`（MSP）が 18 件を示す。
2. Vault に `ai-stock-trading/rabbitmq-users` があること（基盤の bootstrap が入れる。手で入れる値は無い）。
3. 閉場後に `scripts/k8s-local-deploy.sh` を回す。ExternalSecret `ast-rabbitmq-users` が同期し、Worker が新しい資格情報で作り直される
   （OpenD は対象外で再起動しない）。
4. 確認: `kubectl -n ai-stock-trading get externalsecret ast-rabbitmq-users` が `SecretSynced`、全 Worker が Ready、
   `kubectl -n platform-infra exec deploy/rabbitmq -- rabbitmqctl list_connections user` に `ast-*` が並ぶ（AST からの接続に `guest` が無い）。
