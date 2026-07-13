#!/usr/bin/env bash
# #122 / IADR-0051: AST 10 Worker のイメージをローカルビルドし k3d へ import する。
# 単一 Dockerfile を SERVICE_PROJECT/SERVICE_DLL で切替（compose と同一）。タグ規則は
# chart values（global.image.registry=k3d-local, tag=latest）と一致させる。
#
#   scripts/k8s-local-images.sh [cluster-name]     # 既定 msp-ast-dev
set -euo pipefail
CLUSTER="${1:-msp-ast-dev}"
PREFIX="k3d-local"
TAG="latest"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

# service-name : SERVICE_PROJECT : SERVICE_DLL（compose の build args と一致）
MAPPING=(
  "audit-service|backend/Services/AuditService/src/AuditService.Worker/AuditService.Worker.csproj|AuditService.Worker.dll"
  "configuration-service|backend/Services/ConfigurationService/src/ConfigurationService.Worker/ConfigurationService.Worker.csproj|ConfigurationService.Worker.dll"
  "cost-control-service|backend/Services/CostControlService/src/CostControlService.Worker/CostControlService.Worker.csproj|CostControlService.Worker.dll"
  "information-collection-service|backend/Services/InformationCollectionService/src/InformationCollectionService.Worker/InformationCollectionService.Worker.csproj|InformationCollectionService.Worker.dll"
  "market-monitor-service|backend/Services/MarketMonitorService/src/MarketMonitorService.Worker/MarketMonitorService.Worker.csproj|MarketMonitorService.Worker.dll"
  "notification-service|backend/Services/NotificationService/src/NotificationService.Worker/NotificationService.Worker.csproj|NotificationService.Worker.dll"
  "order-execution-service|backend/Services/OrderExecutionService/src/OrderExecutionService.Worker/OrderExecutionService.Worker.csproj|OrderExecutionService.Worker.dll"
  "report-service|backend/Services/ReportService/src/ReportService.Worker/ReportService.Worker.csproj|ReportService.Worker.dll"
  "risk-management-service|backend/Services/RiskManagementService/src/RiskManagementService.Worker/RiskManagementService.Worker.csproj|RiskManagementService.Worker.dll"
  "trade-decision-service|backend/Services/TradeDecisionService/src/TradeDecisionService.Worker/TradeDecisionService.Worker.csproj|TradeDecisionService.Worker.dll"
)

images=()
for entry in "${MAPPING[@]}"; do
  IFS='|' read -r name project dll <<< "$entry"
  ref="${PREFIX}/ai-stock-trading/${name}:${TAG}"
  echo "==> build ${ref}"
  docker build -f backend/Dockerfile \
    --build-arg "SERVICE_PROJECT=${project}" \
    --build-arg "SERVICE_DLL=${dll}" \
    -t "${ref}" .
  images+=("${ref}")
done

echo "==> k3d image import (${#images[@]}) -> ${CLUSTER}"
k3d image import "${images[@]}" -c "${CLUSTER}"
echo "done."
