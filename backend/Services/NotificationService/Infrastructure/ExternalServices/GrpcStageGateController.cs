using Microsoft.Extensions.Logging;
using NotificationService.Features.Notifications;
using RiskProto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace NotificationService.Infrastructure.ExternalServices;

// NFR, FR-20, FR-14, UC-06, MSP:ADR-0029, ADR-0047 決定 1・2, IADR-0284 決定 5（段 5）, IADR-0449 決定 4, #753:
// 段階ゲートの 2 つ目の実装。**現況の照会（読み取り）だけ**を gRPC（`RiskControlsRead/GetStageGate`。段 5 で項目を足した）で行い、
// 段階遷移・撤退評価（書き込み）は REST の実装（HttpStageGateController）へ委ねる（段 5 の後半で移す）。`RiskManagement:Grpc` を宣言したときだけ選ばれる。
// 🔴 表示の整形と Stage 1 の警告は REST と同じ 1 つ（`HttpStageGateController.ToStatusResult`）。
public sealed class GrpcStageGateController(
    RiskManagementGrpcTransport transport,
    IStageGateController rest,
    ILogger<GrpcStageGateController> logger)
    : IStageGateController
{
    private const string Operation = "段階ゲートの照会";

    public async Task<StageGateStatusResult> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var outcome = await transport.Calls.CallAsync(
            Operation,
            options => transport.Read.GetStageGateAsync(new RiskProto.GetStageGateRequest(), options),
            cancellationToken).ConfigureAwait(false);

        if (outcome.Response is not { } response)
            return new StageGateStatusResult(false, NotificationGrpcCalls.FailureMessage(Operation, outcome.Status));

        if (NotificationGrpcWire.ToStageGateView(response) is not { } view)
        {
            logger.LogWarning("段階ゲートの応答を解釈できませんでした（gRPC）。");
            return new StageGateStatusResult(false, "段階ゲートの応答を解釈できませんでした");
        }

        return HttpStageGateController.ToStatusResult(view);
    }

    public Task<StageTransitionCommandResult> RequestTransitionAsync(
        int targetStage, string onBehalfOf, CancellationToken cancellationToken = default) =>
        rest.RequestTransitionAsync(targetStage, onBehalfOf, cancellationToken);

    public Task<StageGateStatusResult> EvaluateWithdrawalAsync(CancellationToken cancellationToken = default) =>
        rest.EvaluateWithdrawalAsync(cancellationToken);
}
