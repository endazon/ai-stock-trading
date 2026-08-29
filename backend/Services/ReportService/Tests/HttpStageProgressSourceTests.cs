using System.Net;
using System.Text;
using ReportService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-15, FR-20, #569, IADR-0051, IADR-0271: 現在の運用段階の s2s 照会と、その fail-safe。
//
// 🔴 段階は三者比較の「空欄（その段をまだ走らせていない）」と「値 0」を分ける鍵である。
// **既定（Stage 0）へ倒すと、到達済みの段の列が静かに空欄になる。**
public class HttpStageProgressSourceTests
{
    private static HttpStageProgressSource Source(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://risk-management") },
            NullLogger<HttpStageProgressSource>.Instance);

    // **対の肯定形**: 供給された段階をそのまま返す。
    [Theory]
    [InlineData(0, TradingStage.Stage0Verification)]
    [InlineData(1, TradingStage.Stage1Simulate)]
    [InlineData(2, TradingStage.Stage2MinimalLive)]
    [InlineData(3, TradingStage.Stage3ScaledLive)]
    public async Task 現在段階を写す(int raw, TradingStage expected)
    {
        var handler = new StubHandler(HttpStatusCode.OK, $$"""{"currentStage":{{raw}},"history":[]}""");

        (await Source(handler).GetCurrentStageAsync()).Should().Be(expected);
        handler.LastUri!.AbsolutePath.Should().Be("/risk-controls/stage-gate");
    }

    // 🔴 **否定形**: 供給不達はすべて null（未供給）へ倒す。Stage 0 へ倒さない。
    [Fact]
    public async Task 非_2xx_は未供給へ倒す()
    {
        (await Source(new StubHandler(HttpStatusCode.Forbidden, "")).GetCurrentStageAsync()).Should().BeNull();
    }

    [Fact]
    public async Task 不正なボディは未供給へ倒す()
    {
        (await Source(new StubHandler(HttpStatusCode.OK, "not-json")).GetCurrentStageAsync()).Should().BeNull();
        (await Source(new StubHandler(HttpStatusCode.OK, "null")).GetCurrentStageAsync()).Should().BeNull();
        (await Source(new StubHandler(HttpStatusCode.OK, "{}")).GetCurrentStageAsync()).Should().BeNull();
    }

    // 🔴 **未定義の列挙値を素通ししない。** 権威源が段階を増やしたとき、未知の値を
    // 「到達済み」として比較へ流すと、走らせていない段の列が埋まる。
    [Fact]
    public async Task 未知の段階値は未供給へ倒す()
    {
        (await Source(new StubHandler(HttpStatusCode.OK, """{"currentStage":99}""")).GetCurrentStageAsync())
            .Should().BeNull();
    }

    [Fact]
    public async Task 例外_不達_は未供給へ倒す()
    {
        (await Source(new ThrowingHandler()).GetCurrentStageAsync()).Should().BeNull();
    }

    [Fact]
    public async Task タイムアウトは未供給へ倒す()
    {
        var http = new HttpClient(new DelayingHandler(TimeSpan.FromSeconds(2)))
        {
            BaseAddress = new Uri("http://risk-management"),
            Timeout = TimeSpan.FromMilliseconds(50),
        };

        (await new HttpStageProgressSource(http, NullLogger<HttpStageProgressSource>.Instance)
            .GetCurrentStageAsync()).Should().BeNull();
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("接続できません");
    }

    private sealed class DelayingHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"currentStage":1}""", Encoding.UTF8, "application/json"),
            };
        }
    }
}
