#!/usr/bin/env bash
# NFR, FR-10, UC-06, ADR-0009, #1530, IADR-0613: **Discord を経ずに新規建てを止める**（ノードへ SSH で入ったオーナーが実行する）。
#
#   bash scripts/ast-trading-stop.sh status                         … 今の状態を見る（何も変えない）
#   bash scripts/ast-trading-stop.sh --live hold --reason "<理由>"  … 保持を入れる（既存の kill switch を起動し、解除を拒む）
#   bash scripts/ast-trading-stop.sh --live release                … 保持を外す（kill switch は起動したまま残る）
#   bash scripts/ast-trading-stop.sh --live isolate-discord        … Discord のボット（notification-service）を 0 台にする
#   bash scripts/ast-trading-stop.sh --live restore-discord        … ボットを 1 台に戻す
#
# 何をするか（hold）:
#   1. 名前空間に Secret `ast-operator-hold`（enabled=true・reason）を作る。chart は risk-management の env へ optional の
#      secretKeyRef で読む（Secret が無ければ env も無い＝既定の振る舞い）。Secret は chart・ESO・ArgoCD の管理の外にある。
#   2. risk-management の Pod を消して作り直させる（Deployment の spec は変えない＝helm upgrade・ArgoCD と食い違わない）。
#   3. 新しい Pod のログに印 `operator-kill-switch-hold: active` が出るまで待つ（出なければ失敗＝別の手段で止める）。
#   起動した kill switch は既存のものと同じ意味である（新規建てを止め、手仕舞い・損切りは止めない。変更履歴に残る）。
#   保持の間は Discord・画面からの解除が 409 で拒まれる。release で外すと、解除は従来どおり Discord（確認ボタン＋確認フレーズ）で行う。
#
# 🔴 稼働クラスタへ書くので、変える操作は `--live` を付けたときだけ動く（付けなければ何も変えずに 3 で終わる）。
# 🔴 秘密の値は扱わない。理由はプロセスの引数へ載せず、0600 の一時ファイルから Secret へ渡す。
#
# 環境変数:
#   KUBECTL                 kubectl の呼び方（既定 `kubectl`。k3s のノードでは `k3s kubectl`）
#   AST_NAMESPACE           名前空間（既定 ai-stock-trading）
#   AST_STOP_WAIT_SECONDS   印を待つ上限（既定 180 秒）
#   AST_STOP_POLL_SECONDS   印を確かめる間隔（既定 5 秒）
#
# 終了コード: 0＝成功 / 1＝失敗（どこで止まったかを告げる） / 2＝引数の誤り / 3＝--live が無い
set -euo pipefail

# 🔴 印の文字列は backend/Services/RiskManagementService/Program.cs のログと同じ（変えるときは両方を直す。試験が突き合わせる）。
HOLD_MARKER='operator-kill-switch-hold: active'
HOLD_SECRET='ast-operator-hold'
RISK_DEPLOY='risk-management-service'
RISK_SELECTOR='app=risk-management-service'
BOT_DEPLOY='notification-service'
MAX_REASON_CHARS=200

NS="${AST_NAMESPACE:-ai-stock-trading}"
WAIT_SECONDS="${AST_STOP_WAIT_SECONDS:-180}"
POLL_SECONDS="${AST_STOP_POLL_SECONDS:-5}"
read -r -a KUBECTL_CMD <<< "${KUBECTL:-kubectl}"

usage() {
	sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//' >&2
	exit 2
}

k() { "${KUBECTL_CMD[@]}" -n "$NS" "$@"; }

live=0
cmd=""
reason=""
while [ "$#" -gt 0 ]; do
	case "$1" in
		--live) live=1 ;;
		--reason)
			[ "$#" -ge 2 ] || { echo "ERROR: --reason に値がありません。" >&2; usage; }
			reason="$2"
			shift
			;;
		status | hold | release | isolate-discord | restore-discord)
			[ -z "$cmd" ] || { echo "ERROR: 操作は 1 つだけ指定してください。" >&2; usage; }
			cmd="$1"
			;;
		-h | --help) usage ;;
		*) echo "ERROR: 未知の引数: $1" >&2; usage ;;
	esac
	shift
done
[ -n "$cmd" ] || usage
case "$WAIT_SECONDS$POLL_SECONDS" in *[!0-9]*) echo "ERROR: AST_STOP_WAIT_SECONDS / AST_STOP_POLL_SECONDS は整数です。" >&2; exit 2 ;; esac

if [ "$cmd" != "status" ] && [ "$live" -ne 1 ]; then
	echo "ERROR: ${cmd} は稼働クラスタを変えます。確かめたうえで --live を付けて実行してください（何も変えていません）。" >&2
	exit 3
fi

if [ "$cmd" = "hold" ]; then
	# 理由は 1 行・制御文字なし・上限つき（ログと変更履歴へ載るため）。空なら risk-management が既定の理由を使う。
	if [[ "$reason" == *[[:cntrl:]]* ]]; then
		echo "ERROR: --reason に改行・制御文字は使えません。" >&2
		exit 2
	fi
	if [ "${#reason}" -gt "$MAX_REASON_CHARS" ]; then
		echo "ERROR: --reason は ${MAX_REASON_CHARS} 文字までです。" >&2
		exit 2
	fi
fi

warn_gitops() {
	# ArgoCD の自動同期（selfHeal）は Deployment の台数を宣言へ戻す。Secret（管理の外）は戻さない。
	if "${KUBECTL_CMD[@]}" get applications.argoproj.io -A -o name 2>/dev/null | grep -q .; then
		echo "NOTE: ArgoCD の Application があります。自動同期（selfHeal）が有効なら台数の変更は宣言へ戻ります。" >&2
		echo "      ボットを止め続けるには、先に自動同期を止めてください（docs/operations/out-of-band-trading-stop-runbook.md §ボットを止める）。" >&2
	fi
}

restart_risk() {
	echo "risk-management の Pod を作り直します（Deployment の spec は変えません）。"
	k delete pod -l "$RISK_SELECTOR" --wait=false >/dev/null
}

wait_for_marker() {
	local since="$1" waited=0
	while [ "$waited" -le "$WAIT_SECONDS" ]; do
		if k logs "deployment/${RISK_DEPLOY}" --since-time="$since" 2>/dev/null | grep -Fq "$HOLD_MARKER"; then
			return 0
		fi
		sleep "$POLL_SECONDS"
		waited=$((waited + POLL_SECONDS))
	done
	return 1
}

case "$cmd" in
	status)
		echo "== 保持（Secret ${HOLD_SECRET}）"
		if k get secret "$HOLD_SECRET" -o name >/dev/null 2>&1; then
			echo "入っている（解除は Discord から拒まれる）"
		else
			echo "入っていない"
		fi
		echo "== Deployment の台数（準備できた台数 / 宣言）"
		for d in "$RISK_DEPLOY" order-execution-service "$BOT_DEPLOY" trade-decision-service opend; do
			got="$(k get deployment "$d" -o jsonpath='{.status.readyReplicas}/{.spec.replicas}' 2>/dev/null || echo '(無い)')"
			printf '  %-28s %s\n' "$d" "${got:-0/?}"
		done
		echo "== risk-management の保持の印（直近 24 時間の最後の 1 行）"
		k logs "deployment/${RISK_DEPLOY}" --since=24h 2>/dev/null | grep -F "$HOLD_MARKER" | tail -n 1 || echo "  （無い）"
		echo "kill switch の状態そのものは Discord の /status か、BFF 経由の照会で確かめる（本スクリプトは所有者のトークンを持たない）。"
		;;
	hold)
		since="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
		tmp="$(mktemp)"
		trap 'rm -f "$tmp"' EXIT
		chmod 600 "$tmp"
		printf '%s' "$reason" > "$tmp"
		# 作り直し（既にあれば置き換える）。値は引数へ載せない（enabled は固定の語）。
		k create secret generic "$HOLD_SECRET" --from-literal=enabled=true --from-file=reason="$tmp" \
			--dry-run=client -o yaml | k apply -f - >/dev/null
		k label secret "$HOLD_SECRET" app.kubernetes.io/part-of=ai-stock-trading --overwrite >/dev/null
		echo "Secret ${HOLD_SECRET} を作りました。"
		restart_risk
		if wait_for_marker "$since"; then
			echo "OK: 保持が効きました（kill switch は起動・Discord からの解除は拒まれます）。"
			echo "    新規建ては止まり、手仕舞い・損切りは続きます。外すときは: bash scripts/ast-trading-stop.sh --live release"
		else
			echo "ERROR: ${WAIT_SECONDS} 秒待っても risk-management のログに印が出ません。保持が効いたと確かめられていません。" >&2
			echo "       Pod の状態: ${KUBECTL:-kubectl} -n ${NS} get pods -l ${RISK_SELECTOR}" >&2
			echo "       止まっていない恐れがあります。手順書 §効かないとき に進んでください。" >&2
			exit 1
		fi
		;;
	release)
		k delete secret "$HOLD_SECRET" --ignore-not-found >/dev/null
		echo "Secret ${HOLD_SECRET} を消しました。"
		restart_risk
		echo "OK: 保持を外しました。kill switch は起動したままです（取引は再開していません）。"
		echo "    再開は原因を確かめてから Discord の /killswitch off（確認ボタン＋確認フレーズ）で行ってください。"
		;;
	isolate-discord)
		warn_gitops
		k scale deployment "$BOT_DEPLOY" --replicas=0 >/dev/null
		echo "OK: Discord のボット（${BOT_DEPLOY}）を 0 台にしました。Discord からの操作と Discord への業務通知は止まります。"
		echo "    基盤の警報（Alertmanager）と kill switch は影響を受けません。戻すときは: bash scripts/ast-trading-stop.sh --live restore-discord"
		;;
	restore-discord)
		k scale deployment "$BOT_DEPLOY" --replicas=1 >/dev/null
		echo "OK: Discord のボット（${BOT_DEPLOY}）を 1 台に戻しました。"
		;;
esac
