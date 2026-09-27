using Grpc.Core;
using Microsoft.Extensions.Logging;
using NotificationService.Features.Notifications;
using RiskProto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace NotificationService.Infrastructure.ExternalServices;

// NFR, FR-20, FR-14, UC-06, MSP:ADR-0029, ADR-0047 決定 1・2, IADR-0284 決定 5（段 5）, IADR-0449 決定 4, IADR-0450 決定 4, #753:
// 段階ゲートの 2 つ目の実装。現況の照会は `RiskControlsRead/GetStageGate`（段 5 の前半で項目を足した）、段階遷移・撤退評価は
// `RiskControlsOwnerWrite/RequestStageTransition`・`EvaluateWithdrawal`（段 5 の後半）。`RiskManagement:Grpc` を宣言したときだけ選ばれる。
// 🔴 表示の整形と Stage 1 の警告は REST と同じ 1 つ（`HttpStageGateController.ToStatusResult`・`ToTransitionResult`・`FormatWithdrawal`）。
// 🔴 書き込みは**再試行しない**（2 回目の遷移は「現段階の指定」で受理不能になり、遷移できていたのに失敗に見える）。時間切れは「状態は不明」。
// 失敗しても REST へ落とさない。承認者は本文の on_behalf_of で運ぶ（ADR-0047 決定 1）。
public sealed class GrpcStageGateController(
    RiskManagementGrpcTransport transport,
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

    public async Task<StageTransitionCommandResult> RequestTransitionAsync(
        int targetStage, string onBehalfOf, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(onBehalfOf);

        var request = new RiskProto.StageTransitionApprovalRequest
        {
            TargetStage = NotificationGrpcWire.ToProtoStage(targetStage),
            OnBehalfOf = onBehalfOf,
        };
        var outcome = await transport.Calls.CallOnceAsync(
            "段階遷移", options => transport.OwnerWrite.RequestStageTransitionAsync(request, options), cancellationToken).ConfigureAwait(false);

        if (outcome.Response is not { } response)
        {
            return outcome.Status switch
            {
                // REST の 400（不正な targetStage・代理の値域外・承認者を特定できない）: Risk の説明をそのまま返す（直し方が分かるように）。
                StatusCode.InvalidArgument => new StageTransitionCommandResult(
                    false, false, NotificationGrpcCalls.ProviderReason(outcome) ?? HttpStageGateController.TransitionNotAcceptedMessage),
                StatusCode.DeadlineExceeded => new StageTransitionCommandResult(false, false, HttpStageGateController.TransitionTimedOutMessage),
                _ => new StageTransitionCommandResult(false, false, NotificationGrpcCalls.FailureMessage("段階遷移", outcome.Status)),
            };
        }

        if (NotificationGrpcWire.ToTransition(response) is not { } result)
        {
            logger.LogWarning("段階遷移の応答を解釈できませんでした（gRPC）。");
            return new StageTransitionCommandResult(false, false, HttpStageGateController.TransitionUnparsableMessage);
        }

        return HttpStageGateController.ToTransitionResult(result.Accepted, result.ToStage, result.RejectionReasons, result.Criteria, targetStage);
    }

    public async Task<StageGateStatusResult> EvaluateWithdrawalAsync(CancellationToken cancellationToken = default)
    {
        var outcome = await transport.Calls.CallOnceAsync(
            "撤退評価",
            options => transport.OwnerWrite.EvaluateWithdrawalAsync(new RiskProto.WithdrawalEvaluationRequest(), options),
            cancellationToken).ConfigureAwait(false);

        if (outcome.Response is not { } response)
        {
            return outcome.Status == StatusCode.DeadlineExceeded
                ? new StageGateStatusResult(false, HttpStageGateController.WithdrawalTimedOutMessage)
                : new StageGateStatusResult(false, NotificationGrpcCalls.FailureMessage("撤退評価", outcome.Status));
        }

        if (NotificationGrpcWire.ToWithdrawalView(response.Assessment) is not { } view)
        {
            logger.LogWarning("撤退評価の応答を解釈できませんでした（gRPC）。");
            return new StageGateStatusResult(false, HttpStageGateController.WithdrawalUnparsableMessage);
        }

        return new StageGateStatusResult(true, HttpStageGateController.FormatWithdrawal(view));
    }
}
