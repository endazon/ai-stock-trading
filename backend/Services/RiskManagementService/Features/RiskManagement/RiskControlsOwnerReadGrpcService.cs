using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using RiskManagementService.Features.RiskManagement.GetRiskStatus;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace RiskManagementService.Features.RiskManagement;

// NFR, NFR-06, FR-10, FR-14, MSP:ADR-0029, MSP:ADR-0075, ADR-0047 決定 1〜3, IADR-0284 決定 5（段 5）, IADR-0449 決定 2・3, #753:
// リスク管理の**所有者限定の読み取り**の gRPC 面（呼び出し元は Discord ボットだけ）。REST の `GET /risk-controls/status`
// （GetRiskStatusEndpoint）と**同じ** `RiskStatusService.Build()` を呼ぶ —— 評価器を 2 つにしない。
//
// 🔴 門は **`GrpcOwnerOnly`**（trading-owner ∧ azp がボットの機密クライアント）。REST の OwnerOnly と同じく s2s には開かない
// （当日損益・建玉を束ねた利用者向けの要約でありサービスに開く用途が無い。GetRiskStatusEndpoint の注記）。人の利用者のトークンも通さない
// （ADR-0047 決定 3。人の利用者は BFF が中継する REST で読む）。
// 🔴 **並走中の正は REST である**（MSP:ADR-0029 の 2026-08-04 追記）。REST 面は変えていない。
[Authorize(Policy = AiStockTradingAuthPolicies.GrpcOwnerOnly)]
public sealed class RiskControlsOwnerReadGrpcService(RiskStatusService status) : Proto.RiskControlsOwnerRead.RiskControlsOwnerReadBase
{
    public override Task<Proto.GetRiskStatusResponse> GetRiskStatus(Proto.GetRiskStatusRequest request, ServerCallContext context) =>
        Task.FromResult(RiskReadWireMapping.ToProto(status.Build()));
}
