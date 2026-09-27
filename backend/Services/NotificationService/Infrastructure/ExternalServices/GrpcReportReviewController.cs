using Grpc.Core;
using Microsoft.Extensions.Logging;
using NotificationService.Features.Notifications;
using ReportProto = AiStockTrading.Shared.Grpc.Report.V1;

namespace NotificationService.Infrastructure.ExternalServices;

// NFR, FR-07, FR-09, FR-14, UC-03〜05, MSP:ADR-0029, ADR-0047 決定 1・2, IADR-0284 決定 5（段 5）, IADR-0449 決定 4, IADR-0450 決定 4, #753:
// 報告書レビューの 2 つ目の実装。レビュー局面の照会と会話キーの一覧は `ReportOwnerRead`（段 5 の前半）、確定・差し戻しは `ReportOwnerWrite`
// （段 5 の後半）。`Reports:Grpc` を宣言したときだけ選ばれる。確定者は本文の on_behalf_of で運ぶ（ADR-0047 決定 1）。
//
// 🔴 解釈は REST と同じ 1 つ（`ReviewResult`・`OrderPeriodKeys`・`StartOf`・`NotFoundMessage`・`InterpretConfirmed`）。
// 🔴 書き込みは**再試行しない**（差し戻しの 2 回目は不正遷移で 409 になり、差し戻せていたのに失敗に見える）。時間切れは「結果は不明」
// （読み取りの時間切れには付けない）。REST の 409（版の不一致）＝ ABORTED は「確定していない」（呼び出しの失敗ではない）。REST へ落とさない。
// 🔴 会話キーの一覧は REST と同じく**失敗をすべて空**で返す（入力補完の候補に使うだけ）。REST の「404 のときだけ従来の全件照会へ退避」は
// gRPC に持たない —— 退避は配備順の窓のためであり、gRPC の rpc は提供側と同時に出るので同じ窓は `UNIMPLEMENTED` になる。
// 障害中に重い照会を重ねない（REST も 500・例外では退避しない）向きに揃え、候補なしへ倒す（IADR-0449 決定 4）。
public sealed class GrpcReportReviewController(
    ReportsGrpcTransport transport,
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
                // 🔴 読み取りの時間切れに「結果は不明」は付けない（状態を変えないので不明になる結果が無い。PR #1069 の監査）。REST と同じ関数。
                StatusCode.DeadlineExceeded => new ReportReviewResult(false, 0, HttpReportReviewController.TimedOutMessage(ReviewOperation, write: false)),
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

    public async Task<ReportConfirmResult> ConfirmAsync(
        string periodKey, int expectedVersion, string onBehalfOf, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(periodKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(onBehalfOf);

        const string operation = "報告書の確定";
        var request = new ReportProto.ReportConfirmationRequest { PeriodKey = periodKey, ExpectedVersion = expectedVersion, OnBehalfOf = onBehalfOf };
        var outcome = await transport.Calls.CallOnceAsync(
            operation, options => transport.OwnerWrite.ConfirmReportAsync(request, options), cancellationToken).ConfigureAwait(false);

        if (outcome.Response is not { } response)
        {
            if (outcome.Status == StatusCode.Aborted)
            {
                logger.LogWarning(
                    "報告書の確定が版不一致で拒否されました（gRPC・PeriodKey={PeriodKey}・版={Version}）。", periodKey, expectedVersion);
                return new ReportConfirmResult(true, false, HttpReportReviewController.VersionMismatchMessage);
            }

            return new ReportConfirmResult(false, false, WriteFailure(operation, outcome.Status));
        }

        // 🔴 gRPC の面は項目と同時に生まれたので、REST の「項目を返さない旧版」の窓は無い。欠落は解釈できない（確定したと騙らない）。
        if (!response.HasTransitioned || !response.HasVersion)
        {
            logger.LogWarning("報告書の確定の応答を解釈できませんでした（gRPC・PeriodKey={PeriodKey}）。", periodKey);
            return new ReportConfirmResult(false, false, ConfirmUnparsableMessage);
        }

        return HttpReportReviewController.InterpretConfirmed(periodKey, expectedVersion, response.Transitioned, response.Version, logger);
    }

    public async Task<ReportReviewResult> RequestChangesAsync(
        string periodKey, int expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(periodKey);

        const string operation = "報告書の差し戻し";
        var request = new ReportProto.ReportChangesRequest { PeriodKey = periodKey, ExpectedVersion = expectedVersion };
        var outcome = await transport.Calls.CallOnceAsync(
            operation, options => transport.OwnerWrite.RequestReportChangesAsync(request, options), cancellationToken).ConfigureAwait(false);

        if (outcome.Response is not { } response)
            return new ReportReviewResult(false, 0, WriteFailure(operation, outcome.Status));

        return response.HasVersion
            ? new ReportReviewResult(true, response.Version, HttpReportReviewController.ChangesRequestedMessage(periodKey, response.Version))
            : new ReportReviewResult(false, 0, HttpReportReviewController.ChangesUnparsableMessage);
    }

    // 書き込みの失敗（REST の非 2xx・例外と同じ種類）: 無い＝会話キーの報告書が無い（404 と同じ文言）、時間切れ＝結果は不明、ほかは状態つきの失敗。
    private string WriteFailure(string operation, StatusCode status)
    {
        if (status == StatusCode.NotFound)
        {
            logger.LogWarning("{Operation}の対象が見つかりませんでした（gRPC NotFound）。", operation);
            return HttpReportReviewController.NotFoundMessage;
        }

        return status == StatusCode.DeadlineExceeded
            ? HttpReportReviewController.TimedOutMessage(operation, write: true)
            : NotificationGrpcCalls.FailureMessage(operation, status);
    }

    internal const string ConfirmUnparsableMessage = "報告書の確定の応答を解釈できませんでした（確定されたかは /report show で確認してください）";

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
