using AiStockTrading.Shared.Contracts.Trading;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using NotificationService.Features.Notifications;
using RiskProto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace NotificationService.Infrastructure.ExternalServices;

// NFR, FR-10, FR-11, FR-14, UC-06, ADR-0041 決定 4, MSP:ADR-0029, ADR-0047 決定 1・2, IADR-0284 決定 5（段 5）, IADR-0450 決定 4, #753:
// 台帳とブローカーの乖離の取り込みの 2 つ目の実装（`RiskControlsOwnerWrite/AdoptPositionDrift`）。`RiskManagement:Grpc` を宣言したときだけ選ばれる。
// ボットの呼び出しは east-west なので gRPC へ移す（REST の端点は ADR-0041 決定 4 の人の窓口として段 6 でも残る）。
// 操作者は本文の on_behalf_of で運ぶ（ADR-0047 決定 1。提供側は信頼クライアントのトークンに限って採る）。
// REST の 422（受理不能）＝ FAILED_PRECONDITION は Succeeded=true・Adopted=false、400 ＝ INVALID_ARGUMENT は失敗。どちらも理由の文言をそのまま返す。
// 🔴 書き込みは**再試行しない**。時間切れは「台帳が変わったかは不明」。REST へ落とさない。
public sealed class GrpcPositionDriftAdoptionController(
    RiskManagementGrpcTransport transport,
    ILogger<GrpcPositionDriftAdoptionController> logger)
    : IPositionDriftAdoptionController
{
    private const string Operation = "乖離の取り込み";

    public async Task<PositionDriftAdoptionResult> AdoptAsync(
        string symbol, Market market, string reason, string onBehalfOf, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(onBehalfOf);

        var request = new RiskProto.DriftAdoptionCommandRequest
        {
            Symbol = symbol,
            Market = NotificationGrpcWire.ToRiskMarket(market),
            Reason = reason,
            OnBehalfOf = onBehalfOf,
        };
        var outcome = await transport.Calls.CallOnceAsync(
            Operation, options => transport.OwnerWrite.AdoptPositionDriftAsync(request, options), cancellationToken).ConfigureAwait(false);

        if (outcome.Response is not { } response)
        {
            var reason2 = NotificationGrpcCalls.ProviderReason(outcome) ?? HttpPositionDriftAdoptionController.NoReasonMessage;
            return outcome.Status switch
            {
                StatusCode.FailedPrecondition =>
                    new PositionDriftAdoptionResult(true, false, $"{HttpPositionDriftAdoptionController.NotAdopted}: {reason2}"),
                StatusCode.InvalidArgument =>
                    new PositionDriftAdoptionResult(false, false, $"{HttpPositionDriftAdoptionController.NotAdopted}: {reason2}"),
                StatusCode.DeadlineExceeded => new PositionDriftAdoptionResult(false, false, HttpPositionDriftAdoptionController.TimedOutMessage),
                _ => new PositionDriftAdoptionResult(false, false, NotificationGrpcCalls.FailureMessage(Operation, outcome.Status)),
            };
        }

        if (NotificationGrpcWire.ToAdoptionView(response) is not { } view)
        {
            logger.LogWarning("乖離の取り込みの応答を解釈できませんでした（gRPC）。");
            return new PositionDriftAdoptionResult(false, false, HttpPositionDriftAdoptionController.UnparsableMessage);
        }

        return new PositionDriftAdoptionResult(true, true, HttpPositionDriftAdoptionController.FormatAdopted(view));
    }
}
