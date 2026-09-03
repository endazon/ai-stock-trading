using System.Net;
using InformationCollectionService.Features.InformationCollection;
using InformationCollectionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InformationCollectionService.Tests;

// NFR（費用）, IADR-0031: 費用統制の GET /costs/state を同期照会する実装の写像とフェイルセーフを fake HttpMessageHandler で
// 検証する（実ネットワーク不使用）。未取得系は Normal（停止せず・1×）へ倒す。
public class HttpCostControlGateTests
{
    private static HttpCostControlGate Gate(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://cost") },
            NullLogger<HttpCostControlGate>.Instance);

    [Fact]
    public async Task 間隔延長_Throttled_応答を写像する()
    {
        // CostControlDecision の JSON（isHalted/intervalMultiplier を疎結合に読む）。
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"state":1,"intervalMultiplier":2.0,"isHalted":false}""");

        var gate = await Gate(handler).GetAsync();

        gate.Halted.Should().BeFalse();
        gate.IntervalMultiplier.Should().Be(2.0m);
        handler.LastPath.Should().Be("/costs/state");
    }

    [Fact]
    public async Task 停止_Halted_応答を写像する()
    {
        var gate = await Gate(new StubHandler(HttpStatusCode.OK,
            """{"state":2,"intervalMultiplier":0,"isHalted":true}""")).GetAsync();

        gate.Halted.Should().BeTrue();
    }

    [Fact]
    public async Task 未取得_404_は_Normal_停止せず()
    {
        var gate = await Gate(new StubHandler(HttpStatusCode.NotFound, "")).GetAsync();
        gate.Should().Be(CostControlGateNormal());
    }

    [Fact]
    public async Task 非_2xx_は_Normal_停止せず()
    {
        var gate = await Gate(new StubHandler(HttpStatusCode.Unauthorized, "")).GetAsync();
        gate.Should().Be(CostControlGateNormal());
    }

    [Fact]
    public async Task 例外_不達_は_Normal_停止せず()
    {
        (await Gate(new ThrowingHandler()).GetAsync()).Should().Be(CostControlGateNormal());
    }

    [Fact]
    public async Task 不正_空ボディの_200_は_Normal_停止せず()
    {
        (await Gate(new StubHandler(HttpStatusCode.OK, "")).GetAsync()).Should().Be(CostControlGateNormal());
    }

    [Fact]
    public async Task タイムアウト_応答遅延_は_Normal_停止せず()
    {
        var http = new HttpClient(new DelayingHandler(TimeSpan.FromSeconds(2)))
        {
            BaseAddress = new Uri("http://cost"),
            Timeout = TimeSpan.FromMilliseconds(50),
        };
        var gate = new HttpCostControlGate(http, NullLogger<HttpCostControlGate>.Instance);

        (await gate.GetAsync()).Should().Be(CostControlGateNormal());
    }

    private static InformationCollectionService.Features.InformationCollection.CostControlGate CostControlGateNormal() =>
        InformationCollectionService.Features.InformationCollection.CostControlGate.Normal;

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
            throw new HttpRequestException("費用統制サービス不達");
    }

    private sealed class DelayingHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
