#!/usr/bin/env bash
# #1094 / IADR-0457: scripts/k8s-local-images.sh の対象の絞り込み（SERVICES）と「無ければ作る」を固定する。
#
#   bash scripts/k8s-local-images.test.sh
#
# 実ランタイムは要らない。PATH の先頭に `nerdctl` / `docker` / `k3d` スタブを置き、スタブが
# 「ランタイムに在るイメージ」「k3d ノードに在るイメージ」を疑似し、build / import の呼び出しを記録する。
# 対象スクリプトは子プロセスとして実走する（引数・環境変数・終了コードをそのまま観測するため）。
# AST_IMAGES_LIB=1 の source が何も作らないことも確かめる（k8s-local-deploy.test.sh と同じ idiom）。
#
# 検証する不変条件（Issue #1094 の受け入れ基準）:
#   - 未知の SERVICES は何も作らずに exit 2（ランタイムが無い環境でも 2）
#   - SERVICES 未設定は従来どおり 12 本すべてを作る（後方互換）
#   - 選んだものだけを作る。`none` は何も選ばない
#   - 選ばれていなくても、ランタイムに :latest が無いイメージは作る
#   - k3d: docker に無ければ作って import、docker に在ってノードに無ければ import だけ、ノードの確認が失敗すれば import
#   - K8S_BUILT_FILE に供給した名前を書く
set -u

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
STATE="$(mktemp -d)"
STUB_BIN="$(mktemp -d)"
BARE_BIN="$(mktemp -d)"   # ランタイムの無い PATH（基本コマンドだけ）
trap 'rm -rf "$STATE" "$STUB_BIN" "$BARE_BIN"' EXIT

# 状態は $AST_TEST_STATE に置く: present（ランタイムに在る ref の一覧）/ node（k3d ノードに在る ref の一覧。
# node-broken があれば確認自体が失敗する）/ built.log（build の -t）/ imported.log（import の ref）。
cat > "$STUB_BIN/nerdctl" <<'STUB'
#!/usr/bin/env bash
S="$AST_TEST_STATE"
args=" $* "
case "$args" in
  *" image inspect "*) grep -Fxq -- "${@: -1}" "$S/present" 2>/dev/null; exit $? ;;
  *" build "*)
    prev=""; for a in "$@"; do [ "$prev" = "-t" ] && printf '%s\n' "$a" >> "$S/built.log"; prev="$a"; done
    exit 0 ;;
esac
exit 0
STUB
cat > "$STUB_BIN/docker" <<'STUB'
#!/usr/bin/env bash
S="$AST_TEST_STATE"
case "${1:-} ${2:-}" in
  "image inspect") grep -Fxq -- "$3" "$S/present" 2>/dev/null; exit $? ;;
  build*)
    prev=""; for a in "$@"; do [ "$prev" = "-t" ] && printf '%s\n' "$a" >> "$S/built.log"; prev="$a"; done
    exit 0 ;;
  exec*)
    printf '%s\n' "$*" >> "$S/exec.log"
    [ -f "$S/node-broken" ] && exit 1
    grep -Fxq -- "${@: -1}" "$S/node" 2>/dev/null; exit $? ;;
esac
exit 0
STUB
cat > "$STUB_BIN/k3d" <<'STUB'
#!/usr/bin/env bash
S="$AST_TEST_STATE"
if [ "${1:-} ${2:-}" = "image import" ]; then
  shift 2
  while [ $# -gt 0 ]; do
    case "$1" in -c) shift ;; *) printf '%s\n' "$1" >> "$S/imported.log" ;; esac
    shift
  done
fi
exit 0
STUB
chmod +x "$STUB_BIN"/*
for c in bash env cat grep sed tr dirname mktemp rm; do ln -s "$(command -v "$c")" "$BARE_BIN/$c"; done
export AST_TEST_STATE="$STATE"

PASSED=0
FAILED=0
ok() { PASSED=$((PASSED + 1)); printf '  ok  %s\n' "$1"; }
ng() { FAILED=$((FAILED + 1)); printf '  NG  %s\n     %s\n' "$1" "$2"; }
assert_contains() { case "$2" in *"$3"*) ok "$1" ;; *) ng "$1" "expected to contain: $3" ;; esac; }
assert_missing() { case "$2" in *"$3"*) ng "$1" "expected NOT to contain: $3" ;; *) ok "$1" ;; esac; }
assert_eq() { [ "$2" = "$3" ] && ok "$1" || ng "$1" "expected [$3] but was [$2]"; }

ref() { printf 'k3d-local/ai-stock-trading/%s:latest' "$1"; }

# MAPPING の名前（単一情報源から読む。件数を本テストへ写さない）。
ALL_NAMES="$(AST_IMAGES_LIB=1 bash -c '. "$1/scripts/k8s-local-images.sh"; ast_images_names' _ "$ROOT_DIR")"
ALL_COUNT="$(printf '%s\n' "$ALL_NAMES" | grep -c .)"

# ランタイム・ノードの状態を作り直す。$1 = ランタイムに在る名前（空白区切り。"all" で全件）/
# $2 = k3d ノードに在る名前（同。"all" で全件。"broken" で確認自体を失敗させる）
given() {
  local n
  rm -rf "$STATE"; mkdir -p "$STATE"
  : > "$STATE/present"; : > "$STATE/node"
  local present="${1:-}" node="${2:-}"
  [ "$present" = "all" ] && present="$ALL_NAMES"
  [ "$node" = "all" ] && node="$ALL_NAMES"
  if [ "$node" = "broken" ]; then : > "$STATE/node-broken"; node=""; fi
  for n in $present; do ref "$n" >> "$STATE/present"; echo >> "$STATE/present"; done
  for n in $node; do ref "$n" >> "$STATE/node"; echo >> "$STATE/node"; done
}

# 対象を子プロセスで実走し、RC / OUT / ERR / BUILT（build した ref）/ IMPORTED / SUPPLIED（K8S_BUILT_FILE）を埋める。
# 引数は env の代入（例: SERVICES=a,b K8S_LOCAL_RUNTIME=rancher）。
run_images() {
  env PATH="$STUB_BIN:$PATH" K8S_BUILT_FILE="$STATE/supplied" "$@" \
    bash "$ROOT_DIR/scripts/k8s-local-images.sh" test-cluster > "$STATE/out" 2> "$STATE/err"
  RC=$?
  OUT="$(cat "$STATE/out")"
  ERR="$(cat "$STATE/err")"
  BUILT="$(cat "$STATE/built.log" 2>/dev/null || true)"
  IMPORTED="$(cat "$STATE/imported.log" 2>/dev/null || true)"
  SUPPLIED="$(cat "$STATE/supplied" 2>/dev/null || true)"
  N_BUILT="$(printf '%s' "$BUILT" | grep -c . || true)"
}

printf 'k8s-local-images.sh: 対象の絞り込み（#1094）\n'

# T-1094-I01: 未知の名前 → 何も作らずに exit 2
given all
run_images K8S_LOCAL_RUNTIME=rancher SERVICES=audit-service,no-such-service
assert_eq   'T-1094-I01 未知: 終了コード 2' "$RC" "2"
assert_contains 'T-1094-I01 未知: 未知の名前を挙げる' "$ERR" 'no-such-service'
assert_contains 'T-1094-I01 未知: 指定できる名前を示す' "$ERR" 'trade-decision-service'
assert_eq   'T-1094-I01 未知: 何も作らない（既知の audit-service も作らない）' "$BUILT" ""

# T-1094-I02: 未知の名前はランタイム判定より前に止める（ランタイムが無くても 2）
given all
env PATH="$BARE_BIN" AST_TEST_STATE="$STATE" SERVICES=bogus \
  "$BARE_BIN/bash" "$ROOT_DIR/scripts/k8s-local-images.sh" > "$STATE/out" 2> "$STATE/err"
assert_eq   'T-1094-I02 未知（ランタイムなし）: 終了コード 2' "$?" "2"
assert_missing 'T-1094-I02 未知（ランタイムなし）: ランタイムの不在を理由にしない' "$(cat "$STATE/err")" 'nerdctl（Rancher'

# T-1094-I03: SERVICES 未設定 → 全件（後方互換）
given all
run_images K8S_LOCAL_RUNTIME=rancher
assert_eq   'T-1094-I03 未設定: 正常終了する' "$RC" "0"
assert_eq   "T-1094-I03 未設定: MAPPING の全件（${ALL_COUNT} 本）を作る" "$N_BUILT" "$ALL_COUNT"
assert_eq   'T-1094-I03 未設定: MAPPING は 12 本（worker 11 本と opend-auth-gateway）' "$ALL_COUNT" "12"
assert_contains 'T-1094-I03 未設定: opend-auth-gateway も作る' "$BUILT" "$(ref opend-auth-gateway)"
assert_eq   'T-1094-I03 未設定: K8S_BUILT_FILE に全件を書く' "$SUPPLIED" "$ALL_NAMES"

# T-1094-I04: 空の SERVICES は未設定と同じ（全件）
given all
run_images K8S_LOCAL_RUNTIME=rancher SERVICES=
assert_eq   'T-1094-I04 空: 全件を作る' "$N_BUILT" "$ALL_COUNT"

# T-1094-I05: 選択 → 選んだものだけ（空白入りの指定も受ける）
given all
run_images K8S_LOCAL_RUNTIME=rancher 'SERVICES= audit-service , report-service '
assert_eq   'T-1094-I05 選択: 正常終了する' "$RC" "0"
assert_eq   'T-1094-I05 選択: 2 本だけ作る' "$N_BUILT" "2"
assert_contains 'T-1094-I05 選択: audit-service を作る' "$BUILT" "$(ref audit-service)"
assert_contains 'T-1094-I05 選択: report-service を作る' "$BUILT" "$(ref report-service)"
assert_eq   'T-1094-I05 選択: K8S_BUILT_FILE は作った 2 件' "$SUPPLIED" "audit-service
report-service"
assert_contains 'T-1094-I05 選択: 作らなかったものを skip と表示する' "$OUT" "skip $(ref trade-decision-service)"

# T-1094-I06: none → 何も選ばない（全部在れば 0 本）
given all
run_images K8S_LOCAL_RUNTIME=rancher SERVICES=none
assert_eq   'T-1094-I06 none: 正常終了する' "$RC" "0"
assert_eq   'T-1094-I06 none: 何も作らない' "$BUILT" ""
assert_eq   'T-1094-I06 none: K8S_BUILT_FILE は空' "$SUPPLIED" ""

# T-1094-I07: 選ばれていなくても、ランタイムに無いイメージは作る
given "audit-service backtest-service configuration-service cost-control-service information-collection-service market-monitor-service order-execution-service report-service risk-management-service trade-decision-service opend-auth-gateway"
run_images K8S_LOCAL_RUNTIME=rancher SERVICES=audit-service
assert_eq   'T-1094-I07 無ければ作る: 2 本（選択 1＋不在 1）' "$N_BUILT" "2"
assert_contains 'T-1094-I07 無ければ作る: 不在の notification-service を作る' "$BUILT" "$(ref notification-service)"
assert_contains 'T-1094-I07 無ければ作る: 理由を表示する' "$OUT" 'ランタイムに無い'
assert_contains 'T-1094-I07 無ければ作る: K8S_BUILT_FILE に不在分も書く' "$SUPPLIED" 'notification-service'

# T-1094-I08: 初回（何も無い）は none でも全件を作る
given ""
run_images K8S_LOCAL_RUNTIME=rancher SERVICES=none
assert_eq   'T-1094-I08 初回: none でも全件を作る' "$N_BUILT" "$ALL_COUNT"

printf '\nk8s-local-images.sh: k3d 経路（docker と k3d ノードの両方を見る）\n'

# T-1094-I09: docker にもノードにも在る → 選んだものだけ作って import
given all all
run_images K8S_LOCAL_RUNTIME=k3d SERVICES=audit-service
assert_eq   'T-1094-I09 k3d: 1 本だけ作る' "$N_BUILT" "1"
assert_eq   'T-1094-I09 k3d: 作ったものだけ import する' "$IMPORTED" "$(ref audit-service)"
assert_eq   'T-1094-I09 k3d: K8S_BUILT_FILE は 1 件' "$SUPPLIED" "audit-service"
assert_contains 'T-1094-I09 k3d: ノードの確認にクラスタ名を使う' "$(cat "$STATE/exec.log")" 'k3d-test-cluster-server-0'

# T-1094-I10: docker に在るがノードに無い（クラスタを作り直した）→ 作らずに import だけ
given all "audit-service backtest-service configuration-service cost-control-service information-collection-service market-monitor-service notification-service order-execution-service report-service risk-management-service opend-auth-gateway"
run_images K8S_LOCAL_RUNTIME=k3d SERVICES=none
assert_eq   'T-1094-I10 k3d ノード不在: 作らない' "$BUILT" ""
assert_eq   'T-1094-I10 k3d ノード不在: import する' "$IMPORTED" "$(ref trade-decision-service)"
assert_eq   'T-1094-I10 k3d ノード不在: K8S_BUILT_FILE に書く（restart の対象）' "$SUPPLIED" "trade-decision-service"

# T-1094-I11: docker に無い → 作って import
given "audit-service" all
run_images K8S_LOCAL_RUNTIME=k3d SERVICES=audit-service
assert_eq   'T-1094-I11 k3d docker 不在: 全件を作る（選択 1＋不在 11）' "$N_BUILT" "$ALL_COUNT"

# T-1094-I12: ノードの確認自体が失敗 → import する側へ倒す（作りはしない）
given all broken
run_images K8S_LOCAL_RUNTIME=k3d SERVICES=none
assert_eq   'T-1094-I12 k3d 確認失敗: 作らない' "$BUILT" ""
assert_eq   'T-1094-I12 k3d 確認失敗: 全件を import する' "$(printf '%s' "$IMPORTED" | grep -c .)" "$ALL_COUNT"

# T-1094-I13: 何も供給しなければ k3d import を呼ばない
given all all
run_images K8S_LOCAL_RUNTIME=k3d SERVICES=none
assert_eq   'T-1094-I13 k3d 供給なし: import を呼ばない' "$IMPORTED" ""
assert_eq   'T-1094-I13 k3d 供給なし: 正常終了する' "$RC" "0"

printf '\nk8s-local-images.sh: source の入口\n'

# T-1094-I14: AST_IMAGES_LIB=1 の source は何も作らない
given ""
env PATH="$STUB_BIN:$PATH" AST_IMAGES_LIB=1 K8S_LOCAL_RUNTIME=rancher \
  bash -c '. "$1/scripts/k8s-local-images.sh"; echo sourced' _ "$ROOT_DIR" > "$STATE/out" 2>&1
assert_eq   'T-1094-I14 source: 何も作らない' "$(cat "$STATE/built.log" 2>/dev/null || true)" ""
assert_contains 'T-1094-I14 source: 呼び出し元へ戻る' "$(cat "$STATE/out")" 'sourced'

printf '\n%d passed, %d failed\n' "$PASSED" "$FAILED"
[ "$FAILED" -eq 0 ] || exit 1
