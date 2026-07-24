#!/usr/bin/env bash
# #122 / IADR-0052: AST を既存の k3d クラスタ（MSP 連結・platform-infra 稼働済み）へデプロイする。
# 前提: MSP 側 scripts/k8s-local-up.sh が platform-infra（Postgres/RabbitMQ/Keycloak/otel）と
# AST 用 DB（ai ユーザ・*_svc）・Keycloak realm `ai-stock-trading` を用意済みであること。
#
#   scripts/k8s-local-deploy.sh [cluster-name]
# #238, IADR-0100: 経路B（ローカル SIMULATE）の ①時価②実LLM③実KB＋Discord＋価格文脈は、chart の
# local プロファイル values-local.yaml を `-f` で重ねて恒常有効化する（臨時 overlay は不要）。本番（ArgoCD＝
# values.yaml のみ）はバイト等価のまま。有効化に要る実値は下記 ast-secrets を env で与える（未設定=空=no-op の fail-safe）。
#
# 機密の上書き（未設定=空=no-op）:
#   FINNHUB_API_KEY（情報収集）/ MARKETDATA_FINNHUB_API_KEY（時価・別枠。未設定は FINNHUB_API_KEY にフォールバック）/
#   EDINET_SUBSCRIPTION_KEY / FRED_API_KEY / DISCORD_WEBHOOK_URL / DISCORD_BOT_TOKEN /
#   DISCORD_BOT_KILLSWITCH_PHRASE / KB_AUTH_CLIENTSECRET（KB 書き込みの s2s・IADR-0093）。
# #226, IADR-0098: Discord Bot 制御コマンドの owner 認証は dev 既定（ai-stock-trading-owner /
# dev-only-owner-secret＝realm-export.json と一致）で解決する。DISCORD_OWNERAUTH_CLIENTID /
# DISCORD_OWNERAUTH_CLIENTSECRET で上書き可。Bot は values-local で Enabled=true だが Token 空なら接続しない（安全側）。
# #18, IADR-0093: KB 書き込みの s2s は MSP レルムの client ai-stock-trading-kb-writer（KB_AUTH_CLIENTID で上書き可）。
# LLM プロバイダ鍵は AST では扱わない（鍵は MSP の LlmGateway 側が保持する。ADR-0010 / IADR-0061 決定6）。
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
  --from-literal=finnhub-api-key="${FINNHUB_API_KEY:-}" \
  --from-literal=marketdata-finnhub-api-key="${MARKETDATA_FINNHUB_API_KEY:-${FINNHUB_API_KEY:-}}" \
  --from-literal=edinet-subscription-key="${EDINET_SUBSCRIPTION_KEY:-}" \
  --from-literal=fred-api-key="${FRED_API_KEY:-}" \
  --from-literal=discord-webhook-url="${DISCORD_WEBHOOK_URL:-}" \
  --from-literal=discord-bot-token="${DISCORD_BOT_TOKEN:-}" \
  --from-literal=discord-bot-killswitch-phrase="${DISCORD_BOT_KILLSWITCH_PHRASE:-}" \
  --from-literal=service-auth-client-id="${SERVICEAUTH_CLIENTID:-ai-stock-trading-svc}" \
  --from-literal=service-auth-client-secret="${SERVICEAUTH_CLIENTSECRET:-dev-only-service-secret}" \
  --from-literal=kb-auth-client-id="${KB_AUTH_CLIENTID:-ai-stock-trading-kb-writer}" \
  --from-literal=kb-auth-client-secret="${KB_AUTH_CLIENTSECRET:-}" \
  --from-literal=discord-owner-auth-client-id="${DISCORD_OWNERAUTH_CLIENTID:-ai-stock-trading-owner}" \
  --from-literal=discord-owner-auth-client-secret="${DISCORD_OWNERAUTH_CLIENTSECRET:-dev-only-owner-secret}" \
  --dry-run=client -o yaml | kubectl apply -f -

echo "==> [3/3] helm upgrade --install (local/SIMULATE プロファイル)"
# namespace は本スクリプトが先に作成（ast-secrets 投入のため）。chart に Namespace を template させると
# 既存 ns に Helm 所有メタデータが無く install が衝突するため、namespace.create=false で無効化する。
# #238, IADR-0100: values-local.yaml を重ねて ①時価②実LLM③実KB＋Discord＋価格文脈を有効化する（本番描画には不関与）。
helm upgrade --install ast deploy/helm/ai-stock-trading -n "$NS" \
  --set namespace.create=false \
  -f deploy/helm/ai-stock-trading/values-local.yaml

echo ""
echo "done. 状態確認:"
echo "  kubectl -n $NS get pods"
echo "#121（CronJob）を有効化する場合: helm ... --set tradingCycle.cronjob.enabled=true"
echo "  （run-once エンドポイント実装が前提。既定は in-process ポーリング維持=fail-safe）"
