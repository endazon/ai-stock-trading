#!/usr/bin/env bash
# #263 / IADR-0109: scripts/k8s-local-deploy.sh の ast-secrets 同期（sync_ast_secrets）の挙動を固定する。
# #626 / IADR-0283: broker.tier / opend.enabled の前回値引き継ぎ（resolve_ast_value_overrides）も併せて固定する。
# #673: discord.bot.* 4 件の前回値引き継ぎ（同じ resolve_ast_value_overrides への合流）と、
#   helm upgrade 後の rollout restart（ast_rollout_restart_workers）を併せて固定する。
#
#   bash scripts/k8s-local-deploy.test.sh
#
# 実クラスタは要らない。PATH の先頭に `kubectl` / `helm` スタブを置き、スタブが「既存 Secret の状態」
# 「前回リリースの values」「現在の Deployment 一覧」を疑似し、生成されたパッチ（--patch-file の中身）・
# rollout restart の呼び出しを記録する。対象スクリプトは AST_DEPLOY_LIB=1 で source すると関数定義だけを
# 読み込み、デプロイ手順（image build / helm）は実行しない。
#
# 検証する不変条件（Issue #263 の受け入れ基準）:
#   - 鍵を export せずに再実行しても投入済みの値が失われない（パッチに載せない＝API サーバ側で保持）
#   - 空上書きが避けられない場合はキー名を列挙して中断し、明示フラグでのみ実行する
#   - 新規環境（Secret 未作成）では従来どおり作成できる
#   - 平文の鍵を標準出力・標準エラーへ出さない（キー名のみ）
#
# 検証する不変条件（Issue #626 の受け入れ基準）:
#   - BROKER_TIER / OPEND_ENABLED を export せずに再実行しても前回リリースの値が引き継がれる
#   - 明示的な空指定で前回の非空値を消す場合は中断し、--force-empty-values でのみ強制できる
#   - 前回リリースが無い（新規環境）場合は helm upgrade へ --set を追加しない（chart 既定に委ねる）
#
# 検証する不変条件（Issue #673 の受け入れ基準）:
#   - DISCORD_BOT_GUILD_ID 等 4 変数を export せずに再実行しても前回リリースの値が引き継がれる
#     （カンマ・バックスラッシュを含む allowedUserIds / userMapping のエスケープが壊れない）
#   - 明示的な空指定で前回の非空値を消す場合は中断し、--force-empty-values でのみ強制できる
#   - helm upgrade の後、OpenD を除く Deployment へ rollout restart が呼ばれる
set -u

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
STATE="$(mktemp -d)"
STUB_BIN="$(mktemp -d)"
PROFILES="$(mktemp -d)"   # #795: ESO 判定に使うプロファイル（values ファイル）の疑似。given_secret で消さない
trap 'rm -rf "$STATE" "$STUB_BIN" "$PROFILES"' EXIT

# ---- kubectl スタブ -------------------------------------------------------
# 状態は $AST_TEST_STATE に置く: exists（Secret 実在）/ nonempty_keys（非空の値を持つキー名）/
# created（create secret が呼ばれた）/ patch.json（適用されたパッチ）/ deployments（現在の Deployment
# 名一覧・ast_rollout_restart_workers 用）/ restarted.log（rollout restart が呼ばれた Deployment 名）。
cat > "$STUB_BIN/kubectl" <<'STUB'
#!/usr/bin/env bash
set -u
S="$AST_TEST_STATE"
case "${1:-} ${2:-}" in
  "get secret")
    # #795: Secret 名ごとの実在（ast-secrets は従来どおり $S/exists、他は $S/exists-<name>）と
    # ownerReferences の kind（$S/owner-<name>）を疑似する。
    name="${3:-}"
    if [ "$name" = "ast-secrets" ]; then f="$S/exists"; else f="$S/exists-$name"; fi
    [ -f "$f" ] || { echo "Error from server (NotFound): secrets \"$name\" not found" >&2; exit 1; }
    for a in "$@"; do
      case "$a" in
        go-template=*) cat "$S/nonempty_keys"; exit 0 ;;
        jsonpath=*ownerReferences*) [ -f "$S/owner-$name" ] && cat "$S/owner-$name"; exit 0 ;;
      esac
    done
    exit 0
    ;;
  "get crd")
    [ -f "$S/crd" ] || { echo 'Error from server (NotFound): customresourcedefinitions not found' >&2; exit 1; }
    exit 0
    ;;
  "get clustersecretstore")
    [ -f "$S/store-${3:-}" ] || { echo 'Error from server (NotFound): clustersecretstores not found' >&2; exit 1; }
    exit 0
    ;;
  "delete secret")
    printf '%s\n' "${3:-}" >> "$S/deleted.log"
    exit 0
    ;;
  "create secret")
    : > "$S/exists"; : > "$S/created"; exit 0
    ;;
  "patch secret")
    prev=""
    for a in "$@"; do
      [ "$prev" = "--patch-file" ] && cp "$a" "$S/patch.json"
      prev="$a"
    done
    exit 0
    ;;
  "get deployment")
    [ -f "$S/deployments" ] && cat "$S/deployments"
    exit 0
    ;;
  "rollout restart")
    prev="" name=""
    for a in "$@"; do
      [ "$prev" = "deployment" ] && name="$a"
      prev="$a"
    done
    printf '%s\n' "$name" >> "$S/restarted.log"
    exit 0
    ;;
esac
exit 0
STUB
chmod +x "$STUB_BIN/kubectl"

# ---- helm スタブ -----------------------------------------------------------
# `helm get values ast -n <ns> -o yaml` だけを疑似する（resolve_ast_value_overrides が唯一使う呼び出し）。
# 状態は $AST_TEST_STATE/prev-values.yaml（release 不在は当該ファイルを置かない）。
cat > "$STUB_BIN/helm" <<'STUB'
#!/usr/bin/env bash
set -u
S="$AST_TEST_STATE"
case "${1:-} ${2:-}" in
  "get values")
    [ -f "$S/prev-values.yaml" ] || { echo 'Error: release: not found' >&2; exit 1; }
    cat "$S/prev-values.yaml"
    exit 0
    ;;
esac
exit 0
STUB
chmod +x "$STUB_BIN/helm"

PATH="$STUB_BIN:$PATH"
export PATH
export AST_TEST_STATE="$STATE"

# ---- 対象の読み込み（関数のみ） -------------------------------------------
AST_DEPLOY_LIB=1
export AST_DEPLOY_LIB
# shellcheck source=./k8s-local-deploy.sh
. "$ROOT_DIR/scripts/k8s-local-deploy.sh"
set +e +o pipefail   # 対象が有効化した set -e を戻し、失敗ケースを観測できるようにする
# 対象スクリプトの `trap ast_cleanup EXIT` が上の EXIT トラップを上書きするため、両方を呼ぶ形で張り直す
# （張り直さないと本スクリプトが確保した一時ディレクトリが残置される）。
trap 'ast_cleanup; rm -rf "$STATE" "$STUB_BIN" "$PROFILES"' EXIT

# ---- テストハーネス -------------------------------------------------------
PASSED=0
FAILED=0
ok() { PASSED=$((PASSED + 1)); printf '  ok  %s\n' "$1"; }
ng() { FAILED=$((FAILED + 1)); printf '  NG  %s\n     %s\n' "$1" "$2"; }
assert_contains() { case "$2" in *"$3"*) ok "$1" ;; *) ng "$1" "expected to contain: $3" ;; esac; }
assert_missing() { case "$2" in *"$3"*) ng "$1" "expected NOT to contain: $3" ;; *) ok "$1" ;; esac; }
assert_eq() { [ "$2" = "$3" ] && ok "$1" || ng "$1" "expected [$3] but was [$2]"; }

SECRET_ENV_VARS="FINNHUB_API_KEY MARKETDATA_FINNHUB_API_KEY EDINET_SUBSCRIPTION_KEY FRED_API_KEY
SEC_EDGAR_USER_AGENT
DISCORD_WEBHOOK_URL DISCORD_BOT_TOKEN DISCORD_BOT_KILLSWITCH_PHRASE SERVICEAUTH_CLIENTID
SERVICEAUTH_CLIENTSECRET KB_AUTH_CLIENTID KB_AUTH_CLIENTSECRET LLM_AUTH_CLIENTID LLM_AUTH_CLIENTSECRET DISCORD_OWNERAUTH_CLIENTID
DISCORD_OWNERAUTH_CLIENTSECRET"

# #673 で resolve_ast_value_overrides に合流した discord.bot.* 4 件の環境変数（ast-secrets の
# DISCORD_WEBHOOK_URL 等とは別枠。values 経路の環境固有 ID）。
VALUE_ENV_VARS="DISCORD_BOT_GUILD_ID DISCORD_BOT_CHANNEL_ID DISCORD_BOT_ALLOWED_USER_IDS DISCORD_BOT_USER_MAPPING"

# 既存 Secret の状態を作り直し、環境変数とフラグを既定へ戻す。
# $1 = "absent" もしくは非空の値を持つキー名の空白区切り（"" なら Secret は在るが全キー空）
given_secret() {
  local v
  for v in $SECRET_ENV_VARS; do unset "$v"; done
  unset BROKER_TIER OPEND_ENABLED
  for v in $VALUE_ENV_VARS; do unset "$v"; done
  unset AST_ESO AST_ESO_MODE AST_PROFILE_VALUES
  FORCE_EMPTY=0
  FORCE_EMPTY_VALUES=0
  rm -rf "$STATE"; mkdir -p "$STATE"
  : > "$STATE/nonempty_keys"
  if [ "${1:-}" != "absent" ]; then
    : > "$STATE/exists"
    for v in ${1:-}; do printf '%s\n' "$v" >> "$STATE/nonempty_keys"; done
  fi
}

# sync_ast_secrets をサブシェルで実行し、RC / OUT / ERR / PATCH を埋める。
run_sync() {
  ( sync_ast_secrets ) > "$STATE/out" 2> "$STATE/err"
  RC=$?
  OUT="$(cat "$STATE/out")"
  ERR="$(cat "$STATE/err")"
  PATCH=""
  [ -f "$STATE/patch.json" ] && PATCH="$(cat "$STATE/patch.json")"
}

# 前回リリースの values（helm get values の疑似出力）を作り直す。
# $1 = "absent"（release 不在）もしくは values-local.yaml と同じ書式の YAML 断片
given_release_values() {
  rm -f "$STATE/prev-values.yaml"
  if [ "${1:-}" != "absent" ]; then
    printf '%s\n' "$1" > "$STATE/prev-values.yaml"
  fi
}

# resolve_ast_value_overrides をサブシェルで実行し、RC / OUT / ERR / OVERRIDES を埋める。
# AST_VALUE_OVERRIDES は配列のためサブシェル越しに直接は返せない。デバッグ用の可読表現をファイルへ書かせる。
run_resolve() {
  (
    resolve_ast_value_overrides
    rc=$?
    printf '%s\n' "${AST_VALUE_OVERRIDES[@]:-}" > "$STATE/overrides.txt"
    exit $rc
  ) > "$STATE/out" 2> "$STATE/err"
  RC=$?
  OUT="$(cat "$STATE/out")"
  ERR="$(cat "$STATE/err")"
  OVERRIDES="$(cat "$STATE/overrides.txt" 2>/dev/null || true)"
}

b64() { printf '%s' "${1:-}" | base64 | tr -d '\r\n'; }

# 現在の Deployment 名一覧（kubectl get deployment の疑似出力）を作り直す。
# $1 = 改行区切りの Deployment 名一覧（未指定なら空＝クラスタに Deployment が無い）
given_deployments() {
  rm -f "$STATE/deployments" "$STATE/restarted.log"
  [ -n "${1:-}" ] && printf '%s\n' "$1" > "$STATE/deployments"
}

# ast_rollout_restart_workers をサブシェルで実行し、RC / OUT / ERR / RESTARTED を埋める。
run_rollout() {
  ( ast_rollout_restart_workers ) > "$STATE/out" 2> "$STATE/err"
  RC=$?
  OUT="$(cat "$STATE/out")"
  ERR="$(cat "$STATE/err")"
  RESTARTED="$(cat "$STATE/restarted.log" 2>/dev/null || true)"
}

printf 'k8s-local-deploy.sh: ast-secrets 同期（#263 / IADR-0109）\n'

# T-263-01: env 未設定 ＋ 既存に非空値 → 触らない（保持）
given_secret "fred-api-key finnhub-api-key"
run_sync
assert_eq   'T-263-01 保持: 正常終了する' "$RC" "0"
assert_missing 'T-263-01 保持: パッチに fred-api-key を載せない' "$PATCH" '"fred-api-key"'
assert_missing 'T-263-01 保持: パッチに finnhub-api-key を載せない' "$PATCH" '"finnhub-api-key"'
assert_contains 'T-263-01 保持: 保持したキー名を表示する' "$OUT" 'fred-api-key'

# T-263-02: env に非空値を指定 → その値で上書きする
given_secret "fred-api-key"
FRED_API_KEY="new-fred-key"; export FRED_API_KEY
run_sync
assert_eq   'T-263-02 上書き: 正常終了する' "$RC" "0"
assert_contains 'T-263-02 上書き: 指定値が base64 で載る' "$PATCH" "\"fred-api-key\":\"$(b64 new-fred-key)\""

# T-263-03: env に空を明示指定 ＋ 既存が非空 → キー名を列挙して中断（パッチしない）
given_secret "fred-api-key discord-bot-token"
FRED_API_KEY=""; export FRED_API_KEY
run_sync
assert_eq   'T-263-03 中断: 非ゼロ終了する' "$RC" "1"
assert_contains 'T-263-03 中断: 対象キー名を列挙する' "$ERR" 'fred-api-key'
assert_contains 'T-263-03 中断: 環境変数名を示す' "$ERR" 'FRED_API_KEY'
assert_eq   'T-263-03 中断: パッチを適用しない' "$PATCH" ""
assert_missing 'T-263-03 中断: 明示指定していないキーは巻き込まない' "$ERR" 'discord-bot-token'

# T-263-04: T-263-03 ＋ 明示フラグ → 空で上書きする
given_secret "fred-api-key"
FRED_API_KEY=""; export FRED_API_KEY
FORCE_EMPTY=1
run_sync
assert_eq   'T-263-04 強制: 正常終了する' "$RC" "0"
assert_contains 'T-263-04 強制: 空値で上書きする' "$PATCH" '"fred-api-key":""'

# T-263-05: Secret 未作成（新規環境）→ 作成し dev 既定を投入する（後方互換）
given_secret "absent"
run_sync
assert_eq   'T-263-05 新規: 正常終了する' "$RC" "0"
[ -f "$STATE/created" ] && ok 'T-263-05 新規: Secret を作成する' || ng 'T-263-05 新規: Secret を作成する' 'create secret が呼ばれていない'
assert_contains 'T-263-05 新規: dev 既定（s2s クライアント ID）が入る' "$PATCH" "\"service-auth-client-id\":\"$(b64 ai-stock-trading-svc)\""
# #734, IADR-0323: LLM ゲートウェイ呼び出しの s2s は KB 書き込みとは別主体（llm-caller）。dev 既定が入ること。
assert_contains 'T-734-01 新規: dev 既定（LLM caller クライアント ID）が入る' "$PATCH" "\"llm-auth-client-id\":\"$(b64 ai-stock-trading-llm-caller)\""
assert_contains 'T-263-05 新規: 空既定のキーも作られる' "$PATCH" '"fred-api-key":""'

# T-263-06: 平文の鍵を標準出力・標準エラーへ出さない（キー名のみ）
given_secret "fred-api-key"
FINNHUB_API_KEY="plaintext-canary-do-not-log"; export FINNHUB_API_KEY
run_sync
assert_missing 'T-263-06 秘匿: stdout に平文を出さない' "$OUT" 'plaintext-canary-do-not-log'
assert_missing 'T-263-06 秘匿: stderr に平文を出さない' "$ERR" 'plaintext-canary-do-not-log'
assert_contains 'T-263-06 秘匿: 対象キー名は表示する' "$OUT" 'ast-secrets'

# T-263-07: env の空指定 ＋ 既存も空/不在 → 失うものが無いので中断しない
given_secret ""
FRED_API_KEY=""; export FRED_API_KEY
run_sync
assert_eq   'T-263-07 空同士: 中断しない' "$RC" "0"
assert_contains 'T-263-07 空同士: 空のまま載る' "$PATCH" '"fred-api-key":""'

# T-263-08: dev 既定を持つキーも既存の非空値を上書きしない（既定への黙った巻き戻しを防ぐ）
given_secret "service-auth-client-secret discord-owner-auth-client-secret"
run_sync
assert_eq   'T-263-08 既定巻き戻し防止: 正常終了する' "$RC" "0"
assert_missing 'T-263-08 既定巻き戻し防止: s2s シークレットを dev 既定へ戻さない' "$PATCH" '"service-auth-client-secret"'
assert_missing 'T-263-08 既定巻き戻し防止: OwnerAuth シークレットを dev 既定へ戻さない' "$PATCH" '"discord-owner-auth-client-secret"'

# ---- #279 / IADR-0114: SEC EDGAR の連絡先入り User-Agent -------------------
# SEC 規約で必須の連絡先（実在のメールアドレス）は個人情報のため values へ直書きせず ast-secrets 経由で与える。
# 供給経路が IADR-0109 の不変条件（保持・上書き・空中断・新規作成）をそのまま継承することを固定する。

# T-279-01: 新規環境では空既定で作られる（未設定＝SEC EDGAR だけが無効に倒れる fail-safe）
given_secret "absent"
run_sync
assert_eq   'T-279-01 新規: 正常終了する' "$RC" "0"
assert_contains 'T-279-01 新規: 空既定のキーが作られる' "$PATCH" '"sec-edgar-user-agent":""'

# T-279-02: env に指定した UA がそのまま載る（連絡先入りの文字列）
given_secret ""
SEC_EDGAR_USER_AGENT="AiStockTrading/1.0 (ops@example.com)"; export SEC_EDGAR_USER_AGENT
run_sync
assert_eq   'T-279-02 供給: 正常終了する' "$RC" "0"
assert_contains 'T-279-02 供給: 指定した UA が base64 で載る' \
  "$PATCH" "\"sec-edgar-user-agent\":\"$(b64 'AiStockTrading/1.0 (ops@example.com)')\""

# T-279-03: export し忘れ（env 未設定）＋既存に非空値 → 触らない（保持）
given_secret "sec-edgar-user-agent"
run_sync
assert_eq   'T-279-03 保持: 正常終了する' "$RC" "0"
assert_missing 'T-279-03 保持: パッチに sec-edgar-user-agent を載せない' "$PATCH" '"sec-edgar-user-agent"'
assert_contains 'T-279-03 保持: 保持したキー名を表示する' "$OUT" 'sec-edgar-user-agent'

# T-279-04: 明示的な空指定 ＋ 既存が非空 → キー名を列挙して中断（無言で SEC 収集を止めない）
given_secret "sec-edgar-user-agent"
SEC_EDGAR_USER_AGENT=""; export SEC_EDGAR_USER_AGENT
run_sync
assert_eq   'T-279-04 中断: 非ゼロ終了する' "$RC" "1"
assert_contains 'T-279-04 中断: 対象キー名を列挙する' "$ERR" 'sec-edgar-user-agent'
assert_contains 'T-279-04 中断: 環境変数名を示す' "$ERR" 'SEC_EDGAR_USER_AGENT'
assert_eq   'T-279-04 中断: パッチを適用しない' "$PATCH" ""

# T-279-05: UA は機密ではないが、他の値と同様に平文を stdout/stderr へ出さない
given_secret ""
SEC_EDGAR_USER_AGENT="ua-canary-do-not-log"; export SEC_EDGAR_USER_AGENT
run_sync
assert_missing 'T-279-05 秘匿: stdout に平文を出さない' "$OUT" 'ua-canary-do-not-log'
assert_missing 'T-279-05 秘匿: stderr に平文を出さない' "$ERR" 'ua-canary-do-not-log'


# ---- #626 / IADR-0283: broker.tier / opend.enabled の前回値引き継ぎ --------
# helm upgrade --install が --reuse-values を使わないため、env passthrough が無いと前回リリースの
# 値が既定（paper / false）へ黙って戻る（opend.enabled=false は OpenD の Deployment/PVC を削除する）。
printf '\nk8s-local-deploy.sh: broker.tier / opend.enabled の前回値引き継ぎ（#626 / IADR-0283）\n'

# T-626-01: env 未設定 ＋ 前回値あり → 引き継ぐ（--set が前回値で載る）
given_secret ""
given_release_values 'broker:
  tier: moomoo-sim
opend:
  enabled: true'
run_resolve
assert_eq   'T-626-01 引き継ぎ: 正常終了する' "$RC" "0"
assert_contains 'T-626-01 引き継ぎ: broker.tier=moomoo-sim を --set する' "$OVERRIDES" 'broker.tier=moomoo-sim'
assert_contains 'T-626-01 引き継ぎ: opend.enabled=true を --set する' "$OVERRIDES" 'opend.enabled=true'
assert_contains 'T-626-01 引き継ぎ: 引き継いだキー名を表示する（stderr）' "$ERR" 'broker.tier=moomoo-sim'

# T-626-02: env に非空値を指定 → その値で上書きする（前回値は無視）
given_release_values 'broker:
  tier: moomoo-sim'
BROKER_TIER="paper"; export BROKER_TIER
run_resolve
assert_eq   'T-626-02 上書き: 正常終了する' "$RC" "0"
assert_contains 'T-626-02 上書き: 指定値 paper が載る' "$OVERRIDES" 'broker.tier=paper'
assert_missing  'T-626-02 上書き: 前回値 moomoo-sim は載らない' "$OVERRIDES" 'broker.tier=moomoo-sim'
unset BROKER_TIER

# T-626-03: env に空を明示指定 ＋ 前回値が非空 → キー名を列挙して中断（--set しない）
given_release_values 'opend:
  enabled: true'
OPEND_ENABLED=""; export OPEND_ENABLED
run_resolve
assert_eq   'T-626-03 中断: 非ゼロ終了する' "$RC" "1"
assert_contains 'T-626-03 中断: 対象キー名を列挙する' "$ERR" 'opend.enabled'
assert_contains 'T-626-03 中断: 環境変数名を示す' "$ERR" 'OPEND_ENABLED'
assert_contains 'T-626-03 中断: 前回値を示す' "$ERR" '前回値=true'
unset OPEND_ENABLED

# T-626-04: T-626-03 ＋ 明示フラグ → 空で上書きする（chart 既定と同値に倒す）
given_release_values 'opend:
  enabled: true'
OPEND_ENABLED=""; export OPEND_ENABLED
FORCE_EMPTY_VALUES=1
run_resolve
assert_eq   'T-626-04 強制: 正常終了する' "$RC" "0"
assert_contains 'T-626-04 強制: 空値で上書きする' "$OVERRIDES" 'opend.enabled='
unset OPEND_ENABLED
FORCE_EMPTY_VALUES=0

# T-626-05: 前回リリースが無い（新規環境）＋ env 未設定 → 何も --set しない（chart 既定に委ねる）
given_release_values "absent"
run_resolve
assert_eq   'T-626-05 新規: 正常終了する' "$RC" "0"
assert_eq   'T-626-05 新規: --set を追加しない' "$OVERRIDES" ''

# T-626-06: 前回リリースが無い ＋ env 明示指定 → その値を --set する
given_release_values "absent"
BROKER_TIER="moomoo-sim"; export BROKER_TIER
run_resolve
assert_eq   'T-626-06 新規+明示: 正常終了する' "$RC" "0"
assert_contains 'T-626-06 新規+明示: 指定値が --set される' "$OVERRIDES" 'broker.tier=moomoo-sim'
unset BROKER_TIER

# T-626-07: env の空指定 ＋ 前回値も空/不在 → 失うものが無いので中断しない
given_release_values "absent"
OPEND_ENABLED=""; export OPEND_ENABLED
run_resolve
assert_eq   'T-626-07 空同士: 中断しない' "$RC" "0"
assert_contains 'T-626-07 空同士: 空のまま載る' "$OVERRIDES" 'opend.enabled='
unset OPEND_ENABLED

# ---- #673: discord.bot.* の前回値引き継ぎ ---------------------------------
# broker.tier / opend.enabled と同じ resolve_ast_value_overrides へ discord.bot.* 4 件を合流させた。
# 3 階層ネスト（discord: bot: guildId 等）の読み取りと、--set-string 用エスケープが壊れないことを固定する。
printf '\nk8s-local-deploy.sh: discord.bot.* の前回値引き継ぎ（#673）\n'

# T-673-01: env 未設定 ＋ 前回値あり（3 階層）→ 引き継ぐ（--set-string・エスケープ込み）
given_secret ""
given_release_values 'discord:
  bot:
    guildId: "111111111111111111"
    channelId: "222222222222222222"
    allowedUserIds: 111,222
    userMapping: 111:alice,222:bob'
run_resolve
assert_eq   'T-673-01 引き継ぎ: 正常終了する' "$RC" "0"
assert_contains 'T-673-01 引き継ぎ: guildId が --set-string で載る' "$OVERRIDES" 'discord.bot.guildId=111111111111111111'
assert_contains 'T-673-01 引き継ぎ: allowedUserIds のカンマがエスケープされる' "$OVERRIDES" 'discord.bot.allowedUserIds=111\,222'
assert_contains 'T-673-01 引き継ぎ: userMapping のカンマがエスケープされる（コロンは温存）' "$OVERRIDES" 'discord.bot.userMapping=111:alice\,222:bob'
assert_contains 'T-673-01 引き継ぎ: 引き継いだキー名を表示する（stderr）' "$ERR" 'discord.bot.guildId=111111111111111111'

# T-673-02: env に非空値を指定 → その値で上書きする（前回値は無視）
given_release_values 'discord:
  bot:
    guildId: "111111111111111111"'
DISCORD_BOT_GUILD_ID="999999999999999999"; export DISCORD_BOT_GUILD_ID
run_resolve
assert_eq   'T-673-02 上書き: 正常終了する' "$RC" "0"
assert_contains 'T-673-02 上書き: 指定値が載る' "$OVERRIDES" 'discord.bot.guildId=999999999999999999'
assert_missing  'T-673-02 上書き: 前回値は載らない' "$OVERRIDES" '111111111111111111'
unset DISCORD_BOT_GUILD_ID

# T-673-03: env に空を明示指定 ＋ 前回値が非空 → キー名を列挙して中断（--set-string しない）
given_release_values 'discord:
  bot:
    userMapping: 111:alice,222:bob'
DISCORD_BOT_USER_MAPPING=""; export DISCORD_BOT_USER_MAPPING
run_resolve
assert_eq   'T-673-03 中断: 非ゼロ終了する' "$RC" "1"
assert_contains 'T-673-03 中断: 対象キー名を列挙する' "$ERR" 'discord.bot.userMapping'
assert_contains 'T-673-03 中断: 環境変数名を示す' "$ERR" 'DISCORD_BOT_USER_MAPPING'
unset DISCORD_BOT_USER_MAPPING

# T-673-04: T-673-03 ＋ 明示フラグ → 空で上書きする
given_release_values 'discord:
  bot:
    userMapping: 111:alice,222:bob'
DISCORD_BOT_USER_MAPPING=""; export DISCORD_BOT_USER_MAPPING
FORCE_EMPTY_VALUES=1
run_resolve
assert_eq   'T-673-04 強制: 正常終了する' "$RC" "0"
assert_contains 'T-673-04 強制: 空値で上書きする' "$OVERRIDES" 'discord.bot.userMapping='
unset DISCORD_BOT_USER_MAPPING
FORCE_EMPTY_VALUES=0

# ---- #673: helm upgrade 後の rollout restart（OpenD を除く） --------------
# タグ :latest 固定 + imagePullPolicy IfNotPresent のため、Pod テンプレートが変わらないサービスは
# イメージを焼き直しても Pod が古いまま残る。ast_rollout_restart_workers が OpenD を除く全 Deployment を
# 再起動することを固定する。
printf '\nk8s-local-deploy.sh: helm upgrade 後の rollout restart（#673）\n'

# T-673-05: OpenD を含む Deployment 一覧 → OpenD を除く全件へ rollout restart を打つ
given_secret ""
given_deployments 'audit-service
backtest-service
configuration-service
cost-control-service
information-collection-service
market-monitor-service
notification-service
order-execution-service
report-service
risk-management-service
trade-decision-service
opend'
run_rollout
assert_eq   'T-673-05 rollout: 正常終了する' "$RC" "0"
assert_contains 'T-673-05 rollout: audit-service を再起動する' "$RESTARTED" 'audit-service'
assert_contains 'T-673-05 rollout: notification-service を再起動する' "$RESTARTED" 'notification-service'
assert_contains 'T-673-05 rollout: trade-decision-service を再起動する' "$RESTARTED" 'trade-decision-service'
assert_missing  'T-673-05 rollout: OpenD は除外する' "$RESTARTED" 'opend'
assert_contains 'T-673-05 rollout: 件数を表示する（OpenD を除いた 11 件）' "$OUT" '11 件'

# T-673-06: Deployment が 1 つも無い（クラスタ未作成等）→ エラーにならず何も再起動しない
given_deployments ""
run_rollout
assert_eq   'T-673-06 空: 正常終了する' "$RC" "0"
assert_eq   'T-673-06 空: 何も再起動しない' "$RESTARTED" ''

# ---- #795 / IADR-0341: 連結ローカルの ESO 所有（画面から入れた値を Pod へ届ける） ----------
# ESO が ast-secrets / moomoo-credentials / moomoo-rsa を所有するプロファイルでは、本スクリプトがそれらを作る・
# パッチする・discord.bot.* を values で渡すと所有が割れる（画面で入れた値と食い違う）。AST_ESO で経路を選ぶ。
printf '\nk8s-local-deploy.sh: ESO 所有の経路切り替え（#795 / IADR-0341）\n'

cat > "$PROFILES/eso.yaml" <<'YAML'
# コメント: コロンを含む行でも降下が崩れないこと
externalSecrets:
  # 入れ子のコメント: 同上
  enabled: true
  appSecrets:
    enabled: true
YAML
cat > "$PROFILES/noeso.yaml" <<'YAML'
externalSecrets:
  enabled: true
  appSecrets:
    enabled: false
YAML

# resolve_ast_eso_mode をサブシェルで実行し、RC / OUT / ERR / MODE / ESO_OVERRIDES を埋める。
run_eso_mode() {
  (
    resolve_ast_eso_mode
    rc=$?
    printf '%s\n' "${AST_ESO_MODE:-}" > "$STATE/mode.txt"
    printf '%s\n' "${AST_ESO_OVERRIDES[@]:-}" > "$STATE/eso-overrides.txt"
    exit $rc
  ) > "$STATE/out" 2> "$STATE/err"
  RC=$?
  OUT="$(cat "$STATE/out")"
  ERR="$(cat "$STATE/err")"
  MODE="$(cat "$STATE/mode.txt" 2>/dev/null || true)"
  ESO_OVERRIDES="$(cat "$STATE/eso-overrides.txt" 2>/dev/null || true)"
}

# 経路を解決したうえで ast_prepare_secrets を実行し、RC / OUT / ERR / PATCH / DELETED を埋める。
run_prepare() {
  ( resolve_ast_eso_mode > /dev/null 2>&1 || exit 9; ast_prepare_secrets ) > "$STATE/out" 2> "$STATE/err"
  RC=$?
  OUT="$(cat "$STATE/out")"
  ERR="$(cat "$STATE/err")"
  PATCH=""
  [ -f "$STATE/patch.json" ] && PATCH="$(cat "$STATE/patch.json")"
  DELETED="$(cat "$STATE/deleted.log" 2>/dev/null || true)"
}

# 経路を解決したうえで resolve_ast_value_overrides を実行する（run_resolve の ESO 版）。
run_resolve_with_mode() {
  (
    resolve_ast_eso_mode > /dev/null 2>&1 || exit 9
    resolve_ast_value_overrides
    rc=$?
    printf '%s\n' "${AST_VALUE_OVERRIDES[@]:-}" > "$STATE/overrides.txt"
    exit $rc
  ) > "$STATE/out" 2> "$STATE/err"
  RC=$?
  OUT="$(cat "$STATE/out")"
  ERR="$(cat "$STATE/err")"
  OVERRIDES="$(cat "$STATE/overrides.txt" 2>/dev/null || true)"
}

# ESO が稼働している連結クラスタ（CRD と ClusterSecretStore vault-backend が在る）を疑似する。
given_eso_cluster() { : > "$STATE/crd"; : > "$STATE/store-vault-backend"; }

# T-795-01: AST_ESO 未設定 → 実プロファイル（values-local.yaml）から ESO を導出し、helm へ両フラグ true を明示する
given_secret ""
run_eso_mode
assert_eq   'T-795-01 導出: 正常終了する' "$RC" "0"
assert_eq   'T-795-01 導出: values-local は ESO 所有（mode=1）' "$MODE" "1"
assert_contains 'T-795-01 導出: externalSecrets.enabled=true を明示する' "$ESO_OVERRIDES" 'externalSecrets.enabled=true'
assert_contains 'T-795-01 導出: appSecrets.enabled=true を明示する' "$ESO_OVERRIDES" 'externalSecrets.appSecrets.enabled=true'

# T-795-02: AST_ESO=0 → 非 ESO（従来経路）。プロファイルが ESO でも helm へ両フラグ false を明示する
given_secret ""
AST_ESO=0; export AST_ESO
run_eso_mode
assert_eq   'T-795-02 明示 0: mode=0' "$MODE" "0"
assert_contains 'T-795-02 明示 0: externalSecrets.enabled=false を明示する' "$ESO_OVERRIDES" 'externalSecrets.enabled=false'
assert_contains 'T-795-02 明示 0: appSecrets.enabled=false を明示する' "$ESO_OVERRIDES" 'externalSecrets.appSecrets.enabled=false'

# T-795-03: AST_ESO 未設定 ＋ appSecrets を有効にしていないプロファイル → 非 ESO（ast-secrets を ESO が持たない）
given_secret ""
AST_PROFILE_VALUES="$PROFILES/noeso.yaml"; export AST_PROFILE_VALUES
run_eso_mode
assert_eq   'T-795-03 導出（非 ESO プロファイル）: mode=0' "$MODE" "0"
AST_PROFILE_VALUES="$PROFILES/eso.yaml"
run_eso_mode
assert_eq   'T-795-03 導出（コメント入り ESO プロファイル）: mode=1' "$MODE" "1"

# T-795-04: AST_ESO に解釈できない値 → 中断（黙って片方の経路へ倒さない）
given_secret ""
AST_ESO=yes; export AST_ESO
run_eso_mode
assert_eq   'T-795-04 不正値: 終了コード 2' "$RC" "2"
assert_contains 'T-795-04 不正値: 変数名を示す' "$ERR" 'AST_ESO'

# T-795-05: ESO モード → ast-secrets を作成もパッチもしない（sync_ast_secrets を呼ばない）
given_secret "absent"
given_eso_cluster
run_prepare
assert_eq   'T-795-05 ESO: 正常終了する' "$RC" "0"
[ -f "$STATE/created" ] && ng 'T-795-05 ESO: Secret を作成しない' 'create secret が呼ばれた' || ok 'T-795-05 ESO: Secret を作成しない'
assert_eq   'T-795-05 ESO: パッチしない' "$PATCH" ""
assert_contains 'T-795-05 ESO: 同期を行わない旨を表示する' "$OUT" 'ExternalSecret'

# T-795-06: ESO モード ＋ CRD 不在（基盤を ESO=1 で起動していない）→ 案内して中断（helm upgrade を失敗させない）
given_secret "absent"
run_prepare
assert_eq   'T-795-06 CRD 不在: 非ゼロ終了する' "$RC" "1"
assert_contains 'T-795-06 CRD 不在: 基盤側の起動方法を示す' "$ERR" 'ESO=1'
assert_contains 'T-795-06 CRD 不在: 従来経路への逃げ道を示す' "$ERR" 'AST_ESO=0'
assert_eq   'T-795-06 CRD 不在: パッチしない' "$PATCH" ""

# T-795-06b: ESO モード ＋ CRD はあるが ClusterSecretStore が無い → 中断
given_secret "absent"
: > "$STATE/crd"
run_prepare
assert_eq   'T-795-06b store 不在: 非ゼロ終了する' "$RC" "1"
assert_contains 'T-795-06b store 不在: store 名を示す' "$ERR" 'vault-backend'

# T-795-07: ESO モード ＋ 管理外の既存 Secret → 1 回だけ警告して名前を列挙し、削除しない
given_secret "fred-api-key"
given_eso_cluster
: > "$STATE/exists-moomoo-credentials"
: > "$STATE/exists-moomoo-rsa"
printf 'ExternalSecret\n' > "$STATE/owner-moomoo-rsa"
run_prepare
assert_eq   'T-795-07 管理外: 正常終了する（警告のみ）' "$RC" "0"
assert_contains 'T-795-07 管理外: ast-secrets を列挙する' "$ERR" '- ast-secrets'
assert_contains 'T-795-07 管理外: moomoo-credentials を列挙する' "$ERR" '- moomoo-credentials'
assert_missing  'T-795-07 管理外: ESO 所有済みの moomoo-rsa は列挙しない' "$ERR" '- moomoo-rsa'
assert_eq   'T-795-07 管理外: 警告は 1 回だけ' "$(printf '%s\n' "$ERR" | grep -c 'WARN:' || true)" "1"
assert_contains 'T-795-07 管理外: 解消手順（画面で先に入れる）を示す' "$ERR" '画面'
assert_eq   'T-795-07 管理外: 削除しない' "$DELETED" ""
assert_eq   'T-795-07 管理外: パッチしない' "$PATCH" ""

# T-795-08: ESO モード ＋ 鍵の env を export 済み → 使わない旨を変数名で警告し、値は出さない
given_secret "absent"
given_eso_cluster
FINNHUB_API_KEY="eso-canary-do-not-log"; export FINNHUB_API_KEY
run_prepare
assert_contains 'T-795-08 env 無視: 変数名を示す' "$ERR" 'FINNHUB_API_KEY'
assert_missing  'T-795-08 env 無視: stderr に値を出さない' "$ERR" 'eso-canary-do-not-log'
assert_missing  'T-795-08 env 無視: stdout に値を出さない' "$OUT" 'eso-canary-do-not-log'
assert_eq   'T-795-08 env 無視: パッチしない' "$PATCH" ""

# T-795-09: ESO モード → discord.bot.* を env からも前回リリースからも渡さない（broker.tier は従来どおり引き継ぐ）
given_secret ""
given_release_values 'broker:
  tier: moomoo-sim
discord:
  bot:
    guildId: "111111111111111111"'
DISCORD_BOT_CHANNEL_ID="222222222222222222"; export DISCORD_BOT_CHANNEL_ID
run_resolve_with_mode
assert_eq   'T-795-09 ESO: 正常終了する' "$RC" "0"
assert_contains 'T-795-09 ESO: broker.tier は引き継ぐ' "$OVERRIDES" 'broker.tier=moomoo-sim'
assert_missing  'T-795-09 ESO: 前回値の discord.bot.guildId を渡さない' "$OVERRIDES" 'discord.bot.guildId'
assert_missing  'T-795-09 ESO: env の discord.bot.channelId を渡さない' "$OVERRIDES" 'discord.bot.channelId'
assert_contains 'T-795-09 ESO: 使わない旨を警告する' "$ERR" 'discord.bot'

# T-795-10: AST_ESO=0 → 従来どおり ast-secrets を同期し、discord.bot.* を引き継ぐ
given_secret "absent"
AST_ESO=0; export AST_ESO
run_prepare
assert_eq   'T-795-10 非 ESO: 正常終了する' "$RC" "0"
[ -f "$STATE/created" ] && ok 'T-795-10 非 ESO: Secret を作成する' || ng 'T-795-10 非 ESO: Secret を作成する' 'create secret が呼ばれていない'
assert_contains 'T-795-10 非 ESO: dev 既定が載る' "$PATCH" "\"service-auth-client-id\":\"$(b64 ai-stock-trading-svc)\""
given_release_values 'discord:
  bot:
    guildId: "111111111111111111"'
run_resolve_with_mode
assert_contains 'T-795-10 非 ESO: discord.bot.guildId を引き継ぐ' "$OVERRIDES" 'discord.bot.guildId=111111111111111111'

printf '\n%d passed, %d failed\n' "$PASSED" "$FAILED"
[ "$FAILED" -eq 0 ] || exit 1
