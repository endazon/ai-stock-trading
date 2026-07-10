using System.Net;
using AiStockTrading.TradeDecision.Worker.Composable.Adapters;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.TradeDecision.Worker.Tests;

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
}
