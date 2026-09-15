#!/usr/bin/env bash
# #122 / IADR-0052: AST を既存の k3d クラスタ（MSP 連結・platform-infra 稼働済み）へデプロイする。
# 前提: MSP 側 scripts/k8s-local-up.sh が platform-infra（Postgres/RabbitMQ/Keycloak/otel）と
# AST 用 DB（ai ユーザ・*_svc）・Keycloak realm `ai-stock-trading` を用意済みであること。
# #727, IADR-0324: MSP 連結では利用者認証と s2s の token 発行を values-local.yaml の global.authAuthority＝MSP レルム
#   （platform）で行う。realm `ai-stock-trading` の import は単体 E2E（IADR-0050）用に残るだけで、本スクリプトの経路では使わない。
#
#   scripts/k8s-local-deploy.sh [--force-empty-secrets] [--force-empty-values] [cluster-name]
#
# #795, IADR-0341: **秘密情報・接続設定の供給経路は AST_ESO で選ぶ**（未設定＝values-local.yaml から導出＝既定は ESO 所有）。
#   - ESO 所有（AST_ESO=1 / values-local 既定）: 基盤を VAULT=1 ESO=1 で起動した連結クラスタ向け。ast-secrets /
#     moomoo-credentials / moomoo-rsa は ExternalSecret が所有し、値は**基盤の画面（秘密情報・接続設定の管理）**から
#     Vault へ書く。本スクリプトは ast-secrets を作らず・パッチせず、discord.bot.* も helm へ渡さない（両方から書くと
#     所有が割れ、画面で入れた値と Pod が読む値が食い違う）。CRD / ClusterSecretStore が無ければ案内して中断する。
#     管理外の既存 Secret（従来経路で作ったもの）があれば 1 回だけ警告する（削除はしない）。
#   - 従来経路（AST_ESO=0）: ESO の無いクラスタ向け。下記の env → ast-secrets 同期と discord.bot.* の values 経路。
#   どちらでも helm へ externalSecrets.enabled / appSecrets.enabled を明示する（プロファイルとの食い違いを残さない）。
# #267, IADR-0111 / #132, IADR-0060: ブローカ階層・OpenD 常駐配備は BROKER_TIER（"" / paper / moomoo-sim）/
#   OPEND_ENABLED（true / false）で切り替える。**未設定なら前回リリースの値を引き継ぐ**（#626, IADR-0283）。
#   明示的な空指定で前回の非空値を消す場合は --force-empty-values が要る（ast-secrets と同型）。
# #238, IADR-0100: 経路B（ローカル SIMULATE）の ①時価②実LLM③実KB＋Discord＋価格文脈は、chart の
# local プロファイル values-local.yaml を `-f` で重ねて恒常有効化する（臨時 overlay は不要）。本番（ArgoCD＝
# values.yaml のみ）はバイト等価のまま。有効化に要る実値は ESO 所有なら画面から、従来経路なら下記 ast-secrets を env で与える
# （未設定=空=no-op の fail-safe）。
#
# 機密の上書き（**従来経路（AST_ESO=0）でだけ使う**。未設定=空=no-op）:
#   FINNHUB_API_KEY（情報収集）/ MARKETDATA_FINNHUB_API_KEY（①時価・価格文脈。IADR-0068 の別枠＝収集鍵とは独立の
#     opt-in。FINNHUB_API_KEY へはフォールバックしない＝収集鍵の設定だけで①が黙って全面有効化されない）/
#   EDINET_SUBSCRIPTION_KEY / FRED_API_KEY（為替レートの**フォールバック**＝FRED DEXJPUS。#262, IADR-0107。
#     **第一の情報源は日銀（Fx__Provider=boj・認証不要）であり、本キーは必須前提ではない**（#686, IADR-0308）。
#     未設定だと冗長化が失われるだけで、日銀単独で換算できる。レートが解決できないときに見送られるのは
#     非基準通貨＝JPY 建て銘柄であり、基準通貨の米国株は定義上レート 1 で無影響〔#364 で入れ替わった〕）/
#   DISCORD_WEBHOOK_URL / DISCORD_BOT_TOKEN /
#   DISCORD_BOT_KILLSWITCH_PHRASE / KB_AUTH_CLIENTSECRET（KB 書き込みの s2s・IADR-0093）。
#   ESO 所有の経路で export されていたら、変数名だけを挙げて「使わない」と警告する（値は表示しない）。
# #279, IADR-0114: SEC_EDGAR_USER_AGENT は**機密ではない**が、SEC 規約が求める**連絡先（実在のメール
#   アドレス）入り**の User-Agent＝環境固有の個人情報のため values へ直書きせず本 Secret 経由で与える
#   （例: export SEC_EDGAR_USER_AGENT="AiStockTrading/1.0 (you@example.com)"）。
#   未設定=空 → SEC EDGAR **だけ**が警告つきで収集対象から外れる（finnhub/FRED は有効なまま・IADR-0064 決定1）。
# #226, IADR-0098: Discord Bot 制御コマンドの owner 認証は dev 既定（ai-stock-trading-owner /
# dev-only-owner-secret＝realm-export.json と一致）で解決する。DISCORD_OWNERAUTH_CLIENTID /
# DISCORD_OWNERAUTH_CLIENTSECRET で上書き可。Bot は values-local で Enabled=true だが Token 空なら接続しない（安全側）。
# #245, IADR-0102: Discord Bot の**環境固有 ID**（非機密）は、従来経路では values 経路（--set-string discord.bot.*）で渡す:
#   DISCORD_BOT_GUILD_ID / DISCORD_BOT_CHANNEL_ID / DISCORD_BOT_ALLOWED_USER_IDS / DISCORD_BOT_USER_MAPPING。
#   未設定=空=差し替えなし（IADR-0062 の安全既定＝空は「全許可」ではなく全拒否で no-op）。
#   **未設定なら前回リリースの値を引き継ぐ**（#673。broker.tier / opend.enabled と同じ AST_VALUE_KEYS の
#   仕組みに 2026-09-03 に合流した。従来は引き継ぎ対象外で、4 変数を export し忘れると投入済みの ID が
#   空へ戻り Bot が無言で no-op へ落ちる事故が実際に発生していた＝#263 と同型の再発）。
#   #795, IADR-0341: ESO 所有の経路では 4 件とも ast-secrets の discord-bot-* から読むため、env も前回値も渡さない
#   （chart は appSecrets 有効かつ discord.bot.* 非空を描画時に止める）。
#   ⚠️ `kubectl set env deploy/notification-service ...` で注入しないこと。env の所有が Helm と kubectl(kubectl-set)へ
#      割れ、次回の helm upgrade が `conflict with "kubectl-set"` で失敗する。既に競合している場合は
#      `kubectl set env deploy/notification-service -n ai-stock-trading Notifications__Discord__Bot__GuildId- ...`
#      （`KEY-`＝削除）で剥がしてから本スクリプトを回す（chart README 参照）。
# #18, IADR-0093: KB 書き込みの s2s は MSP レルムの client ai-stock-trading-kb-writer（KB_AUTH_CLIENTID で上書き可）。
# #734, IADR-0323: LLM ゲートウェイ呼び出しの s2s は MSP レルムの client ai-stock-trading-llm-caller
#   （LLM_AUTH_CLIENTID で上書き可・秘密は LLM_AUTH_CLIENTSECRET）。KB 書き込みとは別主体（MSP#1368）。
# LLM プロバイダ鍵は AST では扱わない（鍵は MSP の LlmGateway 側が保持する。ADR-0010 / IADR-0061 決定6）。
# #263, IADR-0109: ast-secrets は**再作成しない**。env 未設定のキーには触れず（投入済みの値を保持）、
# 明示的な空指定が既存の非空値を消す場合だけキー名を列挙して中断する（--force-empty-secrets で許可）。
# 従来の「env から毎回まるごと再作成」は、export し忘れた鍵を空で上書きして無言で壊していた
# （症状は「デプロイは成功するのに外部連携が静かに no-op へ倒れる」）。挙動は
# scripts/k8s-local-deploy.test.sh が固定する。
# #626, IADR-0283: `helm upgrade --install` は `--reuse-values` を使わない（-f values-local.yaml との
# 合成順序が読みにくく、「values-local.yaml を直しても一部が反映されない」副作用を実測したため）。
# そのため broker.tier / opend.enabled は BROKER_TIER / OPEND_ENABLED を env で明示しないと、
# 前回リリースで手動 --set した値が黙って既定（paper / false）へ戻る。opend.enabled=false は
# OpenD の Deployment・PVC ごと削除する（デバイス信頼の再認証が要る）。ast-secrets と同じ
# 「保持／明示上書き／明示空値は --force-empty-values で確認／新規環境」を Helm values へ適用する。
# #673: 上記と同じ「触らない＝保持」の 4 分岐を discord.bot.* の 4 環境固有 ID にも適用する
# （IADR-0283 決定時は対象外としたが、export し忘れで Bot が無言 no-op へ落ちる事故が実際に発生した）。
# また、helm upgrade だけでは Pod テンプレートが変わらないサービスにイメージ更新が届かない
# （タグ :latest 固定 + imagePullPolicy IfNotPresent）ため、末尾で OpenD を除く Deployment へ
# `kubectl rollout restart` を打つ（OpenD は SMS/画像認証済みセッションを切らないため除外）。
set -euo pipefail

FORCE_EMPTY=0
FORCE_EMPTY_VALUES=0
CLUSTER=""
for arg in "$@"; do
  case "$arg" in
    --force-empty-secrets) FORCE_EMPTY=1 ;;
    --force-empty-values) FORCE_EMPTY_VALUES=1 ;;
    -*) echo "unknown option: $arg" >&2; exit 2 ;;
    *) CLUSTER="$arg" ;;
  esac
done
CLUSTER="${CLUSTER:-msp-ast-dev}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
NS="ai-stock-trading"
SECRET_NAME="ast-secrets"
RELEASE="ast"
# #795, IADR-0341: ESO 所有の判定に使う chart の values（プロファイル＝helm upgrade が -f で重ねるもの）。
# AST_PROFILE_VALUES は検査（k8s-local-deploy.test.sh）がプロファイルを差し替えるための入口。
AST_CHART_VALUES="$ROOT/deploy/helm/ai-stock-trading/values.yaml"
AST_PROFILE_VALUES_DEFAULT="$ROOT/deploy/helm/ai-stock-trading/values-local.yaml"
# #795, IADR-0341: ESO が所有する Secret（MSP#1477 と共有する契約の同期先名。ExternalSecret 名も同一で、
# 基盤 BFF はこの名前で force-sync する。描画との一致は .github/workflows/helm.yml の #795 ステップが検査する）。
AST_ESO_TARGET_SECRETS=("$SECRET_NAME" moomoo-credentials moomoo-rsa)

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
  "llm-auth-client-id|LLM_AUTH_CLIENTID|ai-stock-trading-llm-caller"
  "llm-auth-client-secret|LLM_AUTH_CLIENTSECRET|"
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

# #245, IADR-0102: helm の --set パーサはカンマを要素区切り・バックスラッシュをエスケープ文字として解釈するため、
# 値側の `,` `\` を退避する（AllowedUserIds / UserMapping はカンマ区切り）。これで helm へは値がそのまま届く。
# 注: 届いた先（DiscordBotOptionsReader のコンパクト形式）は `,` で要素分割するため、**keycloak 利用者名に `,` は
# 使えない**（本スクリプトのエスケープではなくアプリ側の値形式の制約。`:` は最初の 1 つのみ区切り・形式不正の
# 要素は破棄＝拒否側。chart README「Discord の環境固有 ID」参照）。
# #673: resolve_ast_value_overrides（AST_DEPLOY_LIB=1 でも読み込む関数）から呼ぶため、ここ（ヘルパー群）で
# 定義する。以前は helm upgrade 直前（実行末尾）にのみ定義されており、テスト経路からは未定義エラーになっていた。
helm_escape() { printf '%s' "${1:-}" | sed 's/[\\,]/\\&/g'; }

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

# #626, IADR-0283 / #673: 前回リリースの値を個別に引き継ぐ Helm values のキー定義:
# <ドット区切りパス>|<環境変数>|<set 方式>。<set 方式> は "set"（--set。列挙値・真偽値向け）または
# "set-string"（--set-string。snowflake ID 等の数値化事故を避ける。helm_escape を適用する）。
# ast-secrets と異なり dev 既定は持たない（未指定・前回値なしは chart 既定＝paper / false / 空文字 に委ねる）。
# discord.bot.* の 4 件は #673 で合流した（従来は values-local.yaml の env 直渡しのみで引き継ぎ対象外だった）。
# #795, IADR-0341: ESO 所有の経路では discord.bot.* を解決しない（ast-secrets の discord-bot-* から読むため）。
AST_VALUE_KEYS=(
  "broker.tier|BROKER_TIER|set"
  "opend.enabled|OPEND_ENABLED|set"
  "discord.bot.guildId|DISCORD_BOT_GUILD_ID|set-string"
  "discord.bot.channelId|DISCORD_BOT_CHANNEL_ID|set-string"
  "discord.bot.allowedUserIds|DISCORD_BOT_ALLOWED_USER_IDS|set-string"
  "discord.bot.userMapping|DISCORD_BOT_USER_MAPPING|set-string"
)

# 標準入力の YAML からドット区切りパス（任意階層。例 "broker.tier" / "discord.bot.guildId"）の値を読む
# （パス不在は空を返す）。インデント幅（2 space/階層）とキー名の一致で先頭から逐次降下照合する。
# jq / yq 等の追加依存を持ち込まない（本リポの他スクリプトは go-template / awk のみで完結させる慣行）。
# #673 で 2 階層専用から任意階層へ一般化した。#795 で helm get values 以外（values ファイル）も読むため
# ast_prev_release_value から切り出し、コメント行（`#` 始まり）と、引用符で始まらない値の行末コメントを飛ばすようにした
# （values ファイルはコメントを持つ。コメント行のインデントで降下がリセットされると判定を誤る）。
ast_yaml_path_value() {
  local path="$1"
  awk -v path="$path" '
    BEGIN { n = split(path, segs, "."); depth = 0 }
    {
      line = $0
      sub(/\r$/, "", line)
      if (match(line, /^ */)) { indent = RLENGTH } else { indent = 0 }
      content = substr(line, indent + 1)
      if (content == "") next
      if (substr(content, 1, 1) == "#") next
      if (!match(content, /^[^:]+:/)) next
      key = substr(content, 1, RLENGTH - 1)
      rest = substr(content, RLENGTH + 1)
      sub(/^[ \t]+/, "", rest)
      if (substr(rest, 1, 1) != "\"") sub(/[ \t]+#.*$/, "", rest)
      gsub(/^[ \t"]+|["]+$/, "", rest)

      expected = depth * 2
      if (indent < expected) { depth = 0; expected = 0 }
      if (indent == expected && key == segs[depth + 1]) {
        depth++
        if (depth == n) { print rest; exit }
      }
    }
  ' || true
}

# 直前リリースの user-supplied values からドット区切りパスの値を読む（release 不在・パス不在は空を返す）。
ast_prev_release_value() {
  helm get values "$RELEASE" -n "$NS" -o yaml 2>/dev/null | ast_yaml_path_value "$1" || true
}

# #795, IADR-0341: プロファイル（helm upgrade が -f で重ねる values-local.yaml）からドット区切りパスの値を読む。
ast_profile_value() {
  local f="${AST_PROFILE_VALUES:-$AST_PROFILE_VALUES_DEFAULT}"
  [ -f "$f" ] || return 0
  ast_yaml_path_value "$1" < "$f"
}

# #795, IADR-0341: プロファイル → chart 既定（values.yaml）の順に値を読み、どちらにも無ければ $2 を返す。
ast_effective_value() {
  local v
  v="$(ast_profile_value "$1")"
  if [ -z "$v" ] && [ -f "$AST_CHART_VALUES" ]; then v="$(ast_yaml_path_value "$1" < "$AST_CHART_VALUES")"; fi
  printf '%s' "${v:-${2:-}}"
}

# #795, IADR-0341: 秘密情報・接続設定の供給経路を決め、AST_ESO_MODE（1=ESO 所有 / 0=従来経路）と
# AST_ESO_OVERRIDES（helm へ渡す externalSecrets.enabled / appSecrets.enabled の明示）を組み立てる。
#   AST_ESO=1|true  → ESO 所有 / AST_ESO=0|false → 従来経路 / 未設定・空 → プロファイルの
#   externalSecrets.enabled と appSecrets.enabled が両方 true なら ESO 所有（values-local の既定）。
#   解釈できない値は中断する（黙って片方の経路へ倒さない）。
resolve_ast_eso_mode() {
  local v="${AST_ESO:-}" es app flag label
  case "$v" in
    1|true) AST_ESO_MODE=1; AST_ESO_SOURCE="AST_ESO=$v" ;;
    0|false) AST_ESO_MODE=0; AST_ESO_SOURCE="AST_ESO=$v" ;;
    "")
      es="$(ast_profile_value externalSecrets.enabled)"
      app="$(ast_profile_value externalSecrets.appSecrets.enabled)"
      if [ "$es" = "true" ] && [ "$app" = "true" ]; then AST_ESO_MODE=1; else AST_ESO_MODE=0; fi
      AST_ESO_SOURCE="AST_ESO 未設定＝プロファイル $(basename "${AST_PROFILE_VALUES:-$AST_PROFILE_VALUES_DEFAULT}") から導出"
      ;;
    *)
      {
        echo "ERROR: AST_ESO は 1 / 0（true / false）で指定してください（実際: 「$v」）。未設定ならプロファイルから導出します。"
        echo "       1 = ESO 所有（値は基盤の画面から Vault へ入れる） / 0 = 従来経路（env → ast-secrets）。#795 / IADR-0341"
      } >&2
      return 2
      ;;
  esac
  flag=false
  label="従来経路（env → ast-secrets・discord.bot.* は values）"
  if [ "$AST_ESO_MODE" = "1" ]; then
    flag=true
    label="ESO 所有（値は基盤の画面から Vault へ入れる）"
  fi
  AST_ESO_OVERRIDES=(--set "externalSecrets.enabled=${flag}" --set "externalSecrets.appSecrets.enabled=${flag}")
  echo "  secret 供給: ${label}（${AST_ESO_SOURCE}）"
  return 0
}

# #795, IADR-0341: ESO 所有の前提（CRD と SecretStore）がクラスタに在るかを確かめる。無いまま helm upgrade すると
# ExternalSecret の apply が「no matches for kind」で失敗し、リリースが中途半端に残る。
ast_eso_preflight() {
  local store kind
  store="$(ast_effective_value externalSecrets.secretStoreRef.name vault-backend)"
  kind="$(ast_effective_value externalSecrets.secretStoreRef.kind ClusterSecretStore)"
  if ! kubectl get crd externalsecrets.external-secrets.io >/dev/null 2>&1; then
    {
      echo "ERROR: 秘密情報の供給が ESO 所有（${AST_ESO_SOURCE}）ですが、クラスタに External Secrets Operator の CRD"
      echo "       （externalsecrets.external-secrets.io）がありません。このまま helm upgrade すると失敗します（#795 / IADR-0341）。"
      echo "       - 基盤を VAULT=1 ESO=1 で起動する（microservices-platform: VAULT=1 ESO=1 bash scripts/k8s-local-up.sh）。"
      echo "       - ESO を使わない従来経路で配備するなら AST_ESO=0 を付けて再実行する（env → ast-secrets）。"
    } >&2
    return 1
  fi
  local found=0
  if [ "$kind" = "ClusterSecretStore" ]; then
    kubectl get clustersecretstore "$store" >/dev/null 2>&1 && found=1
  else
    kubectl get secretstore "$store" -n "$NS" >/dev/null 2>&1 && found=1
  fi
  if [ "$found" != "1" ]; then
    {
      echo "ERROR: ESO の ${kind} \"${store}\" がありません（ExternalSecret が同期先を持たず、Secret が作られません）。"
      echo "       - 基盤を VAULT=1 ESO=1 で起動し直す（store は基盤が作る）。"
      echo "       - ESO を使わない従来経路で配備するなら AST_ESO=0 を付けて再実行する。#795 / IADR-0341"
    } >&2
    return 1
  fi
  return 0
}

# #795, IADR-0341: ESO が所有しにいく Secret が、ExternalSecret の管理外で既に在れば 1 回だけ警告する。
# 🔴 削除はしない（中の値を失うのは利用者の判断に残す）。管理の判定は ownerReferences の kind（平文は読まない）。
ast_warn_unmanaged_eso_targets() {
  local name owners unmanaged=''
  for name in "${AST_ESO_TARGET_SECRETS[@]}"; do
    kubectl get secret "$name" -n "$NS" >/dev/null 2>&1 || continue
    owners="$(kubectl get secret "$name" -n "$NS" \
      -o 'jsonpath={range .metadata.ownerReferences[*]}{.kind}{"\n"}{end}' 2>/dev/null || true)"
    if printf '%s\n' "$owners" | grep -Fxq ExternalSecret; then continue; fi
    unmanaged="${unmanaged}${name}
"
  done
  [ -z "$unmanaged" ] && return 0
  {
    echo "WARN: 次の Secret は ExternalSecret の管理外で既に存在します（手動作成・従来経路の本スクリプトが作ったもの）:"
    printf '%s' "$unmanaged" | sed 's/^/         - /'
    echo "      ESO（creationPolicy: Owner）が同名の Secret を所有しにいくため、中の値が Vault の値で置き換わるか、所有の"
    echo "      衝突で同期が止まるかのいずれかになり、画面で入れた値と Pod が読む値が食い違い得ます。本スクリプトは削除しません。"
    echo "      解消: 1) 基盤の画面（秘密情報・接続設定の管理）で必要な値を入れる（moomoo の RSA 鍵は画面の「生成」）"
    echo "            2) kubectl -n $NS delete secret <上記の名前>   # ESO が Vault の値で作り直す"
    echo "      従来経路のまま使うなら AST_ESO=0 を付けて再実行する。#795 / IADR-0341"
  } >&2
  return 0
}

# #795, IADR-0341: ESO 所有の経路で従来経路用の鍵 env が export されていたら、変数名だけを挙げて使わない旨を伝える。
ast_warn_ignored_secret_env() {
  local spec var names=''
  for spec in "${AST_SECRET_KEYS[@]}"; do
    var="${spec#*|}"; var="${var%%|*}"
    if [ -n "${!var+set}" ]; then names="${names} ${var}"; fi
  done
  [ -z "$names" ] && return 0
  {
    echo "WARN: ESO 所有の経路では次の環境変数を使いません（ast-secrets へ書きません。値は表示しません）:${names}"
    echo "      値は基盤の画面から Vault へ入れてください。env で入れる従来経路は AST_ESO=0。#795 / IADR-0341"
  } >&2
  return 0
}

# #795, IADR-0341: 経路に応じて Secret を用意する。ESO 所有なら作らず（事前確認と警告のみ）、従来経路なら同期する。
ast_prepare_secrets() {
  if [ "${AST_ESO_MODE:-0}" = "1" ]; then
    ast_eso_preflight || return 1
    echo "  ${AST_ESO_TARGET_SECRETS[*]}: ExternalSecret（ESO）が所有するため作成・同期しません（値は基盤の画面から入れる）"
    ast_warn_ignored_secret_env
    ast_warn_unmanaged_eso_targets
    return 0
  fi
  sync_ast_secrets
}

# AST_VALUE_KEYS を解決し、AST_VALUE_OVERRIDES（helm upgrade へ渡す --set / --set-string 列）を組み立てる。
resolve_ast_value_overrides() {
  local spec key var setmode existing value clobber='' eso_skipped=''
  local n_set=0 n_preserved=0
  AST_VALUE_OVERRIDES=()

  for spec in "${AST_VALUE_KEYS[@]}"; do
    key="${spec%%|*}"
    var="${spec#*|}"; var="${var%%|*}"
    setmode="${spec##*|}"
    existing="$(ast_prev_release_value "$key")"

    # #795, IADR-0341: ESO 所有では Discord ID を ast-secrets から読む。values で渡すと chart が描画時に止める。
    if [ "${AST_ESO_MODE:-0}" = "1" ]; then
      case "$key" in
        discord.bot.*)
          if [ -n "${!var+set}" ] || [ -n "$existing" ]; then eso_skipped="${eso_skipped} ${key}"; fi
          continue
          ;;
      esac
    fi

    if [ -n "${!var+set}" ]; then
      # env の明示指定が唯一の権威。ただし「明示的な空」で既存の非空値を消すのは確認を挟む（#263 と同型）。
      value="${!var}"
      if [ -z "$value" ] && [ -n "$existing" ]; then
        clobber="${clobber}${key} (\$${var}, 前回値=${existing})
"
        [ "$FORCE_EMPTY_VALUES" = "1" ] || continue
      fi
    elif [ -n "$existing" ]; then
      # env 未設定＋前回値あり → 引き継ぐ（うっかり既定へ戻すのを防ぐ）。
      value="$existing"
      n_preserved=$((n_preserved + 1))
      echo "  引き継ぎ: ${key}=${existing}（前回リリースの値。\$${var} 未設定）" >&2
    else
      # env 未設定＋前回値も無い（新規環境）→ 何もしない。chart 既定に委ねる。
      continue
    fi

    if [ "$setmode" = "set-string" ]; then
      # #245, IADR-0102: --set-string はカンマ・バックスラッシュを helm 側の要素区切り/エスケープ文字として
      # 解釈するため、env 由来・前回値由来のどちらでも helm へ渡す直前で 1 回だけエスケープする
      # （helm get values が返す前回値は既にエスケープ解除済みの生値のため、ここで初めて退避が要る）。
      AST_VALUE_OVERRIDES+=("--set-string" "${key}=$(helm_escape "$value")")
    else
      AST_VALUE_OVERRIDES+=("--set" "${key}=${value}")
    fi
    n_set=$((n_set + 1))
  done

  if [ -n "$clobber" ] && [ "$FORCE_EMPTY_VALUES" != "1" ]; then
    {
      echo "ERROR: 次の設定は前回リリースに値がありますが、環境変数が**空**で指定されています。"
      echo "       空で上書きすると前回の設定を失うため中断しました（#626 / IADR-0283、discord.bot.* は #673）:"
      printf '%s' "$clobber" | sed 's/^/         - /'
      echo "       - export し忘れなら当該変数を unset して再実行する（前回値がそのまま引き継がれます）。"
      echo "       - 意図した既定への戻しなら --force-empty-values を付けて再実行する。"
    } >&2
    return 1
  fi

  if [ -n "$eso_skipped" ]; then
    {
      echo "WARN: ESO 所有の経路では次の設定を helm へ渡しません（env・前回リリースの値とも。値は表示しません）:${eso_skipped}"
      echo "      Discord の環境固有 ID は ast-secrets の discord-bot-guild-id / -channel-id / -allowed-user-ids / -user-mapping から読みます。"
      echo "      値は基盤の画面から入れてください（前回リリースの discord.bot.* は引き継がれません）。#795 / IADR-0341"
    } >&2
  fi

  echo "  release values: 設定 ${n_set} 件（うち引き継ぎ ${n_preserved} 件）"
  return 0
}

# #673: helm upgrade は Pod テンプレートを変えない限り既存 Pod を再作成しない。タグ :latest 固定 +
# imagePullPolicy IfNotPresent のため、イメージを焼き直しても（k8s-local-images.sh）helm upgrade だけでは
# 新イメージが Pod へ届かない（実測: OpenD Pod が Helm revision 13→14 を跨いで 25 時間生存）。
# 実クラスタの Deployment 一覧から動的に導出する（chart の .Values.services が増減しても本関数の
# 追随作業が要らない）。OpenD（Deployment 名 "opend"）は除外する: SMS/画像認証済みの moomoo セッションを
# 持ち、Recreate 戦略・単一レプリカで再起動コストが高い（ADR-0024 決定3/4）ため、ローカル配備の便宜で
# 不要な再起動によりセッションを失わせない。
ast_rollout_restart_workers() {
  local dep restarted='' n=0
  while IFS= read -r dep; do
    [ -z "$dep" ] && continue
    [ "$dep" = "opend" ] && continue
    kubectl rollout restart deployment "$dep" -n "$NS" >/dev/null
    restarted="${restarted}${dep}
"
    n=$((n + 1))
  done < <(kubectl get deployment -n "$NS" -o jsonpath='{range .items[*]}{.metadata.name}{"\n"}{end}' 2>/dev/null || true)

  echo "  rollout restart: ${n} 件（OpenD は除外）"
  [ -n "$restarted" ] && printf '%s' "$restarted" | sed 's/^/    /'
  return 0
}

# scripts/k8s-local-deploy.test.sh から関数だけを読み込むための入口（デプロイ手順は実行しない）。
if [ "${AST_DEPLOY_LIB:-}" = "1" ]; then
  return 0 2>/dev/null || exit 0
fi

echo "==> [1/5] build & import AST images"
"$ROOT/scripts/k8s-local-images.sh" "$CLUSTER"

echo "==> [2/5] namespace & secret 供給（ESO 所有＝作らない / 従来経路＝ast-secrets を差分同期・#795 / IADR-0341）"
kubectl create namespace "$NS" --dry-run=client -o yaml | kubectl apply -f -
resolve_ast_eso_mode
ast_prepare_secrets

echo "==> [3/5] broker.tier / opend.enabled / discord.bot.* (前回リリースの値を引き継ぐ・#626 / IADR-0283 / #673。ESO 所有では discord.bot.* を渡さない)"
resolve_ast_value_overrides

echo "==> [4/5] helm upgrade --install (local/SIMULATE プロファイル)"
# namespace は本スクリプトが先に作成（ast-secrets 投入のため）。chart に Namespace を template させると
# 既存 ns に Helm 所有メタデータが無く install が衝突するため、namespace.create=false で無効化する。
# #238, IADR-0100: values-local.yaml を重ねて ①時価②実LLM③実KB＋Discord＋価格文脈を有効化する（本番描画には不関与）。
# #673: discord.bot.* は resolve_ast_value_overrides が AST_VALUE_OVERRIDES へ --set-string 済みで積む
# （helm_escape も内部で適用済み）。以前ここにあった 4 行の直接 --set-string は削除した
# （引き継ぎ機構の外にあり、export し忘れで前回値を無条件に空へ戻していたため）。
# #795, IADR-0341: AST_ESO_OVERRIDES は externalSecrets.enabled / appSecrets.enabled を経路どおりに明示する
# （--set は -f より優先されるため、AST_ESO=0 ならプロファイルの true を false で上書きする）。
helm upgrade --install "$RELEASE" deploy/helm/ai-stock-trading -n "$NS" \
  --set namespace.create=false \
  "${AST_VALUE_OVERRIDES[@]}" \
  "${AST_ESO_OVERRIDES[@]}" \
  -f deploy/helm/ai-stock-trading/values-local.yaml

echo "==> [5/5] rollout restart (イメージ更新を Pod へ反映。OpenD は除外=SMS/画像認証セッションを維持・#673)"
ast_rollout_restart_workers

echo ""
echo "done. 状態確認:"
echo "  kubectl -n $NS get pods"
if [ "${AST_ESO_MODE:-0}" = "1" ]; then
  echo "  kubectl -n $NS get externalsecret   # ast-secrets / moomoo-credentials / moomoo-rsa が SecretSynced であること"
  echo "  OpenD（opend.enabled=true）は moomoo-credentials / moomoo-rsa が Vault に入り同期されるまで起動を待つ（画面で入力・生成する）"
fi
echo "#121（CronJob）を有効化する場合: helm ... --set tradingCycle.cronjob.enabled=true"
echo "  （run-once エンドポイント実装が前提。既定は in-process ポーリング維持=fail-safe）"
