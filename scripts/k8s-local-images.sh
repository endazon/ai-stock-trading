#!/usr/bin/env bash
# #122 / IADR-0052: AST 10 Worker のイメージをローカルビルドし k3s へ供給する。
# 単一 Dockerfile を SERVICE_PROJECT/SERVICE_DLL で切替（compose と同一）。
# ランタイム自動判定（Rancher Desktop 内蔵 k3s / docker+k3d）。タグ規則は chart values と一致
# （global.image.registry=k3d-local, tag=latest）。
#
#   scripts/k8s-local-images.sh [cluster-name]      # cluster は k3d 経路でのみ使用
#   K8S_LOCAL_RUNTIME=rancher|k3d で明示指定可（既定 auto）。
#
# #1094 / IADR-0457: 作り直す対象を絞れる（既定は従来どおり全件）。
#   SERVICES=a,b          MAPPING の名前で指定（空白可）。**未知の名前は何も作らずに exit 2**。
#                         未設定・空＝全件。`none`＝選択なし（下の「無ければ作る」だけが働く）。
#   K8S_BUILT_FILE=<path> ランタイムへ新たに供給した（作った・import した）名前を 1 行 1 名で書く
#                         （開始時に空にする。k8s-local-deploy.sh が restart の絞り込みに使う）。
#   🔴 **選ばれていなくても、ランタイムに :latest が無いイメージは作る**（初回・作り直したクラスタへの安全策）。
#     rancher: `nerdctl --namespace k8s.io image inspect`（ビルド先がそのまま k3s の containerd）。
#     k3d: 作るかは `docker image inspect`、import するかはノードの `crictl inspecti` で分けて見る。docker に
#       残っていてもクラスタを作り直すとノードからは消えるため。ノードの確認が失敗したら import する側へ倒す
#       （import は作り直しより桁で安い）。
#   変わったサービスを git の差分から選ぶのは k8s-local-deploy.sh --changed-since（scripts/select-changed-services.js）。
#   AST_IMAGES_LIB=1 で source すると関数定義だけを読む（scripts/k8s-local-images.test.sh 用）。
set -euo pipefail
PREFIX="k3d-local"
TAG="latest"
AST_IMAGES_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# service-name : SERVICE_PROJECT : SERVICE_DLL（compose の build args と一致）
MAPPING=(
  "audit-service|backend/Services/AuditService/AuditService.csproj|AuditService.dll"
  "backtest-service|backend/Services/BacktestService/BacktestService.csproj|BacktestService.dll"
  "configuration-service|backend/Services/ConfigurationService/ConfigurationService.csproj|ConfigurationService.dll"
  "cost-control-service|backend/Services/CostControlService/CostControlService.csproj|CostControlService.dll"
  "information-collection-service|backend/Services/InformationCollectionService/InformationCollectionService.csproj|InformationCollectionService.dll"
  "market-monitor-service|backend/Services/MarketMonitorService/MarketMonitorService.csproj|MarketMonitorService.dll"
  "notification-service|backend/Services/NotificationService/NotificationService.csproj|NotificationService.dll"
  "order-execution-service|backend/Services/OrderExecutionService/OrderExecutionService.csproj|OrderExecutionService.dll"
  "report-service|backend/Services/ReportService/ReportService.csproj|ReportService.dll"
  "risk-management-service|backend/Services/RiskManagementService/RiskManagementService.csproj|RiskManagementService.dll"
  "trade-decision-service|backend/Services/TradeDecisionService/TradeDecisionService.csproj|TradeDecisionService.dll"
  # #722 / IADR-0320: OpenD Pod のサイドカー（検証コードの投入面）。Worker ではないが、Web SDK・
  # :8080 待受という点は同型なので共有 Dockerfile で足りる。opend.authGateway.enabled=true と
  # deploy/opend/k8s/opend.yaml がこのイメージを参照する。
  "opend-auth-gateway|backend/Services/OpendAuthGateway/OpendAuthGateway.csproj|OpendAuthGateway.dll"
)

# MAPPING の名前を 1 行 1 つで出す。
ast_images_names() {
  local entry
  for entry in "${MAPPING[@]}"; do printf '%s\n' "${entry%%|*}"; done
}

# SERVICES を検証し、選ばれた名前を AST_IMAGES_SELECTED（改行区切り）へ入れる。
# AST_IMAGES_SELECT_ALL=1 は「全件」（未設定・空）。未知の名前は stderr に挙げて 2 を返す。
ast_images_resolve_selection() {
  local raw="${SERVICES:-}" item unknown='' names
  AST_IMAGES_SELECTED=''
  AST_IMAGES_SELECT_ALL=0
  raw="$(printf '%s' "$raw" | tr -d '[:space:]')"
  if [ -z "$raw" ]; then
    AST_IMAGES_SELECT_ALL=1
    return 0
  fi
  [ "$raw" = "none" ] && return 0
  names="$(ast_images_names)"
  local IFS=','
  for item in $raw; do
    [ -z "$item" ] && continue
    if grep -Fxq -- "$item" <<< "$names"; then
      AST_IMAGES_SELECTED="${AST_IMAGES_SELECTED}${item}
"
    else
      unknown="${unknown} ${item}"
    fi
  done
  if [ -n "$unknown" ]; then
    {
      echo "ERROR: SERVICES に MAPPING に無い名前がある:${unknown}"
      echo "       指定できる名前（scripts/k8s-local-images.sh の MAPPING）:"
      printf '%s\n' "$names" | sed 's/^/         /'
      echo "       何も作らずに中断した。#1094"
    } >&2
    return 2
  fi
  return 0
}

ast_images_is_selected() {
  [ "$AST_IMAGES_SELECT_ALL" = "1" ] && return 0
  # here-string で渡す（`printf | grep -q` は grep の早期終了で printf が SIGPIPE になり、pipefail 下で一致を偽と読む）。
  grep -Fxq -- "$1" <<< "$AST_IMAGES_SELECTED"
}

# ランタイムにイメージが在るか（在れば 0）。k3d はノードの有無を別に見る（ast_image_on_k3d_node）。
ast_image_present() {
  if [ "$RUNTIME" = "rancher" ]; then
    nerdctl --namespace k8s.io image inspect "$1" >/dev/null 2>&1
  else
    docker image inspect "$1" >/dev/null 2>&1
  fi
}

# k3d のサーバノードの containerd にイメージが在るか（在れば 0）。確認自体が失敗しても 1（＝import する側へ倒す）。
ast_image_on_k3d_node() {
  docker exec "k3d-${CLUSTER}-server-0" crictl inspecti "$1" >/dev/null 2>&1
}

ast_images_main() {
  CLUSTER="${1:-msp-ast-dev}"
  cd "$AST_IMAGES_ROOT"

  # 未知の名前はランタイム判定より前に止める（何も作らない・ランタイムが無くても 2）。
  ast_images_resolve_selection || return $?

  RUNTIME="${K8S_LOCAL_RUNTIME:-auto}"
  if [ "$RUNTIME" = "auto" ]; then
    if command -v nerdctl >/dev/null 2>&1; then RUNTIME="rancher";
    elif command -v k3d >/dev/null 2>&1 && command -v docker >/dev/null 2>&1; then RUNTIME="k3d";
    else echo "ERROR: nerdctl（Rancher Desktop/containerd）か docker+k3d が必要です。" >&2; return 1; fi
  fi
  echo "==> runtime: $RUNTIME"
  if [ "$AST_IMAGES_SELECT_ALL" = "1" ]; then
    echo "==> 対象: 全件（SERVICES 未指定）"
  else
    echo "==> 対象: ${SERVICES}（選ばれていなくてもランタイムに無いイメージは作る）"
  fi
  [ -n "${K8S_BUILT_FILE:-}" ] && : > "$K8S_BUILT_FILE"

  local entry name project dll ref reason n_built=0 n_skipped=0
  local k3d_images=() k3d_names=()
  for entry in "${MAPPING[@]}"; do
    IFS='|' read -r name project dll <<< "$entry"
    ref="${PREFIX}/ai-stock-trading/${name}:${TAG}"
    reason=''
    if ast_images_is_selected "$name"; then
      reason='選択'
    elif ! ast_image_present "$ref"; then
      reason='ランタイムに無い'
    fi

    if [ -n "$reason" ]; then
      echo "==> build ${ref}（${reason}）"
      if [ "$RUNTIME" = "rancher" ]; then
        nerdctl --namespace k8s.io build -f backend/Dockerfile \
          --build-arg "SERVICE_PROJECT=${project}" --build-arg "SERVICE_DLL=${dll}" -t "${ref}" .
      else
        docker build -f backend/Dockerfile \
          --build-arg "SERVICE_PROJECT=${project}" --build-arg "SERVICE_DLL=${dll}" -t "${ref}" .
      fi
      n_built=$((n_built + 1))
    elif [ "$RUNTIME" = "k3d" ] && ! ast_image_on_k3d_node "$ref"; then
      echo "==> import only ${ref}（docker に在り、クラスタのノードに無い）"
    else
      echo "==> skip ${ref}（選ばれていない・ランタイムに在る）"
      n_skipped=$((n_skipped + 1))
      continue
    fi

    if [ "$RUNTIME" = "k3d" ]; then
      k3d_images+=("${ref}")
      k3d_names+=("${name}")
    elif [ -n "${K8S_BUILT_FILE:-}" ]; then
      printf '%s\n' "$name" >> "$K8S_BUILT_FILE"
    fi
  done

  if [ "$RUNTIME" = "k3d" ] && [ "${#k3d_images[@]}" -gt 0 ]; then
    echo "==> k3d image import (${#k3d_images[@]}) -> ${CLUSTER}"
    k3d image import "${k3d_images[@]}" -c "${CLUSTER}"
    [ -n "${K8S_BUILT_FILE:-}" ] && printf '%s\n' "${k3d_names[@]}" >> "$K8S_BUILT_FILE"
  fi
  echo "done.（build ${n_built} 件 / skip ${n_skipped} 件）"
}

# scripts/k8s-local-images.test.sh から関数だけを読み込むための入口（ビルドは実行しない）。
if [ "${AST_IMAGES_LIB:-}" = "1" ]; then
  return 0 2>/dev/null || exit 0
fi

ast_images_main "$@"
