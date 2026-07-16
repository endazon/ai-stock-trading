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

- 非機能要件（NFR・運用/可用性）: デプロイ・監視・障害対応、および**データ保持**（重複排除ストアの
  無期限肥大化の防止・#137）
- 関連 ADR / 技術検討: [IADR-0052](../adr/IADR-0052_k8s-helm-chart-shared-infra.md)（K8s/Helm）、
  [IADR-0054](../adr/IADR-0054_collection-scheduler-mode-run-once.md)（スケジューラ）、
  [IADR-0056](../adr/IADR-0056_moomoo-simulate-poc-complete-real-gated.md)（実弾ゲート）、
  [IADR-0057](../adr/IADR-0057_order-dispatch-idempotency.md)（発注の冪等化）、
  [IADR-0059](../adr/IADR-0059_dedupe-retention-purge.md)（重複排除ストアの保持期間・パージ）

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

## データ保持・パージ（#137 / [IADR-0059](../adr/IADR-0059_dedupe-retention-purge.md)）

冪等化のための**重複排除ストア**は追記専用のため、保持期間ベースでパージする。対象は下表の 2 つに限る。
`cost_entries`（月次費用台帳）・`executed_orders`（発注履歴）・`audit_events`（監査証跡）は**業務台帳・
監査証跡であり保持要件が異なる**ため、本方針の対象外である（監査は長期保全が要求される）。

| テーブル | DB | パージ対象 | 判定列 |
| --- | --- | --- | --- |
| `processed_messages` | `cost_control_svc` | 全行が終端（処理済み）。`ProcessedAt < cutoff` | `ProcessedAt` |
| `order_dispatch_reservations` | `order_execution_svc` | **`State=Completed`（＝1）の終端行のみ**。`CompletedAt < cutoff` | `CompletedAt` |

> **`Reserved`（＝0）の予約は、どれだけ古くてもパージしない。** `Reserved` は「ブローカへ発注済みか不明」を
> 意味し、消せば再配送で**二重発注**（実弾では実損）になる。滞留 `Reserved` の解消は下の Runbook の人手の
> 判断か自動リコンサイル（**#141**）であって、時間経過ではない。パージジョブは `Reserved` に一切触れない。

### 保持期間の根拠

保持期間は**再配信の現実的な猶予より桁違いに長く**取る。短くすると重複排除が素通りし、LLM 費用の
二重計上（`processed_messages`）や二重発注（`order_dispatch_reservations`）が起きる。

| 再配信の経路 | 猶予 |
| --- | --- |
| 自動再試行（`UseAiStockTradingRetry`＝2s/10s/30s の 3 回） | 約 42 秒 |
| `_error` キューからの手動再投入（インシデント対応） | 時間〜数日 |
| **保持期間（既定）** | **90 日** |

**保持期間には下限 7 日のクランプがある**（`RetentionPolicy.MinimumRetentionDays`）。`RetentionDays: 0` の
ような設定ミスでも 7 日より新しい行は消えない。設定値ではなく構造で安全性を担保している。

### 設定（既定は無効）

不可逆な `DELETE` の自動実行は**明示的なオプトイン**である（既定 `Enabled: false`＝1 行も消さない）。
費用統制・発注執行の各 Worker が同じ `Retention` 節を読む。

```yaml
Retention:
  Enabled: false # 既定。true でパージジョブを有効化する
  RetentionDays: 90 # 保持期間（下限 7 日でクランプされる）
  IntervalHours: 24 # 巡回間隔（下限 1 時間）
  BatchSize: 500 # 1 巡回あたりの最大削除行数
```

- **有効化手順**: appsettings もしくは環境変数（`Retention__Enabled=true`）を設定してデプロイする。
  有効化直後の初回巡回では、保持期間より古い行が `BatchSize` ずつ複数巡回に分けて削除される
  （1 巡回で消し切らない）。
- **停止**: `Retention__Enabled=false` に戻して再デプロイすれば削除は止まる。
- パージの失敗はログに記録し、**サービスは停止しない**（次回巡回で再試行する）。削除件数は
  `processed_messages を N 件パージしました` 等の情報ログに出る。

### 確認クエリ

```sql
-- パージ対象の残存量（cost_control_svc）
SELECT count(*) FROM processed_messages WHERE "ProcessedAt" < now() - interval '90 days';
-- パージ対象の残存量（order_execution_svc・終端行のみ）
SELECT count(*) FROM order_dispatch_reservations
 WHERE "State" = 1 AND "CompletedAt" < now() - interval '90 days';
-- 消してはならない滞留（Reserved）。パージとは無関係に監視する（下の Runbook 参照）
SELECT count(*) FROM order_dispatch_reservations WHERE "State" = 0;
```

## 障害対応（Runbook）

| 事象 | 検知 | 一次対応 | エスカレーション |
| --- | --- | --- | --- |
| **発注予約が `Reserved` のまま滞留**（#131 / [IADR-0057](../adr/IADR-0057_order-dispatch-idempotency.md)） | `order-approved_error` キューの滞留。および `order_dispatch_reservations` に `State=Reserved`（＝0）の行が残る（`SELECT * FROM order_dispatch_reservations WHERE "State" = 0 ORDER BY "ReservedAt";`） | **自動再開はしない**（意図的な at-most-once）。ブローカ側の注文状態を確認し、①発注済み→当該注文を台帳へ手動計上して予約を確定／②未発注→予約行を削除して再配送を許可 | **不明なら「発注済み」として扱う**（二重発注を避ける側に倒す）。実弾運用中は建玉と突き合わせ、判断が付かなければ取引を停止して人間が判断する |
| **重複排除ストアが肥大化する**（#137 / [IADR-0059](../adr/IADR-0059_dedupe-retention-purge.md)） | 「データ保持・パージ」の確認クエリで、保持期間より古い行が減らない | パージジョブが有効か確認する（既定は**無効**）。ログに「パージは無効です（Retention:Enabled=false）」が出ていれば `Retention__Enabled=true` で有効化する。有効なのに減らない場合はパージ失敗のエラーログ（DB 権限・接続）を確認する | 行量に対して 1 巡回の削除上限が小さすぎる場合は `BatchSize` / `IntervalHours` を調整する。恒常的に追いつかないならパーティション化を検討（IADR-0059 代替案） |
| **パージを止めたい**（誤設定・調査中） | — | `Retention__Enabled=false` に戻して再デプロイすれば次回巡回から no-op になる | **削除済みの行は戻らない**。`RetentionDays` を短く誤設定していた場合、重複排除の記憶が消えた期間に再配信が起きると二重計上／二重発注の可能性があるため、費用台帳・発注履歴の重複を確認する |

> **`Reserved` 滞留の発生条件**: ブローカ発注の前後でプロセスが落ちる／DB が書けない場合に限る。moomoo の
> API 瞬断・不達そのものは `MoomooBrokerAdapter` が終端 `Rejected` へ倒すため（IADR-0056）、滞留にはならない。
>
> **実弾（`TrdEnv_Real`）解禁の前提**: 上記の検知（滞留の監視・アラート）と自動リコンサイル（**#141**）を
> 整備してから解禁すること。滞留は「発注済みか不明な注文」＝実弾では未確定の建玉を意味する（IADR-0056 §3）。
>
> **保持期間パージとの関係（#137 / IADR-0059）**: パージジョブは `State=Reserved` に一切触れないため、
> 滞留行が自動で消えることはない。滞留の解消は本 Runbook の手順（または #141）だけが行う。

## 未決事項
