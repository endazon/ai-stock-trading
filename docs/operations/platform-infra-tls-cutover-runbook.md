---
title: 運用 Runbook — 基盤への接続を TLS へ切り替える（経路 B のローカルの PoC）
type: runbook
status: draft
author: claude (Claude Code)
created: 2026-11-06
updated: 2026-11-06
---
<!-- trace:
ids: [NFR, NFR-18]
adrs: [ADR-0006]
iadrs: [IADR-0621, MSP:IADR-0591]
specs: [20261106_1535_platform-infra-tls]
issues: [#1535, #1525]
-->

# 運用 Runbook: 基盤への接続を TLS へ切り替える

> **運用仕様書（[operations.md](operations.md)）の下位にあたる手順書である。**
>
> 🔴 **本書の手順はすべてオーナーが行う。** 配備・クラスタの操作は、AI（コーディングエージェント）は行わない。

## 何が変わるか

- `values-local.yaml` は、基盤（MSP）の Postgres・RabbitMQ への接続を TLS にする。
  - DB: `SSL Mode=VerifyFull`（基盤の CA で検証し、host の名前も照合する）。
  - ブローカ: `amqps://…@rabbitmq:5671`（名前の不一致は受け入れない）。
- 基盤の CA は ConfigMap `platform-infra-ca`（キー `ca.crt`）。基盤の起動器が AST の名前空間へ写す。
  - AST の Pod は、これを `/etc/platform-infra-ca` へ非 optional でマウントする。
  - 🔴 **ConfigMap が無いと Pod が起動しない**（平文へは戻らない）。
- LLM ゲートウェイの HttpClient は、https の URL のとき基盤の CA で検証する。経路 B のゲートウェイは http のままで、本書の切り替えでは変わらない。

## 切り替えの順番（場の時間外に行う）

1. **基盤の段 1 が配備済みであることを確かめる。** 基盤の Postgres が `ssl=on`、RabbitMQ が 5671 で TLS を受けている（基盤の起動器を最新の版で実行済み）。
2. **CA の ConfigMap が AST の名前空間にあることを確かめる**:
   `kubectl -n ai-stock-trading get configmap platform-infra-ca -o jsonpath='{.data.ca\.crt}' | head -1`
   - `-----BEGIN CERTIFICATE-----` が出ること。
   - 無ければ、基盤の起動器を再実行して写す。CA は公開してよい証明書だけで、秘密ではない。
3. 新規発注の一時停止（pause）を入れる。
4. `scripts/k8s-local-deploy.sh` で配備する（`values-local.yaml` が TLS を有効にしている）。
5. **確かめる**:
   - 全 Worker の Pod が Ready になる。
   - サーバの側で TLS になっている。
     - Postgres: `pg_stat_ssl` で AST の利用者（`ai`）の接続が `ssl = t`。
     - RabbitMQ: `rabbitmqctl list_connections user ssl` で `ast-*` の接続が `true`。
   - 取引判断・日報・発注の経路のログにエラーが無い。
6. 一時停止を解く。
7. 基盤の段 4b-3（平文を閉じる）へ進めることを基盤の担当へ伝える（基盤の作業）。

## 戻し方

1. 新規発注の一時停止（pause）を入れる。
2. `values-local.yaml` の `global.db.tls.enabled` と `global.rabbitmq.tls.enabled` を `false` にして、`scripts/k8s-local-deploy.sh` を再実行する（平文の 5432・5672 へ戻る）。
   - 🔴 基盤が平文の口を閉じた後（段 4b-3 の後）は、平文へは戻せない。戻すなら、原因（CA の不一致・ConfigMap の欠落など）を直して TLS のまま再配備する。
3. 全 Worker の Pod が Ready になったら、一時停止を解く。

## よくある失敗

| 症状 | 原因 | 対処 |
| --- | --- | --- |
| Pod が `ContainerCreating` のまま（`configmap "platform-infra-ca" not found`） | CA の ConfigMap が無い | 手順 2 で写してから再配備する |
| DB に繋がらない（証明書の検証の失敗） | CA が古い（基盤の CA を作り直した）、または host の名前がサーバ証明書と違う | 基盤の起動器を再実行して CA を写し直す。host は `postgres` のまま（ExternalName の名前で照合する） |
| ブローカに繋がらない（名前の不一致） | 接続先の host がサーバ証明書の名前と違う | `global.rabbitmq.host` を `rabbitmq` のままにする |
