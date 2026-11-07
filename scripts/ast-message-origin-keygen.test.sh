#!/usr/bin/env bash
# NFR, FR-10, IADR-0633: scripts/ast-message-origin-keygen.sh（発行元の署名の鍵の組の生成）の挙動を固定する。基盤の口はスタブ（openssl は本物）。
#   - --live が無ければ 3、前置きが無ければ 6、知らない発行元は 2（どれも何も書かない）
#   - 7 つの発行元ごとに、秘密の鍵（PKCS#8・P-256）と公開の鍵（SPKI）が別のパスの同じキーへ入り、組が合う
#   - 鍵の値は基盤の口の標準入力にだけ渡り、引数・標準出力・標準エラーに出ない
#   - 既に在れば作り直さず 5（値は変わらない）。--rotate は --overwrite を渡して作り直す
#   - 公開の鍵を書けなければ 1 で止まり、--rotate での作り直しを案内する
#   - --rotate は、いまの公開の鍵（kubectl はスタブ）を前の鍵のパスへ残してから作り直す。読めなければ何も変えずに 1。
#     --no-previous は前の鍵を残さない（--rotate なしでは 2）
#   - 窓の中の同じ発行元の再 --rotate は、何も変えずに 8（置いた時刻は ConfigMap のスタブ）。--force-within-window は警告して続ける。窓の外は通る
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
case "${pos[0]}" in */ast-message-origin-verify-keys) [ -f "$AST_TEST_STATE/fail-verify" ] && { cat >/dev/null; exit 1; } ;; esac
if [ -f "$f" ] && [ "$over" -eq 0 ]; then cat >/dev/null; exit 5; fi
cat > "$f"
STUB
chmod +x "$BIN/put-external-secret"
# kubectl のスタブ: Secret ast-message-origin-verify-keys の <発行元> を、ストアの公開の鍵から k8s の data の形（base64）で返す。
# ConfigMap ast-message-origin-rotation は $AST_TEST_STATE/cm/<発行元> のファイル（置いた時刻）で模す。
cat > "$BIN/kubectl" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$AST_TEST_STATE/kubectl.log"
mkdir -p "$AST_TEST_STATE/cm"
case " $* " in
  *" get configmap "*" -o name "*) [ -n "$(ls "$AST_TEST_STATE/cm")" ]; exit $? ;;
  *" get configmap "*) i="$(printf '%s' "$*" | sed -n 's/.*index \.data "\([a-z-]*\)".*/\1/p')"; [ -f "$AST_TEST_STATE/cm/$i" ] && cat "$AST_TEST_STATE/cm/$i"; exit 0 ;;
  *" patch configmap "*) i="$(printf '%s' "$*" | sed -n 's/.*"data":{"\([a-z-]*\)":"\([0-9]*\)".*/\1/p')"; t="$(printf '%s' "$*" | sed -n 's/.*"data":{"\([a-z-]*\)":"\([0-9]*\)".*/\2/p')"; printf '%s' "$t" > "$AST_TEST_STATE/cm/$i"; exit 0 ;;
  *" create configmap "*) kv="$(printf '%s' "$*" | sed -n 's/.*--from-literal=\([a-z-]*\)=\([0-9]*\).*/\1 \2/p')"; printf '%s' "${kv#* }" > "$AST_TEST_STATE/cm/${kv%% *}"; exit 0 ;;
esac
[ -f "$AST_TEST_STATE/kubectl-fail" ] && exit 1
issuer="$(printf '%s' "$*" | sed -n 's/.*index \.data "\([a-z-]*\)".*/\1/p')"
f="$AST_TEST_STATE/store/cloud_ast_ast-message-origin-verify-keys__$issuer"
[ -f "$f" ] || exit 1
base64 < "$f" | tr -d '\n'
STUB
chmod +x "$BIN/kubectl"
export KUBECTL="$BIN/kubectl"
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
[ "$(store previous-verify market-monitor)" = "$oldpub" ] && ok "--rotate は作り直す前の公開の鍵を前の鍵として残す" || ng "前の鍵が残っていない"
grep -qF "$oldpub" "$STATE/put-args.log" "$STATE/out" "$STATE/err" "$STATE/kubectl.log" && ng "前の鍵の値が出た" || ok "前の鍵の値は引数・出力に出ない"

touch "$STATE/kubectl-fail"; before="$(store signing report)"
rc="$(run --live --rotate report)"
[ "$rc" = 1 ] && [ "$(store signing report)" = "$before" ] && grep -q -- '--no-previous' "$STATE/err" \
  && ok "前の公開の鍵を読めなければ何も変えずに 1" || ng "前の鍵が読めない: rc=$rc"
rc="$(run --live --rotate --no-previous report)"
[ "$rc" = 0 ] && [ "$(store signing report)" != "$before" ] && [ -z "$(store previous-verify report)" ] \
  && ok "--no-previous は前の鍵を残さずに作り直す" || ng "--no-previous: rc=$rc"
rm -f "$STATE/kubectl-fail"
rc="$(run --live --no-previous report)"
[ "$rc" = 2 ] && ok "--no-previous は --rotate なしでは 2" || ng "--no-previous 単独: rc=$rc"

touch "$STATE/fail-verify"
rc="$(run --live --rotate backtest)"
[ "$rc" = 1 ] && grep -q -- '--rotate backtest' "$STATE/err" && ok "公開の鍵を書けなければ 1 で作り直しを案内する" || ng "公開の鍵の失敗: rc=$rc"

rm -f "$STATE/fail-verify"
# 書き込みが失敗した --rotate は置いた時刻を記録しない（再実行が窓に止められない）。
[ ! -e "$STATE/cm/backtest" ] && ok "書き込みが失敗した --rotate は置いた時刻を記録しない" || ng "失敗した --rotate が時刻を記録した"
rc="$(run --live --rotate backtest)"
[ "$rc" = 0 ] && [ -s "$STATE/cm/backtest" ] && ok "書き込みが失敗した後の再実行は窓に止められず、成功したら記録する" || ng "失敗の後の再実行: rc=$rc"
# 窓の中の再 --rotate（market-monitor は上で --rotate 済み＝置いた時刻が記録されている）。
[ -s "$STATE/cm/market-monitor" ] && ok "--rotate は前の鍵を置いた時刻を記録する" || ng "置いた時刻が記録されていない"
prev="$(store previous-verify market-monitor)"; sig="$(store signing market-monitor)"
rc="$(run --live --rotate market-monitor)"
[ "$rc" = 8 ] && [ "$(store previous-verify market-monitor)" = "$prev" ] && [ "$(store signing market-monitor)" = "$sig" ] \
  && grep -q -- '--force-within-window' "$STATE/err" && ok "窓の中の再 --rotate は何も変えずに 8" || ng "窓の中の再 --rotate: rc=$rc"
rc="$(run --live --rotate --force-within-window market-monitor)"
[ "$rc" = 0 ] && [ "$(store signing market-monitor)" != "$sig" ] && grep -q 'WARNING: --force-within-window' "$STATE/err" \
  && ok "--force-within-window は警告して続ける" || ng "--force-within-window: rc=$rc"
printf '%s' "$(( $(date +%s) - 1446 * 60 ))" > "$STATE/cm/market-monitor"
rc="$(run --live --rotate market-monitor)"
[ "$rc" = 0 ] && ok "窓の外の --rotate は通る" || ng "窓の外: rc=$rc"
rc="$(run --live --no-previous --force-within-window market-monitor)"
[ "$rc" = 2 ] && ok "--force-within-window は --rotate なし・--no-previous と併用できない" || ng "旗の組み合わせ: rc=$rc"

[ "$fails" -eq 0 ] || { echo "FAILED: $fails"; exit 1; }
echo "all ok"
