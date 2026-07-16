---
title: 運用仕様書
type: operations-spec
status: draft
related_ids: []
author: <作成者>
created: <YYYY-MM-DD>
updated: <YYYY-MM-DD>
plan_refs: []
---

# 運用仕様書

> 必須ドキュメント（リポジトリ単位）。本リポジトリの運用を定める。雛形は `docs/templates/operations_spec_template.md`。
> **未記入のまま放置しない**。デプロイ・監視・バックアップ・障害対応を埋めること。

## 起点となる計画書（トレーサビリティ）

- 非機能要件（NFR・運用/可用性）:
- 関連 ADR / 技術検討:

## デプロイ

| 項目 | 内容 |
| --- | --- |
| 環境 | dev（ローカル k8s: k3d / Rancher Desktop 内蔵 k3s）/ stg・prod（k3s・#24） |
| 実行基盤 | Kubernetes（IADR-0052）。Helm chart [`deploy/helm/ai-stock-trading`](../../deploy/helm/ai-stock-trading)。共有インフラは MSP `platform-infra` を ExternalName で参照（microservices-platform#266 / IADR-0066） |
| 手順（dev） | `scripts/k8s-local-images.sh`（10 Worker のビルド＆import）→ `scripts/k8s-local-deploy.sh`（ns/secret/helm）。詳細は chart README。fail-safe 既定（外部連携空=no-op / Broker=paper） |
| スケジューラ | 取引サイクルは既定 in-process。本番は `tradingCycle.cronjob.enabled=true` で K8s CronJob 駆動（#121 / IADR-0054） |
| moomoo OpenD | 常駐モデル（IADR-0053）。`deploy/opend/`。起動時のみ対話デバイス検証が必要（無人自動再起動は不可）。#13 は `opend:11111` へ SIMULATE 接続 |
| ロールバック | `helm rollback ast <revision>` もしくは Git revert（GitOps・#24） |

## 監視・アラート

| 監視対象 | 指標 | 閾値 | 通知先 |
| --- | --- | --- | --- |
|  |  |  |  |

## バックアップ・リストア

<!-- 対象・頻度・保管期間・リストア手順・RPO/RTO -->

## 障害対応（Runbook）

| 事象 | 検知 | 一次対応 | エスカレーション |
| --- | --- | --- | --- |
| **発注予約が `Reserved` のまま滞留**（#131 / [IADR-0057](../adr/IADR-0057_order-dispatch-idempotency.md)） | `order-approved_error` キューの滞留。および `order_dispatch_reservations` に `State=Reserved`（＝0）の行が残る（`SELECT * FROM order_dispatch_reservations WHERE "State" = 0 ORDER BY "ReservedAt";`） | **自動再開はしない**（意図的な at-most-once）。ブローカ側の注文状態を確認し、①発注済み→当該注文を台帳へ手動計上して予約を確定／②未発注→予約行を削除して再配送を許可 | **不明なら「発注済み」として扱う**（二重発注を避ける側に倒す）。実弾運用中は建玉と突き合わせ、判断が付かなければ取引を停止して人間が判断する |

> **`Reserved` 滞留の発生条件**: ブローカ発注の前後でプロセスが落ちる／DB が書けない場合に限る。moomoo の
> API 瞬断・不達そのものは `MoomooBrokerAdapter` が終端 `Rejected` へ倒すため（IADR-0056）、滞留にはならない。
>
> **実弾（`TrdEnv_Real`）解禁の前提**: 上記の検知（滞留の監視・アラート）と自動リコンサイル（**#141**）を
> 整備してから解禁すること。滞留は「発注済みか不明な注文」＝実弾では未確定の建玉を意味する（IADR-0056 §3）。

## 未決事項
