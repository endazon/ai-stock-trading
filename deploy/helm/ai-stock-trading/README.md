# ai-stock-trading k8s Helm chart（MSP 連結ローカル k8s dev）

> 起点: [IADR-0052](../../../docs/adr/IADR-0052_k8s-helm-chart-shared-infra.md) /
> 作業仕様書 [`docs/specs/20260713_122_k8s-helm-chart.md`](../../../docs/specs/20260713_122_k8s-helm-chart.md) /
> Issue #122・#24・#121

AST の 10 Worker を k8s へデプロイする chart。**共有インフラ（Postgres/RabbitMQ/Keycloak/otel）は
MSP 側 `platform-infra`** を参照する（MSP `deploy/local` / IADR-0066 と対）。単体では動かない。

## 前提

MSP 側で `platform-infra` と AST 用 DB（user `ai`・`*_svc`）・Keycloak realm `ai-stock-trading` を
起動済みであること。MSP リポの `scripts/k8s-local-up.sh` がこれを用意する
（AST realm は submodule 検出時に同梱 import される）。

## デプロイ

k8s ランタイムは MSP 側と同じく **Rancher Desktop（内蔵 k3s・推奨）** か **Docker Desktop + k3d**。
スクリプトが `nerdctl`/`k3d` の有無で自動判定する（`K8S_LOCAL_RUNTIME=rancher|k3d` で明示可）。
> 注: 判定は **`nerdctl` の存在**を優先し rancher 経路を選ぶ。両方入っている等で意図と異なる場合は
> `K8S_LOCAL_RUNTIME` で明示指定する。

```bash
scripts/k8s-local-deploy.sh              # build（Rancher=nerdctl/k3d=docker+import）→ ast-secrets → helm install
kubectl -n ai-stock-trading get pods
```

## 外部連携（fail-safe 既定）

`ast-secrets`（未設定=空=no-op）を明示設定した時のみ有効化する。

| 環境変数 | `ast-secrets` キー | 対象 | 既定 |
| --- | --- | --- | --- |
| `FINNHUB_API_KEY` | `finnhub-api-key` | information-collection | 空=NoOp（#81） |
| `DISCORD_WEBHOOK_URL` | `discord-webhook-url` | notification | 空=NoOp（#15） |
| （chart 値）`Broker__Provider` | — | order-execution | `paper`（実発注しない・#13。`moomoo.enabled` で切替） |
| （chart 値）`LlmGateway__BaseUrl` | — | trade-decision | 空=Placeholder LLM（呼ばない＝常に Hold・#11） |

> **LLM プロバイダ鍵は AST では扱わない**。鍵は MSP の LlmGateway 側（`Llm:ApiKey`）が保持し、AST は
> `LlmGateway__BaseUrl` 経由でゲートウェイを呼ぶだけである（ADR-0010 / IADR-0061 決定6）。
> `ast-secrets` に LLM 鍵を足さないこと。

## #13: moomoo（OpenD）発注

既定 **無効**（`order-execution` は `Broker__Provider=paper`＝実発注しない・fail-safe・IADR-0016）。有効化すると
`order-execution` へ moomoo 発注構成が注入される（**SIMULATE 限定**・実弾は撃たない）。

> ⚠️ **前提**（#13・#124 はマージ済み。以下は**運用側で揃える**もの）:
> - **常駐 OpenD が稼働し、ログイン済みであること**（下記「#132: OpenD の本番配備」または `deploy/opend/k8s` の
>   生 manifest）。OpenD は初回に**有人**のデバイス検証が要る。listen だけしていてもログイン前は使えない。
> - **Secret `moomoo-rsa`**（OpenD と**同一の RSA 秘密鍵**）。cross-network（worker→opend）の trade は RSA 暗号化必須。
>   鍵が構成済みで不在なら `order-execution` は**起動時 preflight で停止する**（#132。従来は黙って非暗号化に倒れ、
>   「接続はするが trade だけ失敗する」形でしか表面化しなかった）。

**有効化**（OpenD 常駐＋`moomoo-rsa` Secret 作成後）:
```bash
helm upgrade --install ast deploy/helm/ai-stock-trading -n ai-stock-trading \
  --set moomoo.enabled=true
```

有効化すると `order-execution` へ `Broker__Provider=moomoo` ＋ `Broker__Moomoo__OpenD__{Host=opend,Port=11111,
RsaPrivateKeyPath=/opt/opend/rsa/opend_rsa.pem}` が注入され、`moomoo-rsa` が read-only（`defaultMode: 0400`）で
マウントされる。発注時に OpenD 未接続なら**アダプタが per-order `Rejected` に倒す**（fail-safe）。
実結合は #13 で実 OpenD の SIMULATE 口座に対し live 検証済み（IADR-0056）。

> **実弾（`TrdEnv_Real`）は撃たない。** `moomoo.enabled=true` にしても取引環境は `TrdEnv_Simulate` 固定である
> （IADR-0016 / IADR-0056）。`Broker:Moomoo:TrdEnv` に `real` 等を与えると `order-execution` は**起動時に停止する**
> （黙って SIMULATE で流して「実弾で動いている」と誤認させないための閂・#132 / IADR-0060）。実弾解禁には別 IADR と
> 前提条件の充足が要る（[運用仕様書の本番切替チェックリスト](../../../docs/operations/operations.md#opend-の本番切替チェックリスト132)）。

## #132: OpenD の本番配備（`opend.enabled`）

既定 **無効**（何も描画しない＝fail-safe）。dev の現行経路は `deploy/opend/k8s` の生 manifest で、そちらは残してある
（IADR-0060 決定 1）。本 chart 経路は**本番配備**用で、既定値では生 manifest と同等に描画される。

```bash
# 前提: イメージのビルド＆import（scripts/opend-build.sh）、Secret moomoo-credentials / moomoo-rsa の作成。
helm upgrade --install ast deploy/helm/ai-stock-trading -n ai-stock-trading \
  --set opend.enabled=true \
  --set opend.nodeSelector."kubernetes\.io/hostname"=<安定ノード名>
# 初回のみ有人のデバイス検証（画像 CAPTCHA / SMS）:
kubectl -n ai-stock-trading attach -it deploy/opend
```

> ⚠️ **ノードを固定すること**。無人再ログインの成立条件は「デバイス信頼の永続化（PVC）＋ **egress IP の安定**」で、
> egress IP はノードの NAT 後 IP である。ノードを跨いで再スケジュールされると**有人の再検証に戻る**見込み
> （マルチノード/クラウドでの実測は **#132 で未了**）。

### 非 root 実行へ切り替える（既定は root・**未検証**）

OpenD はデバイス信頼を `$HOME/.com.moomoo.OpenD` に書くため、非 root 化は **HOME の再調整とセット**で行う。
イメージ側は uid/gid **10001** と `/home/opend` を用意済み（`USER` は切り替えていない＝既定 root）。

```bash
helm upgrade --install ast deploy/helm/ai-stock-trading -n ai-stock-trading \
  --set opend.enabled=true \
  --set opend.home=/home/opend \
  --set opend.securityContext.runAsNonRoot=true \
  --set opend.securityContext.runAsUser=10001 \
  --set opend.podSecurityContext.fsGroup=10001 \
  --set opend.rsaSecretDefaultMode=288   # 0440（--set は 10 進で渡す。values ファイルなら 0440 と書ける）
```

> ⚠️ **既定 ON にしていない理由**（IADR-0060 決定 2）: HOME が変わると**確立済みのデバイス信頼を失い、有人検証から
> やり直しになる恐れ**がある。実 OpenD でしか確かめられず、**#132 の実測フェーズで未検証**。切替時は既存 PVC の
> `.com.moomoo.OpenD` を新 HOME へ移すか、再検証を覚悟すること。
>
> ⚠️ `rsaSecretDefaultMode` は **k8s のファイルモード（8 進を 10 進で表す整数）**。values ファイルでは `0400` / `0440`
> と 8 進で書ける（YAML が解釈する）が、`--set` では **10 進**（`256` / `288`）で渡すこと。非 root では Secret の所有 uid が
> root のままなので、`fsGroup` を付けたうえで**グループ読み（0440）**が要る。

## #132: 秘匿情報の External Secrets（Vault）受け口（`externalSecrets.enabled`）

既定 **無効**。有効化すると `moomoo-credentials` / `moomoo-rsa` を Vault から同期する `ExternalSecret` を描画する。

```bash
helm upgrade --install ast deploy/helm/ai-stock-trading -n ai-stock-trading \
  --set externalSecrets.enabled=true \
  --set externalSecrets.secretStoreRef.name=vault-backend
```

> ⚠️ **これは Vault 化の充足ではない**（IADR-0060 決定 4）。ストア（Vault / External Secrets Operator）は **#24 の管掌**で
> 本リポジトリには無く、CRD が無いクラスタで有効化すると apply が失敗する。IADR-0056 §3 が実弾解禁の前提に挙げる
> 「秘匿情報の Vault 化」は**未充足のまま**である。

## #121: 取引サイクル CronJob

既定 **無効**（在来 in-process ポーリング IADR-0023 を維持＝fail-safe）。有効化:

```bash
helm upgrade --install ast deploy/helm/ai-stock-trading -n ai-stock-trading \
  --set tradingCycle.cronjob.enabled=true
```

有効化すると CronJob が収集の run-once（`POST /internal/collection/run-once`）を叩き、同時に情報収集へ
`Collection__Trigger=External` が注入され in-process ポーリングは停止する（二重起動防止）。既定（無効）は
in-process 維持＝fail-safe。休場日ガードは下流 TradeDecision の市場カレンダー（IADR-0023）が担保する。
run-once/モード切替は実装済み（#121 / IADR-0054）。
