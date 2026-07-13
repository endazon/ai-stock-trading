#!/usr/bin/env bash
# #122 / IADR-0051: AST を既存の k3d クラスタ（MSP 連結・platform-infra 稼働済み）へデプロイする。
# 前提: MSP 側 scripts/k8s-local-up.sh が platform-infra（Postgres/RabbitMQ/Keycloak/otel）と
# AST 用 DB（ai ユーザ・*_svc）・Keycloak realm `ai-stock-trading` を用意済みであること。
#
#   scripts/k8s-local-deploy.sh [cluster-name]
# 機密の上書き: ANTHROPIC_API_KEY / FINNHUB_API_KEY / DISCORD_WEBHOOK_URL（未設定=空=no-op）。
set -euo pipefail
CLUSTER="${1:-msp-ast-dev}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
NS="ai-stock-trading"

echo "==> [1/3] build & import AST images"
"$ROOT/scripts/k8s-local-images.sh" "$CLUSTER"

echo "==> [2/3] namespace & ast-secrets (fail-safe 空既定)"
kubectl create namespace "$NS" --dry-run=client -o yaml | kubectl apply -f -
kubectl create secret generic ast-secrets -n "$NS" \
  --from-literal=anthropic-api-key="${ANTHROPIC_API_KEY:-}" \
  --from-literal=finnhub-api-key="${FINNHUB_API_KEY:-}" \
  --from-literal=discord-webhook-url="${DISCORD_WEBHOOK_URL:-}" \
  --dry-run=client -o yaml | kubectl apply -f -

echo "==> [3/3] helm upgrade --install"
helm upgrade --install ast deploy/helm/ai-stock-trading -n "$NS"

echo ""
echo "done. 状態確認:"
echo "  kubectl -n $NS get pods"
echo "#121（CronJob）を有効化する場合: helm ... --set tradingCycle.cronjob.enabled=true"
echo "  （run-once エンドポイント実装が前提。既定は in-process ポーリング維持=fail-safe）"
