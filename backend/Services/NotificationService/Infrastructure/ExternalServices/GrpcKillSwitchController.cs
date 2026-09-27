using Microsoft.Extensions.Logging;
using NotificationService.Features.Notifications;
using RiskProto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace NotificationService.Infrastructure.ExternalServices;

// NFR, FR-10, FR-14, UC-06, MSP:ADR-0029, ADR-0047 決定 1・2, IADR-0284 決定 5（段 5）, IADR-0450 決定 4, #753:
// kill switch の 2 つ目の実装（`RiskControlsOwnerWrite/EngageKillSwitch`・`DisengageKillSwitch`）。`RiskManagement:Grpc` を宣言したときだけ選ばれる。
// 🔴 書き込みは**再試行しない**（`CallOnceAsync`）。時間切れは「状態は不明」、提供側が明確に拒否した失敗は「失敗」（REST の非 2xx と同じ）。
// 🔴 失敗しても REST へ落とさない（落とすと「時間切れ＝実は起動済み」を REST で再実行することになる）。文言は REST と同じ 1 つ。
public sealed class GrpcKillSwitchController(
    RiskManagementGrpcTransport transport,
    ILogger<GrpcKillSwitchController> logger)
    : IKillSwitchController
{
    public Task<KillSwitchResult> EngageAsync(string reason, CancellationToken cancellationToken = default) =>
        ChangeAsync("起動", options => transport.OwnerWrite.EngageKillSwitchAsync(new RiskProto.KillSwitchChangeRequest { Reason = reason }, options),
            cancellationToken);

    public Task<KillSwitchResult> DisengageAsync(string reason, CancellationToken cancellationToken = default) =>
        ChangeAsync("解除", options => transport.OwnerWrite.DisengageKillSwitchAsync(new RiskProto.KillSwitchChangeRequest { Reason = reason }, options),
            cancellationToken);

    private async Task<KillSwitchResult> ChangeAsync(
        string operation,
        Func<Grpc.Core.CallOptions, Grpc.Core.AsyncUnaryCall<RiskProto.KillSwitchChangeResponse>> call,
        CancellationToken cancellationToken)
    {
        var outcome = await transport.Calls.CallOnceAsync($"kill switch の{operation}", call, cancellationToken).ConfigureAwait(false);

        if (outcome.Response is not { } response)
        {
            return outcome.Status == Grpc.Core.StatusCode.DeadlineExceeded
                ? new KillSwitchResult(false, false, HttpKillSwitchController.TimedOutMessage(operation))
                : new KillSwitchResult(false, false, NotificationGrpcCalls.FailureMessage($"kill switch の{operation}", outcome.Status));
        }

        // 状態を騙らない（欠落は失敗。停止したと誤認させない）。
        if (!response.HasEngaged)
        {
            logger.LogWarning("kill switch の{Operation}の応答を解釈できませんでした（gRPC）。", operation);
            return new KillSwitchResult(false, false, HttpKillSwitchController.UnparsableMessage(operation));
        }

        return new KillSwitchResult(true, response.Engaged, HttpKillSwitchController.SucceededMessage(operation));
    }
}
