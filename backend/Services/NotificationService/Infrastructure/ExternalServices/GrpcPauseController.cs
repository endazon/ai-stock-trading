using Microsoft.Extensions.Logging;
using NotificationService.Features.Notifications;
using RiskProto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace NotificationService.Infrastructure.ExternalServices;

// NFR, FR-10, FR-14, UC-07, MSP:ADR-0029, ADR-0047 決定 1・2, IADR-0284 決定 5（段 5）, IADR-0449 決定 4, #753:
// 一時停止/再開・稼働状態の照会の 2 つ目の実装。**稼働状態の照会（読み取り）だけ**を gRPC（`RiskControlsOwnerRead/GetRiskStatus`）で行い、
// 一時停止/再開（書き込み）は REST の実装（HttpPauseController）へ委ねる（段 5 の後半で移す）。`RiskManagement:Grpc` を宣言したときだけ選ばれる。
// 🔴 表示の整形は REST と同じ 1 つ（`HttpPauseController.Format`）。失敗を成功に見せない。
public sealed class GrpcPauseController(
    RiskManagementGrpcTransport transport,
    IPauseController rest,
    ILogger<GrpcPauseController> logger)
    : IPauseController
{
    private const string Operation = "稼働状態の照会";

    public Task<PauseResult> PauseAsync(string reason, CancellationToken cancellationToken = default) =>
        rest.PauseAsync(reason, cancellationToken);

    public Task<PauseResult> ResumeAsync(string reason, CancellationToken cancellationToken = default) =>
        rest.ResumeAsync(reason, cancellationToken);

    public async Task<RiskStatusResult> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var outcome = await transport.Calls.CallAsync(
            Operation,
            options => transport.OwnerRead.GetRiskStatusAsync(new RiskProto.GetRiskStatusRequest(), options),
            cancellationToken).ConfigureAwait(false);

        if (outcome.Response is not { } response)
            return new RiskStatusResult(false, NotificationGrpcCalls.FailureMessage(Operation, outcome.Status));

        if (NotificationGrpcWire.ToRiskStatusView(response) is not { } view)
        {
            logger.LogWarning("稼働状態の応答を解釈できませんでした（gRPC）。");
            return new RiskStatusResult(false, "稼働状態の応答を解釈できませんでした");
        }

        return new RiskStatusResult(true, HttpPauseController.Format(view));
    }
}
