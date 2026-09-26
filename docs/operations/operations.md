---
title: 運用仕様書
type: operations-spec
status: draft
created: 2026-07-08
updated: 2026-09-26
author: endazon (with Claude Code)
---
<!-- trace:
ids: [FR-01, FR-04, FR-05, FR-08, FR-19, FR-20, NFR-03, NFR-07, NFR-08, NFR-10, NFR-11, NFR-13, FR-10]
adrs: [ADR-0002, ADR-0004, ADR-0007, ADR-0013, ADR-0022]
iadrs: [IADR-0016, IADR-0052, IADR-0053, IADR-0054, IADR-0056, IADR-0057, IADR-0059, IADR-0060, IADR-0066, IADR-0074, IADR-0107, IADR-0109, IADR-0111, IADR-0112, IADR-0122, IADR-0129, IADR-0152, IADR-0175, IADR-0187, IADR-0194, IADR-0308, IADR-0315, IADR-0374, IADR-0370, IADR-0395, IADR-0344, IADR-0428, IADR-0436, IADR-0439, IADR-0441]
specs: [20260716_132_opend-production-readiness, 20260905_686_fx-provider-boj-first, 20260909_705_kb-tags-static-vocabulary, 20260917_817_llm-pricing-env-names, 20260923_891_decision-skip-reasons-and-first-alert, 20260923_858_drift-adoption-protective-stop-followup, 20260925_942_drift-followup-abandoned-alert, 20260925_937_host-liveness-monitor, 20260925_853_protective-leg-indeterminate-hold, 20260926_346_cutover-plan-decisions, 20260926_1028_report-kb-reingest, 20260926_1022_helm-release-drift, 20260926_856_reconciler-broker-action-map-and-metrics]
issues: [#13, #24, #121, #131, #132, #137, #141, #243, #262, #263, #267, #268, #303, #364, #380, #407, #627, #686, #705, #817, #891, #858, #942, #937, #853, #346, #1028, #1022, #856, MSP#266, MSP#635, planning#54]
-->


# 運用仕様書

> 必須ドキュメント（リポジトリ単位）。本リポジトリの運用を定める。雛形は `docs/templates/operations_spec_template.md`。
> **未記入のまま放置しない**。デプロイ・監視・バックアップ・障害対応を埋めること。

## 本書が受け持つ範囲

- 非機能要件（運用・可用性）: デプロイ・監視・障害対応、および**データ保持**（重複排除ストアの
  無期限肥大化の防止・#137）
- 関連する実装 ADR / 技術検討:
  - AST の k8s デプロイは Helm chart とし、共有インフラは MSP の platform-infra を参照する（K8s/Helm）
  - 取引サイクルの本番スケジューラは run-once HTTP トリガ＋`Collection:Trigger` モードとする（スケジューラ）
  - moomoo SIMULATE PoC 完了に基づき実アダプタを実装する（実弾は引き続きゲート）
  - 発注の冪等化は「発注前 `DecisionId` 予約」の 3 相で行い、不明な窓は再発注せず拒否する
  - 重複排除ストアは「終端行のみ・保持期間 90 日・下限クランプ付き」でパージし、未確定の行には触れない

## デプロイ

| 項目 | 内容 |
| --- | --- |
| 環境 | dev（ローカル k8s: k3d / Rancher Desktop 内蔵 k3s）/ stg・prod（k3s・#24） |
| 実行基盤 | Kubernetes。Helm chart [`deploy/helm/ai-stock-trading`](../../deploy/helm/ai-stock-trading)。共有インフラは MSP `platform-infra` を ExternalName で参照 |
| 手順（dev） | `scripts/k8s-local-images.sh`（10 Worker のビルド＆import）→ `scripts/k8s-local-deploy.sh`（ns/secret/helm）。詳細は chart README。fail-safe 既定（外部連携空=no-op / Broker=paper） |
| スケジューラ | 取引サイクルは既定 in-process。本番は `tradingCycle.cronjob.enabled=true` で K8s CronJob 駆動 |
| 発注経路（ブローカ階層） | 単一スイッチ `broker.tier`（`paper` ＜ `moomoo-sim` ＜ `moomoo-live`・#267。ブローカー選択は provider × environment の直交 2 軸で表現する）。**既定 `paper` ＝プロセス内蔵の擬似約定で moomoo へは接続しない**。`moomoo-sim` は OpenD 経由で moomoo 模擬口座へ実発注する別経路であり、**約定の主体・残高・注文履歴の所在が別**である（取り違え防止・識別手順は [発注経路の区別と識別 Runbook](broker-execution-paths-runbook.md)・#268）。`moomoo-live`（実弾）は未解禁＝描画時 `fail` |
| moomoo OpenD | 常駐モデル。dev は `deploy/opend/k8s` の生 manifest、**本番は chart の `opend.enabled=true`**。**初回のみ**有人のデバイス検証が要り、以降は「デバイス信頼の永続化＋egress IP の安定（＝ノード固定）」で無人再ログインが成立する。#13 は `opend:11111` へ **SIMULATE** 接続（実弾は撃たない） |
| リリースとチャートの差（設定の未反映） | **Pod の入れ替え（`kubectl rollout restart`・イメージの焼き直し・Reloader の再起動）では values・テンプレートの変更は入らない**。入れるのは `helm upgrade`（`k8s-local-deploy.sh`）だけである。配備の**前後**に `node scripts/helm-release-drift.js --release ast --namespace ai-stock-trading --values deploy/helm/ai-stock-trading/values-local.yaml`（**読み取り専用**・秘密の値を出さない）で差を出し、前は「入る差が意図どおりか」、後は「差が無い（終了コード 0）」を確かめる。🔴 **OpenD の Deployment は変わってはならない**（出力の 1 行目が「変化なし」。終了コード 3 なら配備しない）。手順と読み方は [chart README「配備の前後でリリースとチャートの差を確かめる」](../../deploy/helm/ai-stock-trading/README.md) |
| ロールバック | `helm rollback ast <revision>` もしくは Git revert（GitOps・#24） |
| GitOps（ArgoCD） | AST チャートの宣言的同期は [`deploy/argocd`](../../deploy/argocd/README.md)（Application/AppProject・#24。ローカル経路の GitOps は AST リポ内の opt-in manifest として整備する）。ブートストラップのみ kubectl・以降 Git 同期。ArgoCD 本体 install は MSP 共有 stand-up、実同期は Tier 3 |
| 秘匿情報 | 既定は k8s Secret 直（`ast-secrets` 手動）。Vault 化は opt-in（[Vault 秘匿 runbook](vault-secrets-runbook.md)・#24）。実充足は MSP stand-up＋Tier 3。**`k8s-local-deploy.sh` の再実行は env 未設定のキーに触れない**（投入済みの値を保持する。明示的な空指定だけはキー名を列挙して中断・#263。`ast-secrets` は差分パッチで同期する） |
| 為替レート（日本株） | **第一の情報源は日銀「外国為替市況（日次）」**（系列 `FXERD04`・`db=fm08`・**認証不要**・毎営業日公表／収録は翌々営業日 8:50 頃）。**`FRED_API_KEY` は必須前提ではなくフォールバック用**（無くても日銀単独で換算できるが冗長化が失われ、起動時に警告が 1 回出る・#686）。換算は判断境界の 1 点で行い、基準通貨は USD である（#262 / #364）。レート未解決＝JPY 建て銘柄は判断前に全件見送り（米国株は無影響）。設定点 `Fx__Provider=boj` は **`report` / `risk-management` / `trade-decision` の 3 サービス分**あり、**1 箇所だけ直すと挙動が食い違う**。手順・切り分けは [chart README「為替換算」](../../deploy/helm/ai-stock-trading/README.md) |
| 可観測性 | OTLP→otel-collector→Prometheus/Loki/Tempo（[可観測性仕様](../observability/observability.md)）。環境境界は [インフラ仕様](../infra/infra.md) |

> 環境境界（経路A/B／実基盤 Tier 3）と #24 受け入れ基準の充足状況は [インフラ仕様](../infra/infra.md) を単一情報源とする。

## OpenD の本番切替チェックリスト

> 起点: [#132](https://github.com/endazon/ai-stock-trading/issues/132)（OpenD 常駐の本番化・残検証）／
> 設計判断: OpenD 本番化は「既定 no-op の整備」として先行し、切替はゲート＋チェックリストで人手に残す／
> 仕様書: 仕様書: OpenD 本番化の整備
>
> **現在地**: 本番化に必要な**整備は済んでいる**（chart 化・ハードニングの切替口・秘匿の受け口・切替ゲート）が、
> **実測が要る項目は未充足**である。利用者方針は「**まずシミュレータ環境で全動作を確認してから本番移行**」。
> **本番稼働（実接続の常用・実弾）は未着手**であり、下表が埋まるまで切り替えない。

### 段階

| 段階 | 内容 | 状態 |
| --- | --- | --- |
| 1. 整備 | chart 化（`opend.enabled`）・パーミッション・秘匿受け口・切替ゲート・手順書 | **済** |
| 2. シミュレータ環境での全動作確認 | SIMULATE のまま、本番相当の配備（chart 経路）で一巡を確認する | **未** |
| 3. 本番移行（SIMULATE 常用） | 安定ノード・Vault・監視を整えて常駐運用 | **未** |
| 4. 実弾解禁 | **別の実装 ADR が要る**。本表と実アダプタ実装の実装 ADR §3 の前提がすべて充足してから | **未**（本 issue の対象外） |

### 前提条件（切替前に潰す）

| # | 前提 | 状態 | 確かめ方 / 担当 |
| --- | --- | --- | --- |
| 1 | **egress-IP 変更時に再検証が要るか**の切り分け | 🔴 **未充足** | 単一ノード（安定 egress IP）では Pod 再作成をまたぐ**無人再ログインが成立**すると確認済み（OpenD 常駐の実装 ADR の追検証）。**マルチノード/クラウド（egress IP 変動）での実測が未了**。ノードを跨ぐ再スケジュールを起こして再検証の有無を見る |
| 2 | **ノード固定**（egress IP の安定） | 🟡 **手段は用意済み・設定は運用側** | `opend.nodeSelector` / `affinity` を指定する（chart README）。**指定しないと #1 の危険に晒される** |
| 3 | `securityContext`（非 root 実行） | 🔴 **未充足**（切替口のみ） | イメージは uid/gid 10001 と `/home/opend` を用意済み。`opend.home=/home/opend` ＋ `securityContext` で切替（chart README）。**実 OpenD で未検証**。HOME 変更でデバイス信頼を失う恐れがあり、切替時は PVC の `.com.moomoo.OpenD` 移設か再検証が要る |
| 4 | `OpenD.xml`（`login_pwd_md5` を含む）のパーミッション | 🟢 **充足** | entrypoint が `umask 077` ＋ `chmod 600` で生成する |
| 5 | RSA 秘密鍵ファイルのパーミッション | 🟢 **充足** | Secret マウントを `defaultMode: 0400`（非 root 時は `fsGroup` ＋ `0440`）。entrypoint が実際のモードを起動時に検査し警告する |
| 6 | **資格情報の Vault / External Secrets 化** | 🔴 **未充足** | `ExternalSecret` の**受け口のみ**用意（`externalSecrets.enabled`・既定 false）。**ストア（Vault / ESO）は #24 の管掌で未整備**。受け口の存在は充足ではない |
| 7 | Hetzner（海外 IP）からの接続可否・**ToS** | 🔴 **未充足** | 人手の確認・契約判断（#24 / 証券会社連携の計画 ADR の未決事項） |
| 8 | 長期常駐の安定性・強制アップデート頻度 | 🔴 **未充足** | 実測（常駐させて観測する） |
| 9 | 取引パスワードのアンロック | 🔴 **未充足** | SIMULATE では不要な範囲の切り分けが要る（証券会社連携の計画 ADR が未決） |
| 10 | OpenD の**ログイン済み**判定（healthcheck） | 🟡 **限界を明示** | readiness は **TCP 疎通のみ**。OpenD は**検証前から listen する**ため、**probe 通過≠ログイン完了**。「使える」判定は `kubectl attach` でのログイン成功確認に依る。liveness は付けない（自動再起動が有人検証待ちの停止を招くため） |
| 11 | **発注予約 `Reserved` 滞留の監視・自動リコンサイル** | 🔴 **未充足** | 自動リコンサイルは配備で有効（下記「発注予約の自動リコンサイル」）で、**発注済みと確定できた側だけ**が自動で片付く。**未発注・判定不能は解放の門が閉じているため人手のまま**（下記 Runbook）であり、門を開ける判断（実機で「未発注」の誤判定が無いことの記録）は **#856** に残る。実弾では「発注済みか不明な注文」＝未確定の建玉を意味する（実アダプタ実装の実装 ADR §3） |
| 12 | `TradingDefaults`（リスク統制・上限）の**実弾向け再確認** | 🔴 **未充足** | 実弾解禁の実装 ADR の前提（実アダプタ実装の実装 ADR §3） |

> 🔴 が一つでも残る限り**実弾（`TrdEnv_Real`）は解禁しない**。解禁には**別の実装 ADR ＋ 明示 config** が要り、
> 現状のコードは `TrdEnv_Simulate` 固定・`BrokerFactory` の config ゲート・`Broker:Moomoo:TrdEnv` の拒否という
> **三重の閂**で塞いである。
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

アラートルールの実体は [`deploy/observability/alerts/ai-stock-trading-alerts.yaml`](../../deploy/observability/alerts/ai-stock-trading-alerts.yaml)
にあり、置き場所・命名・重大度の規約は
[`deploy/observability/README.md`](../../deploy/observability/README.md) が正本である（ここへ複写しない）。

| 監視対象 | 指標 | 閾値 | 通知先 |
| --- | --- | --- | --- |
| 保有状況が不明なための新規建て見送り（`AstEntriesBlockedByUnknownHoldings`） | `ast_trade_cycle_decision_skips_total{reason=~"HoldingsUnknownOpen\|WorkingEntriesUnknownOpen"}` | 15 分窓で 1 件以上が **30 分継続** | Alertmanager（配備は基盤側の共有 overlay） |
| 乖離の取り込みの追随の打ち切り（`AstDriftAdoptionFollowUpAbandoned`） | `ast_order_drift_adoption_followup_abandoned_total`（`reason`＝`positions-unknown` / `positions-query-failed`） | 15 分窓で 1 件以上が **1 分継続**（1 件で鳴る） | Alertmanager（配備は基盤側の共有 overlay） |

- 🔴 **この閾値は実測を要しない。** 対象の見送りは保有照会が**実結線のときにしか立たず**、
  **平常時の期待値が 0 件**だからである。「N 分間に M 件」という形の閾値は実測してから決める
  （[`../observability/observability.md`](../observability/observability.md)）。
- 🔴 **なぜこの 1 件目なのか**: 照会先の誤設定や恒久的な失敗が起きると、**手仕舞いは通るまま新規建てだけが
  静かに止まり続ける**。「取引が全部止まった」形にならないため、ログを読みに行かない限り誰も気付かない（`#891`）。
- **最初に見る場所**: 取引判断サービスの WARN ログ（「保有状況が不明なため新規建てを見送る」／
  「未約定の新規建て注文が不明なため新規建てを見送る」）と、リスク管理サービスの
  `GET /risk-controls/open-positions`・`GET /risk-controls/working-entry-orders`。設定では `RiskManagement:BaseUrl` を疑う。
- 内訳の読み分けは業務ダッシュボードのパネル「取引サイクル: 見送りの理由の内訳」で行う。
- 🔴 **2 件目（追随の打ち切り）も閾値に実測を要しない。** 数えるのは建玉照会の不明・失敗のまま**再試行を使い切った**
  打ち切りだけで（途中の失敗は数えない）、平常時の期待値は 0 件である。系列は発注執行の起動完了時に 0 で作られる。
  取引時間に依らず、系列が無い（サービス停止・送出の断）ときは鳴らない。
  🔴 **解消（resolved）は `_error` が空になったことを意味しない**（1 件の打ち切りは 15 分窓を抜けると解消になるが、
  メッセージは再投入するまで残る）。対応は下の障害対応表の「乖離の取り込みが保護注文に追随しないまま `_error` キューに残る」行。
- **`_error` キュー全般の滞留は監視していない。** RabbitMQ のキュー長はどこからも scrape されていないため、
  `order-approved_error` などほかの `_error` キューは、引き続き RabbitMQ 管理画面とログで見る。

### クラスタの外からの死活監視（ホスト側）

- 🔴 **上のアラートはすべてクラスタの中で動き、クラスタ（ホスト）と一緒に止まる。** ホストの再起動で Rancher Desktop が
  起動しなかった間、ソフトウェア逆指値（S1）は働かず、そのことはどこからも知らされなかった（#937）。
- ホストの上で `scripts/host-liveness-monitor.ps1` を 5 分ごとに走らせ、**米国株の通常取引時間だけ** Kubernetes API・
  市場監視と発注執行の Pod・2 つの生存要約（損切り評価・S1 の保護記録）の鮮度を**読む**。通知はクラスタに依らない経路
  （Windows の通知・任意の Discord Webhook・ログファイル）だけで行う。**登録・自動起動・Windows Update の設定はオーナーが行う**
  （[ホスト側の死活監視 Runbook](host-liveness-monitor-runbook.md)）。
- **ホストそのものが止まっている・ログオンしていない間は、この監視も止まっている**（ホストの外から見張る仕組みは無い）。

## バックアップ・リストア

> 🔴 **未裁定の案である。** 保管先・保管期間・リストア試験の頻度は利用者が決める（再実装版への切替の移行仕様書 §承認事項 2）。
> **現状（2026-09-26 実測）: バックアップは 1 本も無い。** クラスタに CronJob は無く、業務台帳の入った `postgres-data`
> （local-path・PV の回収方針 `Delete`）はクラスタや namespace を作り直すと消える。**7 年保持の台帳が 1 回の作り直しで全損する状態である。**

| 項目 | 案（利用者が決める） |
| --- | --- |
| 対象 | 本システムの 7 DB（`audit_svc` `configuration_svc` `cost_control_svc` `market_monitor_svc` `order_execution_svc` `report_svc` `risk_management_svc`）。全数表は移行仕様書 §保全対象の全数表。**秘密情報（Vault の `ai-stock-trading/*`）も対象**だが方式は基盤の Vault の構成に依る（下記） |
| 頻度 | 日次（米国市場の引け後・日本の寄り前。JST 07:00 前後）＋切替・スキーマを変える配備の前 |
| 保管期間 | 日次の世代は 30 日。**月初の世代と切替前の世代は 7 年**（業務台帳・監査証跡の保持要件） |
| 保管先 | **クラスタの外に 2 か所**（例: ホストの暗号化された置き場＋外部のオブジェクトストレージ）。dump は監査ログの本文（銘柄・判断根拠）を含むため**暗号化して置く**。リポジトリ配下には置かない |
| RPO / RTO | RPO 24 時間・RTO 1 時間（案） |
| リストア試験 | 四半期に 1 回と切替の前。**別名の DB へ戻して件数を突き合わせる**（本番の DB は触らない） |

取得（利用者が実行する。値の出力先はクラスタの外）:

```bash
ts=$(date -u +%Y%m%dT%H%M%SZ); out="<クラスタ外の保管先>/ast-$ts"; mkdir -p "$out"
for d in audit_svc configuration_svc cost_control_svc market_monitor_svc order_execution_svc report_svc risk_management_svc; do
  kubectl -n platform-infra exec deploy/postgres -- pg_dump -U ai -Fc "$d" > "$out/$d.dump"
done
( cd "$out" && sha256sum *.dump > SHA256SUMS )
AST_PSQL="kubectl -n platform-infra exec -i deploy/postgres -- psql -U ai" \
  bash scripts/cutover-count-reconcile.sh snapshot "$out/counts.tsv"   # 取得時点の件数と指紋（リストア試験の基準）
```

リストア試験（別名の DB `restore_test_<db>` へ戻し、同じ manifest で測って比べる。**このブロックだけで完結する**——取得のブロックの変数は使わない）:

```bash
# 試験する世代のディレクトリ（取得のブロックが作った ast-<時刻>。中に <db>.dump・SHA256SUMS・counts.tsv がある）
src="<クラスタ外の保管先>/ast-<試験する世代の時刻>"
dbs="audit_svc configuration_svc cost_control_svc market_monitor_svc order_execution_svc report_svc risk_management_svc"
psql_cmd="kubectl -n platform-infra exec -i deploy/postgres -- psql -U ai"

( cd "$src" && sha256sum -c SHA256SUMS )                 # 保管中に壊れていないこと
for d in $dbs; do
  kubectl -n platform-infra exec deploy/postgres -- createdb -U ai -O ai "restore_test_$d"
  kubectl -n platform-infra exec -i deploy/postgres -- pg_restore -U ai -d "restore_test_$d" < "$src/$d.dump"
done
AST_PSQL="$psql_cmd" AST_DB_PREFIX=restore_test_ bash scripts/cutover-count-reconcile.sh snapshot restored.tsv
bash scripts/cutover-count-reconcile.sh compare "$src/counts.tsv" restored.tsv   # exit 0 で合格（下の注意を参照）
for d in $dbs; do
  kubectl -n platform-infra exec deploy/postgres -- dropdb -U ai "restore_test_$d"   # 試験用の別名 DB だけを消す
done
```

- **`compare` が FAIL 0 で通るのは、凍結中（移行仕様書の手順 1: kill switch と `replicas=0`）に取った dump だけである。**
  稼働中に取ると、件数の snapshot と dump のあいだに監査ログが増え、統制状態の行も更新されるため、`compare` は件数・指紋の差を FAIL にする
  （2026-09-03 のリハーサルで実測: 監査が 2 行増えただけで FAIL 3 件）。日次の稼働中の取得を試験するときは、
  **FAIL の内訳が「取得中に書かれ得るテーブル」の件数の増加・指紋の差だけで、減少と `order_dispatch_reservations` の未確定件数の減少が無い**ことを目で確かめる。
- **Vault**: 値を平文で書き出す方式（`vault kv get` の JSON をファイルへ落とす等）は採らない。基盤の Vault のストレージ種別に合った
  スナップショット（または `vault-data` のボリュームの暗号化されたコピー）を基盤と揃えて決める（未決）。
- 本番の DB へのリストアは切替のロールバック（移行仕様書 §ロールバック・リスク）でだけ行う。**リストアの前に現状も dump する。**

## 基盤の切替の後の KB への入れ直し

基盤は自分の切替で文書 DB と索引を破棄する。確定報告書の KB 上の写しも消えるが、正は `report_svc` の `reports`（本文つき）に残る。
基盤の切替の後に、報告書サービスの所有者専用の操作で**確定済みの報告書を KB へ入れ直す**（本文なしで入った古い写しの修復にも使える）。

1. **先に基盤のタグ辞書を登録する**（[KB タグ辞書登録 Runbook](kb-tag-dictionary-runbook.md)）。文書 DB と一緒にタグ辞書が消えていると、
   作成は未登録タグの 400 で `Failed` になる（理由に基盤の応答が出る）。
2. 所有者（`trading-owner`）のトークンで 1 回呼ぶ。全件は `all: true` を明示する（範囲なら `fromPeriodKey` / `toPeriodKey`。期間キーは
   `daily-yyyy-MM-dd` / `weekly-yyyy-Www` / `monthly-yyyy-MM`）。

   ```bash
   curl -sS -X POST "<報告書サービスの URL>/reports/knowledge-base/reingest" \
     -H "Authorization: Bearer <所有者のアクセストークン>" -H "Content-Type: application/json" \
     -d '{"all": true}'
   ```

3. 応答を読む。**200 以外（503＝KB 未構成・502＝KB の文書一覧を引けなかった）は 1 件も書いていない**ので、原因を直してそのまま再実行する。
   200 でも報告書ごとの結果（`items[].outcome`）を確かめる。

   | 結果 | 意味 | 次の手 |
   | --- | --- | --- |
   | `Created` / `BodyAttached` / `BodyRefreshed` | 送った（作った・本文の無い写しに本文を入れた・本文を入れ直した） | なし |
   | `AlreadyPresent` | 本文つきの写しが既に在る | なし |
   | `SkippedEmptyBody` / `SkippedBodyTooLarge` | 送らなかった（本文が空＝手動確定など・本文が 1 MB 超） | 必要なら本文を持つ版を作り直す |
   | `Failed` | 拒否された・届かなかった（理由つき） | 理由を直して再実行する。理由が「別の主体が所有」なら、その写し（`documentId`）を基盤の管理者が削除してから再実行する |
   | `Unknown` | タイムアウト等で結果が分からない | **1 分ほど待ってから再実行する**（入っていれば一覧で見つかるので作り直さない。直後だと基盤側でまだ処理中の作成が一覧に現れず、2 つ目を作り得る） |

4. **写しの見つけ方と、作らない場合**。一覧の文書のうち、期間キーと種別が一致し、`project=ai-stock-trading` を持つもの、または
   `project` を持たず表題が `確定報告書 <種別> <期間キー>`（例 `確定報告書 Daily daily-2026-07-10`）と完全に一致するもの
   （2026-09-03 より前に保存された写し。それより前に本文なしで入った写しはこの形。2026-09-03 以降の手動確定で本文なしで入った写しは `project` を持つので前の条件で見つかる）を、その報告書の写しとみなす。
   **写しが 1 件でもあれば新しく作らない。** 本文の無い写しには本文を入れる（文書は増えない）。旧い写しは本システムの資格で書けない
   （所有者が本システムの KB 用クライアントではない）ことがあり、そのときは `Failed`（別の主体の所有）になる——基盤の管理者が削除してから再実行すると作り直される。
   表題を変えた `project` なしの文書は写しと判定できないので、隣に新しく作る。
5. **同じ条件で何度実行しても KB の件数は増えない**（上の条件で写しを探してから書く。例外は上の `Unknown` の直後の再実行だけ）。
   索引だけが消えて文書が残った場合は `refreshExisting: true` で本文を入れ直す（文書は増えない）。同じ報告書の写しが複数あると、
   その行の `matchedCopies` が 2 以上になり、件数が `duplicatesInKb` に出る（本システムの資格では消せない）。
6. 実行は監査台帳に `ReportKnowledgeReingested`（操作者・範囲・件数・送らなかった／失敗／不明の内訳・写しが重複した期間キー）として残る。
   **同時に 2 本は走らない（409）が、この排他は報告書サービスの 1 プロセスの中だけ**である。レプリカを増やしている間や、ローリング更新で新旧の
   Pod が重なっている間は 2 本が同時に走り得て、重複を作り得る。更新の最中には実行しない。

## メッセージング（RabbitMQ のキュー）

- キュー名は **`<ServiceName>.<メッセージ型名>`**（例 `ai-stock-trading.risk-management-service.TradeDecisionMade`）。
  デッドレターは **`<queue>_error`**。いずれもサービス起動時に自動生成される（AutoProvision）。
  規則の根拠は Wolverine 移行のトポロジ設計（キュー名にサービス名を前置し、ローカルルーティングを無効化する）である。
- **キュー名から所有サービスが読める**（接頭辞）。`consumers = 0` のキューは所有サービスが購読できていない印である。
- 移行前（MassTransit）の旧キュー 47 本はブローカ上に残るため、Wolverine 版の安定稼働後に削除する:
  [旧キュー削除 Runbook](wolverine-queue-cleanup-runbook.md)。

## データ保持・パージ（#137。重複排除ストアは「終端行のみ・保持期間 90 日・下限クランプ付き」でパージする）

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

## 発注予約の自動リコンサイル（#141。プローブ・ポート＋fail-safe 既定 no-op で行い、実照会は後続へ分離する）

`Reserved` 滞留（上の Runbook で人手対応する「発注済みか不明な予約」）を、ブローカ照会で自動解消する
バックグラウンド機構（`OrderReservationReconciliationService`）。**二重発注を絶対に起こさない fail-safe** を守る。

- **判定**: 滞留閾値より古い `Reserved` を走査し、次の 6 分岐へ振り分ける。右列は巡回サマリの内訳名である。

  | # | 条件 | すること | サマリの内訳 |
  | --- | --- | --- | --- |
  | ① | `executed_orders` に記録あり | 確定の自己修復（ブローカへ照会しない） | `終端化` |
  | ② | 照会が発注済み（`Placed`） | 記録して確定＋`OrderExecuted` 発行。続けて、エントリーなら承認時の手法で保護レグを張る（下記） | `終端化` |
  | ③ | 照会が未発注（`NotPlaced`）**かつ解放の門が開いている** | 予約を解放（＝再発注を許可） | `解放` |
  | ④ | 照会が未発注（`NotPlaced`）**だが解放の門が閉じている** | **据え置き**（解放しない） | `据え置き〔未発注だが門が閉〕` |
  | ⑤ | 照会不達・判定不能（`Indeterminate`） | **据え置き**（人手/`_error`・解放しない） | `不確定` |
  | ⑥ | 当該 1 件の処理で例外 | **据え置き**（次回巡回で再試行。他の件は巻き添えにしない） | `失敗` |

  🔴 **配備では解放の門が閉じている**（`Reconciliation__ReleaseOnNotPlaced=false`）ため、**③は起こらない**——
  未発注と出ても④になる。したがって `解放` は**常に 0** であり、0 は故障ではない。
- **ブローカーへ触れる操作**（#856 で棚卸しした）: 照会は**読み取りだけ**（証券会社の注文一覧を備考で突き合わせる）。
  リコンサイラ自身は発注も取消もしない。**ブローカーへの書き込みが起き得るのは①②で確定した 1 件の後だけ**で、
  承認時の保護の文脈（エントリーを送る前に残した記録）が残っているエントリーに限り、平常の発注と同じ経路で
  逆指値（S0）か代替注文種別（S3）を 1 本送る。その逆指値が拒否されたら、未約定ならエントリーを取り消し、約定済みなら成行で手仕舞う。
  逆指値が届いたか不明なら、取消も成行もせず据え置く（2026-09-25 のオーナー裁定。#853）。
  🔴 **③〜⑥では何も書かない。** ③の解放も書き込みではない——予約を消して再発注を**許可**するだけで、送り直すのは
  `_error` キューからの再投入か保護逆指値ガードの次の巡回である（門を閉じているのはこのため）。
- **プローブ**: 既定の照会実装は**常に `Indeterminate`**（`IndeterminateReservationBrokerProbe`）。
  実 OpenD 照会（`DecisionId` を備考〔remark〕として伝播し注文一覧と突合する）は
  `Reconciliation__UseBrokerProbe=true` かつ証券会社が moomoo のときだけ配線される。
  それ以外（paper 構成）では `Placed`/`NotPlaced` 経路は発火せず、自己修復（①）のみが作動する（ブローカ非依存）。

### 設定（アプリ既定は無効。配備〔Helm values〕で有効化している）

```yaml
Reconciliation:
  Enabled: false # アプリ既定。配備は true
  UseBrokerProbe: false # アプリ既定。配備は true（moomoo のときだけ実照会が配線される）
  ReleaseOnNotPlaced: false # 🔴 解放の門。アプリ既定・配備ともに false
  StallThresholdHours: 24 # アプリ既定。配備は 2（滞留とみなす経過時間。下限 1 時間・再配送窓の外側）
  IntervalHours: 6 # アプリ既定。配備は 1（巡回間隔。下限 1 時間）
  BatchSize: 200 # アプリ既定。配備は 50（1 巡回あたりの最大処理件数）
```

> `BatchSize` を既定より下げているのは、1 予約あたり最大 4 往復（全対応市場 × 現在＋履歴）を
> **発注アダプタと単一インスタンスを共有する** OpenD 接続へ流すためである（返信待ちの既定は 15 秒）。
> 200 件だと最悪 800 往復が 1 バーストで同じ接続に乗り、**発注そのものを詰まらせ得る**。
> 溢れた分は次の巡回（1 時間後）が拾う——滞留の解消は急がなくてよいが、発注は待てない。

- **配備での実値**は `deploy/helm/ai-stock-trading/values.yaml` の `services.order-execution.extraEnv` が持つ。
  アプリ側の既定を反転させていないのは、docker-compose・単体開発環境の挙動を変えないためである。
  無効のまま起動するとログに「発注予約の自動リコンサイルは無効です（Reconciliation:Enabled=false）」が出る
  ——**配備でこの行が出たら、構成が Pod に届いていない**ということである。values を変えた後に Pod の入れ替えだけで済ませると
  こうなる（values は `helm upgrade` でしか入らない。実際に 8 日間届いていなかった）。配備の前後に上の「リリースとチャートの差」の手順で確かめる。
- 🔴 **`ReleaseOnNotPlaced`（解放の門）は閉じたままにする。** 解放は予約行を消して**再発注を許可する**操作であり、
  誤判定は二重発注に直結する。「未発注」の根拠は備考の突合であって証券会社が「無い」と答えた事実ではなく、
  SIMULATE が備考を往復させるかは**実機未検証**である（往復していなければ発注済みの注文も「一致ゼロ」に見える）。
  **`true` にしてよいのは、実機で誤判定が無いことを記録つきで示した後だけである。**
- **`Indeterminate` の意味（運用上の要点）**: 照会が「不明」＝**解放しない**。門の開閉に依らない。
  これは二重発注を招く解放を構造的に封じる安全既定であり、想定挙動である。
- **監視すべきログ**:
  - 各巡回の
    `発注予約リコンサイル: 滞留 N 件を走査（終端化 … / 解放 … / 据え置き〔未発注だが門が閉〕 … / 不確定 … / 失敗 …）`。
    内訳は上の「判定」の 6 分岐に 1 対 1 で対応する（①②はどちらも `終端化`）。
    **解放の門が閉じている現在の配備では、5 つの合計は必ず `N` になる。**
    ⚠️ 門を開けると合計が `N` を下回り得る——③で解放しようとした行が並行に消えていた場合
    （`Release()` が false を返す）は、**どの内訳にも計上されない**ためである。
    `解放` は解放の門を開けるまで常に 0 であり、**0 は故障ではない**。
    `据え置き〔未発注だが門が閉〕` が増え続けるなら、
    門を開ける判断（実機での誤判定の確認）が滞っているということである。
    ⚠️ この内訳名は門を開けても変わらない（そのときは値が 0 になるだけである）。
    `失敗` が継続的に出る場合は照会・保存の恒常障害（DB 権限・接続・プローブ実装の不具合）を疑う。
  - 🔴 **Critical** `…突合で「発注済み」と確定しました…この時点では保護レグがありません。承認時の手法で続けて張ります…`。
    確定した時点では、エントリーであれば保護レグが無い。**直後の行**が結果である——`…保護逆指値を張りました…` /
    `…ソフトウェア逆指値（S1）で守られています…` なら対応不要、`…保護の記録がありません…` / `…保護レグを張る処理が失敗しました…`
    （Critical）や直後の行が無いときは、証券会社の画面で保護レグの有無を確認する（詳細は発注経路の Runbook）。
  - **Warning** `…照会は「未発注」と答えましたが、解放の門が閉じているため据え置きます…`。
    滞留は解消していない。Runbook の人手手順で解決する。
  - **業務メトリクス** `ast_order_reservation_reconciliations_total{outcome}`（#856）。上の 6 分岐を件数で数える
    （①＝`self-healed`・②＝`probe-placed`・③＝`released`・④＝`held-not-placed`・⑤＝`indeterminate`・⑥＝`failed`）。
    ログの grep と違い保持期間に縛られないので、**解放の門を開けてよいかの観測**（#856）に使う——証券会社の画面で存在を確かめた注文の
    `DecisionId` について `held-not-placed` が出ていたら、門を開けてはならない（備考が往復していない）。
    ④⑤⑥の予約は Reserved のまま次の巡回にも載るため、**同じ予約が巡回ごとに数え直される**（件数は判定の回数であり、予約の数ではない）。
    リコンサイルが有効な構成でだけ、起動完了時に 6 系列が 0 で現れる。アラートは置いていない。
- **停止**: `Reconciliation__Enabled=false` に戻して再デプロイすれば次回巡回から走査しない。

## LLM 単価の定期見直し（#303。LLM 費用のモデル別単価解決）

LLM 費用は**応答が名乗った実効モデル**の単価（`LlmPricing__PerModel__<model-id>__*`・円/1k トークン）で計上する。
**env 名ではモデル ID の `-` を `_` で書く**（例: `LlmPricing__PerModel__claude_sonnet_5__InputPer1kTokens`）。
コンテナはシェル経由で起動するため、`-` を含む env 名はプロセスへ届かず、単価表が空のまま全呼び出しが 0 円で計上される
（照合側は `-` と `_` を同一視する）。LLM ゲートウェイを構成しているのに単価が実質 0 なら、trade-decision / report が
起動時に `LLM 単価が未設定` で始まる WARNING を出す。**反映後は起動ログにこの WARNING が無いこと、
`LLM 費用計上イベントを発行 … amount=` が 0 より大きいことを確認する。**
単価は外部の公開価格と為替から導いた値であり、**恒久値ではない**。放置すると月次上限（¥15,000）の判定が
実態からずれる（過大なら取引機会を失い、過小なら上限を素通りする）。

| 見直し契機 | 期日・条件 | 対応 |
| --- | --- | --- |
| ~~`claude-sonnet-5` の導入価格終了~~ | ~~2026-08-31~~（**解消済み・2026-08-28 確認**） | Anthropic が $2/$10 を恒久価格にすると公式発表し、2026-09-01 予定の $3/$15 改定は行われない。単価は変更不要（`values-local.yaml` は据え置き）。#243 で確認 |
| 公開単価の改定・モデルの追加 | 基盤の `Llm:Routing:PurposeModels` が変わったとき | 新しい実効モデルの行を表へ足す。**足さないと最大単価で過大計上される**（安全側だが実態とずれる） |
| 為替の乖離 | USD→JPY が投入時の **163.71**（FRED `DEXJPUS`）から大きく離れたとき | 換算し直して表を更新する |

- 実測に基づく再ベースライン（実消費と月次上限の妥当性）は
  [#243](https://github.com/endazon/ai-stock-trading/issues/243)、計画側の上限評価は
  計画リポジトリの担当 issue が担う。本節は**単価の鮮度**のみを扱う。
- 単価の出所・換算・丸めは `deploy/helm/ai-stock-trading/README.md`「LLM 費用の単価」に表で残している。
- 本番（ArgoCD＝`values.yaml`）には単価を置かない。よって本番の計上は従来どおり ¥0 であり、
  本節の見直しは経路B（`values-local.yaml`）に対して行う。

## Stage 1 の営業日カウントと市場の祝日（#407。祝日を判別しないのは裁定であり、祝日表を足すことは裁定違反である）

**Stage 1 の「60 営業日」は市場の祝日を除外しない。** これは未実装ではなく、
**利用者裁定（2026-08-07・質問票 第13回 Q3 案2）で「そうする」と決まった設計**である
（計画側のデイトレード方針レビュー §4.2「**分母と除外の判定に外部カレンダーを用いない**」）。

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
- **統制状態参照画面の Stage 1 進捗（営業日数）を読むときは、この過大計上を織り込むこと。**
  厳密な営業日数が要る場面（監査・報告）では、対象期間の米国市場の休場日を人手で差し引いて評価する。

### 🔴 やってはならないこと

**祝日表・休場日リスト・外部カレンダーを実装へ足すこと**は**裁定違反**である。
「未実装の項目」に見えるため善意で足される危険が最も高い箇所であり、
構造テスト（`Stage1SessionUptimeTests.カレンダーを内蔵していない__同じ稼働なら曜日だけで結果が決まる`）が
**3 年ぶんの全日付**で機械的に止める。**このテストを消さないこと。**

## 障害対応（Runbook）

| 事象 | 検知 | 一次対応 | エスカレーション |
| --- | --- | --- | --- |
| **禁止銘柄の建玉が手仕舞えない**（#380。取引ガードの計画 ADR の 2026-08-04 追補） | 手仕舞い注文が拒否理由 **`BannedSymbol`** で拒否される。**不具合ではなく設計どおり**である——禁止銘柄ガードは新規建てだけでなく**手仕舞いにも適用する**（理由: **インサイダー取引は売付けも対象**であり、AI が利用者の関知しないタイミングで規制対象銘柄を自動売却する経路を残さない） | **一時解除 → 手仕舞い → 再登録**（[禁止銘柄の一時解除 Runbook](banned-symbol-unlock-runbook.md) が単一情報源）。解除・再登録はアクターと理由が必須で、日時・対象銘柄とともに設定変更履歴へ自動で残る | **再登録の忘れを検知する仕組みは無い**（解除しっぱなしでも警告は出ない）。解除中は当該銘柄への新規建ても通るため、手順の所要時間を最小にする |
| **発注予約が `Reserved` のまま滞留**（#131。発注の冪等化は 3 相で行い、不明な窓は再発注せず拒否する。自動化は #141） | `order-approved_error` キューの滞留。および `order_dispatch_reservations` に `State=Reserved`（＝0）の行が残る（`SELECT * FROM order_dispatch_reservations WHERE "State" = 0 ORDER BY "ReservedAt";`） | **自動再開はしない**（意図的な at-most-once）。配備では自動リコンサイルが有効で実照会プローブも配線済みなので、**「発注済み」と確定できたものは自動で解消される**（エントリーなら続けて承認時の手法で保護レグを張る〔2026-09-25 改定。旧: 張らなかった〕。確定の Critical ログと直後の結果の行を見て確認する）。**「未発注」「判定不能」は解放の門が閉じているため据え置かれる**ので手動で: ブローカ側の注文状態を確認し、①発注済み→当該注文を台帳へ手動計上して予約を確定／②未発注→予約行を削除して再配送を許可 | **不明なら「発注済み」として扱う**（二重発注を避ける側に倒す）。実弾運用中は建玉と突き合わせ、判断が付かなければ取引を停止して人間が判断する |
| **重複排除ストアが肥大化する**（#137。終端行のみを保持期間でパージする） | 「データ保持・パージ」の確認クエリで、保持期間より古い行が減らない | パージジョブが有効か確認する（既定は**無効**）。ログに「パージは無効です（Retention:Enabled=false）」が出ていれば `Retention__Enabled=true` で有効化する。有効なのに減らない場合はパージ失敗のエラーログ（DB 権限・接続）を確認する | 行量に対して 1 巡回の削除上限が小さすぎる場合は `BatchSize` / `IntervalHours` を調整する。恒常的に追いつかないならパーティション化を検討（パージ方針の代替案） |
| **パージを止めたい**（誤設定・調査中） | — | `Retention__Enabled=false` に戻して再デプロイすれば次回巡回から no-op になる | **削除済みの行は戻らない**。`RetentionDays` を短く誤設定していた場合、重複排除の記憶が消えた期間に再配信が起きると二重計上／二重発注の可能性があるため、費用台帳・発注履歴の重複を確認する |
| **日本株だけ何も起きない**（米国株は判断・発注が回る）（#262 / #364。基準通貨は USD で、換算は判断境界の 1 点で行う） | trade-decision のログ `基準通貨への換算レートが解決できないため見送り（発注抑止・安全側）: {Symbol} market=Japan`、および初回 1 回の `NoOpFxRateSource を使用中: …`。確定判定は `GET /internal/introspection` の `fx-rate` ポートが `none` を申告すること | 為替レート源が未接続。**設定点は `Fx__Provider=boj`（日銀・認証不要）で、3 サービス分ある**（#686 以降。`fred` は鍵があるときだけ後段に積まれるフォールバックであり、第一に据えない）。`fx-rate` が `boj` を申告することを確認する（手順は [chart README「為替換算」](../../deploy/helm/ai-stock-trading/README.md)）。`Fx__Provider=fred` は鍵が空なら `none` を申告する＝「設定したのに効いていない」の検知点。**`boj` は認証不要のため鍵の有無で `none` へ倒れない** | `none` のままなら provider 名の誤り（未知の値は警告して no-op）か、`Fx__Provider` を空へ戻している。`boj` 申告でも見送りが続く場合は日銀側の収録停止を疑う（**鮮度上限 30 日**超過は採らない。上限・警告しきい値はデータ源の公表周期から計画が定めた値）。日銀は**毎営業日**公表だが**実装が読む経路への収録は翌々営業日 8:50 頃**であり、**最新観測が 2〜4 日前でも正常**である。FRED へフォールバック中は `DEXJPUS` の公表が **H.10 週次リリース**（月曜・前週金曜まで一括収載／月曜が祝日なら火曜）であるため**最新観測が 10 日前でも正常**（警告しきい値 5 日を常に超えうる）。**見送り自体は fail-safe であり緊急停止は不要**（古い/無いレートで発注しない・主ターゲットの米国株の取引は継続する） |
| **再デプロイ後に外部連携（実市況・為替・KB・Discord）が静かに止まる**（#263。`ast-secrets` は差分パッチで同期し、明示的な空上書きだけを中断で防ぐ） | デプロイは成功するのに各アダプタが no-op 警告を出し、`GET /internal/introspection` の該当ポートが `none` を申告する。`kubectl -n ai-stock-trading get secret ast-secrets -o go-template='{{range $k,$v := .data}}{{if not $v}}{{$k}}{{"\n"}}{{end}}{{end}}'` で**空値のキー名**を列挙できる（値は出さない） | `ast-secrets` の値が空で上書きされている。現行の `scripts/k8s-local-deploy.sh` は **env 未設定のキーに触れない**ため再発しないが、旧版で潰された値は戻らない。当該 env を `export` して再実行し、値を入れ直す | 鍵の実値はリポジトリ・ログ・チャットに残さない（端末外へ出さない）。**明示的に空を指定した場合のみ**スクリプトはキー名を列挙して中断する（意図した消去は `--force-empty-secrets`）。Vault（ESO）同期を有効化した環境では `ast-secrets` は ExternalSecret が所有するため、値の投入は [Vault 秘匿 runbook](vault-secrets-runbook.md) 側で行う |
| **KB 保存が未登録タグで 400 になり全件失敗する**（#705。基盤のタグ辞書検証が未登録タグを拒否する） | 収集サイクル・報告確定のログに `KB 保存: 0/N` が継続出力される | 監視銘柄コードのような**運用中に増える動的な値をタグへ載せていないか**を確認する（属性へは載せてよい。単値完全一致フィルタで絞り込める）。登録すべき静的タグ一覧の生成は [KB タグ辞書登録 Runbook](kb-tag-dictionary-runbook.md) の手順に従う | タグ辞書への実登録操作は基盤（document-service）側の所有物であり、本リポジトリからは登録 API の有無を確認できない。実 KB での `N/N`（N=N）確認は接続性の残件（[ブロック中のタスク](../blocked-tasks.md) A-12）に依存する |
| **新規建てだけが一切通らない**（#869。自己資金の供給元はブローカーの口座照会であり、照会できないときは新規建てを拒否する） | 発注審査が拒否理由 **`CapitalBaselineUnavailable`** を返し、`/status` の資金が「取得できていません」と表示される。**手仕舞い・損切りは通る** | **不具合ではなく設計どおりの fail-closed**である。まず巡回（口座照会）が生きているかをログで確認する。🔴 **当日より前の取引日の観測が 1 件も無い場合（平日の日中に配備した直後など）は、その日は一日中止まる**——急ぐなら[基準資金の供給が無いときの Runbook](capital-baseline-seed-runbook.md) の手順で 1 行を投入する | 口座照会そのものが通らない（OpenD 不達・口座状態）場合は発注経路の問題であり、[発注経路の区別と識別 Runbook](broker-execution-paths-runbook.md) 側の切り分けへ移る |
| **乖離の取り込みが保護注文に追随しないまま `_error` キューに残る**（#858。取り込みで消えた建玉の保護逆指値を取り消す前に、発注執行はブローカーの建玉を照会し直す。照会が不明・失敗なら何も変えずに打ち切る） | キュー **`ai-stock-trading.order-execution-service.PositionDriftAdopted_error`** にメッセージが溜まる。入るのは「利用者が承認した乖離の取り込み」のうち、**建玉照会が 4 回（初回＋再試行 2s/10s/30s の 3 回）続けて不明または失敗した**ものだけである。🔴 **見えるのは発注執行サービスの Critical ログ 4 行だけ**（「乖離の取り込みの追随で建玉を照会できませんでした（不明または失敗）…」。取り込み ID・銘柄・台帳の前後が載る）で、**Discord には何も届かない**（打ち切った処理の発行は Wolverine が捨てるため、通知も監査も出ない）。**検知はアラート `AstDriftAdoptionFollowUpAbandoned`（warning）で行う**——4 回目の失敗（＝`_error` へ送られる時点）で業務メトリクス `ast_order_drift_adoption_followup_abandoned_total` が 1 増え、15 分窓で 1 件以上が 1 分続くと上がる。最後の 1 行の Critical ログは「再試行を使い切ったため、このメッセージは _error キューへ送られます」と書く。🔴 **アラートの解消はキューが空になったことを意味しない**（15 分窓を抜けると解消になる）。キューの滞留そのものは引き続き RabbitMQ 管理画面で見る | 保護記録もブローカー側の保護逆指値も**取り込み前のまま**である（建玉が消えたと確かめられないまま保護を消さない、が設計）。まず**証券会社の画面で建玉と未約定の逆指値を確認する**——🔴 **建玉が本当に 0 なのに売りの逆指値が残っていると、発火したとき意図しないショートが建つ**ので、急ぐなら画面から逆指値を取り消す。次に建玉照会（OpenD）が回復したことを発注執行のログで確かめてから、`_error` キューのメッセージを元のキュー `ai-stock-trading.order-execution-service.PositionDriftAdopted` へ戻す（RabbitMQ 管理画面）。**再投入は何度行っても安全**である——目標は取り込み後の数量（絶対値）で差分ではなく、二重に取り消さない。追随は再投入の時点で建玉を照会し直し、その時点で建玉があれば保護を消さない。画面から先に逆指値を取り消していた場合、再投入の取消が「取り消せた」と確認できれば記録は終端化され、確認できなければ Discord に「取り込みで消えた建玉の保護注文を取り消せていません」（Critical）が出て記録は巡回に残る——どちらも保護を失う側には倒れない（後者は画面で逆指値が無いことを確かめ済みなら既知として扱う） | 照会が回復しない（OpenD 不達・口座状態）なら[発注経路の区別と識別 Runbook](broker-execution-paths-runbook.md) 側の切り分けへ移る。メッセージを**消さない**——消すと保護記録が `Active` のまま残り、ガードの巡回が実在しない建玉の保護を見続ける |

> **`Reserved` 滞留の発生条件**: ブローカ発注の前後でプロセスが落ちる／DB が書けない場合に限る。moomoo の
> API 瞬断・不達そのものは `MoomooBrokerAdapter` が終端 `Rejected` へ倒すため、滞留にはならない。
>
> **実弾（`TrdEnv_Real`）解禁の前提**: 上記の検知（滞留の監視・アラート）と自動リコンサイル（**#141**）を
> 整備してから解禁すること。滞留は「発注済みか不明な注文」＝実弾では未確定の建玉を意味する（実アダプタ実装の実装 ADR §3）。
>
> **保持期間パージとの関係**: パージジョブは `State=Reserved` に一切触れないため、
> 滞留行が自動で消えることはない。滞留の解消は本 Runbook の手順（または #141）だけが行う。

## 関連文書

| 文書 | 本書との関係 |
| --- | --- |
| [セキュリティ仕様書](../security/security.md) | **認証・認可／データ保護／秘密情報管理／監査ログ／脅威と対策**。運用者が触る統制（`ast-secrets` の投入・Vault 化の充足状況・監査ログの記録項目と**保持期間が未実装であること**）はすべて同書に実測で書いてある。**本書の「データ保持・パージ」は重複排除ストア 2 つだけを対象とし、`audit_events` は対象外である —— それが「7 年保持が担保されている」ことを意味しない点も同書に明記した**（セキュリティ仕様書における「無いこと」の書き分けの決定 3） |
| [禁止銘柄の一時解除 Runbook](banned-symbol-unlock-runbook.md) | **建玉を手仕舞えないとき**の手順（一時解除 → 手仕舞い → 再登録）。解除・再登録が監査に残る根拠つき |
| [Discord Webhook 再発行 Runbook](discord-webhook-rotation-runbook.md) | Webhook の URL が漏れたときの失効（新規作成 → Vault へ書く → 旧い方を削除）と、ログ・トレース等の蓄積分の扱い。**値は Vault 側で変える**（`ast-secrets` は ExternalSecret が所有する） |
| [KB タグ辞書登録 Runbook](kb-tag-dictionary-runbook.md) | 基盤（document-service）のタグ辞書へ事前登録すべきタグ一覧の生成手順。KB 保存が未登録タグで 400 になる事象への対処 |
| [ホスト側の死活監視 Runbook](host-liveness-monitor-runbook.md) | **クラスタの外から**損切りの生存を見張るスクリプトの登録、Rancher Desktop の自動起動、場中に Windows Update で再起動しない設定（オーナーが行う） |
| [基準資金の供給が無いときの Runbook](capital-baseline-seed-runbook.md) | **新規建てが `CapitalBaselineUnavailable` で止まるとき**の手順。供給の条件（当日より前の取引日の観測・鮮度 4 日）と、`account_equity_days` へ 1 行投入する埋め合わせ |
| [develop のルールセット Runbook](branch-protection-runbook.md) | develop の必須チェック・コードオーナー・バイパスの現況（実測）と、利用者が実行する `gh api` の本文。**リポジトリの統制設定であり AI は実行しない** |
| [ブロック中のタスク](../blocked-tasks.md) | 基盤・実機待ちで本リポジトリだけでは進められない項目 |

## 未決事項
