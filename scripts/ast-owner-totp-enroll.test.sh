#!/usr/bin/env bash
# NFR-06, #1542, IADR-0615: scripts/ast-owner-totp-enroll.sh（TOTP のシークレットの登録）の挙動を固定する。kubectl と qrencode はスタブ。
#   - --live が無ければ kubectl を呼ばずに 3。qrencode が無ければ何も作らずに 4。既に登録済みで --rotate が無ければ 5
#   - シークレットは Secret ast-owner-totp の key secret に入り、32 文字以上の base32 である
#   - シークレットはどの kubectl の引数にも、標準出力にも出ない（QR の入力＝qrencode の stdin にだけ渡る）
#   - 作ったあと risk-management と report の Pod を作り直す。status は変える動詞を使わない
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
  *" logs "*) echo 'owner-second-factor: operation=TradingResume outcome=Verified mode=Audit allowed=True'; exit 0 ;;
esac
exit 0
STUB
cat > "$BIN/qrencode" <<'STUB'
#!/usr/bin/env bash
cat > "$AST_TEST_STATE/qr-input.txt"; echo "[QR]"
STUB
chmod +x "$BIN/kubectl" "$BIN/qrencode"
# qrencode を含まない PATH（基本コマンドだけ）。
BARE="$STATE/bare"; mkdir -p "$BARE"
for c in bash sed head base32 tr wc mktemp rm cat printf grep tail; do p="$(command -v "$c")" && ln -sf "$p" "$BARE/$c"; done

pass=0; fail=0
check() { if [ "$2" -eq 0 ]; then pass=$((pass + 1)); else echo "FAIL: $1"; fail=$((fail + 1)); fi; }
reset() { rm -f "$STATE"/calls.log "$STATE"/secret.txt "$STATE"/qr-input.txt "$STATE"/exists; : > "$STATE/calls.log"; }
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

reset; rc="$(run status)"
check "status は 0" "$([ "$rc" = 0 ] && echo 0 || echo 1)"
check "status は変える動詞を使わない" "$(grep -Eq '(^| )(create|apply|delete|label|scale|patch)( |$)' "$STATE/calls.log" && echo 1 || echo 0)"

# chart は risk-management と report で同じ Secret を optional で読む（values.yaml・values-local.yaml）。
for v in values.yaml values-local.yaml; do
	check "$v: 2 サービスが ast-owner-totp を optional で読む" \
		"$([ "$(grep -c 'secretKeyRef: { name: ast-owner-totp, key: secret, optional: true }' "$ROOT_DIR/deploy/helm/ai-stock-trading/$v")" = 2 ] && echo 0 || echo 1)"
done
# 印はゲートのログと同じ。
check "印が OwnerSecondFactorGate にある" \
	"$(grep -Fq 'LogMarker = "owner-second-factor:"' "$ROOT_DIR/backend/Shared/AiStockTrading.Shared.Infrastructure/Composable/SecondFactor/OwnerSecondFactorGate.cs" && echo 0 || echo 1)"

echo "ast-owner-totp-enroll.test.sh: ${pass} passed, ${fail} failed"
[ "$fail" -eq 0 ]
