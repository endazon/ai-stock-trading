using Grpc.Core;
using Microsoft.Extensions.Logging;
using NotificationService.Features.Notifications;
using RiskProto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace NotificationService.Infrastructure.ExternalServices;

// NFR, FR-19, FR-10, FR-14, UC-06, ADR-0028 決定2/決定3, MSP:ADR-0029, ADR-0047 決定 1・2, IADR-0284 決定 5（段 5）, IADR-0450 決定 4, #753:
// GFV 違反による停止の解除の 2 つ目の実装（`RiskControlsOwnerWrite/ClearGoodFaithViolations`）。`RiskManagement:Grpc` を宣言したときだけ選ばれる。
// REST の 422（解除対象なし）＝ FAILED_PRECONDITION・400（理由の欠如）＝ INVALID_ARGUMENT は「Risk は明確に応答した」（Succeeded=true・Cleared=false）。
// 🔴 書き込みは**再試行しない**（2 回目は「解除対象なし」になり、解除できていたのに失敗に見える）。時間切れは「状態は不明」。REST へ落とさない。
public sealed class GrpcGoodFaithViolationController(
    RiskManagementGrpcTransport transport,
    ILogger<GrpcGoodFaithViolationController> logger)
    : IGoodFaithViolationController
{
    private const string Operation = "GFV 解除";

    public async Task<GoodFaithViolationClearResult> ClearAsync(string reason, CancellationToken cancellationToken = default)
    {
        var outcome = await transport.Calls.CallOnceAsync(
            Operation,
            options => transport.OwnerWrite.ClearGoodFaithViolationsAsync(new RiskProto.GoodFaithViolationClearanceRequest { Reason = reason }, options),
            cancellationToken).ConfigureAwait(false);

        if (outcome.Response is not { } response)
        {
            return outcome.Status switch
            {
                StatusCode.FailedPrecondition or StatusCode.InvalidArgument => new GoodFaithViolationClearResult(
                    true, false, NotificationGrpcCalls.ProviderReason(outcome) ?? HttpGoodFaithViolationController.NotAcceptedMessage),
                StatusCode.DeadlineExceeded => new GoodFaithViolationClearResult(false, false, HttpGoodFaithViolationController.TimedOutMessage),
                _ => new GoodFaithViolationClearResult(false, false, NotificationGrpcCalls.FailureMessage(Operation, outcome.Status)),
            };
        }

        // 残件数の欠落を 0 と読まない（「停止は解けた」と誤認させない）。
        if (!response.HasRemainingCount)
        {
            logger.LogWarning("GFV 解除の応答を解釈できませんでした（gRPC）。");
            return new GoodFaithViolationClearResult(false, false, HttpGoodFaithViolationController.UnparsableMessage);
        }

        return HttpGoodFaithViolationController.Cleared(response.ClearedOrderIds.Count, response.RemainingCount);
    }
}
