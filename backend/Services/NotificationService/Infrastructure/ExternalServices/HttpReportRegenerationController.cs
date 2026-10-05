using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NotificationService.Features.Notifications;

namespace NotificationService.Infrastructure.ExternalServices;

// FR-06, FR-14, UC-03〜05, 計画 ADR-0052 決定 1, #1156, IADR-0491 決定 1: 報告書サービスの `POST /reports/{periodKey}/regenerate` を呼ぶだけの
// アダプタ。当該エンドポイントは OwnerOnly のため、名前付き HttpClient（`report-regeneration`）に Bot 専用の owner マップ機密クライアントの
// トークンを付与する（報告書レビュー・`/policy` と同じ資格情報）。上限は期間の入力の取得と散文の LLM を見込んで長く取る。
//
// 🔴 **原則 A**: 200 → 作り直した。4xx / 5xx → 作り直していない（報告書サービスは 200 以外で下書きを変えない契約）。本文の `error` を見せる。
// タイムアウト・伝送の例外 → **不明**（作り直したかもしれない）。**再試行しない**（冪等でない）。
public sealed class HttpReportRegenerationController(
    HttpClient httpClient,
    ILogger<HttpReportRegenerationController> logger)
    : IReportRegenerationController
{
    // 表示する `error` の上限（報告書サービスの定数文・会話キー・入力の表示名だけのはずだが、想定外の長文で投稿を壊さない）。
    private const int MaxErrorLength = 600;

    public static string PathOf(string periodKey) => $"/reports/{Uri.EscapeDataString(periodKey)}/regenerate";

    public async Task<ReportRegenerationCommandOutcome> RegenerateAsync(
        string periodKey, string onBehalfOf, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(periodKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(onBehalfOf);

        try
        {
            using var response = await httpClient
                .PostAsJsonAsync(PathOf(periodKey), new RegenerateBody(onBehalfOf), cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var error = await ErrorOf(response, cancellationToken).ConfigureAwait(false);
                logger.LogWarning("報告書の作り直しが受理されませんでした（{Status}）。", (int)response.StatusCode);
                var hint = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "（Bot の owner クライアント設定・trading-owner ロール割当を確認してください）"
                    : string.Empty;
                return ReportRegenerationCommandOutcome.Rejected($"HTTP {(int)response.StatusCode}", error, hint);
            }

            var view = await response.Content.ReadFromJsonAsync<RegenerationView>(cancellationToken).ConfigureAwait(false);
            return Interpret(view?.PeriodKey, view?.Version ?? 0, view?.Message, logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (ex is OperationCanceledException)
                logger.LogWarning("報告書の作り直しがタイムアウトしました（結果は不明）。");
            else
                logger.LogWarning(ex, "報告書の作り直しで例外が発生しました（結果は不明）。");

            return new ReportRegenerationCommandOutcome(false, true, ReportRegenerationCommandOutcome.UnknownMessage);
        }
    }

    // 200 の本文 → 結果。解釈できない＝作り直された可能性がある（200 は保存の後に返る）。不明として扱う。gRPC 実装と共有する。
    internal static ReportRegenerationCommandOutcome Interpret(string? periodKey, int version, string? message, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(periodKey) || version < 1 || string.IsNullOrWhiteSpace(message))
        {
            logger.LogWarning("報告書の作り直しの応答を解釈できませんでした。");
            return new ReportRegenerationCommandOutcome(
                false, true, "報告書の作り直しの応答を解釈できませんでした（作り直された可能性があります。/report show で確認してください）。");
        }

        return new ReportRegenerationCommandOutcome(true, false, message, version);
    }

    private static async Task<string?> ErrorOf(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorView>(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body?.Error))
                return null;

            var error = body.Error.Trim();
            return error.Length <= MaxErrorLength ? error : error[..(MaxErrorLength - 1)] + "…";
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    // 報告書サービス側 RegenerateReportRequest と同形（名前を変えてあるのは、送り手の型の目印〔IADR-0420 の検査器〕と区別するため）。
    private sealed record RegenerateBody(string OnBehalfOf);

    // 報告書サービス側 ReportRegenerationResponse の射影（Bot が使うのは版と案内文だけ）。
    internal sealed record RegenerationView(string? PeriodKey, int Version, string? Message);

    private sealed record ErrorView(string? Error);
}
