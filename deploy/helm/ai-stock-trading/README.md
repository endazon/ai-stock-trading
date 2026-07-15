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
| `ANTHROPIC_API_KEY` | `anthropic-api-key` | trade-decision（LLM） | 空=Placeholder（#79） |
| `FINNHUB_API_KEY` | `finnhub-api-key` | information-collection | 空=NoOp（#81） |
| `DISCORD_WEBHOOK_URL` | `discord-webhook-url` | notification | 空=NoOp（#15） |
| （chart 値）`Broker__Provider` | — | order-execution | `paper`（実発注しない・#13。`moomoo.enabled` で切替） |

## #13: moomoo（OpenD）発注

既定 **無効**（`order-execution` は `Broker__Provider=paper`＝実発注しない・fail-safe・IADR-0016）。有効化すると
`order-execution` へ moomoo 発注構成が注入される（**SIMULATE 限定**・実弾は撃たない）。

> ⚠️ **前提となる未マージ PR（本 chart 単独では動かない）**:
> - **#13（PR #130・moomoo アダプタ本体）**: これが**未マージのイメージ**で `moomoo.enabled=true` にすると、
>   `order-execution` は起動時に `InvalidOperationException` で停止する（`BrokerFactory` が moomoo 未実装を
>   拒否する **IADR-0016 の意図的ゲート**）＝Pod は **CrashLoopBackOff**。graceful な per-order `Rejected` では
>   ない。#13 をマージしたイメージで初めて発注経路が有効になる。
> - **#124（PR #126・OpenD 常駐＋RSA）**: `deploy/opend/` は本 chart リポジトリにまだ存在しない（#124 未マージ）。
>   OpenD 常駐と Secret `moomoo-rsa`（OpenD と**同一の RSA 秘密鍵**）が前提。cross-network trade は RSA 暗号化必須。

**有効化**（#13・#124 をマージ済みのイメージ＋ OpenD 常駐＋`moomoo-rsa` Secret 作成後）:
```bash
helm upgrade --install ast deploy/helm/ai-stock-trading -n ai-stock-trading \
  --set moomoo.enabled=true
```

有効化すると `order-execution` へ `Broker__Provider=moomoo` ＋ `Broker__Moomoo__OpenD__{Host=opend,Port=11111,
RsaPrivateKeyPath=/opt/opend/rsa/opend_rsa.pem}` が注入され、`moomoo-rsa` が read-only マウントされる。
**前提が揃った状態で**は、発注時に OpenD 未接続・鍵不備なら**アダプタが per-order `Rejected` に倒す**（fail-safe）。
実結合（アダプタ）自体は #13（PR #130）で実 OpenD の SIMULATE 口座に対し live 検証済（本 chart ブランチには未マージ）。

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
