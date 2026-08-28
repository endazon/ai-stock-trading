using System.Net;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;

namespace InformationCollectionService.Infrastructure.Tests;

// FR-01, IADR-0064: 公式ソースコネクタのテスト用テストダブル（実ネットワーク・実時間を使わない）。

// 常に同じ応答を返し、直近の要求（URL・ヘッダー）を記録する fake HttpMessageHandler。
internal sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public string? LastUrl { get; private set; }

    public string? LastUserAgent { get; private set; }

    public int Requests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastUrl = request.RequestUri?.ToString();
        LastUserAgent = request.Headers.UserAgent.Count > 0 ? request.Headers.UserAgent.ToString() : null;
        Requests++;
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}

// 要求ごとに順番に応答を返す fake HttpMessageHandler（1 件目失敗・2 件目成功などの検証用）。
internal sealed class SequenceHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
{
    private int _index;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = responses[Math.Min(_index++, responses.Length - 1)];
        return Task.FromResult(new HttpResponseMessage(response.Status) { Content = new StringContent(response.Body) });
    }
}

// 待機回数だけを数えるレート制限（実時間を待たない）。
internal sealed class CountingRateLimiter : IRateLimiter
{
    public int Waits { get; private set; }

    public Task WaitAsync(CancellationToken cancellationToken = default)
    {
        Waits++;
        return Task.CompletedTask;
    }
}
