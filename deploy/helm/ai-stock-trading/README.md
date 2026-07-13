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
| （chart 値）`Broker__Provider` | — | order-execution | `paper`（実発注しない・#13） |

## #121: 取引サイクル CronJob

既定 **無効**（在来 in-process ポーリング IADR-0023 を維持＝fail-safe）。有効化:

```bash
helm upgrade --install ast deploy/helm/ai-stock-trading -n ai-stock-trading \
  --set tradingCycle.cronjob.enabled=true
```

⚠️ 有効化には収集の run-once エンドポイント（`/internal/collection/run-once`）が必要。**未実装**のため、
現状は骨子のみ。C# 実装は #121 の後続 PR で行う（休場日・費用停止は収集側でゲート）。
