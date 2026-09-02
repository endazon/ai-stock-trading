#!/usr/bin/env bash
# #263 / IADR-0109: scripts/k8s-local-deploy.sh の ast-secrets 同期（sync_ast_secrets）の挙動を固定する。
# #626 / IADR-0275: broker.tier / opend.enabled の前回値引き継ぎ（resolve_ast_value_overrides）も併せて固定する。
#
#   bash scripts/k8s-local-deploy.test.sh
#
# 実クラスタは要らない。PATH の先頭に `kubectl` / `helm` スタブを置き、スタブが「既存 Secret の状態」
# 「前回リリースの values」を疑似し、生成されたパッチ（--patch-file の中身）を記録する。対象スクリプトは
# AST_DEPLOY_LIB=1 で source すると関数定義だけを読み込み、デプロイ手順（image build / helm）は実行しない。
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
set -u

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
STATE="$(mktemp -d)"
STUB_BIN="$(mktemp -d)"
trap 'rm -rf "$STATE" "$STUB_BIN"' EXIT

# ---- kubectl スタブ -------------------------------------------------------
# 状態は $AST_TEST_STATE に置く: exists（Secret 実在）/ nonempty_keys（非空の値を持つキー名）/
# created（create secret が呼ばれた）/ patch.json（適用されたパッチ）。
cat > "$STUB_BIN/kubectl" <<'STUB'
#!/usr/bin/env bash
set -u
S="$AST_TEST_STATE"
case "${1:-} ${2:-}" in
  "get secret")
    [ -f "$S/exists" ] || { echo 'Error from server (NotFound): secrets "ast-secrets" not found' >&2; exit 1; }
    for a in "$@"; do
      case "$a" in go-template=*) cat "$S/nonempty_keys"; exit 0 ;; esac
    done
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
trap 'ast_cleanup; rm -rf "$STATE" "$STUB_BIN"' EXIT

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
SERVICEAUTH_CLIENTSECRET KB_AUTH_CLIENTID KB_AUTH_CLIENTSECRET DISCORD_OWNERAUTH_CLIENTID
DISCORD_OWNERAUTH_CLIENTSECRET"

# 既存 Secret の状態を作り直し、環境変数とフラグを既定へ戻す。
# $1 = "absent" もしくは非空の値を持つキー名の空白区切り（"" なら Secret は在るが全キー空）
given_secret() {
  local v
  for v in $SECRET_ENV_VARS; do unset "$v"; done
  unset BROKER_TIER OPEND_ENABLED
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


# ---- #626 / IADR-0275: broker.tier / opend.enabled の前回値引き継ぎ --------
# helm upgrade --install が --reuse-values を使わないため、env passthrough が無いと前回リリースの
# 値が既定（paper / false）へ黙って戻る（opend.enabled=false は OpenD の Deployment/PVC を削除する）。
printf '\nk8s-local-deploy.sh: broker.tier / opend.enabled の前回値引き継ぎ（#626 / IADR-0275）\n'

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

printf '\n%d passed, %d failed\n' "$PASSED" "$FAILED"
[ "$FAILED" -eq 0 ] || exit 1
