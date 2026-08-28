#!/usr/bin/env bash
# #122 / IADR-0052: AST 10 Worker のイメージをローカルビルドし k3s へ供給する。
# 単一 Dockerfile を SERVICE_PROJECT/SERVICE_DLL で切替（compose と同一）。
# ランタイム自動判定（Rancher Desktop 内蔵 k3s / docker+k3d）。タグ規則は chart values と一致
# （global.image.registry=k3d-local, tag=latest）。
#
#   scripts/k8s-local-images.sh [cluster-name]      # cluster は k3d 経路でのみ使用
#   K8S_LOCAL_RUNTIME=rancher|k3d で明示指定可（既定 auto）。
set -euo pipefail
CLUSTER="${1:-msp-ast-dev}"
PREFIX="k3d-local"
TAG="latest"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

RUNTIME="${K8S_LOCAL_RUNTIME:-auto}"
if [ "$RUNTIME" = "auto" ]; then
  if command -v nerdctl >/dev/null 2>&1; then RUNTIME="rancher";
  elif command -v k3d >/dev/null 2>&1 && command -v docker >/dev/null 2>&1; then RUNTIME="k3d";
  else echo "ERROR: nerdctl（Rancher Desktop/containerd）か docker+k3d が必要です。" >&2; exit 1; fi
fi
echo "==> runtime: $RUNTIME"

# service-name : SERVICE_PROJECT : SERVICE_DLL（compose の build args と一致）
MAPPING=(
  "audit-service|backend/Services/AuditService/AuditService.csproj|AuditService.dll"
  "backtest-service|backend/Services/BacktestService/src/BacktestService.Api/BacktestService.Api.csproj|BacktestService.Api.dll"
  "configuration-service|backend/Services/ConfigurationService/src/ConfigurationService.Api/ConfigurationService.Api.csproj|ConfigurationService.Api.dll"
  "cost-control-service|backend/Services/CostControlService/src/CostControlService.Api/CostControlService.Api.csproj|CostControlService.Api.dll"
  "information-collection-service|backend/Services/InformationCollectionService/src/InformationCollectionService.Api/InformationCollectionService.Api.csproj|InformationCollectionService.Api.dll"
  "market-monitor-service|backend/Services/MarketMonitorService/src/MarketMonitorService.Api/MarketMonitorService.Api.csproj|MarketMonitorService.Api.dll"
  "notification-service|backend/Services/NotificationService/src/NotificationService.Api/NotificationService.Api.csproj|NotificationService.Api.dll"
  "order-execution-service|backend/Services/OrderExecutionService/src/OrderExecutionService.Api/OrderExecutionService.Api.csproj|OrderExecutionService.Api.dll"
  "report-service|backend/Services/ReportService/src/ReportService.Api/ReportService.Api.csproj|ReportService.Api.dll"
  "risk-management-service|backend/Services/RiskManagementService/src/RiskManagementService.Api/RiskManagementService.Api.csproj|RiskManagementService.Api.dll"
  "trade-decision-service|backend/Services/TradeDecisionService/src/TradeDecisionService.Api/TradeDecisionService.Api.csproj|TradeDecisionService.Api.dll"
)

k3d_images=()
for entry in "${MAPPING[@]}"; do
  IFS='|' read -r name project dll <<< "$entry"
  ref="${PREFIX}/ai-stock-trading/${name}:${TAG}"
  echo "==> build ${ref}"
  if [ "$RUNTIME" = "rancher" ]; then
    nerdctl --namespace k8s.io build -f backend/Dockerfile \
      --build-arg "SERVICE_PROJECT=${project}" --build-arg "SERVICE_DLL=${dll}" -t "${ref}" .
  else
    docker build -f backend/Dockerfile \
      --build-arg "SERVICE_PROJECT=${project}" --build-arg "SERVICE_DLL=${dll}" -t "${ref}" .
    k3d_images+=("${ref}")
  fi
done

if [ "$RUNTIME" = "k3d" ]; then
  echo "==> k3d image import (${#k3d_images[@]}) -> ${CLUSTER}"
  k3d image import "${k3d_images[@]}" -c "${CLUSTER}"
fi
echo "done."
