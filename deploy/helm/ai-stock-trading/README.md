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
scripts/k8s-local-deploy.sh              # build（Rancher=nerdctl/k3d=docker+import）→ ast-secrets → helm install（-f values-local.yaml）
kubectl -n ai-stock-trading get pods
```

## 経路B（ローカル SIMULATE）の機能有効化: `values-local.yaml`

> 起点: [IADR-0100](../../../docs/adr/IADR-0100_route-b-values-local-standing-config.md) /
> 作業仕様書 [`docs/specs/20260725_route-b-values-local-standing-config.md`](../../../docs/specs/20260725_route-b-values-local-standing-config.md) /
> Issue #238

経路B（ローカル k8s / SIMULATE）で取引サイクルを end-to-end 検証するための **local/SIMULATE 有効化プロファイル**を
`values-local.yaml` として同梱する。`scripts/k8s-local-deploy.sh` が `helm upgrade -f values-local.yaml` で**標準手順として
自動適用**する（臨時 overlay を手当てする必要はない）。有効化する内容:

- **①時価評価（mark-to-market）**: risk-management `MarketData__EnableMarkToMarket=true` / `Provider=finnhub`、
  market-monitor・report の `MarketData__Provider=finnhub`（FR-10/16・IADR-0068）。
- **②実 LLM**: trade-decision・report の `LlmGateway__BaseUrl`（MSP LlmGateway・ADR-0010 / IADR-0061）。
- **③実 KB 保存**: information-collection・report の `KnowledgeBase__Documents__BaseUrl`（MSP DocumentService）＋
  `KnowledgeBase__Auth`（MSP レルムの `ai-stock-trading-kb-writer`・IADR-0093）。
- **Discord 通知**: notification `Notifications__Provider=discord-webhook` / `Bot__Enabled=true`（FR-09/14・IADR-0062）。
- **価格文脈（#236 / IADR-0099）**: trade-decision へ現在値を供給し権威価格でサイジング
  （`MarketData__Provider=finnhub`＋鍵で `ICurrentPriceProvider.IsEnabled` が真・鮮度 `MaxQuoteStalenessSeconds=300`）。
- **サイクル配線**: 収集の finnhub＋AAPL、trade-decision の watchlist（AAPL/UnitedStates）・`Reports`/`RiskManagement` BaseUrl。

**本番（ArgoCD）はバイト等価**: `deploy/argocd/application.yaml` は `valueFiles` を持たず `values.yaml` のみを描画するため、
`values-local.yaml` は本番描画に一切関与しない。`helm.yml` の CI が「既定描画に経路B有効化が漏れていないこと」と
「`values-local` 描画で①②③＋Discord＋価格文脈が ON かつ Broker=paper・opend/ExternalSecret 不在であること」を両検証する。

**secret は平文で埋め込まない**: `values-local.yaml` の鍵・トークンはすべて `secretKeyRef`（`ast-secrets`・`optional`）参照。
実値は下記 env で `ast-secrets` へ与える（未設定=空=no-op の fail-safe）か、ESO(Vault) 同期（IADR-0094）に委ねる。

| 環境変数 | `ast-secrets` キー | 用途 | 既定 |
| --- | --- | --- | --- |
| `MARKETDATA_FINNHUB_API_KEY` | `marketdata-finnhub-api-key` | ①時価・価格文脈（情報収集の `FINNHUB_API_KEY` とは**別枠**の opt-in・IADR-0068。フォールバックしない＝収集鍵の設定だけで①が黙って有効化されない） | 空=NoOp |
| `EDINET_SUBSCRIPTION_KEY` / `FRED_API_KEY` | `edinet-subscription-key` / `fred-api-key` | 収集ソース（任意） | 空=当該ソース無効 |
| `KB_AUTH_CLIENTSECRET` | `kb-auth-client-secret` | ③KB 書き込みの s2s（`kb-auth-client-id` は dev 既定 `ai-stock-trading-kb-writer`） | 空=401→未保存（fail-safe） |
| `DISCORD_BOT_TOKEN` | `discord-bot-token` | Discord Bot（双方向） | 空=Gateway に接続しない |
| `DISCORD_BOT_KILLSWITCH_PHRASE` | `discord-bot-killswitch-phrase` | kill switch 確認フレーズ | 空=kill switch 起動不可（安全側） |

> **Discord の環境固有値**（`GuildId` / `ChannelId` / `AllowedUserIds` / `UserMapping`）は `values-local.yaml` で**空既定**にしている。
> 使うときは各自の値を `values-local.yaml`（またはローカル専用の上乗せ values）へ記入する。**空のまま**だと IADR-0062 の
> 安全既定（空 GuildId/ChannelId/AllowedUserIds は「全許可」ではなく**全拒否**）で Bot は接続しても操作を受け付けない。
> Discord を使わないなら `Notifications__Provider=""` / `Bot__Enabled="false"` に戻す。

> **プロンプト全量ログは既定オフ（opt-in）**: `values-local.yaml` の `LlmGateway__LogPrompts` は安全既定 `""`（記録しない・
> IADR-0061 決定1）。②実 LLM の要求/生応答（判断根拠・監視銘柄等の機微を含む）をログ基盤に残してプロンプトレベルで
> 検証したいときだけ、report / trade-decision の当該値を `"true"` に変える（ローカル専用の上乗せ values でも可）。

> **G4（日報の確定）はランタイム操作**であり設定化しない。定時サイクルの判断が確定済み日報方針を要求するため、
> 検証時は Discord Bot（`/report confirm` 等・FR-07/14）または該当 API で当日方針を確定してから回す
> （[通知・Bot 運用](../../../docs/operations/operations.md) 参照）。

> **旧 `overlay-cycle.yaml` は不要**: MSP 側ホストの臨時 overlay（`microservices-platform/overlay-cycle.yaml`）を
> `helm upgrade -f overlay-cycle.yaml` で手当てする従来運用は、本プロファイルの committed 化により**不要**になった
> （overlay には #236 の価格文脈が欠けていた点も本プロファイルで解消済み）。overlay ファイルの物理削除は任意。

## 外部連携（fail-safe 既定）

`ast-secrets`（未設定=空=no-op）を明示設定した時のみ有効化する。

| 環境変数 | `ast-secrets` キー | 対象 | 既定 |
| --- | --- | --- | --- |
| `FINNHUB_API_KEY` | `finnhub-api-key` | information-collection | 空=NoOp（#81） |
| `DISCORD_WEBHOOK_URL` | `discord-webhook-url` | notification | 空=NoOp（#15） |
| `DISCORD_OWNERAUTH_CLIENTID` | `discord-owner-auth-client-id` | notification | dev 既定 `ai-stock-trading-owner`（Bot 制御コマンドの OwnerAuth・#226 / IADR-0098） |
| `DISCORD_OWNERAUTH_CLIENTSECRET` | `discord-owner-auth-client-secret` | notification | dev 既定 `dev-only-owner-secret`（realm-export.json と一致・本番は Secret/Vault） |
| （chart 値）`Notifications__Discord__OwnerAuth__TokenEndpoint` | — | notification | AST レルム token エンドポイント（#226。空だと IsEnabled=false→401） |
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

### #24, IADR-0094: API 鍵群（`ast-secrets`）の Vault 同期（`externalSecrets.appSecrets.enabled`）

既定 **無効**。`externalSecrets.enabled=true` と併せて有効化すると、API 鍵群の Secret `ast-secrets` を Vault の
単一 KV から `dataFrom.extract` で同期する `ExternalSecret` を描画する（**平文の鍵は values にも manifest にも無い**）。

```bash
helm upgrade --install ast deploy/helm/ai-stock-trading -n ai-stock-trading \
  --set externalSecrets.enabled=true \
  --set externalSecrets.appSecrets.enabled=true \
  --set externalSecrets.secretStoreRef.name=vault-backend
```

> Vault 側のプロパティ名は **Secret キー名**（`finnhub-api-key` / `service-auth-client-id` 等）に一致させる。
> 欠けた鍵は同期されず、消費側 `secretKeyRef.optional=true` で許容する（fail-safe）。既定オフ＝手動 Secret 直運用を維持。
> 手順は [`docs/operations/vault-secrets-runbook.md`](../../../docs/operations/vault-secrets-runbook.md)。GitOps（ArgoCD）は
> [`deploy/argocd`](../../argocd/README.md)、可観測性は [`docs/observability/observability.md`](../../../docs/observability/observability.md)。

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
