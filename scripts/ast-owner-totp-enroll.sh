#!/usr/bin/env bash
# NFR-06, FR-14, #1542, IADR-0615: 危険な操作の第 2 要素（TOTP）のシークレットを作り、認証アプリへ登録する（オーナーが SSH から実行する）。
#
#   bash scripts/ast-owner-totp-enroll.sh --live [--rotate]            … シークレットを作り、QR を端末に出し、Enter で消す
#   bash scripts/ast-owner-totp-enroll.sh --live --openbao [--rotate]  … 同じ。ただし k8s の Secret ではなく OpenBao へ入れる（クラウド）
#   bash scripts/ast-owner-totp-enroll.sh status                        … Secret の有無と、直近の検証の結果（ログ）を見る（何も変えない）
#
# 何をするか（--live）:
#   1. 乱数 20 バイトの base32 のシークレットを作る（0600 の一時ファイル。終了時に消す）。
#   2. 名前空間に Secret `ast-owner-totp`（key: secret）を作る。chart は risk-management と report の env へ optional で読む。
#   3. otpauth の URI を `qrencode` で端末に QR として出す（URI は stdin で渡し、引数に載せない）。Enter で画面と履歴を消す。
#   4. risk-management と report の Pod を作り直させる（新しいシークレットを読む）。
# 🔴 シークレットの値は表示しない（QR だけ）・ファイルに残さない・プロセスの引数に載せない。`qrencode` が無ければ何も作らずに 4 で終わる。
# 🔴 既に Secret があれば作り直さない（`--rotate` を付けたときだけ。旧い登録は使えなくなる）。
#
# --openbao（クラウド。NFR-05, ADR-0080, #1560, IADR-0623 決定 5）: 2 の代わりに、基盤の put-external-secret.sh の**標準入力へだけ**
#   シークレットを流し、OpenBao の cloud/ast/ast-owner-totp（property secret）へ書く。k8s の Secret は ESO が写す
#   （ExternalSecret ast-owner-totp。chart の values-cloud）。書いた後に同期を求め、写し終えてから 4 を行う。
#   🔶 仮: put-external-secret.sh の呼び方は基盤の PR ② で確定する。確定までは、呼び出しの前置き（スクリプトのパスと
#      オーナー用の資格のファイルの引数。秘密は持たない）を環境変数 AST_PUT_EXTERNAL_SECRET で与える。末尾に
#      `cloud/ast/ast-owner-totp secret` を足して呼ぶ。例:
#        AST_PUT_EXTERNAL_SECRET='bash ../microservices-platform/deploy/cloud/vault/put-external-secret.sh --live --operator-file <…>.age --identity <…>'
#   既に登録されているかは、ESO が写した k8s の Secret の有無で見る。
#
# 環境変数: KUBECTL（既定 kubectl。k3s は `k3s kubectl`）・AST_NAMESPACE（既定 ai-stock-trading）・AST_TOTP_ISSUER（既定 ai-stock-trading）
# 終了コード: 0＝成功 / 1＝失敗 / 2＝引数の誤り / 3＝--live が無い / 4＝qrencode が無い / 5＝既に登録されている（--rotate なし）
#             6＝--openbao なのに AST_PUT_EXTERNAL_SECRET が無い
set -euo pipefail

SECRET_NAME='ast-owner-totp'
MARKER='owner-second-factor:'
NS="${AST_NAMESPACE:-ai-stock-trading}"
ISSUER="${AST_TOTP_ISSUER:-ai-stock-trading}"
read -r -a KUBECTL_CMD <<< "${KUBECTL:-kubectl}"
k() { "${KUBECTL_CMD[@]}" -n "$NS" "$@"; }

usage() { sed -n '2,6p' "$0" | sed 's/^# \{0,1\}//' >&2; exit 2; }

live=0; rotate=0; openbao=0; cmd="enroll"
while [ "$#" -gt 0 ]; do
	case "$1" in
		--live) live=1 ;;
		--rotate) rotate=1 ;;
		--openbao) openbao=1 ;;
		status) cmd="status" ;;
		-h | --help) usage ;;
		*) echo "ERROR: 未知の引数: $1" >&2; usage ;;
	esac
	shift
done

if [ "$cmd" = "status" ]; then
	if k get secret "$SECRET_NAME" -o name >/dev/null 2>&1; then echo "シークレット: 登録済み"; else echo "シークレット: 未登録"; fi
	for d in risk-management-service report-service; do
		echo "== ${d} の直近の検証（最後の 3 行。コードは記録されない）"
		k logs "deployment/${d}" --since=24h 2>/dev/null | grep -F "$MARKER" | tail -n 3 || echo "  （無い）"
	done
	exit 0
fi

if [ "$live" -ne 1 ]; then
	echo "ERROR: 稼働クラスタへシークレットを作ります。確かめたうえで --live を付けて実行してください（何も変えていません）。" >&2
	exit 3
fi
if [ "$openbao" -eq 1 ]; then
	[ -n "${AST_PUT_EXTERNAL_SECRET:-}" ] || {
		echo "ERROR: --openbao には AST_PUT_EXTERNAL_SECRET（基盤の put-external-secret.sh の呼び出しの前置き）が要ります（何も変えていません）。" >&2
		exit 6
	}
	read -r -a PUT_CMD <<< "$AST_PUT_EXTERNAL_SECRET"
fi
if ! command -v qrencode >/dev/null 2>&1; then
	echo "ERROR: qrencode がありません（例: apt-get install qrencode）。値を画面へ文字で出さないため、無ければ作りません（何も変えていません）。" >&2
	exit 4
fi
if k get secret "$SECRET_NAME" -o name >/dev/null 2>&1 && [ "$rotate" -ne 1 ]; then
	echo "ERROR: 既に登録されています。作り直すと旧い登録は使えなくなります。作り直すなら --rotate を付けてください（何も変えていません）。" >&2
	exit 5
fi

umask 077
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
head -c 20 /dev/urandom | base32 | tr -d '=\n' > "$tmp/secret"
[ "$(wc -c < "$tmp/secret")" -ge 32 ] || { echo "ERROR: シークレットを作れませんでした。" >&2; exit 1; }

if [ "$openbao" -eq 1 ]; then
	# シークレットは標準入力でだけ渡す（引数・環境変数に載せない）。
	"${PUT_CMD[@]}" "cloud/ast/${SECRET_NAME}" secret < "$tmp/secret" >/dev/null \
		|| { echo "ERROR: OpenBao へ書けませんでした（何も登録していません。QR は出していません）。" >&2; exit 1; }
	echo "OpenBao の cloud/ast/${SECRET_NAME} に書きました（値は表示しません）。"
else
	k create secret generic "$SECRET_NAME" --from-file=secret="$tmp/secret" --dry-run=client -o yaml | k apply -f - >/dev/null
	k label secret "$SECRET_NAME" app.kubernetes.io/part-of=ai-stock-trading --overwrite >/dev/null
	echo "Secret ${SECRET_NAME} を作りました（値は表示しません）。"
fi

# otpauth の URI はファイル越しに qrencode の stdin へ渡す（引数・環境変数に載せない）。
{ printf 'otpauth://totp/%s:owner?secret=' "$ISSUER"; cat "$tmp/secret"; printf '&issuer=%s&algorithm=SHA1&digits=6&period=30' "$ISSUER"; } > "$tmp/uri"
echo "認証アプリで次の QR を読み取ってください。読み取ったら Enter を押すと画面から消します。"
qrencode -t ANSIUTF8 < "$tmp/uri"
read -r -s _ || true
# 画面とスクロールバックを消す（端末によっては履歴が残る。残る端末では窓を閉じる）。
printf '\033[3J\033[2J\033[H'

if [ "$openbao" -eq 1 ]; then
	# ESO に同期を求め、写し終える（同期した版が変わる）まで待つ。待ちきれなければ作り直しをせず、手で確かめる手順を示す。
	before="$(k get externalsecret "$SECRET_NAME" -o 'jsonpath={.status.syncedResourceVersion}' 2>/dev/null || true)"
	k annotate externalsecret "$SECRET_NAME" "force-sync=$(date +%s)" --overwrite >/dev/null
	synced=0
	for _ in $(seq 1 "${AST_TOTP_SYNC_WAIT:-30}"); do
		now="$(k get externalsecret "$SECRET_NAME" -o 'jsonpath={.status.syncedResourceVersion}' 2>/dev/null || true)"
		if [ -n "$now" ] && [ "$now" != "$before" ]; then synced=1; break; fi
		sleep "${AST_TOTP_SYNC_INTERVAL:-2}"
	done
	if [ "$synced" -ne 1 ]; then
		echo "ERROR: ESO の同期を確かめられませんでした。kubectl -n ${NS} get externalsecret ${SECRET_NAME} で SecretSynced を確かめ、" >&2
		echo "       risk-management と report の Pod を作り直してください（登録は OpenBao に入っています）。" >&2
		exit 1
	fi
fi
for d in risk-management-service report-service; do
	k delete pod -l "app=${d}" --wait=false >/dev/null
done
echo "OK: 登録しました。risk-management と report を作り直しています。"
echo "    確かめ方: Discord の危険な操作で 6 桁を入れたあと、status で outcome=Verified を見る（段 1 は記録だけ。6 桁の入力はボットの対応後）。"
