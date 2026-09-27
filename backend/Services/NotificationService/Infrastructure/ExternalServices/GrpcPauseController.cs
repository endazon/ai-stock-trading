using Microsoft.Extensions.Logging;
using NotificationService.Features.Notifications;
using RiskProto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace NotificationService.Infrastructure.ExternalServices;

// NFR, FR-10, FR-14, UC-07, MSP:ADR-0029, ADR-0047 決定 1・2, IADR-0284 決定 5（段 5）, IADR-0449 決定 4, IADR-0450 決定 4, #753:
// 一時停止/再開・稼働状態の照会の 2 つ目の実装。稼働状態の照会は `RiskControlsOwnerRead/GetRiskStatus`（段 5 の前半）、一時停止/再開は
// `RiskControlsOwnerWrite/PauseTrading`・`ResumeTrading`（段 5 の後半）。`RiskManagement:Grpc` を宣言したときだけ選ばれる。
// 🔴 表示の整形・文言は REST と同じ 1 つ（`HttpPauseController`）。失敗を成功に見せない。
// 🔴 書き込みは**再試行しない**。時間切れは「状態は不明」。失敗しても REST へ落とさない。
public sealed class GrpcPauseController(
    RiskManagementGrpcTransport transport,
    ILogger<GrpcPauseController> logger)
    : IPauseController
{
    private const string Operation = "稼働状態の照会";

    public Task<PauseResult> PauseAsync(string reason, CancellationToken cancellationToken = default) =>
        ChangeAsync("一時停止", options => transport.OwnerWrite.PauseTradingAsync(new RiskProto.TradingPauseChangeRequest { Reason = reason }, options),
            cancellationToken);

    public Task<PauseResult> ResumeAsync(string reason, CancellationToken cancellationToken = default) =>
        ChangeAsync("再開", options => transport.OwnerWrite.ResumeTradingAsync(new RiskProto.TradingPauseChangeRequest { Reason = reason }, options),
            cancellationToken);

    private async Task<PauseResult> ChangeAsync(
        string operation,
        Func<Grpc.Core.CallOptions, Grpc.Core.AsyncUnaryCall<RiskProto.TradingPauseChangeResponse>> call,
        CancellationToken cancellationToken)
    {
        var outcome = await transport.Calls.CallOnceAsync($"取引の{operation}", call, cancellationToken).ConfigureAwait(false);

        if (outcome.Response is not { } response)
        {
            return outcome.Status == Grpc.Core.StatusCode.DeadlineExceeded
                ? new PauseResult(false, false, HttpPauseController.TimedOutMessage(operation))
                : new PauseResult(false, false, NotificationGrpcCalls.FailureMessage($"取引の{operation}", outcome.Status));
        }

        if (!response.HasPaused)
        {
            logger.LogWarning("取引の{Operation}の応答を解釈できませんでした（gRPC）。", operation);
            return new PauseResult(false, false, HttpPauseController.UnparsableMessage(operation));
        }

        return new PauseResult(true, response.Paused, HttpPauseController.SucceededMessage(operation));
    }

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
