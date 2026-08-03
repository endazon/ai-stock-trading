using System.Net;
using AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.TradeDecision.Infrastructure.Tests;

// FR-04, FR-07, IADR-0028: 報告書サービスの GET /reports/daily-policy を同期照会する実装の写像とフェイルセーフを
// fake HttpMessageHandler で検証する（実ネットワーク不使用）。
public class HttpDailyPolicyProviderTests
{
    private static HttpDailyPolicyProvider Provider(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://reports") },
            NullLogger<HttpDailyPolicyProvider>.Instance);

    [Fact]
    public async Task 応答を_DailyPolicy_に写像する()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"date":"2026-07-10","summary":"米国株の押し目買い","assumptionsVersion":1}""");

        var policy = await Provider(handler).GetCurrentAsync();

        policy.Should().NotBeNull();
        policy!.Date.Should().Be(new DateOnly(2026, 7, 10));
        policy.Summary.Should().Be("米国株の押し目買い");
        handler.LastPath.Should().Be("/reports/daily-policy");
    }

    [Fact]
    public async Task 未確定_404_は_null_取引しない()
    {
        var policy = await Provider(new StubHandler(HttpStatusCode.NotFound, "")).GetCurrentAsync();
        policy.Should().BeNull();
    }

    [Fact]
    public async Task 非_2xx_は_null_取引しない()
    {
        var policy = await Provider(new StubHandler(HttpStatusCode.Unauthorized, "")).GetCurrentAsync();
        policy.Should().BeNull();
    }

    [Fact]
    public async Task 例外_不達_は_null_取引しない()
    {
        var policy = await Provider(new ThrowingHandler()).GetCurrentAsync();
        policy.Should().BeNull();
    }

    [Fact]
    public async Task 不正_空ボディの_200_は_null_取引しない()
    {
        (await Provider(new StubHandler(HttpStatusCode.OK, "")).GetCurrentAsync()).Should().BeNull();
        (await Provider(new StubHandler(HttpStatusCode.OK, "not-json")).GetCurrentAsync()).Should().BeNull();
    }

    [Fact]
    public async Task タイムアウト_応答遅延_は_null_取引しない()
    {
        // HttpClient のタイムアウトを短くし、ハンドラを遅延させてタイムアウトを起こす（呼び出し側キャンセルではない）。
        var http = new HttpClient(new DelayingHandler(TimeSpan.FromSeconds(2)))
        {
            BaseAddress = new Uri("http://reports"),
            Timeout = TimeSpan.FromMilliseconds(50),
        };
        var provider = new HttpDailyPolicyProvider(http, NullLogger<HttpDailyPolicyProvider>.Instance);

        (await provider.GetCurrentAsync()).Should().BeNull();
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("報告書サービス不達");
    }

    // 指定時間待機してから応答するハンドラ（HttpClient のタイムアウトを起こすため）。
    private sealed class DelayingHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
