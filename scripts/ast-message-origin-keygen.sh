#!/usr/bin/env bash
# NFR, FR-10, IADR-0633: メッセージの発行元の署名の鍵の組（ECDSA P-256）を作り、OpenBao へ入れる。**オーナーが実行する。**
#
#   bash scripts/ast-message-origin-keygen.sh --live [--rotate [--no-previous]] [<発行元>…]
#
# 何をするか（--live）:
#   発行元ごと（既定は 7 つすべて: backtest information-collection market-monitor order-execution report risk-management trade-decision）に、
#   1. 手元で P-256 の鍵の組を作る（メモリの中だけ。ディスク・引数・画面に出さない）。
#   2. 秘密の鍵（PKCS#8 DER の base64）を cloud/ast/ast-message-origin-signing-keys の <発行元> へ、
#      公開の鍵（SubjectPublicKeyInfo DER の base64）を cloud/ast/ast-message-origin-verify-keys の <発行元> へ、
#      基盤の put-external-secret.sh の標準入力で入れる（この順。秘密の鍵が入らなければ公開の鍵も入れない）。
#
# 基盤の口の呼び出しの前置き（スクリプトのパスとオーナー用の資格のファイルの引数。秘密は持たない）を環境変数
# AST_PUT_EXTERNAL_SECRET で与える。例:
#   AST_PUT_EXTERNAL_SECRET='bash ../microservices-platform/deploy/cloud/vault/put-external-secret.sh --live --operator-file <…>.age --identity <…>'
#
# 🔴 既に秘密の鍵があれば、その発行元は作り直さない（--rotate を付けたときだけ、基盤の口へ --overwrite を渡して組ごと作り直す）。
# 🔴 --rotate は、作り直す前に、いまの公開の鍵（k8s の Secret ast-message-origin-verify-keys。公開してよい値。表示はしない）を
#    「前の公開の鍵」として cloud/ast/ast-message-origin-previous-verify-keys の <発行元> へ残す。chart の global.messageOrigin.previousVerify に
#    その発行元を足して当てると、受け手は入れ替えの前に署名されたメッセージも確かめられる（手順書 §5.1）。前の鍵を読めなければ何も変えずに 1。
#    漏れの疑いで前の鍵を受け付けたくないときだけ --no-previous（前の鍵を残さない。入れ替えの前の未処理のメッセージは段 2 で処理されない）。
# 🔴 秘密の鍵だけ入って公開の鍵が入らなかったときは 1 で止まる（組が食い違ったまま）。その発行元を --rotate で作り直す。
#
# 終了コード: 0＝すべて入れた / 1＝失敗 / 2＝引数の誤り / 3＝--live が無い / 5＝既に在る発行元を飛ばした（--rotate なし。その分は何も変えない）
#             6＝AST_PUT_EXTERNAL_SECRET が無い / 7＝openssl が無い
# 環境変数（既定）: AST_NAMESPACE=ai-stock-trading  KUBECTL=kubectl（--rotate で前の公開の鍵を読むときだけ使う）
set -euo pipefail

ALL_ISSUERS=(backtest information-collection market-monitor order-execution report risk-management trade-decision)
SIGNING_PATH="cloud/ast/ast-message-origin-signing-keys"
VERIFY_PATH="cloud/ast/ast-message-origin-verify-keys"
PREVIOUS_PATH="cloud/ast/ast-message-origin-previous-verify-keys"
NAMESPACE="${AST_NAMESPACE:-ai-stock-trading}"
KUBECTL_BIN="${KUBECTL:-kubectl}"

usage() { sed -n '2,4p' "$0" | sed 's/^# \{0,1\}//' >&2; exit 2; }

live=0; rotate=0; previous=1; issuers=()
for arg in "$@"; do
  case "$arg" in
    --live) live=1 ;;
    --rotate) rotate=1 ;;
    --no-previous) previous=0 ;;
    -h|--help) sed -n '2,33p' "$0"; exit 0 ;;
    -*) echo "ERROR: 知らない引数: $arg" >&2; usage ;;
    *)
      known=0
      for i in "${ALL_ISSUERS[@]}"; do [ "$i" = "$arg" ] && known=1; done
      [ "$known" -eq 1 ] || { echo "ERROR: 知らない発行元: $arg（${ALL_ISSUERS[*]}）" >&2; exit 2; }
      issuers+=("$arg") ;;
  esac
done
[ "${#issuers[@]}" -gt 0 ] || issuers=("${ALL_ISSUERS[@]}")
[ "$previous" -eq 1 ] || [ "$rotate" -eq 1 ] || { echo "ERROR: --no-previous は --rotate と一緒に使います。" >&2; exit 2; }

if [ "$live" -ne 1 ]; then
  echo "ERROR: OpenBao へ鍵を書きます。確かめたうえで --live を付けて実行してください（何も変えていません）。" >&2
  exit 3
fi
[ -n "${AST_PUT_EXTERNAL_SECRET:-}" ] || {
  echo "ERROR: AST_PUT_EXTERNAL_SECRET（基盤の put-external-secret.sh の呼び出しの前置き）が要ります（何も変えていません）。" >&2
  exit 6
}
command -v "${OPENSSL:-openssl}" >/dev/null 2>&1 || { echo "ERROR: openssl が要ります（何も変えていません）。" >&2; exit 7; }
OPENSSL_BIN="${OPENSSL:-openssl}"
read -r -a PUT_CMD <<< "$AST_PUT_EXTERNAL_SECRET"
overwrite=()
[ "$rotate" -eq 1 ] && overwrite=(--overwrite)

# 鍵はシェルの変数（メモリ）にだけ置く。printf は組み込みなので、値はプロセスの引数に載らない。
skipped=0
for issuer in "${issuers[@]}"; do
  pem="$("$OPENSSL_BIN" genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -pkeyopt ec_param_enc:named_curve 2>/dev/null)" \
    || { echo "ERROR: $issuer の鍵の組を作れませんでした（何も書いていません）。" >&2; exit 1; }
  # 🔴 PKCS#8 にする（openssl pkey -outform DER は EC の鍵を SEC1 で書くので、受け手の読み込みが通らない）。
  private="$(printf '%s\n' "$pem" | "$OPENSSL_BIN" pkcs8 -topk8 -nocrypt -outform DER 2>/dev/null | base64 | tr -d '\n')"
  public="$(printf '%s\n' "$pem" | "$OPENSSL_BIN" pkey -pubout -outform DER 2>/dev/null | base64 | tr -d '\n')"
  unset pem
  [ -n "$private" ] && [ -n "$public" ] || { echo "ERROR: $issuer の鍵を書き出せませんでした（何も書いていません）。" >&2; exit 1; }

  # --rotate: 作り直す前に、いまの公開の鍵を前の鍵として残す（読めなければ何も変えずに止める）。
  if [ "$rotate" -eq 1 ] && [ "$previous" -eq 1 ]; then
    old="$("$KUBECTL_BIN" -n "$NAMESPACE" get secret ast-message-origin-verify-keys -o "go-template={{index .data \"$issuer\"}}" 2>/dev/null \
      | base64 -d 2>/dev/null | tr -d '\n')" || old=""
    if [ -z "$old" ] || ! printf '%s' "$old" | base64 -d 2>/dev/null | "$OPENSSL_BIN" pkey -pubin -inform DER -noout 2>/dev/null; then
      unset private public old
      echo "ERROR: $issuer のいまの公開の鍵を読めません（Secret ast-message-origin-verify-keys）。何も変えていません。" >&2
      echo "       前の鍵を残さずに作り直すなら --no-previous を付けてください（入れ替えの前の未処理のメッセージは段 2 で処理されません）。" >&2
      exit 1
    fi
    rc=0
    printf '%s' "$old" | "${PUT_CMD[@]}" --overwrite "$PREVIOUS_PATH" "$issuer" >/dev/null || rc=$?
    unset old
    [ "$rc" -eq 0 ] || { unset private public; echo "ERROR: $issuer の前の公開の鍵を残せませんでした（終了コード $rc。何も変えていません）。" >&2; exit 1; }
    echo "前の公開の鍵を残しました: $issuer（$PREVIOUS_PATH。global.messageOrigin.previousVerify に足して当ててください）。"
  fi

  rc=0
  printf '%s' "$private" | "${PUT_CMD[@]}" "${overwrite[@]+"${overwrite[@]}"}" "$SIGNING_PATH" "$issuer" >/dev/null || rc=$?
  unset private
  if [ "$rc" -eq 5 ]; then
    echo "既に在ります: $issuer（$SIGNING_PATH）。作り直すなら --rotate $issuer（何も変えていません）。" >&2
    skipped=1; unset public; continue
  fi
  [ "$rc" -eq 0 ] || { echo "ERROR: $issuer の秘密の鍵を書けませんでした（終了コード $rc。公開の鍵は書いていません）。" >&2; exit 1; }

  rc=0
  printf '%s' "$public" | "${PUT_CMD[@]}" "${overwrite[@]+"${overwrite[@]}"}" "$VERIFY_PATH" "$issuer" >/dev/null || rc=$?
  unset public
  if [ "$rc" -ne 0 ]; then
    echo "ERROR: $issuer の秘密の鍵は書きましたが、公開の鍵を書けませんでした（終了コード $rc）。組が食い違っています。" >&2
    echo "       bash scripts/ast-message-origin-keygen.sh --live --rotate $issuer で組ごと作り直してください。" >&2
    exit 1
  fi
  echo "書きました: $issuer（秘密の鍵と公開の鍵。値は表示しません）。"
done

echo "ESO の同期の後に、鍵を読むサービスを作り直してください（手順書 §5.4）。"
[ "$skipped" -eq 0 ] || exit 5
