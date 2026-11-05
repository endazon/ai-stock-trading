#!/usr/bin/env bash
# NFR, FR-10, UC-06, #1530, IADR-0613: scripts/ast-trading-stop.sh（Discord を経ない停止）の挙動を固定する。
#
#   bash scripts/ast-trading-stop.test.sh
#
# 稼働クラスタは要らない。KUBECTL にスタブを渡し、スタブが受け取った引数を記録して「ログに印が出るか」「Secret が在るか」を疑似する。
# 起動は scripts/scripts.repo.test.js（node の経路）から行う（k8s-local-images.test.sh と同じ）。
#
# 固定する不変条件:
#   - 変える操作は --live が無ければ kubectl を 1 度も呼ばずに 3 で終わる。status は --live なしで動き、変える動詞を使わない
#   - hold は Secret（enabled=true・reason）を作り、risk-management の Pod を消し、ログの印を確かめて 0。印が出なければ 1
#   - hold の理由はプロセスの引数へ載せない（ファイルから渡す）。改行・制御文字・長すぎる理由は 2
#   - release は Secret を消して Pod を作り直す（kill switch の解除はしない）
#   - isolate-discord / restore-discord は notification-service の台数だけを変える
#   - 印の文字列は risk-management の Program.cs のログと同じ
set -u

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SCRIPT="$ROOT_DIR/scripts/ast-trading-stop.sh"
STATE="$(mktemp -d)"
STUB="$STATE/kubectl-stub"
trap 'rm -rf "$STATE"' EXIT

cat > "$STUB" <<'STUB'
#!/usr/bin/env bash
S="$AST_TEST_STATE"
printf '%s\n' "$*" >> "$S/calls.log"
args=" $* "
case "$args" in
  *" create secret generic "*)
    # --from-file=reason=<path> の中身を記録する（引数に理由が載っていないことは呼び出し側の試験が見る）。
    for a in "$@"; do case "$a" in --from-file=reason=*) cat "${a#--from-file=reason=}" > "$S/reason.txt" ;; esac; done
    printf 'apiVersion: v1\nkind: Secret\n'; exit 0 ;;
  *" apply -f - "*) cat > /dev/null; exit 0 ;;
  *" logs "*) [ -f "$S/marker" ] && printf '2026-11-05 [WRN] operator-kill-switch-hold: active (outcome=Engaged)\n'; exit 0 ;;
  *" get secret "*) [ -f "$S/secret" ]; exit $? ;;
  *" get applications.argoproj.io "*) [ -f "$S/argocd" ] && echo application/ai-stock-trading; exit 0 ;;
  *" get deployment "*) printf '1/1'; exit 0 ;;
esac
exit 0
STUB
chmod +x "$STUB"

fail=0
pass=0
check() { # name, condition-result(0=ok)
	if [ "$2" -eq 0 ]; then pass=$((pass + 1)); else echo "FAIL: $1"; fail=$((fail + 1)); fi
}
reset() { rm -f "$STATE"/calls.log "$STATE"/reason.txt "$STATE"/marker "$STATE"/secret "$STATE"/argocd; : > "$STATE/calls.log"; }
run() {
	AST_TEST_STATE="$STATE" KUBECTL="$STUB" AST_STOP_WAIT_SECONDS="${WAIT:-0}" AST_STOP_POLL_SECONDS=1 \
		bash "$SCRIPT" "$@" > "$STATE/out.log" 2>&1
	echo $?
}
mutating() { grep -Eq '(^| )(create|apply|delete|scale|patch|label|set|rollout|edit|annotate)( |$)' "$STATE/calls.log"; }

# 1) --live が無ければ何も呼ばずに 3。
for c in hold release isolate-discord restore-discord; do
	reset
	rc="$(run "$c" --reason x)"
	check "$c: --live なしは 3" "$([ "$rc" = 3 ] && echo 0 || echo 1)"
	check "$c: --live なしは kubectl を呼ばない" "$([ ! -s "$STATE/calls.log" ] && echo 0 || echo 1)"
done

# 2) status は --live なしで動き、変える動詞を使わない。
reset; touch "$STATE/secret"
rc="$(run status)"
check "status: 0" "$([ "$rc" = 0 ] && echo 0 || echo 1)"
check "status: 変える動詞を使わない" "$(mutating && echo 1 || echo 0)"
check "status: 保持の有無を示す" "$(grep -q '入っている' "$STATE/out.log" && echo 0 || echo 1)"

# 3) hold: Secret を作り、Pod を消し、印を確かめて 0。理由は引数へ載せない。
reset; touch "$STATE/marker"
rc="$(run --live hold --reason '値動きの異常を調査中')"
check "hold: 0" "$([ "$rc" = 0 ] && echo 0 || echo 1)"
check "hold: Secret ast-operator-hold を enabled=true で作る" \
	"$(grep -q -- 'create secret generic ast-operator-hold --from-literal=enabled=true --from-file=reason=' "$STATE/calls.log" && echo 0 || echo 1)"
check "hold: 理由は Secret の reason に入る" "$([ "$(cat "$STATE/reason.txt" 2>/dev/null)" = '値動きの異常を調査中' ] && echo 0 || echo 1)"
check "hold: 理由はどの kubectl の引数にも載らない" "$(grep -q '値動きの異常' "$STATE/calls.log" && echo 1 || echo 0)"
check "hold: risk-management の Pod を消す（Deployment は変えない）" \
	"$(grep -q -- '-n ai-stock-trading delete pod -l app=risk-management-service' "$STATE/calls.log" && echo 0 || echo 1)"
check "hold: Deployment の spec を変えない" \
	"$(grep -Eq '(set env|rollout restart|patch deployment|scale deployment)' "$STATE/calls.log" && echo 1 || echo 0)"
check "hold: ログの印を確かめる" "$(grep -q -- 'logs deployment/risk-management-service --since-time=' "$STATE/calls.log" && echo 0 || echo 1)"

# 4) hold: 印が出なければ 1（効いたと確かめられない）。
reset
rc="$(WAIT=1 run --live hold --reason x)"
check "hold: 印が出なければ 1" "$([ "$rc" = 1 ] && echo 0 || echo 1)"

# 5) hold: 理由の検査（改行・長すぎる）は 2 で、kubectl を呼ばない。
reset
rc="$(run --live hold --reason "$(printf 'a\nb')")"
check "hold: 改行を含む理由は 2" "$([ "$rc" = 2 ] && echo 0 || echo 1)"
check "hold: 理由の誤りでは kubectl を呼ばない" "$([ ! -s "$STATE/calls.log" ] && echo 0 || echo 1)"
reset
rc="$(run --live hold --reason "$(printf '%0201d' 0)")"
check "hold: 201 文字の理由は 2" "$([ "$rc" = 2 ] && echo 0 || echo 1)"

# 6) release: Secret を消し、Pod を作り直す。kill switch の解除はしない（解除の口を呼ばない）。
reset; touch "$STATE/secret"
rc="$(run --live release)"
check "release: 0" "$([ "$rc" = 0 ] && echo 0 || echo 1)"
check "release: Secret を消す" "$(grep -q -- 'delete secret ast-operator-hold --ignore-not-found' "$STATE/calls.log" && echo 0 || echo 1)"
check "release: Pod を作り直す" "$(grep -q -- 'delete pod -l app=risk-management-service' "$STATE/calls.log" && echo 0 || echo 1)"
check "release: 解除を呼ばない" "$(grep -q 'disengage' "$STATE/calls.log" && echo 1 || echo 0)"

# 7) isolate / restore: notification-service の台数だけ。ArgoCD があれば警告する。
reset; touch "$STATE/argocd"
rc="$(run --live isolate-discord)"
check "isolate-discord: 0" "$([ "$rc" = 0 ] && echo 0 || echo 1)"
check "isolate-discord: notification-service を 0 台" \
	"$(grep -q -- '-n ai-stock-trading scale deployment notification-service --replicas=0' "$STATE/calls.log" && echo 0 || echo 1)"
check "isolate-discord: ArgoCD の自動同期を警告する" "$(grep -q 'ArgoCD' "$STATE/out.log" && echo 0 || echo 1)"
reset
rc="$(run --live restore-discord)"
check "restore-discord: 1 台に戻す" \
	"$([ "$rc" = 0 ] && grep -q -- 'scale deployment notification-service --replicas=1' "$STATE/calls.log" && echo 0 || echo 1)"

# 8) 名前空間・kubectl の呼び方を環境変数で替えられる（k3s kubectl のような 2 語も）。
reset; touch "$STATE/marker"
AST_TEST_STATE="$STATE" KUBECTL="$STUB extra" AST_NAMESPACE=ast-cloud AST_STOP_WAIT_SECONDS=0 \
	bash "$SCRIPT" --live hold > "$STATE/out.log" 2>&1
check "KUBECTL の 2 語目・AST_NAMESPACE が効く" "$(grep -q -- '^extra -n ast-cloud delete pod' "$STATE/calls.log" && echo 0 || echo 1)"

# 9) 未知の引数・操作なしは 2。
reset
rc="$(run --live frobnicate)"; check "未知の操作は 2" "$([ "$rc" = 2 ] && echo 0 || echo 1)"
rc="$(run)"; check "操作なしは 2" "$([ "$rc" = 2 ] && echo 0 || echo 1)"

# 10) 印の文字列は risk-management の Program.cs のログと同じ（片方だけ変えると hold が必ず失敗する）。
marker="$(sed -n "s/^HOLD_MARKER='\(.*\)'$/\1/p" "$SCRIPT")"
check "印が Program.cs にある" \
	"$([ -n "$marker" ] && grep -Fq "\"${marker}" "$ROOT_DIR/backend/Services/RiskManagementService/Program.cs" && echo 0 || echo 1)"
# 11) Secret の名前・キーは chart の risk-management の env と同じ（values.yaml・values-local.yaml・values-cloud.yaml。
#     後ろ 2 つは risk-management の extraEnv を丸ごと置き換えるので、無いとその profile では保持が効かない）。
for v in values.yaml values-local.yaml values-cloud.yaml; do
	f="$ROOT_DIR/deploy/helm/ai-stock-trading/$v"
	check "$v: 保持の env が Secret ast-operator-hold を optional で読む" \
		"$([ "$(grep -c 'secretKeyRef: { name: ast-operator-hold, key: \(enabled\|reason\), optional: true }' "$f")" = 2 ] && echo 0 || echo 1)"
done

# 12) values-cloud の描画（helm があるときだけ。CI の helm.yml は別に描画を検査する）でも、risk-management が 2 本の参照を持つ。
if command -v helm >/dev/null 2>&1; then
	chart="$ROOT_DIR/deploy/helm/ai-stock-trading"
	rendered="$(helm template ast "$chart" -f "$chart/values-cloud.yaml" -f "$chart/ci/values-cloud-digests.fixture.yaml" 2>/dev/null)"
	check "values-cloud の描画: ast-operator-hold の参照が 2 本" "$([ "$(printf '%s\n' "$rendered" | grep -c 'name: ast-operator-hold')" = 2 ] && echo 0 || echo 1)"
fi

echo "ast-trading-stop.test.sh: ${pass} passed, ${fail} failed"
[ "$fail" -eq 0 ]
