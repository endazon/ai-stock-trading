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
        DisengageAsync(reason, null, cancellationToken);

    // NFR-06, #1552, IADR-0615: 解除だけが確認コードを運ぶ（起動は止める向き＝ADR-0009 の非対称）。
    public Task<KillSwitchResult> DisengageAsync(string reason, string? secondFactorCode, CancellationToken cancellationToken = default) =>
        DisengageAsync(reason, secondFactorCode, null, cancellationToken);

    // FR-10, FR-14, #1561, IADR-0620: 解除だけが見た状態の版を運ぶ（起動は止める向き）。欄は入れたときだけ立てる。
    public Task<KillSwitchResult> DisengageAsync(
        string reason, string? secondFactorCode, string? expectedVersion, CancellationToken cancellationToken = default)
    {
        var request = new RiskProto.KillSwitchChangeRequest { Reason = reason };
        // NFR-06, #1552, IADR-0615: 確認コードは入れたときだけ載せる（欄は optional。null を代入すると例外）。値はログに出さない。
        if (secondFactorCode is not null)
            request.SecondFactorCode = secondFactorCode;
        if (expectedVersion is not null)
            request.ExpectedVersion = expectedVersion;
        return ChangeAsync("解除", options => transport.OwnerWrite.DisengageKillSwitchAsync(request, options), cancellationToken);
    }

    // FR-10, FR-14, #1561, IADR-0620: 今の版は稼働状態の照会（RiskControlsOwnerRead/GetRiskStatus）から読む。失敗・欠落は null。
    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        var outcome = await transport.Calls.CallAsync(
            "kill switch の版の照会",
            options => transport.OwnerRead.GetRiskStatusAsync(new RiskProto.GetRiskStatusRequest(), options),
            cancellationToken).ConfigureAwait(false);
        return outcome.Response is { HasKillSwitchVersion: true } response ? KillSwitchVersions.Normalize(response.KillSwitchVersion) : null;
    }

    private async Task<KillSwitchResult> ChangeAsync(
        string operation,
        Func<Grpc.Core.CallOptions, Grpc.Core.AsyncUnaryCall<RiskProto.KillSwitchChangeResponse>> call,
        CancellationToken cancellationToken)
    {
        var outcome = await transport.Calls.CallOnceAsync($"kill switch の{operation}", call, cancellationToken).ConfigureAwait(false);

        if (outcome.Response is not { } response)
        {
            // NFR, #1530, IADR-0613, FR-10, #1561, IADR-0620: 拒否の印（末尾の metadata の code）で、保持・版の競合・版なしを分ける。
            if (KillSwitchVersions.RejectionOf(outcome.ErrorCode) is { } rejected)
                return rejected;

            // 印の無い解除の ABORTED は、印を送らない旧版の提供側の保持の拒否（当時は解除の 409 は保持だけだった）。
            if (operation == "解除" && outcome.Status == Grpc.Core.StatusCode.Aborted)
                return new KillSwitchResult(false, true, HttpKillSwitchController.OperatorHoldMessage);

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
