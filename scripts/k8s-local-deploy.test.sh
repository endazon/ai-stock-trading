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
#
# 検証する不変条件（NFR, IADR-0570。Refs GHSA-j4qv-2w99-xg5p）:
#   - AST_MESH_INJECTION=1|0 を明示すれば mesh.sidecarInjection.enabled をその値で渡す（有効なら excludeOpend=true も）
#   - 未指定の再実行は現行のリリースの値を引き継ぎ、新規・未宣言では helm へ何も足さない（従来どおり）
#   - 有効なら名前空間へ注入ラベルを貼り、明示の 0 なら剥がし、それ以外は触らない
#   - 注入の状態が変わった回は絞り込みがあっても OpenD を除く全件を restart する
#   - 不正な値・読めない前回値・ラベルの読み書きの失敗は止まる
#
# 検証する不変条件（Issue #1094 の受け入れ基準。images 側は scripts/k8s-local-images.test.sh）:
#   - 絞り込み（SERVICES / --changed-since）が無ければ restart は従来どおり全件（上の #673 の検査を無改修で維持）
#   - 絞り込みがあれば、作り直したサービスの Deployment だけを restart する（OpenD は除外のまま）
#   - SERVICES / --changed-since の結果を k8s-local-images.sh の SERVICES へ中継する（ALL・選択器の失敗は全件へ倒す）
#   - --changed-since の値欠落・未知のオプションは exit 2
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
        # GHSA-pcf9: 1 キーの値（base64 のまま）。実の kubectl と同じく、キーが無いとき
        # `{{index .data "k"}}` は `<no value>` を出し、`{{with index .data "k"}}…{{end}}` は空を出す。
        go-template={{*index\ .data*)
          k="${a#*\"}"; k="${k%%\"*}"
          if [ -f "$S/data-$k" ]; then cat "$S/data-$k"
          else case "$a" in *"{{with index"*) ;; *) printf '<no value>' ;; esac
          fi
          exit 0 ;;
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
    # #1351: 名前つき（get deployment <name> ...）は稼働中の strategy を "<type>|<rollingUpdate>" で返す。
    # $S/strategy-<name> が無ければ未作成（--ignore-not-found の空出力）、$S/get-deploy-fail があれば失敗する。
    case "${3:-}" in
      ""|-*) ;;
      *)
        [ -f "$S/get-deploy-fail" ] && { echo 'Error from server (Forbidden): deployments.apps is forbidden' >&2; exit 1; }
        [ -f "$S/strategy-$3" ] && cat "$S/strategy-$3"
        exit 0
        ;;
    esac
    [ -f "$S/deployments" ] && cat "$S/deployments"
    exit 0
    ;;
  "patch deployment")
    # #1351: 適用した Deployment 名とパッチ（-p の値）を記録する。$S/patch-deploy-fail があれば失敗する。
    [ -f "$S/patch-deploy-fail" ] && { echo 'Error from server: patch failed' >&2; exit 1; }
    prev="" body=""
    for a in "$@"; do
      [ "$prev" = "-p" ] && body="$a"
      prev="$a"
    done
    printf '%s %s\n' "${3:-}" "$body" >> "$S/patched-deploy.log"
    exit 0
    ;;
  "get namespace")
    # NFR, IADR-0570: 名前空間の istio-injection ラベル（$S/ns-label の中身。無ければ空）。$S/get-ns-fail があれば失敗する。
    [ -f "$S/get-ns-fail" ] && { echo 'Error from server (Forbidden): namespaces is forbidden' >&2; exit 1; }
    [ -f "$S/ns-label" ] && cat "$S/ns-label"
    exit 0
    ;;
  "label namespace")
    # NFR, IADR-0570: 貼る（istio-injection=enabled）・剥がす（istio-injection-）を記録し、状態へ反映する。
    [ -f "$S/label-ns-fail" ] && { echo 'Error from server: label failed' >&2; exit 1; }
    for a in "$@"; do
      case "$a" in
        istio-injection=*) printf '%s\n' "$a" >> "$S/ns-label.log"; printf '%s' "${a#istio-injection=}" > "$S/ns-label" ;;
        istio-injection-) printf '%s\n' "$a" >> "$S/ns-label.log"; rm -f "$S/ns-label" ;;
      esac
    done
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
  "template "*)
    # #1351: 描画（ast_migrate_recreate_strategy が使う）。$S/rendered.yaml を返し、無ければ失敗する。
    [ -f "$S/rendered.yaml" ] || { echo 'Error: render failed' >&2; exit 1; }
    cat "$S/rendered.yaml"
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
SERVICEAUTH_CLIENTSECRET KB_AUTH_CLIENTID KB_AUTH_CLIENTSECRET KB_READER_AUTH_CLIENTID KB_READER_AUTH_CLIENTSECRET LLM_AUTH_CLIENTID LLM_AUTH_CLIENTSECRET DISCORD_OWNERAUTH_CLIENTID
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
  unset AST_MESH_INJECTION AST_MESH_INJECTION_EFFECTIVE AST_MESH_INJECTION_SOURCE AST_MESH_INJECTION_PREV AST_MESH_INJECTION_CHANGED
  FORCE_EMPTY=0
  FORCE_EMPTY_VALUES=0
  ADOPT_EXISTING=0
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
# #1078, IADR-0485: KB 検索の s2s は書き手とは別主体（kb-reader）。dev 既定が入り、秘密は空既定（env で与える）。
assert_contains 'T-1078-01 新規: dev 既定（KB 読み手クライアント ID）が入る' "$PATCH" "\"kb-reader-auth-client-id\":\"$(b64 ai-stock-trading-kb-reader)\""
assert_contains 'T-1078-02 新規: KB 読み手の秘密キーも作られる' "$PATCH" '"kb-reader-auth-client-secret":""'
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

# ---- #1094 / IADR-0457: 作り直したサービスだけの rollout restart --------------
printf '\nk8s-local-deploy.sh: 作り直したサービスだけの rollout restart（#1094）\n'

ALL_DEPLOYMENTS='audit-service
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

# 作り直した名前の一覧（k8s-local-images.sh の K8S_BUILT_FILE の疑似）。$1 = 改行区切り
given_built() { printf '%s' "${1:-}" > "$STATE/built"; [ -n "${1:-}" ] && echo >> "$STATE/built"; return 0; }
run_rollout_built() {
  ( ast_rollout_restart_workers "$STATE/built" ) > "$STATE/out" 2> "$STATE/err"
  RC=$?
  OUT="$(cat "$STATE/out")"
  ERR="$(cat "$STATE/err")"
  RESTARTED="$(cat "$STATE/restarted.log" 2>/dev/null || true)"
}

# T-1094-D01: 作り直した 2 件だけを restart する
given_secret ""
given_deployments "$ALL_DEPLOYMENTS"
given_built 'risk-management-service
trade-decision-service'
run_rollout_built
assert_eq   'T-1094-D01 絞り込み: 正常終了する' "$RC" "0"
assert_eq   'T-1094-D01 絞り込み: 作り直した 2 件だけを restart する' "$RESTARTED" 'risk-management-service
trade-decision-service'
assert_contains 'T-1094-D01 絞り込み: 件数と範囲を表示する' "$OUT" '2 件（作り直したサービスだけ'

# T-1094-D02: 一覧が空（何も作り直していない）→ 何も restart しない
given_deployments "$ALL_DEPLOYMENTS"
given_built ''
run_rollout_built
assert_eq   'T-1094-D02 空: 正常終了する' "$RC" "0"
assert_eq   'T-1094-D02 空: 何も restart しない' "$RESTARTED" ''

# T-1094-D03: 一覧に OpenD（Deployment 名 opend）が紛れても restart しない。opend-auth-gateway は案内だけ出す
given_deployments "$ALL_DEPLOYMENTS"
given_built 'opend
opend-auth-gateway
audit-service'
run_rollout_built
assert_eq   'T-1094-D03 OpenD: audit-service だけを restart する' "$RESTARTED" 'audit-service'
assert_contains 'T-1094-D03 OpenD: サイドカーの作り直しを案内する' "$ERR" 'rollout restart deploy/opend'

# T-1094-D04: 一覧にあってもクラスタに Deployment が無い名前は restart しない（新規サービスは helm が作る）
given_deployments 'audit-service'
given_built 'audit-service
report-service'
run_rollout_built
assert_eq   'T-1094-D04 不在: クラスタに在る分だけ' "$RESTARTED" 'audit-service'

# T-1094-D05: 引数なしは従来どおり全件（絞り込みを指定しない既定の不変）
given_deployments "$ALL_DEPLOYMENTS"
run_rollout
assert_contains 'T-1094-D05 既定: 件数は OpenD を除いた 11 件のまま' "$OUT" '11 件（OpenD は除外）'

# 手順 5 の範囲の決定（ast_restart_step）。$1 = AST_IMAGE_SELECTION、$2 = AST_SECRETS_PATCHED
run_restart_step() {
  ( AST_IMAGE_SELECTION="$1"; AST_SECRETS_PATCHED="$2"; ast_restart_step "$STATE/built" ) > "$STATE/out" 2> "$STATE/err"
  RC=$?
  OUT="$(cat "$STATE/out")"
  RESTARTED="$(cat "$STATE/restarted.log" 2>/dev/null || true)"
}

# T-1094-D06: 絞り込みあり・Secret を触っていない → 作り直した分だけ
given_deployments "$ALL_DEPLOYMENTS"
given_built 'audit-service'
run_restart_step 1 0
assert_eq   'T-1094-D06 絞り込み: 作り直した分だけ' "$RESTARTED" 'audit-service'

# T-1094-D07: 🔴 絞り込みあり・ast-secrets をパッチした → 全件（Secret 参照の env は再起動でしか届かない）
given_deployments "$ALL_DEPLOYMENTS"
given_built 'audit-service'
run_restart_step 1 1
assert_contains 'T-1094-D07 Secret 更新: 全件へ戻す' "$OUT" '11 件（OpenD は除外）'
assert_contains 'T-1094-D07 Secret 更新: 理由を表示する' "$OUT" 'ast-secrets を更新したため'

# T-1094-D08: 絞り込みなし → 全件（従来どおり）
given_deployments "$ALL_DEPLOYMENTS"
given_built 'audit-service'
run_restart_step 0 0
assert_contains 'T-1094-D08 絞り込みなし: 全件' "$OUT" '11 件（OpenD は除外）'

# T-1094-D09: 🔴 ast-secrets のパッチは AST_SECRETS_PATCHED=1 を立て、作り直した名前の一覧（AST_BUILT_DIR）を消さない
# （以前は後始末の関数ごと呼び、手順 5 の前に一覧が消えて絞り込みの restart が 0 件になっていた）
given_secret "absent"
mkdir -p "$STATE/built-dir"; printf 'audit-service\n' > "$STATE/built-dir/built"
(
  AST_BUILT_DIR="$STATE/built-dir"
  sync_ast_secrets >/dev/null 2>&1
  printf '%s' "$AST_SECRETS_PATCHED" > "$STATE/patched-flag"
)
assert_eq   'T-1094-D09 パッチ: 目印を立てる' "$(cat "$STATE/patched-flag")" '1'
[ -f "$STATE/built-dir/built" ] && ok 'T-1094-D09 パッチ: 作り直した名前の一覧を消さない' || ng 'T-1094-D09 パッチ: 作り直した名前の一覧を消さない' 'built が消えた'

# T-1094-D10: 全キーが既存で env の指定が無い（パッチしない）回は目印を立てない
given_secret "$(for spec in "${AST_SECRET_KEYS[@]}"; do printf '%s ' "${spec%%|*}"; done)"
(
  sync_ast_secrets >/dev/null 2>&1
  printf '%s|%s' "$AST_SECRETS_PATCHED" "$([ -f "$STATE/patch.json" ] && echo patched || echo none)" > "$STATE/patched-flag"
)
assert_eq   'T-1094-D10 パッチなし: 目印は 0 のまま' "$(cat "$STATE/patched-flag")" '0|none'

# T-1094-D12: 🔴 空の既定値を入れ直すだけのパッチは目印を立てない（従来経路で毎回絞り込みが解けるのを防ぐ）
given_secret "$(for spec in "${AST_SECRET_KEYS[@]}"; do k="${spec%%|*}"; [ "$k" = "fred-api-key" ] || printf '%s ' "$k"; done)"
(
  sync_ast_secrets >/dev/null 2>&1
  printf '%s|%s' "$AST_SECRETS_PATCHED" "$(cat "$STATE/patch.json" 2>/dev/null)" > "$STATE/patched-flag"
)
assert_eq   'T-1094-D12 空の既定値だけ: 目印は 0 のまま（パッチは送る）' "$(cat "$STATE/patched-flag")" '0|{"data":{"fred-api-key":""}}'

# T-1094-D13: env の明示指定は（平文を読み比べないので）空でも目印を立てる（--force-empty-secrets で非空の値を消す回）
given_secret "$(for spec in "${AST_SECRET_KEYS[@]}"; do printf '%s ' "${spec%%|*}"; done)"
(
  FRED_API_KEY=""; export FRED_API_KEY
  FORCE_EMPTY=1
  sync_ast_secrets >/dev/null 2>&1
  printf '%s' "$AST_SECRETS_PATCHED" > "$STATE/patched-flag"
)
assert_eq   'T-1094-D13 env の明示指定: 目印を立てる' "$(cat "$STATE/patched-flag")" '1'

# T-1094-D11: 🔴 本体の配線（AST_DEPLOY_LIB=1 の手前で return するため、手順の並びは静的に固定する）
MAIN_PART="$(sed -n '/^if \[ "\${AST_DEPLOY_LIB:-}" = "1" \]; then/,$p' "$ROOT_DIR/scripts/k8s-local-deploy.sh")"
assert_contains 'T-1094-D11 配線: 選択を解決する' "$MAIN_PART" 'resolve_ast_image_selection'
assert_contains 'T-1094-D11 配線: images へ作り直した名前の一覧を渡す' "$MAIN_PART" 'K8S_BUILT_FILE="$AST_BUILT_DIR/built" "$ROOT/scripts/k8s-local-images.sh"'
assert_contains 'T-1094-D11 配線: 手順 5 へ同じ一覧を渡す' "$MAIN_PART" 'ast_restart_step "$AST_BUILT_DIR/built"'
# 順序: 選択の解決 → images（一覧を書く）→ secret 同期 → 手順 5。コメント行は数えない。
MAIN_ORDER="$(printf '%s\n' "$MAIN_PART" | grep -v '^[[:space:]]*#' | grep -nE '^(resolve_ast_image_selection|K8S_BUILT_FILE=|ast_prepare_secrets|ast_restart_step)' | sed -E 's/^[0-9]+:([A-Za-z0-9_]+).*/\1/' | tr '\n' ' ')"
assert_eq   'T-1094-D11 配線: 手順の順序（コメントアウトも捕まえる）' "$MAIN_ORDER" 'resolve_ast_image_selection K8S_BUILT_FILE ast_prepare_secrets ast_restart_step '

printf '\nk8s-local-deploy.sh: 作り直すイメージの絞り込みの中継（#1094）\n'

# 選択器の疑似（node で走る JS。$1 = 標準出力に出す内容、$2 = 終了コード）
SELECTOR_STUB="$STUB_BIN/selector-stub.js"   # given_secret が $STATE を作り直しても消えない場所
given_selector() {
  cat > "$SELECTOR_STUB" <<JS
if (process.argv[2] !== '--since') process.exit(9);
require('fs').writeFileSync(process.env.AST_TEST_STATE + '/selector-args', process.argv.slice(2).join(' '));
process.stdout.write(${1});
process.exit(${2:-0});
JS
}
# resolve_ast_image_selection をサブシェルで実行し、RC / OUT / ERR / SEL_SERVICES / SEL_MODE を埋める。
run_selection() {
  (
    resolve_ast_image_selection
    rc=$?
    printf '%s|%s' "${SERVICES-<unset>}" "${AST_IMAGE_SELECTION:-}" > "$STATE/selection"
    exit $rc
  ) > "$STATE/out" 2> "$STATE/err"
  RC=$?
  OUT="$(cat "$STATE/out")"
  ERR="$(cat "$STATE/err")"
  SEL_SERVICES="$(cut -d'|' -f1 "$STATE/selection" 2>/dev/null)"
  SEL_MODE="$(cut -d'|' -f2 "$STATE/selection" 2>/dev/null)"
}
reset_selection() { unset SERVICES CHANGED_SINCE; export AST_SELECT_CHANGED_SERVICES="$SELECTOR_STUB"; }

# T-1094-S01: 指定なし → 絞り込みなし（SERVICES 未設定のまま＝images は全件・restart も全件）
given_secret ""
reset_selection
run_selection
assert_eq   'T-1094-S01 指定なし: 正常終了する' "$RC" "0"
assert_eq   'T-1094-S01 指定なし: SERVICES を設定しない' "$SEL_SERVICES" '<unset>'
assert_eq   'T-1094-S01 指定なし: 絞り込みなし' "$SEL_MODE" '0'

# T-1094-S02: SERVICES → そのまま中継し、絞り込みあり
reset_selection
SERVICES=audit-service,report-service
run_selection
assert_eq   'T-1094-S02 SERVICES: そのまま中継する' "$SEL_SERVICES" 'audit-service,report-service'
assert_eq   'T-1094-S02 SERVICES: 絞り込みあり' "$SEL_MODE" '1'

# T-1094-S03: --changed-since → 選択器の名前をカンマ区切りで中継し、ref を渡す
reset_selection
CHANGED_SINCE=origin/develop
given_selector "'risk-management-service\\ntrade-decision-service\\n'"
run_selection
assert_eq   'T-1094-S03 changed-since: 正常終了する' "$RC" "0"
assert_eq   'T-1094-S03 changed-since: 名前をカンマ区切りで中継する' "$SEL_SERVICES" 'risk-management-service,trade-decision-service'
assert_eq   'T-1094-S03 changed-since: 絞り込みあり' "$SEL_MODE" '1'
assert_eq   'T-1094-S03 changed-since: ref を選択器へ渡す' "$(cat "$STATE/selector-args")" '--since origin/develop'

# T-1094-S04: --changed-since で該当 0 件 → SERVICES=none（ランタイムに無いものだけ作る）
reset_selection
CHANGED_SINCE=origin/develop
given_selector "''"
run_selection
assert_eq   'T-1094-S04 0 件: SERVICES=none' "$SEL_SERVICES" 'none'
assert_eq   'T-1094-S04 0 件: 絞り込みあり（restart も 0 件側）' "$SEL_MODE" '1'

# T-1094-S05: --changed-since で ALL → 絞り込みなし（全件を作り、全件を restart）
reset_selection
CHANGED_SINCE=origin/develop
given_selector "'ALL\\n'"
run_selection
assert_eq   'T-1094-S05 ALL: SERVICES を設定しない' "$SEL_SERVICES" '<unset>'
assert_eq   'T-1094-S05 ALL: 絞り込みなし' "$SEL_MODE" '0'

# T-1094-S06: 選択器の失敗 → 全件へ倒す（中断しない）
reset_selection
CHANGED_SINCE=origin/develop
given_selector "'audit-service\\n'" 1
run_selection
assert_eq   'T-1094-S06 失敗: 正常終了する（配備は止めない）' "$RC" "0"
assert_eq   'T-1094-S06 失敗: 全件へ倒す' "$SEL_SERVICES|$SEL_MODE" '<unset>|0'
assert_contains 'T-1094-S06 失敗: 警告する' "$ERR" '全件を作り直す'

# T-1094-S07: SERVICES と --changed-since の併用 → exit 2
reset_selection
SERVICES=audit-service
CHANGED_SINCE=origin/develop
run_selection
assert_eq   'T-1094-S07 併用: 終了コード 2' "$RC" "2"
assert_contains 'T-1094-S07 併用: 理由を示す' "$ERR" '併用できない'
reset_selection
rm -f "$SELECTOR_STUB"

printf '\nk8s-local-deploy.sh: 引数の解釈（#1094）\n'

# 引数つきで source し（AST_DEPLOY_LIB=1 は解釈の後で戻る）、RC と CHANGED_SINCE|CLUSTER|FORCE_EMPTY を得る。
parse_args() {
  env AST_DEPLOY_LIB=1 PATH="$PATH" bash -c '. "$1/scripts/k8s-local-deploy.sh" "${@:2}" && printf "%s|%s|%s" "$CHANGED_SINCE" "$CLUSTER" "$FORCE_EMPTY"' \
    _ "$ROOT_DIR" "$@" > "$STATE/out" 2> "$STATE/err"
  RC=$?
  PARSED="$(cat "$STATE/out")"
  ERR="$(cat "$STATE/err")"
}
parse_args --changed-since origin/develop my-cluster --force-empty-secrets
assert_eq   'T-1094-A01 --changed-since <ref>: 解釈する（他のフラグ・クラスタ名も従来どおり）' "$RC|$PARSED" '0|origin/develop|my-cluster|1'
parse_args --changed-since=HEAD~3
assert_eq   'T-1094-A02 --changed-since=<ref>: 解釈する（クラスタ名は既定）' "$RC|$PARSED" '0|HEAD~3|msp-ast-dev|0'
parse_args --changed-since
assert_eq   'T-1094-A03 値の欠落: 終了コード 2' "$RC" "2"
parse_args --changed-since --force-empty-secrets
assert_eq   'T-1094-A04 値が次のオプション: 終了コード 2' "$RC" "2"
parse_args --changed-since=-x
assert_eq   'T-1094-A06 = 形で - 始まりの値: 終了コード 2（git のオプションとして渡さない）' "$RC" "2"
parse_args --changed-since=
assert_eq   'T-1094-A07 = 形で空の値: 終了コード 2' "$RC" "2"
parse_args --no-such-option
assert_eq   'T-1094-A05 未知のオプション: 従来どおり終了コード 2' "$RC" "2"
assert_contains 'T-1094-A05 未知のオプション: 名前を示す' "$ERR" 'unknown option: --no-such-option'

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

# T-795-07: ESO モード ＋ 管理外の既存 Secret → 名前を列挙して中断し（helm upgrade へ進ませない）、削除しない
given_secret "fred-api-key"
given_eso_cluster
: > "$STATE/exists-moomoo-credentials"
: > "$STATE/exists-moomoo-rsa"
printf 'ExternalSecret\n' > "$STATE/owner-moomoo-rsa"
run_prepare
assert_eq   'T-795-07 管理外: 非ゼロ終了する（中断）' "$RC" "1"
assert_contains 'T-795-07 管理外: ast-secrets を列挙する' "$ERR" '- ast-secrets'
assert_contains 'T-795-07 管理外: moomoo-credentials を列挙する' "$ERR" '- moomoo-credentials'
assert_missing  'T-795-07 管理外: ESO 所有済みの moomoo-rsa は列挙しない' "$ERR" '- moomoo-rsa'
assert_eq   'T-795-07 管理外: エラーは 1 回だけ' "$(printf '%s\n' "$ERR" | grep -c 'ERROR:' || true)" "1"
assert_contains 'T-795-07 管理外: 解消手順（画面で先に入れる）を示す' "$ERR" '画面'
assert_contains 'T-795-07 管理外: 取り込みの明示フラグを示す' "$ERR" '--adopt-existing-secrets'
assert_eq   'T-795-07 管理外: 削除しない' "$DELETED" ""
assert_eq   'T-795-07 管理外: パッチしない' "$PATCH" ""

# T-795-07b: T-795-07 ＋ --adopt-existing-secrets → 1 回だけ警告して進む（削除もパッチもしない）
given_secret "fred-api-key"
given_eso_cluster
: > "$STATE/exists-moomoo-credentials"
ADOPT_EXISTING=1
run_prepare
assert_eq   'T-795-07b 取り込み許可: 正常終了する' "$RC" "0"
assert_contains 'T-795-07b 取り込み許可: 名前を列挙する' "$ERR" '- ast-secrets'
assert_eq   'T-795-07b 取り込み許可: 警告は 1 回だけ' "$(printf '%s\n' "$ERR" | grep -c 'WARN:' || true)" "1"
assert_missing  'T-795-07b 取り込み許可: エラーにしない' "$ERR" 'ERROR:'
assert_eq   'T-795-07b 取り込み許可: 削除しない' "$DELETED" ""
assert_eq   'T-795-07b 取り込み許可: パッチしない' "$PATCH" ""

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
: > "$STATE/exists-ast-rabbitmq-users"   # IADR-0568: 従来経路ではブローカの資格情報の Secret を先に作っておく前提
run_prepare
assert_eq   'T-795-10 非 ESO: 正常終了する' "$RC" "0"
[ -f "$STATE/created" ] && ok 'T-795-10 非 ESO: Secret を作成する' || ng 'T-795-10 非 ESO: Secret を作成する' 'create secret が呼ばれていない'
assert_contains 'T-795-10 非 ESO: dev 既定が載る' "$PATCH" "\"service-auth-client-id\":\"$(b64 ai-stock-trading-svc)\""
given_release_values 'discord:
  bot:
    guildId: "111111111111111111"'
run_resolve_with_mode
assert_contains 'T-795-10 非 ESO: discord.bot.guildId を引き継ぐ' "$OVERRIDES" 'discord.bot.guildId=111111111111111111'

# T-pvmh-01（IADR-0568・Refs GHSA-pvmh-cvmf-pvcp）: 従来経路でブローカの資格情報の Secret が無ければ、ast-secrets に触れる前に止まる
given_secret "absent"
AST_ESO=0; export AST_ESO
run_prepare
assert_eq       'T-pvmh-01 非 ESO・ブローカの Secret なし: 非 0' "$RC" "1"
assert_contains 'T-pvmh-01 非 ESO・ブローカの Secret なし: 名指す' "$ERR" 'ast-rabbitmq-users'
[ -f "$STATE/created" ] && ng 'T-pvmh-01 非 ESO: ast-secrets を作らない' '止まる前に create secret が呼ばれた' || ok 'T-pvmh-01 非 ESO: ast-secrets を作らない'
# T-pvmh-02: ESO 所有では、管理外の同名 Secret を名指して止める（ESO が所有しにいく対象に入っている）
assert_contains 'T-pvmh-02 ESO 所有の対象に入る' "${AST_ESO_TARGET_SECRETS[*]}" 'ast-rabbitmq-users'
unset AST_ESO

# ---- #1351: Recreate へ移る Deployment に残る rollingUpdate を upgrade の前に外す ----------
printf '\nk8s-local-deploy.sh: Recreate への移行（#1351）\n'

# 描画の疑似。trade-decision と opend が Recreate、audit は既定（strategy 無し）。
RENDERED_RECREATE='---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: audit-service
  namespace: ai-stock-trading
spec:
  replicas: 1
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: trade-decision-service
  namespace: ai-stock-trading
spec:
  replicas: 1
  strategy:
    type: Recreate
  selector:
    matchLabels:
      app: trade-decision-service
---
apiVersion: v1
kind: Service
metadata:
  name: opend-svc
spec:
  type: Recreate
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: opend
  labels:
    app: opend
spec:
  replicas: 1
  strategy:
    type: Recreate
  selector:
    matchLabels: { app: opend }'
ROLLING='RollingUpdate|{"maxSurge":"25%","maxUnavailable":"25%"}'

# $1 = 描画（"absent" で helm template を失敗させる）。続く引数は "<name>=<type|rollingUpdate>"（稼働中の strategy）
given_strategy() {
  local kv
  rm -f "$STATE"/strategy-* "$STATE/patched-deploy.log" "$STATE/get-deploy-fail" "$STATE/patch-deploy-fail" "$STATE/rendered.yaml"
  [ "$1" != "absent" ] && printf '%s\n' "$1" > "$STATE/rendered.yaml"
  shift
  for kv in "$@"; do printf '%s' "${kv#*=}" > "$STATE/strategy-${kv%%=*}"; done
  return 0
}
run_migrate() {
  ( ast_migrate_recreate_strategy --set namespace.create=false -f values-local.yaml ) > "$STATE/out" 2> "$STATE/err"
  RC=$?
  OUT="$(cat "$STATE/out")"
  ERR="$(cat "$STATE/err")"
  PATCHED="$(cat "$STATE/patched-deploy.log" 2>/dev/null || true)"
}

# T-1351-01: 描画が Recreate で、稼働中が RollingUpdate＋rollingUpdate → その Deployment だけを Recreate へ置き換える
given_secret ""
given_strategy "$RENDERED_RECREATE" "trade-decision-service=$ROLLING" "opend=Recreate|" "audit-service=$ROLLING"
run_migrate
assert_eq   'T-1351-01 要る: 正常終了する' "$RC" "0"
assert_eq   'T-1351-01 要る: trade-decision だけを /spec/strategy ごと Recreate へ置き換える' "$PATCHED" \
  'trade-decision-service [{"op":"replace","path":"/spec/strategy","value":{"type":"Recreate"}}]'
assert_contains 'T-1351-01 要る: 操作を 1 行出す' "$OUT" 'deploy/trade-decision-service RollingUpdate → Recreate'
assert_contains 'T-1351-01 要る: Pod が作り直されることを出す' "$OUT" 'Pod は全数を止めてから作り直される'
assert_eq   'T-1351-01 要る: 操作の行は 1 件' "$(printf '%s\n' "$OUT" | grep -c 'strategy 移行' || true)" "1"

# T-1351-02: すでに Recreate・未作成・描画が Recreate でない → 何もしない（冪等）
given_strategy "$RENDERED_RECREATE" "trade-decision-service=Recreate|" "audit-service=$ROLLING"
run_migrate
assert_eq   'T-1351-02 不要: 正常終了する' "$RC" "0"
assert_eq   'T-1351-02 不要: patch しない' "$PATCHED" ''
assert_contains 'T-1351-02 不要: 対象なしと出す' "$OUT" '対象なし'

# T-1351-03: 描画に Recreate が無い（本番既定）→ 稼働中が RollingUpdate でも何もしない
given_strategy '---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: trade-decision-service
spec:
  replicas: 1' "trade-decision-service=$ROLLING"
run_migrate
assert_eq   'T-1351-03 描画が既定: patch しない' "$PATCHED" ''

# T-1351-04: 🔴 稼働中の取得に失敗 → 止まる（fail-closed）
given_strategy "$RENDERED_RECREATE" "trade-decision-service=$ROLLING"
: > "$STATE/get-deploy-fail"
run_migrate
assert_eq   'T-1351-04 取得失敗: 非 0 で止まる' "$RC" "1"
assert_contains 'T-1351-04 取得失敗: 理由を出す' "$ERR" 'deploy/trade-decision-service の取得に失敗'
assert_eq   'T-1351-04 取得失敗: patch しない' "$PATCHED" ''

# T-1351-05: 🔴 patch に失敗 → 止まる（fail-closed）
given_strategy "$RENDERED_RECREATE" "trade-decision-service=$ROLLING"
: > "$STATE/patch-deploy-fail"
run_migrate
assert_eq   'T-1351-05 patch 失敗: 非 0 で止まる' "$RC" "1"
assert_contains 'T-1351-05 patch 失敗: 理由を出す' "$ERR" 'deploy/trade-decision-service の patch に失敗'

# T-1351-06: 🔴 描画に失敗 → 止まる（対象を判定できないまま upgrade へ進まない）
given_strategy absent "trade-decision-service=$ROLLING"
run_migrate
assert_eq   'T-1351-06 描画失敗: 非 0 で止まる' "$RC" "1"
assert_contains 'T-1351-06 描画失敗: 理由を出す' "$ERR" 'helm template が失敗'

# T-1351-07: 起動器の本体は upgrade と同じ値で移行の段を呼び、その後に helm upgrade する（順序の固定）
main_body="$(sed -n '/^AST_HELM_VALUE_ARGS=(/,/^helm upgrade --install/p' "$ROOT_DIR/scripts/k8s-local-deploy.sh")"
assert_contains 'T-1351-07 順序: 移行の段を upgrade と同じ値で呼ぶ' "$main_body" 'ast_migrate_recreate_strategy "${AST_HELM_VALUE_ARGS[@]}"'
assert_contains 'T-1351-07 順序: その後に upgrade する' "$main_body" 'helm upgrade --install "$RELEASE" deploy/helm/ai-stock-trading -n "$NS" "${AST_HELM_VALUE_ARGS[@]}"'

# ---- NFR, IADR-0098（2026-10-22 追記・Refs GHSA-pcf9-8mh4-8wgh）: 機密クライアントの secret の fail-closed ----
printf '\nk8s-local-deploy.sh: 機密クライアントの secret が開発用の値なら止める（IADR-0098 追記）\n'

GUARDED_KEYS="service-auth-client-secret discord-owner-auth-client-secret kb-auth-client-secret kb-reader-auth-client-secret llm-auth-client-secret"
# 開発用の値は宣言から導く（試験にも値を複写しない）。
dev_of() {
  local spec
  for spec in "${AST_DEV_GUARDED_CLIENT_SECRETS[@]}"; do
    [ "${spec%%|*}" = "$1" ] && { ast_dev_client_secret_value "${spec%%|*}" "${spec#*|}"; return 0; }
  done
  return 1
}
NONDEV="not-a-dev-value-0123456789abcdef"

# $1 = absent | present（ast-secrets の実在）。値は given_data で入れる。
given_guard() {
  given_secret "${1:-absent}"
  [ "${1:-absent}" = "absent" ] && rm -f "$STATE/exists"
  unset ALLOW_DEV_CLIENT_SECRETS
  ALLOW_DEV_CLIENT_SECRETS_FLAG=0
  AST_ESO_MODE=0
}
# $1 = キー, $2 = 平文（base64 にして稼働中の Secret に置く）
given_data() { b64 "$2" > "$STATE/data-$1"; }
run_guard() {
  ( ast_dev_client_secret_guard ) > "$STATE/out" 2> "$STATE/err"
  RC=$?
  OUT="$(cat "$STATE/out")"
  ERR="$(cat "$STATE/err")"
}
# 開発用の値・試験の値のどれも出力に出ていないこと。
assert_no_values() {
  local k leaked=''
  for k in $GUARDED_KEYS; do
    case "$OUT$ERR" in *"$(dev_of "$k")"*) leaked="$leaked $k" ;; esac
  done
  case "$OUT$ERR" in *"$NONDEV"*) leaked="$leaked NONDEV" ;; esac
  assert_eq "$1" "$leaked" ''
}

# T-pcf9-01: 対象は 5 件ちょうど（svc・owner・kb-writer・kb-reader・llm-caller）。開発用の値はどれも空でなく互いに違う。
got_keys="$(for spec in "${AST_DEV_GUARDED_CLIENT_SECRETS[@]}"; do printf '%s ' "${spec%%|*}"; done)"
assert_eq 'T-pcf9-01 対象: 5 件' "$got_keys" "$GUARDED_KEYS "
uniq_n="$(for k in $GUARDED_KEYS; do dev_of "$k"; echo; done | sort -u | grep -c .)"
assert_eq 'T-pcf9-01 開発用の値: 5 件とも非空で互いに違う' "$uniq_n" "5"

# T-pcf9-02: 従来経路の新規環境・env なし → 既定（svc / owner は開発用の値）で止まる
given_guard absent
run_guard
assert_eq       'T-pcf9-02 新規・env なし: 非 0' "$RC" "1"
assert_contains 'T-pcf9-02 新規・env なし: svc を名指す' "$ERR" 'service-auth-client-secret ($SERVICEAUTH_CLIENTSECRET)'
assert_contains 'T-pcf9-02 新規・env なし: owner を名指す' "$ERR" 'discord-owner-auth-client-secret ($DISCORD_OWNERAUTH_CLIENTSECRET)'
assert_missing  'T-pcf9-02 新規・env なし: 空既定の kb は名指さない（空は開発用の値ではない）' "$ERR" 'kb-auth-client-secret'
assert_no_values 'T-pcf9-02 値を出さない'

# T-pcf9-03: 従来経路・svc / owner に開発用でない値 → 進む
given_guard absent
export SERVICEAUTH_CLIENTSECRET="$NONDEV" DISCORD_OWNERAUTH_CLIENTSECRET="$NONDEV"
run_guard
assert_eq 'T-pcf9-03 開発用でない値: 0' "$RC" "0"
assert_no_values 'T-pcf9-03 値を出さない'

# T-pcf9-04: 従来経路・env で kb-reader に開発用の値（宣言から導いた値）→ 止まる
given_guard absent
export SERVICEAUTH_CLIENTSECRET="$NONDEV" DISCORD_OWNERAUTH_CLIENTSECRET="$NONDEV"
export KB_READER_AUTH_CLIENTSECRET="$(dev_of kb-reader-auth-client-secret)"
run_guard
assert_eq       'T-pcf9-04 env の開発用の値: 非 0' "$RC" "1"
assert_contains 'T-pcf9-04 env の開発用の値: kb-reader を名指す' "$ERR" 'kb-reader-auth-client-secret'
assert_no_values 'T-pcf9-04 値を出さない'

# T-pcf9-05: 従来経路・env なしで既存の Secret に開発用の値（llm-caller）→ 止まる。既存値が開発用でなければ進む
given_guard present
given_data service-auth-client-secret "$NONDEV"
given_data discord-owner-auth-client-secret "$NONDEV"
given_data llm-auth-client-secret "$(dev_of llm-auth-client-secret)"
run_guard
assert_eq       'T-pcf9-05 既存の開発用の値: 非 0' "$RC" "1"
assert_contains 'T-pcf9-05 既存の開発用の値: llm-caller を名指す' "$ERR" 'llm-auth-client-secret'
given_data llm-auth-client-secret "$NONDEV"
run_guard
assert_eq 'T-pcf9-05 既存が開発用でない: 0' "$RC" "0"

# T-pcf9-05b: 従来経路・Secret は在るがキーが無い（env なし）→ sync_ast_secrets は既定値（svc は開発用の値）を書くので止まる
given_guard present
given_data discord-owner-auth-client-secret "$NONDEV"
run_guard
assert_eq       'T-pcf9-05b キーなし・env なし: 非 0' "$RC" "1"
assert_contains 'T-pcf9-05b キーなし・env なし: svc を名指す' "$ERR" 'service-auth-client-secret'
assert_missing  'T-pcf9-05b キーなし・env なし: 値のある owner は名指さない' "$ERR" 'discord-owner-auth-client-secret'
# 起動器が出す問い合わせは「キーが無ければ空」の形である（`index` 単独は `<no value>` を出し、すり抜ける）。
assert_contains 'T-pcf9-05b 問い合わせ: with で包む' "$(declare -f ast_secret_data_b64)" '{{with index .data'

# T-pcf9-06: ESO 所有・Secret が無い → 確かめられないので止まる（env は見ない）
given_guard absent
AST_ESO_MODE=1
export SERVICEAUTH_CLIENTSECRET="$NONDEV"
run_guard
assert_eq       'T-pcf9-06 ESO・Secret なし: 非 0' "$RC" "1"
assert_contains 'T-pcf9-06 ESO・Secret なし: 確かめられないと言う' "$ERR" '確かめられない'

# T-pcf9-07: ESO 所有・稼働中の値が開発用（kb-writer）→ 止まる。env に開発用でない値があっても使わない
given_guard present
AST_ESO_MODE=1
for k in $GUARDED_KEYS; do given_data "$k" "$NONDEV"; done
given_data kb-auth-client-secret "$(dev_of kb-auth-client-secret)"
export KB_AUTH_CLIENTSECRET="$NONDEV"
run_guard
assert_eq       'T-pcf9-07 ESO・稼働中が開発用: 非 0' "$RC" "1"
assert_contains 'T-pcf9-07 ESO・稼働中が開発用: kb-writer を名指す' "$ERR" 'kb-auth-client-secret'
assert_no_values 'T-pcf9-07 値を出さない'
# 陰性対照: 全件が開発用でなければ進む。ESO では env の開発用の値も見ない（ESO は env を使わない）
given_data kb-auth-client-secret "$NONDEV"
export SERVICEAUTH_CLIENTSECRET="$(dev_of service-auth-client-secret)"
run_guard
assert_eq 'T-pcf9-07 ESO・全件が開発用でない: 0（env は見ない）' "$RC" "0"

# T-pcf9-08: 明示の opt-in だけが通す（フラグ・ALLOW_DEV_CLIENT_SECRETS=1）。`true` 等は opt-in ではない
given_guard absent
ALLOW_DEV_CLIENT_SECRETS_FLAG=1
run_guard
assert_eq       'T-pcf9-08 フラグ: 0' "$RC" "0"
assert_contains 'T-pcf9-08 フラグ: 大きく警告する' "$ERR" '!!! WARNING'
assert_no_values 'T-pcf9-08 値を出さない'
given_guard absent
export ALLOW_DEV_CLIENT_SECRETS=1
run_guard
assert_eq 'T-pcf9-08 env=1: 0' "$RC" "0"
given_guard absent
export ALLOW_DEV_CLIENT_SECRETS=true
run_guard
assert_eq 'T-pcf9-08 env=true: 非 0（1 だけが opt-in）' "$RC" "1"
unset ALLOW_DEV_CLIENT_SECRETS SERVICEAUTH_CLIENTSECRET DISCORD_OWNERAUTH_CLIENTSECRET KB_AUTH_CLIENTSECRET KB_READER_AUTH_CLIENTSECRET

# T-pcf9-09: 起動器の本体は、イメージを作る前に確かめて、止まったら進まない（順序の固定）
pre_build="$(sed -n '/^echo "==> \[0\/5\]/,/k8s-local-images.sh/p' "$ROOT_DIR/scripts/k8s-local-deploy.sh")"
assert_contains 'T-pcf9-09 順序: イメージを作る前に確かめる' "$pre_build" 'ast_dev_client_secret_guard || exit 1'
assert_contains 'T-pcf9-09 順序: 供給経路を先に決める' "$pre_build" 'resolve_ast_eso_mode'
# 未知のオプションにならない（引数の解釈）
assert_contains 'T-pcf9-09 引数: --allow-dev-client-secrets を受け付ける' \
  "$(sed -n '/^while \[ \$# -gt 0 \]/,/^done/p' "$ROOT_DIR/scripts/k8s-local-deploy.sh")" '--allow-dev-client-secrets) ALLOW_DEV_CLIENT_SECRETS_FLAG=1'

printf '\nk8s-local-deploy.sh: メッシュ参入の受け口と引き継ぎ（IADR-0570）\n'

# 解決と名前空間のラベルを続けて実行し、RC / OUT / ERR / MESH_OVERRIDES / MESH_CHANGED / NS_LABEL_LOG を埋める。
run_mesh() {
  rm -f "$STATE/ns-label.log"
  (
    resolve_ast_mesh_injection
    rc=$?
    printf '%s\n' "${AST_MESH_OVERRIDES[@]:-}" > "$STATE/mesh-overrides.txt"
    [ "$rc" -ne 0 ] && exit "$rc"
    ast_apply_mesh_namespace_label
    rc=$?
    printf '%s' "${AST_MESH_INJECTION_CHANGED:-}" > "$STATE/mesh-changed"
    exit "$rc"
  ) > "$STATE/out" 2> "$STATE/err"
  RC=$?
  OUT="$(cat "$STATE/out")"
  ERR="$(cat "$STATE/err")"
  MESH_OVERRIDES="$(cat "$STATE/mesh-overrides.txt" 2>/dev/null || true)"
  MESH_CHANGED="$(cat "$STATE/mesh-changed" 2>/dev/null || true)"
  NS_LABEL_LOG="$(cat "$STATE/ns-label.log" 2>/dev/null || true)"
}
given_ns_label() { rm -f "$STATE/ns-label"; [ -n "${1:-}" ] && printf '%s' "$1" > "$STATE/ns-label"; return 0; }
MESH_ON='mesh:
  sidecarInjection:
    enabled: true
    excludeOpend: true'
MESH_OFF='mesh:
  sidecarInjection:
    enabled: false'

# T-mesh-01: 新規・未指定 → helm へ何も足さず、名前空間に触らない（既定の不変）
given_secret ""
given_release_values absent
given_ns_label ""
run_mesh
assert_eq 'T-mesh-01 新規: 正常終了する' "$RC" "0"
assert_eq 'T-mesh-01 新規: helm へ何も足さない' "$MESH_OVERRIDES" ''
assert_eq 'T-mesh-01 新規: ラベルに触らない' "$NS_LABEL_LOG" ''
assert_eq 'T-mesh-01 新規: 状態は変わらない' "$MESH_CHANGED" '0'
# 前回リリースはあるが mesh を宣言していない（本 PR より前の配備）→ 同じく何も足さない
given_release_values 'broker:
  tier: moomoo-sim'
run_mesh
assert_eq 'T-mesh-01 未宣言の前回: helm へ何も足さない' "$MESH_OVERRIDES" ''
assert_eq 'T-mesh-01 未宣言の前回: ラベルに触らない' "$NS_LABEL_LOG" ''

# T-mesh-02: 明示の 1 → enabled=true と excludeOpend=true を渡し、ラベルを貼り、状態が変わる
for v in 1 true; do
  given_secret ""
  given_release_values absent
  given_ns_label ""
  export AST_MESH_INJECTION="$v"
  run_mesh
  assert_eq       "T-mesh-02 明示 $v: 正常終了する" "$RC" "0"
  assert_contains "T-mesh-02 明示 $v: enabled=true を渡す" "$MESH_OVERRIDES" 'mesh.sidecarInjection.enabled=true'
  assert_contains "T-mesh-02 明示 $v: OpenD を注入対象外に固定する" "$MESH_OVERRIDES" 'mesh.sidecarInjection.excludeOpend=true'
  assert_eq       "T-mesh-02 明示 $v: ラベルを貼る" "$NS_LABEL_LOG" 'istio-injection=enabled'
  assert_eq       "T-mesh-02 明示 $v: 状態が変わる" "$MESH_CHANGED" '1'
done

# T-mesh-03: 🔴 未指定の再実行は現行の true を引き継ぐ（手で与えた値・注釈が次の配備で外れない）
given_secret ""
given_release_values "$MESH_ON"
given_ns_label enabled
run_mesh
assert_eq       'T-mesh-03 引き継ぎ: 正常終了する' "$RC" "0"
assert_contains 'T-mesh-03 引き継ぎ: enabled=true を渡す' "$MESH_OVERRIDES" 'mesh.sidecarInjection.enabled=true'
assert_contains 'T-mesh-03 引き継ぎ: OpenD を注入対象外に固定する' "$MESH_OVERRIDES" 'mesh.sidecarInjection.excludeOpend=true'
assert_contains 'T-mesh-03 引き継ぎ: 引き継いだことを告げる' "$ERR" '引き継ぎ: mesh.sidecarInjection.enabled=true'
assert_eq       'T-mesh-03 引き継ぎ: 貼ってあるラベルには触らない' "$NS_LABEL_LOG" ''
assert_eq       'T-mesh-03 引き継ぎ: 状態は変わらない（全件 restart に広げない）' "$MESH_CHANGED" '0'

# T-mesh-04: 引き継いだ true だがラベルが無い（名前空間を作り直した等）→ 貼り、状態が変わる
given_ns_label ""
run_mesh
assert_eq 'T-mesh-04 ラベル欠落: 貼る' "$NS_LABEL_LOG" 'istio-injection=enabled'
assert_eq 'T-mesh-04 ラベル欠落: 状態が変わる' "$MESH_CHANGED" '1'

# T-mesh-05: 明示の 0 → enabled=false を渡し（excludeOpend は足さない）、ラベルを剥がし、状態が変わる
for v in 0 false; do
  given_secret ""
  given_release_values "$MESH_ON"
  given_ns_label enabled
  export AST_MESH_INJECTION="$v"
  run_mesh
  assert_eq       "T-mesh-05 明示 $v: 正常終了する" "$RC" "0"
  assert_eq       "T-mesh-05 明示 $v: enabled=false だけを渡す" "$MESH_OVERRIDES" '--set
mesh.sidecarInjection.enabled=false'
  assert_eq       "T-mesh-05 明示 $v: ラベルを剥がす" "$NS_LABEL_LOG" 'istio-injection-'
  assert_eq       "T-mesh-05 明示 $v: 状態が変わる" "$MESH_CHANGED" '1'
done
# 明示の 0 でラベルが元から無く、前回も無効 → 何も変わらない
given_secret ""
given_release_values "$MESH_OFF"
given_ns_label ""
export AST_MESH_INJECTION=0
run_mesh
assert_eq 'T-mesh-05 無効のまま: ラベルに触らない' "$NS_LABEL_LOG" ''
assert_eq 'T-mesh-05 無効のまま: 状態は変わらない' "$MESH_CHANGED" '0'

# T-mesh-06: 引き継いだ false で手のラベルが残る → 触らず告げる
given_secret ""
given_release_values "$MESH_OFF"
given_ns_label enabled
run_mesh
assert_eq       'T-mesh-06 手のラベル: 正常終了する' "$RC" "0"
assert_eq       'T-mesh-06 手のラベル: enabled=false を引き継ぐ' "$MESH_OVERRIDES" '--set
mesh.sidecarInjection.enabled=false'
assert_eq       'T-mesh-06 手のラベル: 剥がさない' "$NS_LABEL_LOG" ''
assert_contains 'T-mesh-06 手のラベル: 告げる' "$ERR" 'WARN: 名前空間 ai-stock-trading に istio-injection=enabled'
# 既定（新規）でも同じく触らない
given_release_values absent
run_mesh
assert_eq 'T-mesh-06 既定・手のラベル: 剥がさない' "$NS_LABEL_LOG" ''

# T-mesh-07: 不正な値（空を含む）は exit 2 で、何も渡さずラベルにも触らない
for v in '' yes 2 TRUE; do
  given_secret ""
  given_release_values "$MESH_ON"
  given_ns_label enabled
  export AST_MESH_INJECTION="$v"
  run_mesh
  assert_eq "T-mesh-07 不正 '$v': exit 2" "$RC" "2"
  assert_eq "T-mesh-07 不正 '$v': 何も渡さない" "$MESH_OVERRIDES" ''
  assert_eq "T-mesh-07 不正 '$v': ラベルに触らない" "$NS_LABEL_LOG" ''
done

# T-mesh-08: 前回値が真偽値として読めない → 止める（黙って既定へ戻さない）。明示すれば進む
given_secret ""
given_release_values 'mesh:
  sidecarInjection:
    enabled: maybe'
run_mesh
assert_eq       'T-mesh-08 読めない前回値: 非 0' "$RC" "1"
assert_contains 'T-mesh-08 読めない前回値: 明示を案内する' "$ERR" 'AST_MESH_INJECTION=1'
export AST_MESH_INJECTION=1
run_mesh
assert_eq 'T-mesh-08 読めない前回値・明示: 進む' "$RC" "0"
assert_contains 'T-mesh-08 読めない前回値・明示: enabled=true' "$MESH_OVERRIDES" 'mesh.sidecarInjection.enabled=true'
assert_eq 'T-mesh-08 読めない前回値・明示: 状態が変わったものとして扱う' "$MESH_CHANGED" '1'

# T-mesh-09: ラベルの読み書きに失敗したら止める
given_secret ""
given_release_values absent
export AST_MESH_INJECTION=1
: > "$STATE/get-ns-fail"
run_mesh
assert_eq 'T-mesh-09 読めない: 非 0' "$RC" "1"
rm -f "$STATE/get-ns-fail"
: > "$STATE/label-ns-fail"
run_mesh
assert_eq 'T-mesh-09 貼れない: 非 0' "$RC" "1"
rm -f "$STATE/label-ns-fail"

# T-mesh-10: 🔴 注入の状態が変わった回は、絞り込みがあっても OpenD を除く全件を restart する
given_secret ""
given_deployments "$ALL_DEPLOYMENTS"
given_built 'audit-service'
( AST_IMAGE_SELECTION=1; AST_SECRETS_PATCHED=0; AST_MESH_INJECTION_CHANGED=1; ast_restart_step "$STATE/built" ) > "$STATE/out" 2> "$STATE/err"
OUT="$(cat "$STATE/out")"
RESTARTED="$(cat "$STATE/restarted.log" 2>/dev/null || true)"
assert_contains 'T-mesh-10 状態の変更: 全件へ戻す' "$OUT" '11 件（OpenD は除外）'
assert_contains 'T-mesh-10 状態の変更: 理由を表示する' "$OUT" 'サイドカー注入の状態を変えたため'
assert_missing  'T-mesh-10 状態の変更: OpenD は作り直さない' "$RESTARTED" 'opend'
# 陰性対照: 変わっていなければ絞り込みのまま
given_deployments "$ALL_DEPLOYMENTS"
( AST_IMAGE_SELECTION=1; AST_SECRETS_PATCHED=0; AST_MESH_INJECTION_CHANGED=0; ast_restart_step "$STATE/built" ) > "$STATE/out" 2> "$STATE/err"
RESTARTED="$(cat "$STATE/restarted.log" 2>/dev/null || true)"
assert_eq 'T-mesh-10 変更なし: 作り直した分だけ' "$RESTARTED" 'audit-service'

# T-mesh-11: 起動器の本体は、解決とラベルを helm upgrade より前に行い、helm の値に載せる（順序の固定）
main_body="$(sed -n '/^echo "==> \[3\/5\]/,/^helm upgrade --install/p' "$ROOT_DIR/scripts/k8s-local-deploy.sh")"
assert_contains 'T-mesh-11 順序: 解決する' "$main_body" 'resolve_ast_mesh_injection || exit $?'
assert_contains 'T-mesh-11 順序: ラベルを扱う' "$main_body" 'ast_apply_mesh_namespace_label || exit 1'
assert_contains 'T-mesh-11 順序: helm の値に載せる' "$main_body" '"${AST_MESH_OVERRIDES[@]}"'
unset AST_MESH_INJECTION

printf '\n%d passed, %d failed\n' "$PASSED" "$FAILED"
[ "$FAILED" -eq 0 ] || exit 1
