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
# #279, IADR-0114: SEC_EDGAR_USER_AGENT は**機密ではない**が、SEC 規約が求める**連絡先（実在のメール
#   アドレス）入り**の User-Agent＝環境固有の個人情報のため values へ直書きせず本 Secret 経由で与える
#   （例: export SEC_EDGAR_USER_AGENT="AiStockTrading/1.0 (you@example.com)"）。
#   未設定=空 → SEC EDGAR **だけ**が警告つきで収集対象から外れる（finnhub/FRED は有効なまま・IADR-0064 決定1）。
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
# #263, IADR-0109: ast-secrets は**再作成しない**。env 未設定のキーには触れず（投入済みの値を保持）、
# 明示的な空指定が既存の非空値を消す場合だけキー名を列挙して中断する（--force-empty-secrets で許可）。
# 従来の「env から毎回まるごと再作成」は、export し忘れた鍵を空で上書きして無言で壊していた
# （症状は「デプロイは成功するのに外部連携が静かに no-op へ倒れる」）。挙動は
# scripts/k8s-local-deploy.test.sh が固定する。
set -euo pipefail

FORCE_EMPTY=0
CLUSTER=""
for arg in "$@"; do
  case "$arg" in
    --force-empty-secrets) FORCE_EMPTY=1 ;;
    -*) echo "unknown option: $arg" >&2; exit 2 ;;
    *) CLUSTER="$arg" ;;
  esac
done
CLUSTER="${CLUSTER:-msp-ast-dev}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
NS="ai-stock-trading"
SECRET_NAME="ast-secrets"

# ast-secrets のキー定義: <Secret キー>|<環境変数>|<既定値>。
# 既定値は「env 未設定 **かつ** 既存 Secret にも値が無い」場合にだけ使う（＝新規環境の後方互換）。
# 非空の既定を持つ 5 キーは dev 既定（realm-export.json と一致）。
AST_SECRET_KEYS=(
  "finnhub-api-key|FINNHUB_API_KEY|"
  "marketdata-finnhub-api-key|MARKETDATA_FINNHUB_API_KEY|"
  "edinet-subscription-key|EDINET_SUBSCRIPTION_KEY|"
  "fred-api-key|FRED_API_KEY|"
  "sec-edgar-user-agent|SEC_EDGAR_USER_AGENT|"
  "discord-webhook-url|DISCORD_WEBHOOK_URL|"
  "discord-bot-token|DISCORD_BOT_TOKEN|"
  "discord-bot-killswitch-phrase|DISCORD_BOT_KILLSWITCH_PHRASE|"
  "service-auth-client-id|SERVICEAUTH_CLIENTID|ai-stock-trading-svc"
  "service-auth-client-secret|SERVICEAUTH_CLIENTSECRET|dev-only-service-secret"
  "kb-auth-client-id|KB_AUTH_CLIENTID|ai-stock-trading-kb-writer"
  "kb-auth-client-secret|KB_AUTH_CLIENTSECRET|"
  "discord-owner-auth-client-id|DISCORD_OWNERAUTH_CLIENTID|ai-stock-trading-owner"
  "discord-owner-auth-client-secret|DISCORD_OWNERAUTH_CLIENTSECRET|dev-only-owner-secret"
)

AST_PATCH_DIR=""
ast_cleanup() { [ -n "$AST_PATCH_DIR" ] && rm -rf "$AST_PATCH_DIR"; return 0; }
trap ast_cleanup EXIT

# 既存 Secret のうち**非空の値を持つキー名だけ**を列挙する（IADR-0109 決定3: 平文は読み出さない）。
# Secret 不在・data 不在は空を返す（呼び出し側は「保持すべき値は無い」と解釈する）。
ast_secret_nonempty_keys() {
  kubectl get secret "$SECRET_NAME" -n "$NS" \
    -o 'go-template={{if .data}}{{range $k, $v := .data}}{{if $v}}{{$k}}{{"\n"}}{{end}}{{end}}{{end}}' \
    2>/dev/null || true
}

ast_has_value() { printf '%s\n' "$1" | grep -Fxq -- "$2"; }

# 値は base64 で載せる（"・\・改行・空白を含む値のエスケープ問題が構造的に消える。IADR-0109 決定4）。
ast_b64() { printf '%s' "${1:-}" | base64 | tr -d '\r\n'; }

sync_ast_secrets() {
  local existing entries='' preserved='' clobber='' spec key var default value
  local n_set=0 n_preserved=0
  existing="$(ast_secret_nonempty_keys)"

  for spec in "${AST_SECRET_KEYS[@]}"; do
    key="${spec%%|*}"
    var="${spec#*|}"; var="${var%%|*}"
    default="${spec##*|}"

    if [ -n "${!var+set}" ]; then
      # env の明示指定が唯一の権威。ただし「明示的な空」で既存の非空値を消すのは確認を挟む。
      value="${!var}"
      if [ -z "$value" ] && ast_has_value "$existing" "$key"; then
        clobber="${clobber}${key} (\$${var})
"
        [ "$FORCE_EMPTY" = "1" ] || continue
      fi
    elif ast_has_value "$existing" "$key"; then
      # #263 の本丸: export し忘れは「消したい」という意思表示ではない。触らない＝現在値が残る。
      preserved="${preserved}${key}
"
      n_preserved=$((n_preserved + 1))
      continue
    else
      value="$default"
    fi

    entries="${entries},\"${key}\":\"$(ast_b64 "$value")\""
    n_set=$((n_set + 1))
  done

  if [ -n "$clobber" ] && [ "$FORCE_EMPTY" != "1" ]; then
    {
      echo "ERROR: 次のキーは $SECRET_NAME に値がありますが、環境変数が**空**で指定されています。"
      echo "       空で上書きすると投入済みの値を失うため中断しました（#263 / IADR-0109）:"
      printf '%s' "$clobber" | sed 's/^/         - /'
      echo "       - export し忘れなら当該変数を unset して再実行する（現在値がそのまま保持されます）。"
      echo "       - 意図した消去なら --force-empty-secrets を付けて再実行する。"
    } >&2
    return 1
  fi

  # 新規環境（Secret 不在）のみ作成する。kubectl apply は last-applied との 3-way merge で
  # 「明示しなかったキー」を削除するため使わない（本件と同じ破壊が再発する・IADR-0109 決定5）。
  if ! kubectl get secret "$SECRET_NAME" -n "$NS" >/dev/null 2>&1; then
    kubectl create secret generic "$SECRET_NAME" -n "$NS" >/dev/null
  fi

  if [ -n "$entries" ]; then
    # パッチはコマンドライン引数ではなく一時ファイルで渡す（ps から平文が見えないようにする）。
    AST_PATCH_DIR="$(mktemp -d)"
    ( umask 077; printf '{"data":{%s}}' "${entries#,}" > "$AST_PATCH_DIR/ast-secrets.json" )
    kubectl patch secret "$SECRET_NAME" -n "$NS" --type=merge \
      --patch-file "$AST_PATCH_DIR/ast-secrets.json" >/dev/null
    ast_cleanup
    AST_PATCH_DIR=""
  fi

  echo "  $SECRET_NAME: 設定 ${n_set} 件 / 既存値を保持 ${n_preserved} 件（値は表示しません）"
  [ -n "$preserved" ] && printf '%s' "$preserved" | sed 's/^/    保持: /'
  return 0
}

# scripts/k8s-local-deploy.test.sh から関数だけを読み込むための入口（デプロイ手順は実行しない）。
if [ "${AST_DEPLOY_LIB:-}" = "1" ]; then
  return 0 2>/dev/null || exit 0
fi

echo "==> [1/3] build & import AST images"
"$ROOT/scripts/k8s-local-images.sh" "$CLUSTER"

echo "==> [2/3] namespace & ast-secrets (fail-safe 空既定・既存値は保持)"
kubectl create namespace "$NS" --dry-run=client -o yaml | kubectl apply -f -
sync_ast_secrets

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
