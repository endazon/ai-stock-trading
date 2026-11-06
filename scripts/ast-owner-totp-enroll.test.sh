#!/usr/bin/env bash
# NFR-06, #1542, IADR-0615: scripts/ast-owner-totp-enroll.sh（TOTP のシークレットの登録）の挙動を固定する。kubectl と qrencode はスタブ。
#   - --live が無ければ kubectl を呼ばずに 3。qrencode が無ければ何も作らずに 4。既に登録済みで --rotate が無ければ 5
#   - シークレットは Secret ast-owner-totp の key secret に入り、32 文字以上の base32 である
#   - シークレットはどの kubectl の引数にも、標準出力にも出ない（QR の入力＝qrencode の stdin にだけ渡る）
#   - 作ったあと risk-management と report の Pod を作り直す。status は変える動詞を使わない
#   - --openbao（NFR-05, #1560, IADR-0623 決定 5）: シークレットは put-external-secret.sh（スタブ）の標準入力にだけ渡り、
#     引数・kubectl の引数・標準出力に出ない。k8s の Secret は作らない。ESO の同期を待ってから Pod を作り直す。前置きが無ければ 6
set -u
ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SCRIPT="$ROOT_DIR/scripts/ast-owner-totp-enroll.sh"
STATE="$(mktemp -d)"
BIN="$STATE/bin"; mkdir -p "$BIN"
trap 'rm -rf "$STATE"' EXIT

cat > "$BIN/kubectl" <<'STUB'
#!/usr/bin/env bash
S="$AST_TEST_STATE"
printf '%s\n' "$*" >> "$S/calls.log"
case " $* " in
  *" create secret generic "*) for a in "$@"; do case "$a" in --from-file=secret=*) cat "${a#--from-file=secret=}" > "$S/secret.txt" ;; esac; done; echo 'kind: Secret'; exit 0 ;;
  *" apply -f - "*) cat >/dev/null; exit 0 ;;
  *" get secret "*) [ -f "$S/exists" ]; exit $? ;;
  *" get externalsecret "*) n="$(cat "$S/es-gets" 2>/dev/null || echo 0)"; n=$((n + 1)); echo "$n" > "$S/es-gets"
    if [ -f "$S/es-stuck" ] || [ "$n" -le 1 ]; then printf 'v1'; else printf 'v2'; fi; exit 0 ;;
  *" logs "*) echo 'owner-second-factor: operation=TradingResume outcome=Verified mode=Audit allowed=True'; exit 0 ;;
esac
exit 0
STUB
cat > "$BIN/qrencode" <<'STUB'
#!/usr/bin/env bash
cat > "$AST_TEST_STATE/qr-input.txt"; echo "[QR]"
STUB
cat > "$BIN/put-external-secret" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$AST_TEST_STATE/put-args.log"
cat > "$AST_TEST_STATE/put-stdin.txt"
# 基盤の口と同じく、キーが在れば --overwrite なしは 5（何も変えない）。
if [ -f "$AST_TEST_STATE/put-exists" ]; then case " $* " in *" --overwrite "*) ;; *) exit 5 ;; esac; fi
STUB
chmod +x "$BIN/kubectl" "$BIN/qrencode" "$BIN/put-external-secret"
# qrencode を含まない PATH（基本コマンドだけ）。
BARE="$STATE/bare"; mkdir -p "$BARE"
for c in bash sed head base32 tr wc mktemp rm cat printf grep tail; do p="$(command -v "$c")" && ln -sf "$p" "$BARE/$c"; done

pass=0; fail=0
check() { if [ "$2" -eq 0 ]; then pass=$((pass + 1)); else echo "FAIL: $1"; fail=$((fail + 1)); fi; }
reset() { rm -f "$STATE"/calls.log "$STATE"/secret.txt "$STATE"/qr-input.txt "$STATE"/exists "$STATE"/put-args.log "$STATE"/put-stdin.txt "$STATE"/es-gets "$STATE"/es-stuck "$STATE"/put-exists; : > "$STATE/calls.log"; }
run() { AST_TEST_STATE="$STATE" KUBECTL="$BIN/kubectl" PATH="${RUNPATH:-$BIN:$PATH}" bash "$SCRIPT" "$@" < /dev/null > "$STATE/out.log" 2>&1; echo $?; }
ok() { [ "$1" ] && echo 0 || echo 1; }

reset; rc="$(run)"
check "--live なしは 3" "$([ "$rc" = 3 ] && echo 0 || echo 1)"
check "--live なしは kubectl を呼ばない" "$([ ! -s "$STATE/calls.log" ] && echo 0 || echo 1)"

reset; rc="$(RUNPATH="$BARE" run --live)"
check "qrencode が無ければ 4" "$([ "$rc" = 4 ] && echo 0 || echo 1)"
check "qrencode が無ければ Secret を作らない" "$(grep -q 'create secret' "$STATE/calls.log" && echo 1 || echo 0)"

reset; touch "$STATE/exists"; rc="$(run --live)"
check "登録済みで --rotate なしは 5" "$([ "$rc" = 5 ] && echo 0 || echo 1)"
check "登録済みなら作り直さない" "$(grep -q 'create secret' "$STATE/calls.log" && echo 1 || echo 0)"

reset; rc="$(run --live)"
check "登録は 0" "$([ "$rc" = 0 ] && echo 0 || echo 1)"
secret="$(cat "$STATE/secret.txt" 2>/dev/null)"
check "シークレットは 32 文字以上の base32" "$(printf '%s' "$secret" | grep -Eq '^[A-Z2-7]{32,}$' && echo 0 || echo 1)"
check "Secret ast-owner-totp の key secret へ入れる" "$(grep -q -- 'create secret generic ast-owner-totp --from-file=secret=' "$STATE/calls.log" && echo 0 || echo 1)"
check "シークレットは kubectl の引数に出ない" "$(grep -qF "$secret" "$STATE/calls.log" && echo 1 || echo 0)"
check "シークレットは標準出力に出ない" "$(grep -qF "$secret" "$STATE/out.log" && echo 1 || echo 0)"
check "QR の入力は otpauth の URI" "$(grep -qF "otpauth://totp/ai-stock-trading:owner?secret=${secret}&issuer=ai-stock-trading&algorithm=SHA1&digits=6&period=30" "$STATE/qr-input.txt" && echo 0 || echo 1)"
check "risk-management と report の Pod を作り直す" \
	"$(grep -q 'delete pod -l app=risk-management-service' "$STATE/calls.log" && grep -q 'delete pod -l app=report-service' "$STATE/calls.log" && echo 0 || echo 1)"

reset; touch "$STATE/exists"; rc="$(run --live --rotate)"
check "--rotate なら作り直す" "$([ "$rc" = 0 ] && grep -q 'create secret' "$STATE/calls.log" && echo 0 || echo 1)"

# ---- --openbao（クラウド） ----
PUT="$BIN/put-external-secret --live --operator-file op.age --identity id.txt"
reset; rc="$(run --live --openbao)"
check "--openbao で前置きが無ければ 6" "$([ "$rc" = 6 ] && echo 0 || echo 1)"
check "--openbao で前置きが無ければ何も作らない" "$([ ! -e "$STATE/put-args.log" ] && ! grep -q 'create secret' "$STATE/calls.log" && echo 0 || echo 1)"

reset; rc="$(AST_PUT_EXTERNAL_SECRET="$PUT" AST_TOTP_SYNC_INTERVAL=0 run --live --openbao)"
check "--openbao の登録は 0" "$([ "$rc" = 0 ] && echo 0 || echo 1)"
osecret="$(cat "$STATE/put-stdin.txt" 2>/dev/null)"
check "--openbao: シークレットは put の標準入力に 32 文字以上の base32 で渡る" "$(printf '%s' "$osecret" | grep -Eq '^[A-Z2-7]{32,}$' && echo 0 || echo 1)"
check "--openbao: put は cloud/ast/ast-owner-totp の secret へ書く" "$(grep -qx -- '--live --operator-file op.age --identity id.txt cloud/ast/ast-owner-totp secret' "$STATE/put-args.log" && echo 0 || echo 1)"
check "--openbao: シークレットは put の引数・kubectl の引数・標準出力に出ない" \
	"$( { grep -qF "$osecret" "$STATE/put-args.log" || grep -qF "$osecret" "$STATE/calls.log" || grep -qF "$osecret" "$STATE/out.log"; } && echo 1 || echo 0)"
check "--openbao: k8s の Secret を直に作らない" "$(grep -q 'create secret' "$STATE/calls.log" && echo 1 || echo 0)"
check "--openbao: ESO に同期を求める" "$(grep -q 'annotate externalsecret ast-owner-totp force-sync=' "$STATE/calls.log" && echo 0 || echo 1)"
check "--openbao: 同期の後に Pod を作り直す" \
	"$(awk '/annotate externalsecret/{a=NR} /delete pod -l app=risk-management-service/{d=NR} END{exit !(a && d && a < d)}' "$STATE/calls.log" && echo 0 || echo 1)"
check "--openbao: QR の入力は put へ渡したのと同じシークレット" "$(grep -qF "secret=${osecret}&" "$STATE/qr-input.txt" && echo 0 || echo 1)"

reset; touch "$STATE/exists"; rc="$(AST_PUT_EXTERNAL_SECRET="$PUT" run --live --openbao)"
check "--openbao: 登録済みで --rotate なしは 5（書かない）" "$([ "$rc" = 5 ] && [ ! -e "$STATE/put-args.log" ] && echo 0 || echo 1)"

reset; touch "$STATE/exists"; rc="$(AST_PUT_EXTERNAL_SECRET="$PUT" AST_TOTP_SYNC_INTERVAL=0 run --live --openbao --rotate)"
check "--openbao --rotate: 基盤の口へ --overwrite を前置きの後ろ・パスの前に渡す" \
	"$([ "$rc" = 0 ] && grep -qx -- '--live --operator-file op.age --identity id.txt --overwrite cloud/ast/ast-owner-totp secret' "$STATE/put-args.log" && echo 0 || echo 1)"
reset; rc="$(AST_PUT_EXTERNAL_SECRET="$PUT" AST_TOTP_SYNC_INTERVAL=0 run --live --openbao)"
check "--openbao: 通常の登録は --overwrite を渡さない" "$([ "$rc" = 0 ] && ! grep -q -- '--overwrite' "$STATE/put-args.log" && echo 0 || echo 1)"
reset; touch "$STATE/put-exists"; rc="$(AST_PUT_EXTERNAL_SECRET="$PUT" AST_TOTP_SYNC_INTERVAL=0 run --live --openbao)"
check "--openbao: OpenBao にキーが在れば（基盤の口が 5）、5 で終わり QR を出さず Pod を作り直さない" \
	"$([ "$rc" = 5 ] && [ ! -e "$STATE/qr-input.txt" ] && ! grep -q 'delete pod' "$STATE/calls.log" && grep -qF -- '--rotate' "$STATE/out.log" && echo 0 || echo 1)"

reset; touch "$STATE/es-stuck"; rc="$(AST_PUT_EXTERNAL_SECRET="$PUT" AST_TOTP_SYNC_WAIT=2 AST_TOTP_SYNC_INTERVAL=0 run --live --openbao)"
check "--openbao: 同期を確かめられなければ 1 で、Pod を作り直さない" "$([ "$rc" = 1 ] && ! grep -q 'delete pod' "$STATE/calls.log" && echo 0 || echo 1)"
check "--openbao: 同期を確かめられないときは、値が同じなら版が変わらない旨を出す" "$(grep -qF '値が前と同じなら同期の版は変わらない' "$STATE/out.log" && echo 0 || echo 1)"

reset; rc="$(run --help)"
check "--help は 2 で、使い方の 3 行（--live・--openbao・status）を切らずに出す" \
	"$([ "$rc" = 2 ] && grep -qF 'ast-owner-totp-enroll.sh --live [--rotate]' "$STATE/out.log" && grep -qF -- '--live --openbao [--rotate]' "$STATE/out.log" && grep -qF 'ast-owner-totp-enroll.sh status' "$STATE/out.log" && echo 0 || echo 1)"

reset; rc="$(run status)"
check "status は 0" "$([ "$rc" = 0 ] && echo 0 || echo 1)"
check "status は変える動詞を使わない" "$(grep -Eq '(^| )(create|apply|delete|label|scale|patch)( |$)' "$STATE/calls.log" && echo 1 || echo 0)"

# chart は risk-management と report で同じ Secret を optional で読む（values.yaml・values-local.yaml）。
# values-local・values-cloud は 2 サービスの extraEnv を丸ごと置き換えるので、無いとその profile では門が Off のままになる。
for v in values.yaml values-local.yaml values-cloud.yaml; do
	check "$v: 2 サービスが ast-owner-totp を optional で読む" \
		"$([ "$(grep -c 'secretKeyRef: { name: ast-owner-totp, key: secret, optional: true }' "$ROOT_DIR/deploy/helm/ai-stock-trading/$v")" = 2 ] && echo 0 || echo 1)"
done
# values-cloud の描画（helm があるときだけ）でも、risk-management と report が段と Secret の参照を持つ。
if command -v helm >/dev/null 2>&1; then
	chart="$ROOT_DIR/deploy/helm/ai-stock-trading"
	rendered="$(helm template ast "$chart" -f "$chart/values-cloud.yaml" -f "$chart/ci/values-cloud-digests.fixture.yaml" 2>/dev/null)"
	check "values-cloud の描画: ast-owner-totp の参照が 2 本" "$([ "$(printf '%s\n' "$rendered" | grep -A1 'secretKeyRef:' | grep -c 'name: ast-owner-totp')" = 2 ] && echo 0 || echo 1)"
	# NFR-05, #1560, IADR-0623: クラウドでは ESO が OpenBao の cloud/ast/ast-owner-totp から写す。
	check "values-cloud の描画: ExternalSecret ast-owner-totp が cloud/ast/ast-owner-totp の secret を写す" \
		"$(printf '%s\n' "$rendered" | grep -A2 'remoteRef:' | grep -A1 'key: "ast/ast-owner-totp"' | grep -q 'property: secret' && echo 0 || echo 1)"
	check "values-cloud の描画: OwnerSecondFactor__Mode が 2 本" "$([ "$(printf '%s\n' "$rendered" | grep -c 'name: OwnerSecondFactor__Mode')" = 2 ] && echo 0 || echo 1)"
fi
# 印はゲートのログと同じ。
check "印が OwnerSecondFactorGate にある" \
	"$(grep -Fq 'LogMarker = "owner-second-factor:"' "$ROOT_DIR/backend/Shared/AiStockTrading.Shared.Infrastructure/Composable/SecondFactor/OwnerSecondFactorGate.cs" && echo 0 || echo 1)"

echo "ast-owner-totp-enroll.test.sh: ${pass} passed, ${fail} failed"
[ "$fail" -eq 0 ]
