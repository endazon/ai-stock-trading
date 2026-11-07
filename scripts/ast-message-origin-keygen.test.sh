#!/usr/bin/env bash
# NFR, FR-10, IADR-0633: scripts/ast-message-origin-keygen.sh（発行元の署名の鍵の組の生成）の挙動を固定する。基盤の口はスタブ（openssl は本物）。
#   - --live が無ければ 3、前置きが無ければ 6、知らない発行元は 2（どれも何も書かない）
#   - 7 つの発行元ごとに、秘密の鍵（PKCS#8・P-256）と公開の鍵（SPKI）が別のパスの同じキーへ入り、組が合う
#   - 鍵の値は基盤の口の標準入力にだけ渡り、引数・標準出力・標準エラーに出ない
#   - 既に在れば作り直さず 5（値は変わらない）。--rotate は --overwrite を渡して作り直す
#   - 公開の鍵を書けなければ 1 で止まり、--rotate での作り直しを案内する
set -u
ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SCRIPT="$ROOT_DIR/scripts/ast-message-origin-keygen.sh"
STATE="$(mktemp -d)"
BIN="$STATE/bin"; mkdir -p "$BIN" "$STATE/store"
trap 'rm -rf "$STATE"' EXIT
export AST_TEST_STATE="$STATE"

cat > "$BIN/put-external-secret" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$AST_TEST_STATE/put-args.log"
over=0; pos=()
for a in "$@"; do case "$a" in --overwrite) over=1 ;; --live|--operator-file|--identity|*.age|*.txt) ;; *) pos+=("$a") ;; esac; done
f="$AST_TEST_STATE/store/$(printf '%s' "${pos[0]}" | tr '/' '_')__${pos[1]}"
case "${pos[0]}" in *verify*) [ -f "$AST_TEST_STATE/fail-verify" ] && { cat >/dev/null; exit 1; } ;; esac
if [ -f "$f" ] && [ "$over" -eq 0 ]; then cat >/dev/null; exit 5; fi
cat > "$f"
STUB
chmod +x "$BIN/put-external-secret"
PUT="$BIN/put-external-secret --live --operator-file op.age --identity id.txt"

fails=0
ok() { echo "ok - $1"; }
ng() { echo "not ok - $1"; fails=$((fails + 1)); }
run() { AST_PUT_EXTERNAL_SECRET="$PUT" bash "$SCRIPT" "$@" >"$STATE/out" 2>"$STATE/err"; echo $?; }
store() { cat "$STATE/store/cloud_ast_ast-message-origin-$1-keys__$2" 2>/dev/null; }
ISSUERS="backtest information-collection market-monitor order-execution report risk-management trade-decision"

rc="$(AST_PUT_EXTERNAL_SECRET="$PUT" bash "$SCRIPT" >/dev/null 2>&1; echo $?)"
[ "$rc" = 3 ] && [ ! -s "$STATE/put-args.log" ] && ok "--live が無ければ 3 で何も書かない" || ng "--live なし: rc=$rc"
rc="$(bash "$SCRIPT" --live >/dev/null 2>&1; echo $?)"
[ "$rc" = 6 ] && ok "前置きが無ければ 6" || ng "前置きなし: rc=$rc"
rc="$(run --live bogus)"
[ "$rc" = 2 ] && [ ! -s "$STATE/put-args.log" ] && ok "知らない発行元は 2" || ng "知らない発行元: rc=$rc"

rc="$(run --live)"
[ "$rc" = 0 ] && ok "7 つの組を書いて 0" || ng "初回: rc=$rc $(cat "$STATE/err")"
all_ok=1
for i in $ISSUERS; do
  priv="$(store signing "$i")"; pub="$(store verify "$i")"
  [ -n "$priv" ] && [ -n "$pub" ] || { all_ok=0; continue; }
  derived="$(printf '%s' "$priv" | base64 -d | openssl pkey -inform DER -pubout -outform DER 2>/dev/null | base64 | tr -d '\n')"
  [ "$derived" = "$pub" ] || all_ok=0
  printf '%s' "$priv" | base64 -d | openssl pkey -inform DER -noout -text 2>/dev/null | grep -q 'NIST CURVE: P-256' || all_ok=0
  # PKCS#8 であること（SEC1 の EC PRIVATE KEY では .NET の ImportPkcs8PrivateKey が読めない）。
  printf '%s' "$priv" | base64 -d | openssl pkcs8 -inform DER -nocrypt -out /dev/null 2>/dev/null || all_ok=0
  printf '%s' "$priv" | base64 -d | openssl asn1parse -inform DER 2>/dev/null | grep -q 'id-ecPublicKey' || all_ok=0
  printf '%s' "$pub" | base64 -d | openssl pkey -pubin -inform DER -noout 2>/dev/null || all_ok=0
  grep -qF "$priv" "$STATE/put-args.log" "$STATE/out" "$STATE/err" && all_ok=0
  grep -qF "$pub" "$STATE/put-args.log" "$STATE/out" "$STATE/err" && all_ok=0
done
[ "$all_ok" = 1 ] && ok "秘密の鍵（P-256）と公開の鍵が組で入り、値は引数・出力に出ない" || ng "鍵の組・値の露出"
[ "$(grep -c 'ast-message-origin-signing-keys' "$STATE/put-args.log")" = 7 ] && [ "$(grep -c 'ast-message-origin-verify-keys' "$STATE/put-args.log")" = 7 ] \
  && ok "秘密の鍵と公開の鍵を別のパスへ 7 つずつ" || ng "パスの数"
a="$(store signing report)"; b="$(store signing trade-decision)"
[ "$a" != "$b" ] && ok "発行元ごとに別の鍵" || ng "鍵が同じ"

before="$(store signing market-monitor)"
rc="$(run --live market-monitor)"
[ "$rc" = 5 ] && [ "$(store signing market-monitor)" = "$before" ] && ok "既に在れば作り直さず 5" || ng "既在: rc=$rc"

oldpub="$(store verify market-monitor)"
rc="$(run --live --rotate market-monitor)"
[ "$rc" = 0 ] && [ "$(store signing market-monitor)" != "$before" ] && [ "$(store verify market-monitor)" != "$oldpub" ] \
  && grep -q -- '--overwrite' "$STATE/put-args.log" && ok "--rotate は --overwrite で組ごと作り直す" || ng "--rotate: rc=$rc"

touch "$STATE/fail-verify"
rc="$(run --live --rotate backtest)"
[ "$rc" = 1 ] && grep -q -- '--rotate backtest' "$STATE/err" && ok "公開の鍵を書けなければ 1 で作り直しを案内する" || ng "公開の鍵の失敗: rc=$rc"

[ "$fails" -eq 0 ] || { echo "FAILED: $fails"; exit 1; }
echo "all ok"
