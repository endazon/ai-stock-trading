#!/usr/bin/env bash
# NFR, FR-10, #1527, IADR-0610: 注文の承認（OrderApproved）の発行元の印の鍵を乱数で作り、置き場へ書く。**利用者が実行する。**
#
# 使い方:
#   scripts/gen-order-approval-signing-key.sh --vault [--rotate]
#       Vault の KV（既定 secret/ai-stock-trading/app-secrets）のプロパティ order-approval-signing-key へ書く（ESO 所有の経路）。
#       vault CLI は VAULT_ADDR / VAULT_TOKEN を環境から読む（このスクリプトは値を受け取らない）。
#   scripts/gen-order-approval-signing-key.sh --k8s [--rotate]
#       名前空間 ai-stock-trading の Secret ast-secrets のキー order-approval-signing-key へ書く（ESO の無い従来経路）。
#
# 🔴 既に鍵があれば何もしない（--rotate のときだけ作り直す）。作り直したら risk-management と order-execution を
#    **両方**作り直して新しい鍵を読ませる（段 1 では片側だけ古い間も発注は止まらない。段 2 では揃うまで承認が通らない）。
# 🔴 鍵の値は表示しない・プロセスの引数に載せない（vault へは stdin、kubectl へは 0600 の一時ファイルで渡し、すぐ消す）。
#
# 環境変数（既定）:
#   AST_SIGNING_VAULT_PATH=secret/ai-stock-trading/app-secrets   AST_SIGNING_PROPERTY=order-approval-signing-key
#   AST_NAMESPACE=ai-stock-trading   AST_SIGNING_SECRET=ast-secrets   VAULT=vault   KUBECTL=kubectl
set -euo pipefail

VAULT_BIN="${VAULT:-vault}"
KUBECTL_BIN="${KUBECTL:-kubectl}"
VAULT_PATH="${AST_SIGNING_VAULT_PATH:-secret/ai-stock-trading/app-secrets}"
PROPERTY="${AST_SIGNING_PROPERTY:-order-approval-signing-key}"
NAMESPACE="${AST_NAMESPACE:-ai-stock-trading}"
SECRET="${AST_SIGNING_SECRET:-ast-secrets}"

target=""
rotate=0
for arg in "$@"; do
  case "$arg" in
    --vault) target=vault ;;
    --k8s) target=k8s ;;
    --rotate) rotate=1 ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "ERROR: 知らない引数: $arg" >&2; exit 2 ;;
  esac
done
[ -n "$target" ] || { echo "ERROR: --vault か --k8s を指定してください" >&2; exit 2; }

# 48 バイトの乱数（base64 で 64 文字。下限は 32 バイト）。
new_key() {
  head -c 48 /dev/urandom | base64 | tr -d '\n'
}

umask 077

case "$target" in
  vault)
    # 🔴 共有の KV（app-secrets）の他のキーを消さないため、put（全置換）は「KV が存在しない」と確定したときだけ使う。
    #    存在すれば patch（このキーだけ）。patch の失敗・存在の確認の失敗（権限・ネットワーク）は止まる（put へ倒さない）。
    #    vault の stderr はそのまま出す（値は stdin で渡しているので stderr に値は出ない）。
    meta_err="$("$VAULT_BIN" kv metadata get "$VAULT_PATH" 2>&1 >/dev/null)" && meta_rc=0 || meta_rc=$?
    if [ "$meta_rc" -eq 0 ]; then
      if [ "$rotate" -eq 0 ]; then
        get_err="$("$VAULT_BIN" kv get -field="$PROPERTY" "$VAULT_PATH" 2>&1 >/dev/null)" && get_rc=0 || get_rc=$?
        if [ "$get_rc" -eq 0 ]; then
          echo "鍵は既にあります（$VAULT_PATH の $PROPERTY）。作り直すときは --rotate。"
          exit 0
        fi
        case "$get_err" in
          *"not present in secret"*|*"No value found"*) ;;
          *) echo "ERROR: 既存の鍵の有無を確かめられません（vault kv get）。書かずに止めます: $get_err" >&2; exit 1 ;;
        esac
      fi
      if ! new_key | "$VAULT_BIN" kv patch -method=patch "$VAULT_PATH" "$PROPERTY=-" >/dev/null; then
        echo "ERROR: vault kv patch に失敗しました。put へは倒しません（共有の KV の他のキーを消さないため）。" >&2
        exit 1
      fi
    else
      case "$meta_err" in
        *"No value found"*)
          # KV が存在しないと確定したときだけ作る。
          new_key | "$VAULT_BIN" kv put "$VAULT_PATH" "$PROPERTY=-" >/dev/null ;;
        *)
          echo "ERROR: KV の有無を確かめられません（vault kv metadata get）。書かずに止めます: $meta_err" >&2
          exit 1 ;;
      esac
    fi
    echo "鍵を書きました（$VAULT_PATH の $PROPERTY。値は表示しません）。ESO の同期の後に risk-management と order-execution を作り直してください。"
    ;;
  k8s)
    if [ "$rotate" -eq 0 ] \
      && [ -n "$("$KUBECTL_BIN" -n "$NAMESPACE" get secret "$SECRET" -o "jsonpath={.data.$PROPERTY}" 2>/dev/null || true)" ]; then
      echo "鍵は既にあります（Secret $SECRET の $PROPERTY）。作り直すときは --rotate。"
      exit 0
    fi
    patch_file="$(mktemp)"
    trap 'rm -f "$patch_file"' EXIT
    printf '{"data":{"%s":"%s"}}' "$PROPERTY" "$(new_key | base64 | tr -d '\n')" >"$patch_file"
    if "$KUBECTL_BIN" -n "$NAMESPACE" get secret "$SECRET" >/dev/null 2>&1; then
      "$KUBECTL_BIN" -n "$NAMESPACE" patch secret "$SECRET" --type merge --patch-file "$patch_file" >/dev/null
    else
      echo "ERROR: Secret $SECRET（名前空間 $NAMESPACE）がありません。先に k8s-local-deploy.sh で作ってください。" >&2
      exit 1
    fi
    echo "鍵を書きました（Secret $SECRET の $PROPERTY。値は表示しません）。risk-management と order-execution を作り直してください。"
    ;;
esac
