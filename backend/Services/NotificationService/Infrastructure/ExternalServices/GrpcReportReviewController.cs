using Grpc.Core;
using Microsoft.Extensions.Logging;
using NotificationService.Features.Notifications;
using ReportProto = AiStockTrading.Shared.Grpc.Report.V1;

namespace NotificationService.Infrastructure.ExternalServices;

// NFR, FR-07, FR-14, UC-03〜05, MSP:ADR-0029, ADR-0047 決定 1・2, IADR-0284 決定 5（段 5）, IADR-0449 決定 4, #753:
// 報告書レビューの 2 つ目の実装。**レビュー局面の照会と会話キーの一覧（読み取り）だけ**を gRPC（`ReportOwnerRead`）で行い、確定・差し戻し
// （書き込み）は REST の実装（HttpReportReviewController）へ委ねる（段 5 の後半で移す）。`Reports:Grpc` を宣言したときだけ選ばれる。
//
// 🔴 解釈は REST と同じ 1 つ（`ReviewResult`・`OrderPeriodKeys`・`StartOf`・`NotFoundMessage`）。
// 🔴 会話キーの一覧は REST と同じく**失敗をすべて空**で返す（入力補完の候補に使うだけ）。REST の「404 のときだけ従来の全件照会へ退避」は
// gRPC に持たない —— 退避は配備順の窓のためであり、gRPC の rpc は提供側と同時に出るので同じ窓は `UNIMPLEMENTED` になる。
// 障害中に重い照会を重ねない（REST も 500・例外では退避しない）向きに揃え、候補なしへ倒す（IADR-0449 決定 4）。
public sealed class GrpcReportReviewController(
    ReportsGrpcTransport transport,
    IReportReviewController rest,
    ILogger<GrpcReportReviewController> logger)
    : IReportReviewController
{
    private const string ReviewOperation = "レビュー局面の照会";

    public async Task<ReportReviewResult> GetReviewAsync(string periodKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(periodKey);

        var outcome = await transport.Calls.CallAsync(
            ReviewOperation,
            options => transport.OwnerRead.GetReportReviewAsync(new ReportProto.GetReportReviewRequest { PeriodKey = periodKey }, options),
            cancellationToken).ConfigureAwait(false);

        if (outcome.Response is not { } response)
        {
            return outcome.Status switch
            {
                StatusCode.NotFound => new ReportReviewResult(false, 0, HttpReportReviewController.NotFoundMessage),
                // REST のタイムアウトと同じ文言（結果は不明）。
                StatusCode.DeadlineExceeded => new ReportReviewResult(false, 0, $"{ReviewOperation}がタイムアウトしました（結果は不明です）"),
                _ => new ReportReviewResult(false, 0, NotificationGrpcCalls.FailureMessage(ReviewOperation, outcome.Status)),
            };
        }

        // 版番号を騙らない（欠落は失敗。誤った版で確定させない）。
        if (!response.HasVersion)
        {
            logger.LogWarning("レビュー局面の応答を解釈できませんでした（gRPC・PeriodKey={PeriodKey}）。", periodKey);
            return new ReportReviewResult(false, 0, "レビュー局面の応答を解釈できませんでした");
        }

        return HttpReportReviewController.ReviewResult(
            periodKey,
            response.Version,
            response.UnsuppliedInputs is { } inputs ? [.. inputs.Names] : null,
            logger);
    }

    public Task<ReportConfirmResult> ConfirmAsync(
        string periodKey, int expectedVersion, string onBehalfOf, CancellationToken cancellationToken = default) =>
        rest.ConfirmAsync(periodKey, expectedVersion, onBehalfOf, cancellationToken);

    public Task<ReportReviewResult> RequestChangesAsync(
        string periodKey, int expectedVersion, CancellationToken cancellationToken = default) =>
        rest.RequestChangesAsync(periodKey, expectedVersion, cancellationToken);

    public async Task<IReadOnlyList<string>> ListPeriodKeysAsync(CancellationToken cancellationToken = default)
    {
        var outcome = await transport.Calls.CallAsync(
            "報告書一覧の照会",
            options => transport.OwnerRead.ListReportPeriodKeysAsync(new ReportProto.ListReportPeriodKeysRequest(), options),
            cancellationToken).ConfigureAwait(false);

        if (outcome.Response is not { } response)
        {
            logger.LogWarning("報告書一覧の照会に失敗しました（gRPC {Status}）。補完の候補なしで続行します。", outcome.Status);
            return [];
        }

        return HttpReportReviewController.OrderPeriodKeys(response.Items.Select(i => (
            i.HasPeriodKey ? i.PeriodKey : null,
            HttpReportReviewController.StartOf(i.HasPeriodStart ? i.PeriodStart : null))));
    }
}
