using Grpc.Core;
using Microsoft.Extensions.Logging;
using NotificationService.Features.Notifications;
using ReportProto = AiStockTrading.Shared.Grpc.Report.V1;

namespace NotificationService.Infrastructure.ExternalServices;

// NFR, FR-06, FR-14, MSP:ADR-0029, 計画 ADR-0052 決定 1, #1156, IADR-0491 決定 1: 作り直しの 2 つ目の実装（`ReportOwnerWrite/RegenerateReport`）。
// `Reports:Grpc` を宣言したときだけ選ばれる。🔴 冪等でない（LLM を呼び新しい版を作り、1 日の回数を消費する）。**再試行しない**。
// deadline は REST と同じ（`Reports:GrpcRegenerationTimeoutSeconds`・既定 300 秒）。届いたか分からない失敗（時間切れ・不達）は「不明」、
// 提供側が明確に断った失敗は「作り直していない」（REST の非 2xx と同じ）。REST へ落とさない。
public sealed class GrpcReportRegenerationController(
    ReportsGrpcTransport transport,
    ILogger<GrpcReportRegenerationController> logger)
    : IReportRegenerationController
{
    public async Task<ReportRegenerationCommandOutcome> RegenerateAsync(
        string periodKey, string onBehalfOf, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(periodKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(onBehalfOf);

        var request = new ReportProto.ReportRegenerationRequest { PeriodKey = periodKey, OnBehalfOf = onBehalfOf };
        var outcome = await transport.RegenerationCalls.CallOnceAsync(
            "報告書の作り直し", options => transport.OwnerWrite.RegenerateReportAsync(request, options), cancellationToken).ConfigureAwait(false);

        if (outcome.Response is not { } response)
        {
            if (NotificationGrpcCalls.IsOutcomeUnknown(outcome.Status))
                return new ReportRegenerationCommandOutcome(false, true, ReportRegenerationCommandOutcome.UnknownMessage);

            logger.LogWarning("報告書の作り直しが受理されませんでした（gRPC {Status}）。", outcome.Status);
            var hint = outcome.Status is StatusCode.Unauthenticated or StatusCode.PermissionDenied ? NotificationGrpcCalls.OwnerHint : string.Empty;
            return ReportRegenerationCommandOutcome.Rejected($"gRPC {outcome.Status}", NotificationGrpcCalls.ProviderReason(outcome), hint);
        }

        return HttpReportRegenerationController.Interpret(
            response.HasPeriodKey ? response.PeriodKey : null,
            response.HasVersion ? response.Version : 0,
            response.HasMessage ? response.Message : null,
            logger);
    }
}
