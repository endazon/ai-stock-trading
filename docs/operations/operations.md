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
  - IADR-0060
  - IADR-0107
  - IADR-0109
  - IADR-0111
  - IADR-0175
author: endazon (with Claude Code)
created: 2026-07-08
updated: 2026-08-07
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md"
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
| 実行基盤 | Kubernetes（IADR-0052）。Helm chart [`deploy/helm/ai-stock-trading`](../../deploy/helm/ai-stock-trading)。共有インフラは MSP `platform-infra` を ExternalName で参照（MSP#266 / IADR-0066） |
| 手順（dev） | `scripts/k8s-local-images.sh`（10 Worker のビルド＆import）→ `scripts/k8s-local-deploy.sh`（ns/secret/helm）。詳細は chart README。fail-safe 既定（外部連携空=no-op / Broker=paper） |
| スケジューラ | 取引サイクルは既定 in-process。本番は `tradingCycle.cronjob.enabled=true` で K8s CronJob 駆動（#121 / IADR-0054） |
| 発注経路（ブローカ階層） | 単一スイッチ `broker.tier`（`paper` ＜ `moomoo-sim` ＜ `moomoo-live`・#267 / [IADR-0111](../adr/IADR-0111_broker-tier-selection.md)）。**既定 `paper` ＝プロセス内蔵の擬似約定で moomoo へは接続しない**。`moomoo-sim` は OpenD 経由で moomoo 模擬口座へ実発注する別経路であり、**約定の主体・残高・注文履歴の所在が別**である（取り違え防止・識別手順は [発注経路の区別と識別 Runbook](broker-execution-paths-runbook.md)・#268）。`moomoo-live`（実弾）は未解禁＝描画時 `fail` |
| moomoo OpenD | 常駐モデル（IADR-0053）。dev は `deploy/opend/k8s` の生 manifest、**本番は chart の `opend.enabled=true`**（#132 / IADR-0060）。**初回のみ**有人のデバイス検証が要り、以降は「デバイス信頼の永続化＋egress IP の安定（＝ノード固定）」で無人再ログインが成立する。#13 は `opend:11111` へ **SIMULATE** 接続（実弾は撃たない） |
| ロールバック | `helm rollback ast <revision>` もしくは Git revert（GitOps・#24） |
| GitOps（ArgoCD） | AST チャートの宣言的同期は [`deploy/argocd`](../../deploy/argocd/README.md)（Application/AppProject・#24 / IADR-0094）。ブートストラップのみ kubectl・以降 Git 同期。ArgoCD 本体 install は MSP 共有 stand-up、実同期は Tier 3 |
| 秘匿情報 | 既定は k8s Secret 直（`ast-secrets` 手動）。Vault 化は opt-in（[Vault 秘匿 runbook](vault-secrets-runbook.md)・#24 / IADR-0094）。実充足は MSP stand-up＋Tier 3。**`k8s-local-deploy.sh` の再実行は env 未設定のキーに触れない**（投入済みの値を保持する。明示的な空指定だけはキー名を列挙して中断・#263 / [IADR-0109](../adr/IADR-0109_deploy-secret-preservation.md)） |
| 為替レート（日本株） | **`FRED_API_KEY` は日本株取引の必須前提**（基準通貨〔USD〕への換算レート源＝FRED `DEXJPUS` の**逆数**・#262 / #364 / [IADR-0107](../adr/IADR-0107_base-currency-conversion.md) / [IADR-0152](../adr/IADR-0152_usd-base-currency-migration.md)）。未設定＝JPY 建て銘柄は判断前に全件見送り（米国株は無影響）。**#364 で必須となる市場が US 株から日本株へ入れ替わった。** 手順・切り分けは [chart README「為替換算」](../../deploy/helm/ai-stock-trading/README.md) |
| 可観測性 | OTLP→otel-collector→Prometheus/Loki/Tempo（[可観測性仕様](../observability/observability.md)）。環境境界は [インフラ仕様](../infra/infra.md) |

> 環境境界（経路A/B／実基盤 Tier 3）と #24 受け入れ基準の充足状況は [インフラ仕様](../infra/infra.md) を単一情報源とする。

## OpenD の本番切替チェックリスト（#132）

> 起点: [#132](https://github.com/endazon/ai-stock-trading/issues/132)（OpenD 常駐の本番化・残検証）／
> 設計判断: [IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md)／
> 仕様書: [20260716_132_opend-production-readiness](../specs/20260716_132_opend-production-readiness.md)
>
> **現在地**: 本番化に必要な**整備は済んでいる**（chart 化・ハードニングの切替口・秘匿の受け口・切替ゲート）が、
> **実測が要る項目は未充足**である。利用者方針は「**まずシミュレータ環境で全動作を確認してから本番移行**」。
> **本番稼働（実接続の常用・実弾）は未着手**であり、下表が埋まるまで切り替えない。

### 段階

| 段階 | 内容 | 状態 |
| --- | --- | --- |
| 1. 整備 | chart 化（`opend.enabled`）・パーミッション・秘匿受け口・切替ゲート・手順書 | **済**（#132 / IADR-0060） |
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
> **三重の閂**で塞いである（IADR-0016 / IADR-0056 / IADR-0060）。
>
> 実弾解禁（段階 4）の前提確認・go-live 手順・切り戻しは [実弾解禁 Runbook](live-trading-cutover-runbook.md) を参照。

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

## メッセージング（RabbitMQ のキュー）

- キュー名は **`<ServiceName>.<メッセージ型名>`**（例 `ai-stock-trading.risk-management-service.TradeDecisionMade`）。
  デッドレターは **`<queue>_error`**。いずれもサービス起動時に自動生成される（AutoProvision）。
  規則の根拠は [IADR-0129](../adr/IADR-0129_wolverine-messaging-topology.md)（ADR-0013・Wolverine 移行）。
- **キュー名から所有サービスが読める**（接頭辞）。`consumers = 0` のキューは所有サービスが購読できていない印である。
- 移行前（MassTransit）の旧キュー 47 本はブローカ上に残るため、Wolverine 版の安定稼働後に削除する:
  [旧キュー削除 Runbook](wolverine-queue-cleanup-runbook.md)。

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
| 自動再試行（`UseAiStockTradingRabbitMq` の共通再試行＝2s/10s/30s の 3 回） | 約 42 秒 |
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

## 発注予約の自動リコンサイル（#141 / [IADR-0074](../adr/IADR-0074_reservation-reconciliation.md)）

`Reserved` 滞留（上の Runbook で人手対応する「発注済みか不明な予約」）を、ブローカ照会で自動解消する
バックグラウンド機構（`OrderReservationReconciliationService`）。**二重発注を絶対に起こさない fail-safe** を守る。

- **判定**: 滞留閾値より古い `Reserved` を走査し、①`executed_orders` に記録あり→確定の自己修復／
  ②発注済み（`Placed`）→記録して確定＋`OrderExecuted` 発行／③未発注（`NotPlaced`）→予約解放／
  ④照会不達・不確定（`Indeterminate`）→**据え置き**（人手/`_error`・解放しない）。
- **既定 no-op プローブ**: 既定の照会実装は**常に `Indeterminate`**（`IndeterminateReservationBrokerProbe`）。
  実 OpenD 照会（`DecisionId` を client order id として伝播 or 時刻窓突合）は opt-in の後続で差し替える
  （実 API 依存・OpenD SIMULATE E2E は #82 系）。**既定構成では `Placed`/`NotPlaced` 経路は発火せず、
  自己修復（①）のみが作動する**（ブローカ非依存）。

### 設定（既定は無効）

```yaml
Reconciliation:
  Enabled: false # 既定。true で自動リコンサイルを有効化する
  StallThresholdHours: 24 # 滞留とみなす経過時間（下限 1 時間・再配送窓の外側）
  IntervalHours: 6 # 巡回間隔（下限 1 時間）
  BatchSize: 200 # 1 巡回あたりの最大処理件数
```

- **有効化手順**: appsettings もしくは環境変数（`Reconciliation__Enabled=true`）を設定してデプロイする。
  無効時はログに「発注予約の自動リコンサイルは無効です（Reconciliation:Enabled=false）」が出る。
- **`Indeterminate` の意味（運用上の要点）**: 照会が「不明」＝**解放しない**。既定 no-op プローブでは全件が
  `Indeterminate` になるため、実照会プローブを配線するまで自動解放・自動終端化は起きない（滞留は人手対応のまま）。
  これは二重発注を招く解放を構造的に封じる安全既定であり、想定挙動である。
- **監視すべきログ**: 各巡回の `発注予約リコンサイル: 滞留 N 件を走査（終端化 … / 解放 … / 不確定 … / 失敗 …）`。
  `失敗` が継続的に出る場合は照会・保存の恒常障害（DB 権限・接続・プローブ実装の不具合）を疑う。
- **停止**: `Reconciliation__Enabled=false` に戻して再デプロイすれば次回巡回から走査しない。

## LLM 単価の定期見直し（#303 / [IADR-0122](../adr/IADR-0122_per-model-llm-pricing.md)）

LLM 費用は**応答が名乗った実効モデル**の単価（`LlmPricing__PerModel__<model-id>__*`・円/1k トークン）で計上する。
単価は外部の公開価格と為替から導いた値であり、**恒久値ではない**。放置すると月次上限（¥15,000）の判定が
実態からずれる（過大なら取引機会を失い、過小なら上限を素通りする）。

| 見直し契機 | 期日・条件 | 対応 |
| --- | --- | --- |
| **`claude-sonnet-5` の導入価格終了** | **2026-08-31**（$2/$10 は同日までの導入価格） | 公開単価を再確認し `values-local.yaml` の該当行を更新する。`trade-decision`・`report-daily` の計上額が直接変わる |
| 公開単価の改定・モデルの追加 | 基盤の `Llm:Routing:PurposeModels` が変わったとき | 新しい実効モデルの行を表へ足す。**足さないと最大単価で過大計上される**（安全側だが実態とずれる） |
| 為替の乖離 | USD→JPY が投入時の **163.71**（FRED `DEXJPUS`・IADR-0107）から大きく離れたとき | 換算し直して表を更新する |

- 実測に基づく再ベースライン（実消費と月次上限の妥当性）は
  [#243](https://github.com/endazon/ai-stock-trading/issues/243)、計画側の上限評価は
  [planning#54](https://github.com/endazon/project-planning/issues/54) が担う。本節は**単価の鮮度**のみを扱う。
- 単価の出所・換算・丸めは `deploy/helm/ai-stock-trading/README.md`「LLM 費用の単価」に表で残している。
- 本番（ArgoCD＝`values.yaml`）には単価を置かない。よって本番の計上は従来どおり ¥0 であり、
  本節の見直しは経路B（`values-local.yaml`）に対して行う。

## Stage 1 の営業日カウントと市場の祝日（#407 / [IADR-0187](../adr/IADR-0187_stage1-holiday-non-detection-arbitration.md)）

**Stage 1 の「60 営業日」は市場の祝日を除外しない。** これは未実装ではなく、
**利用者裁定（2026-08-07・質問票 第13回 Q3 案2）で「そうする」と決まった設計**である
（計画 06_daytrading-review §4.2「**分母と除外の判定に外部カレンダーを用いない**」）。

| 対象 | 判定方法 |
| --- | --- |
| 分母（その日の通常取引時間） | **稼働監視が観測したその日の実際の取引時間**。半日取引日か否かを外部へ照会しない |
| 週末 | **曜日の算術**で除く（カレンダーではないため誤りようが無い） |
| **祝日（市場休場）** | **判別しない。除外しない** |
| OpenD の停止・ブローカー側の障害 | 稼働分数の減少として自然に表れる |

### 🔴 運用上の含意（進捗を読むときに知っておくこと）

- **祝日に OpenD が稼働していた日は営業日として算入される。** 年 **2〜3 日／60 営業日**の**過大計上**であり、
  **「昇格が早まる」側であって fail-safe ではない。**
- **ただし影響は限定的である。** 60 営業日が **57〜58 営業日相当**になる程度であり、
  昇格には**取引件数（§4.1 条件3）と利用者承認**も要る。**期間だけで昇格することはない。**
- **半日取引日は逆に過少計上（安全側）へ倒れる。** 取り得る通常取引時間の仮説
  （半日 210 分／通常日 390 分）の**両方**で 50% 以上を求めるためである。
- **SC-03 の Stage 1 進捗（営業日数）を読むときは、この過大計上を織り込むこと。**
  厳密な営業日数が要る場面（監査・報告）では、対象期間の米国市場の休場日を人手で差し引いて評価する。

### 🔴 やってはならないこと

**祝日表・休場日リスト・外部カレンダーを実装へ足すこと**は**裁定違反**である。
「未実装の項目」に見えるため善意で足される危険が最も高い箇所であり、
構造テスト（`Stage1SessionUptimeTests.カレンダーを内蔵していない__同じ稼働なら曜日だけで結果が決まる`）が
**3 年ぶんの全日付**で機械的に止める。**このテストを消さないこと。**

## 障害対応（Runbook）

| 事象 | 検知 | 一次対応 | エスカレーション |
| --- | --- | --- | --- |
| **禁止銘柄の建玉が手仕舞えない**（#380 / [ADR-0007](../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md) 2026-08-04 追補） | 手仕舞い注文が拒否理由 **`BannedSymbol`** で拒否される。**不具合ではなく設計どおり**である——禁止銘柄ガードは新規建てだけでなく**手仕舞いにも適用する**（理由: **インサイダー取引は売付けも対象**であり、AI が利用者の関知しないタイミングで規制対象銘柄を自動売却する経路を残さない） | **一時解除 → 手仕舞い → 再登録**（[禁止銘柄の一時解除 Runbook](banned-symbol-unlock-runbook.md) が単一情報源）。解除・再登録はアクターと理由が必須で、日時・対象銘柄とともに設定変更履歴へ自動で残る | **再登録の忘れを検知する仕組みは無い**（解除しっぱなしでも警告は出ない）。解除中は当該銘柄への新規建ても通るため、手順の所要時間を最小にする |
| **発注予約が `Reserved` のまま滞留**（#131 / [IADR-0057](../adr/IADR-0057_order-dispatch-idempotency.md) / 自動化は #141 / [IADR-0074](../adr/IADR-0074_reservation-reconciliation.md)） | `order-approved_error` キューの滞留。および `order_dispatch_reservations` に `State=Reserved`（＝0）の行が残る（`SELECT * FROM order_dispatch_reservations WHERE "State" = 0 ORDER BY "ReservedAt";`） | **自動再開はしない**（意図的な at-most-once）。自動リコンサイル（#141）が有効かつ実照会プローブが配線済みなら自動解消される。未配線（既定 no-op）なら手動で: ブローカ側の注文状態を確認し、①発注済み→当該注文を台帳へ手動計上して予約を確定／②未発注→予約行を削除して再配送を許可 | **不明なら「発注済み」として扱う**（二重発注を避ける側に倒す）。実弾運用中は建玉と突き合わせ、判断が付かなければ取引を停止して人間が判断する |
| **重複排除ストアが肥大化する**（#137 / [IADR-0059](../adr/IADR-0059_dedupe-retention-purge.md)） | 「データ保持・パージ」の確認クエリで、保持期間より古い行が減らない | パージジョブが有効か確認する（既定は**無効**）。ログに「パージは無効です（Retention:Enabled=false）」が出ていれば `Retention__Enabled=true` で有効化する。有効なのに減らない場合はパージ失敗のエラーログ（DB 権限・接続）を確認する | 行量に対して 1 巡回の削除上限が小さすぎる場合は `BatchSize` / `IntervalHours` を調整する。恒常的に追いつかないならパーティション化を検討（IADR-0059 代替案） |
| **パージを止めたい**（誤設定・調査中） | — | `Retention__Enabled=false` に戻して再デプロイすれば次回巡回から no-op になる | **削除済みの行は戻らない**。`RetentionDays` を短く誤設定していた場合、重複排除の記憶が消えた期間に再配信が起きると二重計上／二重発注の可能性があるため、費用台帳・発注履歴の重複を確認する |
| **日本株だけ何も起きない**（米国株は判断・発注が回る）（#262 / #364 / [IADR-0107](../adr/IADR-0107_base-currency-conversion.md) / [IADR-0152](../adr/IADR-0152_usd-base-currency-migration.md)） | trade-decision のログ `基準通貨への換算レートが解決できないため見送り（発注抑止・安全側）: {Symbol} market=Japan`、および初回 1 回の `NoOpFxRateSource を使用中: …`。確定判定は `GET /internal/introspection` の `fx-rate` ポートが `none` を申告すること | 為替レート源（FRED `DEXJPUS`）が未接続。**`FRED_API_KEY` は日本株取引の必須前提**（#364 以降。任意の収集ソース鍵ではない）。鍵を `export FRED_API_KEY=…` して `scripts/k8s-local-deploy.sh` を再実行し、`fx-rate` が `fred` を申告することを確認する（手順は [chart README「為替換算」](../../deploy/helm/ai-stock-trading/README.md)）。`Fx__Provider=fred` でも鍵が空なら `none` を申告する＝「設定したのに効いていない」の検知点 | 鍵が正しいのに `none` のままなら provider 名の誤り（未知の値は警告して no-op）。`fred` 申告でも見送りが続く場合は FRED 側の `DEXJPUS` 更新停止（**鮮度上限 14 日**超過は採らない・[IADR-0112](../adr/IADR-0112_fx-rate-freshness-publication-cadence.md)）を疑う。`DEXJPUS` の公表は **H.10 週次リリース**（月曜・前週金曜まで一括収載／月曜が祝日なら火曜）であり、**最新観測が 10 日前でも正常**である点に注意する（鮮度上限はこの公表周期から導いており、これを超える空白は系列側の異常）。**見送り自体は fail-safe であり緊急停止は不要**（古い/無いレートで発注しない・主ターゲットの米国株の取引は継続する） |
| **再デプロイ後に外部連携（実市況・為替・KB・Discord）が静かに止まる**（#263 / [IADR-0109](../adr/IADR-0109_deploy-secret-preservation.md)） | デプロイは成功するのに各アダプタが no-op 警告を出し、`GET /internal/introspection` の該当ポートが `none` を申告する。`kubectl -n ai-stock-trading get secret ast-secrets -o go-template='{{range $k,$v := .data}}{{if not $v}}{{$k}}{{"\n"}}{{end}}{{end}}'` で**空値のキー名**を列挙できる（値は出さない） | `ast-secrets` の値が空で上書きされている。現行の `scripts/k8s-local-deploy.sh` は **env 未設定のキーに触れない**ため再発しないが、旧版で潰された値は戻らない。当該 env を `export` して再実行し、値を入れ直す | 鍵の実値はリポジトリ・ログ・チャットに残さない（端末外へ出さない）。**明示的に空を指定した場合のみ**スクリプトはキー名を列挙して中断する（意図した消去は `--force-empty-secrets`）。Vault（ESO）同期を有効化した環境では `ast-secrets` は ExternalSecret が所有するため、値の投入は [Vault 秘匿 runbook](vault-secrets-runbook.md) 側で行う |

> **`Reserved` 滞留の発生条件**: ブローカ発注の前後でプロセスが落ちる／DB が書けない場合に限る。moomoo の
> API 瞬断・不達そのものは `MoomooBrokerAdapter` が終端 `Rejected` へ倒すため（IADR-0056）、滞留にはならない。
>
> **実弾（`TrdEnv_Real`）解禁の前提**: 上記の検知（滞留の監視・アラート）と自動リコンサイル（**#141**）を
> 整備してから解禁すること。滞留は「発注済みか不明な注文」＝実弾では未確定の建玉を意味する（IADR-0056 §3）。
>
> **保持期間パージとの関係（#137 / IADR-0059）**: パージジョブは `State=Reserved` に一切触れないため、
> 滞留行が自動で消えることはない。滞留の解消は本 Runbook の手順（または #141）だけが行う。

## 関連文書

| 文書 | 本書との関係 |
| --- | --- |
| [セキュリティ仕様書](../security/security.md) | **認証・認可／データ保護／秘密情報管理／監査ログ／脅威と対策**。運用者が触る統制（`ast-secrets` の投入・Vault 化の充足状況・監査ログの記録項目と**保持期間が未実装であること**）はすべて同書に実測で書いてある。**本書の「データ保持・パージ」は重複排除ストア 2 つだけを対象とし、`audit_events` は対象外である —— それが「7 年保持が担保されている」ことを意味しない点も同書に明記した**（[IADR-0175](../adr/IADR-0175_security-spec-absence-notation.md) 決定3） |
| [禁止銘柄の一時解除 Runbook](banned-symbol-unlock-runbook.md) | **建玉を手仕舞えないとき**の手順（一時解除 → 手仕舞い → 再登録）。解除・再登録が監査に残る根拠つき |
| [ブロック中のタスク](../blocked-tasks.md) | 基盤・実機待ちで本リポジトリだけでは進められない項目 |

## 未決事項
