using Microsoft.Extensions.Http.Logging;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Notification.Worker.Composable.Adapters;

// #289, FR-09, NFR（セキュリティ）: リクエスト URI 自体が資格情報である送信先（Discord Webhook）向けの
// HttpClient ログ。IHttpClientFactory の既定ログ（"Sending HTTP request POST <uri>" とスコープ "HTTP POST <uri>"）は
// URL を平文で出すため RemoveAllLoggers() で外し、本 logger で **宛先ホストのみ**・パスとクエリを伏せて記録する。
//
// 目的は「URL を出さない」ことであって「ログを消す」ことではない。送信の成否・HTTP ステータス・所要時間は
// 従来どおり残し、障害切り分けができる状態を保つ（#289 の受け入れ基準）。
internal sealed class RedactedUriHttpClientLogger(ILogger<RedactedUriHttpClientLogger> logger) : IHttpClientLogger
{
    public object? LogRequestStart(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        logger.LogDebug("HTTP 送信開始: {Method} {Destination}", request.Method, Redact(request.RequestUri));
        return null;
    }

    public void LogRequestStop(
        object? context, HttpRequestMessage request, HttpResponseMessage response, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        logger.LogInformation(
            "HTTP 応答: {Status} {Method} {Destination}（{ElapsedMs}ms）",
            (int)response.StatusCode, request.Method, Redact(request.RequestUri), elapsed.TotalMilliseconds);
    }

    public void LogRequestFailed(
        object? context,
        HttpRequestMessage request,
        HttpResponseMessage? response,
        Exception exception,
        TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(request);

        logger.LogWarning(
            exception,
            "HTTP 送信失敗: {Method} {Destination}（{ElapsedMs}ms）",
            request.Method, Redact(request.RequestUri), elapsed.TotalMilliseconds);
    }

    // 秘密はパス（Discord Webhook は /api/webhooks/<id>/<token>）にもクエリにも入り得るため、
    // 宛先の識別に足るスキーム＋ホストだけを残して以降は一律に伏せる（部分開示もしない）。
    private static string Redact(Uri? uri) =>
        uri is null ? "(unknown)" : $"{uri.Scheme}://{uri.Host}/***";
}
