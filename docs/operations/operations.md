---
title: 運用仕様書
type: operations-spec
status: draft
related_ids:
  - NFR
  - FR-05
  - ADR-0002
  - IADR-0052
  - IADR-0053
  - IADR-0056
  - IADR-0057
  - IADR-0059
author: endazon (with Claude Code)
created: 2026-07-08
updated: 2026-07-16
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md"
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
| moomoo OpenD | 常駐モデル（IADR-0053）。dev は `deploy/opend/k8s` の生 manifest、**本番は chart の `opend.enabled=true`**（#132 / IADR-0059）。**初回のみ**有人のデバイス検証が要り、以降は「デバイス信頼の永続化＋egress IP の安定（＝ノード固定）」で無人再ログインが成立する。#13 は `opend:11111` へ **SIMULATE** 接続（実弾は撃たない） |
| ロールバック | `helm rollback ast <revision>` もしくは Git revert（GitOps・#24） |

## OpenD の本番切替チェックリスト（#132）

> 起点: [#132](https://github.com/endazon/ai-stock-trading/issues/132)（OpenD 常駐の本番化・残検証）／
> 設計判断: [IADR-0059](../adr/IADR-0059_opend-production-cutover-gates.md)／
> 仕様書: [20260716_132_opend-production-readiness](../specs/20260716_132_opend-production-readiness.md)
>
> **現在地**: 本番化に必要な**整備は済んでいる**（chart 化・ハードニングの切替口・秘匿の受け口・切替ゲート）が、
> **実測が要る項目は未充足**である。利用者方針は「**まずシミュレータ環境で全動作を確認してから本番移行**」。
> **本番稼働（実接続の常用・実弾）は未着手**であり、下表が埋まるまで切り替えない。

### 段階

| 段階 | 内容 | 状態 |
| --- | --- | --- |
| 1. 整備 | chart 化（`opend.enabled`）・パーミッション・秘匿受け口・切替ゲート・手順書 | **済**（#132 / IADR-0059） |
| 2. シミュレータ環境での全動作確認 | SIMULATE のまま、本番相当の配備（chart 経路）で一巡を確認する | **未** |
| 3. 本番移行（SIMULATE 常用） | 安定ノード・Vault・監視を整えて常駐運用 | **未** |
| 4. 実弾解禁 | **別 IADR が要る**。本表と IADR-0056 §3 の前提がすべて充足してから | **未**（本 issue の対象外） |

### 前提条件（切替前に潰す）

| # | 前提 | 状態 | 確かめ方 / 担当 |
| --- | --- | --- | --- |
| 1 | **egress-IP 変更時に再検証が要るか**の切り分け | 🔴 **未充足** | 単一ノード（安定 egress IP）では Pod 再作成をまたぐ**無人再ログインが成立**すると確認済み（IADR-0053 追検証）。**マルチノード/クラウド（egress IP 変動）での実測が未了**。ノードを跨ぐ再スケジュールを起こして再検証の有無を見る |
| 2 | **ノード固定**（egress IP の安定） | 🟡 **手段は用意済み・設定は運用側** | `opend.nodeSelector` / `affinity` を指定する（chart README）。**指定しないと #1 の危険に晒される** |
| 3 | `securityContext`（非 root 実行） | 🔴 **未充足**（切替口のみ） | イメージは uid/gid 10001 と `/home/opend` を用意済み。`opend.home=/home/opend` ＋ `securityContext` で切替（chart README）。**実 OpenD で未検証**。HOME 変更でデバイス信頼を失う恐れがあり、切替時は PVC の `.com.moomoo.OpenD` 移設か再検証が要る |
| 4 | `OpenD.xml`（`login_pwd_md5` を含む）のパーミッション | 🟢 **充足** | entrypoint が `umask 077` ＋ `chmod 600` で生成する（#132） |
| 5 | RSA 秘密鍵ファイルのパーミッション | 🟢 **充足** | Secret マウントを `defaultMode: 0400`（非 root 時は `fsGroup` ＋ `0440`）。entrypoint が実際のモードを起動時に検査し警告する |
| 6 | **資格情報の Vault / External Secrets 化** | 🔴 **未充足** | `ExternalSecret` の**受け口のみ**用意（`externalSecrets.enabled`・既定 false）。**ストア（Vault / ESO）は #24 の管掌で未整備**。受け口の存在は充足ではない |
| 7 | Hetzner（海外 IP）からの接続可否・**ToS** | 🔴 **未充足** | 人手の確認・契約判断（#24 / ADR-0002 の未決事項） |
| 8 | 長期常駐の安定性・強制アップデート頻度 | 🔴 **未充足** | 実測（常駐させて観測する） |
| 9 | 取引パスワードのアンロック | 🔴 **未充足** | SIMULATE では不要な範囲の切り分けが要る（ADR-0002 未決） |
| 10 | OpenD の**ログイン済み**判定（healthcheck） | 🟡 **限界を明示** | readiness は **TCP 疎通のみ**。OpenD は**検証前から listen する**ため、**probe 通過≠ログイン完了**。「使える」判定は `kubectl attach` でのログイン成功確認に依る。liveness は付けない（自動再起動が有人検証待ちの停止を招くため） |
| 11 | **発注予約 `Reserved` 滞留の監視・自動リコンサイル** | 🔴 **未充足** | 現状は人手（下記 Runbook）。自動化は **#141**。実弾では「発注済みか不明な注文」＝未確定の建玉を意味する（IADR-0056 §3） |
| 12 | `TradingDefaults`（リスク統制・上限）の**実弾向け再確認** | 🔴 **未充足** | 実弾解禁 IADR の前提（IADR-0056 §3） |

> 🔴 が一つでも残る限り**実弾（`TrdEnv_Real`）は解禁しない**。解禁には**別 IADR ＋ 明示 config** が要り、
> 現状のコードは `TrdEnv_Simulate` 固定・`BrokerFactory` の config ゲート・`Broker:Moomoo:TrdEnv` の拒否という
> **三重の閂**で塞いである（IADR-0016 / IADR-0056 / IADR-0059）。

### 切替手順（段階 2→3・SIMULATE のまま）

1. イメージを用意する（`scripts/opend-build.sh`。OpenD バイナリは非コミット・EULA）。
2. Secret を作る（`deploy/opend/k8s/secret.example.yaml` / `rsa-secret.example.yaml`。実値は Git に載せない）。
   Vault 化（前提 #6）が済んだら `externalSecrets.enabled=true` へ移す。
3. **ノードを固定して** OpenD を配備する: `--set opend.enabled=true --set opend.nodeSelector."kubernetes\.io/hostname"=<node>`。
4. **初回のみ有人**でデバイス検証する: `kubectl -n ai-stock-trading attach -it deploy/opend`
   → `input_pic_verify_code` / `input_phone_verify_code`。初回は API 利用規制アンケート（口座単位・一度きり）も要る。
5. ログイン成功をログで確認する（**readiness の通過では判定できない**＝前提 #10）。
6. 発注経路を SIMULATE で有効化する: `--set moomoo.enabled=true`。**実弾にはならない**（`TrdEnv_Simulate` 固定）。
7. 一巡（発注→照会→取消）を確認する。以降は**再起動を最小化**して常駐させる。

### 切替をやめる（切り戻し）

`--set moomoo.enabled=false`（＝`Broker__Provider=paper`）で**発注はペーパーに戻る**。OpenD 自体は
`--set opend.enabled=false` で落とせるが、**Pod を消すとデバイス信頼の再確立（有人検証）が要る場合がある**ため、
発注を止めるだけなら `moomoo.enabled=false` に留めるのが安い。

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
