using AiStockTrading.Shared.Kernel.Trading;
using ReportService.Features.Reports;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace ReportService.Infrastructure.ExternalServices;

// NFR, FR-06, FR-15, FR-20, MSP:ADR-0029, IADR-0284 決定 5（段 2）, IADR-0271, IADR-0427, #997 (#753):
// 現在の運用段階を **gRPC 生成クライアント**（`RiskControlsRead/GetStageGate`）で照会する `IStageProgressSource` の
// 2 つ目の実装。REST 実装（HttpStageProgressSource）と並走する（**既定は REST**）。
//
// 🔴 **倒す向きは REST と同じ null（未供給）**。誤った既定（Stage 0）は三者比較の読み方を反転させる。
// 🔴 **原則 A**: 未指定・未知の段階は null（未供給）。proto の 0 は「未指定」であり Stage 0 ではない
//   （C# の `Stage0Verification` は 0 だが、線上では 1。IADR-0427 決定 3）。
public sealed class GrpcStageProgressSource(RiskManagementGrpcTransport transport, ILogger<GrpcStageProgressSource> logger)
    : IStageProgressSource
{
    public async Task<TradingStage?> GetCurrentStageAsync(CancellationToken cancellationToken = default)
    {
        var response = await transport.CallAsync(
            "運用段階",
            "**未供給として扱います**（Stage 0 とは書きません）。",
            (client, options) => client.GetStageGateAsync(new Proto.GetStageGateRequest(), options),
            cancellationToken).ConfigureAwait(false);
        if (response is null)
            return null;

        return HttpStageProgressSource.Interpret(RiskManagementWire.Stage(response.CurrentStage), logger);
    }
}
