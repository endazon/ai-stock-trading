#!/usr/bin/env bash
# #263 / IADR-0109: scripts/k8s-local-deploy.sh の ast-secrets 同期（sync_ast_secrets）の挙動を固定する。
#
#   bash scripts/k8s-local-deploy.test.sh
#
# 実クラスタは要らない。PATH の先頭に `kubectl` スタブを置き、スタブが「既存 Secret の状態」を
# 疑似し、生成されたパッチ（--patch-file の中身）を記録する。対象スクリプトは AST_DEPLOY_LIB=1 で
# source すると関数定義だけを読み込み、デプロイ手順（image build / helm）は実行しない。
#
# 検証する不変条件（Issue #263 の受け入れ基準）:
#   - 鍵を export せずに再実行しても投入済みの値が失われない（パッチに載せない＝API サーバ側で保持）
#   - 空上書きが避けられない場合はキー名を列挙して中断し、明示フラグでのみ実行する
#   - 新規環境（Secret 未作成）では従来どおり作成できる
#   - 平文の鍵を標準出力・標準エラーへ出さない（キー名のみ）
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
  FORCE_EMPTY=0
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

printf '\n%d passed, %d failed\n' "$PASSED" "$FAILED"
[ "$FAILED" -eq 0 ] || exit 1
