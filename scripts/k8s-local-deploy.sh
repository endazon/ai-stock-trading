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
#   FINNHUB_API_KEY（情報収集）/ MARKETDATA_FINNHUB_API_KEY（①時価・価格文脈。IADR-0068 の別枠＝収集鍵とは独立の
#     opt-in。FINNHUB_API_KEY へはフォールバックしない＝収集鍵の設定だけで①が黙って全面有効化されない）/
#   EDINET_SUBSCRIPTION_KEY / FRED_API_KEY（**US 株取引の必須前提**＝基準通貨・円への換算レート源 FRED DEXJPUS。
#     #262, IADR-0107。未設定だと USD 建て銘柄は判断前に全件見送りになる。日本株は定義上レート 1 で無影響）/
#   DISCORD_WEBHOOK_URL / DISCORD_BOT_TOKEN /
#   DISCORD_BOT_KILLSWITCH_PHRASE / KB_AUTH_CLIENTSECRET（KB 書き込みの s2s・IADR-0093）。
# #226, IADR-0098: Discord Bot 制御コマンドの owner 認証は dev 既定（ai-stock-trading-owner /
# dev-only-owner-secret＝realm-export.json と一致）で解決する。DISCORD_OWNERAUTH_CLIENTID /
# DISCORD_OWNERAUTH_CLIENTSECRET で上書き可。Bot は values-local で Enabled=true だが Token 空なら接続しない（安全側）。
# #245, IADR-0102: Discord Bot の**環境固有 ID**（非機密）は values 経路（--set-string discord.bot.*）で渡す:
#   DISCORD_BOT_GUILD_ID / DISCORD_BOT_CHANNEL_ID / DISCORD_BOT_ALLOWED_USER_IDS / DISCORD_BOT_USER_MAPPING。
#   未設定=空=差し替えなし（IADR-0062 の安全既定＝空は「全許可」ではなく全拒否で no-op）。
#   ⚠️ `kubectl set env deploy/notification-service ...` で注入しないこと。env の所有が Helm と kubectl(kubectl-set)へ
#      割れ、次回の helm upgrade が `conflict with "kubectl-set"` で失敗する。既に競合している場合は
#      `kubectl set env deploy/notification-service -n ai-stock-trading Notifications__Discord__Bot__GuildId- ...`
#      （`KEY-`＝削除）で剥がしてから本スクリプトを回す（chart README 参照）。
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
  --from-literal=marketdata-finnhub-api-key="${MARKETDATA_FINNHUB_API_KEY:-}" \
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
# #245, IADR-0102: helm の --set パーサはカンマを要素区切り・バックスラッシュをエスケープ文字として解釈するため、
# 値側の `,` `\` を退避する（AllowedUserIds / UserMapping はカンマ区切り）。これで helm へは値がそのまま届く。
# 注: 届いた先（DiscordBotOptionsReader のコンパクト形式）は `,` で要素分割するため、**keycloak 利用者名に `,` は
# 使えない**（本スクリプトのエスケープではなくアプリ側の値形式の制約。`:` は最初の 1 つのみ区切り・形式不正の
# 要素は破棄＝拒否側。chart README「Discord の環境固有 ID」参照）。
helm_escape() { printf '%s' "${1:-}" | sed 's/[\\,]/\\&/g'; }
# namespace は本スクリプトが先に作成（ast-secrets 投入のため）。chart に Namespace を template させると
# 既存 ns に Helm 所有メタデータが無く install が衝突するため、namespace.create=false で無効化する。
# #238, IADR-0100: values-local.yaml を重ねて ①時価②実LLM③実KB＋Discord＋価格文脈を有効化する（本番描画には不関与）。
# #245, IADR-0102: Discord の環境固有 ID は --set-string（--set だと 18〜19 桁の snowflake が float64 に解釈され
# 1.234567890123456e+18 に化ける）。空指定は差し替えなし＝描画は既定のまま（fail-safe）。
helm upgrade --install ast deploy/helm/ai-stock-trading -n "$NS" \
  --set namespace.create=false \
  --set-string discord.bot.guildId="$(helm_escape "${DISCORD_BOT_GUILD_ID:-}")" \
  --set-string discord.bot.channelId="$(helm_escape "${DISCORD_BOT_CHANNEL_ID:-}")" \
  --set-string discord.bot.allowedUserIds="$(helm_escape "${DISCORD_BOT_ALLOWED_USER_IDS:-}")" \
  --set-string discord.bot.userMapping="$(helm_escape "${DISCORD_BOT_USER_MAPPING:-}")" \
  -f deploy/helm/ai-stock-trading/values-local.yaml

echo ""
echo "done. 状態確認:"
echo "  kubectl -n $NS get pods"
echo "#121（CronJob）を有効化する場合: helm ... --set tradingCycle.cronjob.enabled=true"
echo "  （run-once エンドポイント実装が前提。既定は in-process ポーリング維持=fail-safe）"
