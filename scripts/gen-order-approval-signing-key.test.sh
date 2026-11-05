#!/usr/bin/env bash
# NFR, #1527, IADR-0610: gen-order-approval-signing-key.sh の Bash 試験（vault / kubectl はスタブ。実クラスタ不要）。
# 鍵が引数・標準出力に出ないこと、既にあれば書かないこと、--rotate で書き直すこと、鍵の長さを確かめる。
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SCRIPT="$ROOT/scripts/gen-order-approval-signing-key.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
fail=0
check() { if eval "$2"; then echo "ok - $1"; else echo "not ok - $1"; fail=1; fi; }

# --- vault スタブ: 引数を記録し、stdin の値を保存する。HAS=1 なら kv get が成功する ---
cat >"$work/vault" <<'STUB'
#!/usr/bin/env bash
echo "$*" >>"$STUB_DIR/vault.args"
if [ "$1 $2" = "kv get" ]; then [ "${HAS:-0}" = "1" ] && { echo "existing"; exit 0; }; exit 2; fi
if [ "$1 $2" = "kv patch" ] && [ "${KV_EXISTS:-1}" = "0" ]; then exit 2; fi
cat >"$STUB_DIR/vault.stdin"
STUB
chmod +x "$work/vault"
export STUB_DIR="$work"

out="$(VAULT="$work/vault" bash "$SCRIPT" --vault)"
key="$(cat "$work/vault.stdin")"
check "vault: 鍵を stdin で渡す（64 文字の base64）" '[ "${#key}" -eq 64 ]'
check "vault: 鍵を 32 バイト以上へ復号できる" '[ "$(printf "%s" "$key" | base64 -d | wc -c)" -ge 32 ]'
check "vault: 鍵が引数に出ない" '! grep -qF "$key" "$work/vault.args"'
check "vault: 鍵が標準出力に出ない" '! printf "%s" "$out" | grep -qF "$key"'
check "vault: キー=- で書く" 'grep -q "kv patch -method=patch secret/ai-stock-trading/app-secrets order-approval-signing-key=-" "$work/vault.args"'

rm -f "$work/vault.stdin" "$work/vault.args"
HAS=1 VAULT="$work/vault" bash "$SCRIPT" --vault >/dev/null
check "vault: 既にあれば書かない" '[ ! -f "$work/vault.stdin" ]'
HAS=1 VAULT="$work/vault" bash "$SCRIPT" --vault --rotate >/dev/null
check "vault: --rotate で書き直す" '[ -s "$work/vault.stdin" ]'

rm -f "$work/vault.stdin" "$work/vault.args"
KV_EXISTS=0 VAULT="$work/vault" bash "$SCRIPT" --vault >/dev/null
check "vault: KV が無ければ put で作る" 'grep -q "kv put secret/ai-stock-trading/app-secrets order-approval-signing-key=-" "$work/vault.args"'

# --- kubectl スタブ: 引数と patch ファイルの中身を記録する ---
cat >"$work/kubectl" <<'STUB'
#!/usr/bin/env bash
echo "$*" >>"$STUB_DIR/kubectl.args"
case "$*" in
  *"-o jsonpath="*) [ "${HAS:-0}" = "1" ] && printf 'ZXhpc3Rpbmc='; exit 0 ;;
  *" patch secret "*) for a in "$@"; do [ -n "${next:-}" ] && { cp "$a" "$STUB_DIR/patch.json"; stat -c %a "$a" >"$STUB_DIR/patch.mode"; next=""; }; [ "$a" = "--patch-file" ] && next=1; done; exit 0 ;;
  *" get secret "*) exit 0 ;;
esac
STUB
chmod +x "$work/kubectl"
out="$(KUBECTL="$work/kubectl" bash "$SCRIPT" --k8s)"
data="$(sed -E 's/.*"order-approval-signing-key":"([^"]+)".*/\1/' "$work/patch.json")"
inner="$(printf '%s' "$data" | base64 -d)"
check "k8s: Secret の data へ base64 で書く（中身は 32 バイト以上の鍵）" '[ "$(printf "%s" "$inner" | base64 -d | wc -c)" -ge 32 ]'
check "k8s: 鍵が引数に出ない" '! grep -qF "$data" "$work/kubectl.args" && ! grep -qF "$inner" "$work/kubectl.args"'
check "k8s: 鍵が標準出力に出ない" '! printf "%s" "$out" | grep -qF "$inner"'
check "k8s: patch ファイルは 0600" '[ "$(cat "$work/patch.mode")" = "600" ]'
rm -f "$work/patch.json"
HAS=1 KUBECTL="$work/kubectl" bash "$SCRIPT" --k8s >/dev/null
check "k8s: 既にあれば書かない" '[ ! -f "$work/patch.json" ]'

check "引数なしは exit 2" '! bash "$SCRIPT" >/dev/null 2>&1'
exit "$fail"
