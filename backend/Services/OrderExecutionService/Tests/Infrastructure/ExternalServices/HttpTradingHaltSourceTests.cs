extern alias RiskManagementWorker;

using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder;
using OrderExecutionService.Infrastructure.ExternalServices;
using RiskManagementWorker::RiskManagementService.Features.RiskManagement.GetTradingHalt;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-2967・T-10-2970, FR-10, ADR-0009, NFR-04, IADR-0565 決定 2, IADR-0420: 停止の状態の照会（受け手）。
// 送り手の本物の型（TradingHaltView）を web 既定で直列化した本文を読める（契約）。失敗・タイムアウト・解釈できない本文・
// 項目の欠落は **null（不明）**——欠落を false（停止していない）と読む向きの誤りを必ず不明へ倒す。
public class HttpTradingHaltSourceTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class StubHandler(HttpStatusCode status, string body, TimeSpan? delay = null) : HttpMessageHandler
    {
        public Uri? Requested { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested = request.RequestUri;
            if (delay is { } d)
                await Task.Delay(d, cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("接続できません（テスト）"));
    }

    private static HttpTradingHaltSource Source(HttpMessageHandler handler, TimeSpan? timeout = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://risk"), Timeout = timeout ?? HttpTradingHaltSource.RequestTimeout },
            NullLogger<HttpTradingHaltSource>.Instance);

    // T-10-2970（契約）: 送り手の本物の型の値を、送り手の JSON 設定（web 既定）で直列化した本文を読む。
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task 送り手の本物の型の本文を読む(bool killSwitch, bool paused)
    {
        var handler = new StubHandler(HttpStatusCode.OK, JsonSerializer.Serialize(new TradingHaltView(killSwitch, paused), Web));

        var state = await Source(handler).GetAsync();

        state.Should().Be(new TradingHaltState(killSwitch, paused));
        handler.Requested!.AbsolutePath.Should().Be("/risk-controls/trading-halt");
    }

    // T-10-2967: 照会の失敗は不明。
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task 非2xxは不明(HttpStatusCode status)
    {
        var body = JsonSerializer.Serialize(new TradingHaltView(false, false), Web);

        (await Source(new StubHandler(status, body)).GetAsync()).Should().BeNull();
    }

    // T-10-2967: 本文が問いに答えていなければ不明（欠落を false と読まない）。
    [Theory]
    [InlineData("""{"killSwitchEngaged":false}""")]
    [InlineData("""{"tradingPaused":false}""")]
    [InlineData("""{"killSwitch":false,"paused":false}""")]
    [InlineData("""{"killSwitchEngaged":null,"tradingPaused":false}""")]
    [InlineData("""{}""")]
    [InlineData("""null""")]
    [InlineData("""{"killSwitchEngaged":"no","tradingPaused":false}""")]
    [InlineData("""not json""")]
    [InlineData("")]
    public async Task 解釈できない本文は不明(string body)
    {
        (await Source(new StubHandler(HttpStatusCode.OK, body)).GetAsync()).Should().BeNull();
    }

    [Fact]
    public async Task 通信の例外は不明()
    {
        (await Source(new ThrowingHandler()).GetAsync()).Should().BeNull();
    }

    [Fact]
    public async Task タイムアウトは不明()
    {
        var body = JsonSerializer.Serialize(new TradingHaltView(false, false), Web);
        var handler = new StubHandler(HttpStatusCode.OK, body, delay: TimeSpan.FromSeconds(5));

        (await Source(handler, timeout: TimeSpan.FromMilliseconds(100)).GetAsync()).Should().BeNull();
    }

    [Fact]
    public async Task 呼び出し元の取り消しでも投げずに不明()
    {
        var body = JsonSerializer.Serialize(new TradingHaltView(false, false), Web);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        (await Source(new StubHandler(HttpStatusCode.OK, body)).GetAsync(cts.Token)).Should().BeNull();
    }

    // IADR-0565 決定 4: 待ちの上限（記録した値と同じ）。
    [Fact]
    public void 待ちの上限は3秒()
    {
        HttpTradingHaltSource.RequestTimeout.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task 照会先が未構成なら常に不明()
    {
        (await new UnconfiguredTradingHaltSource(NullLogger<UnconfiguredTradingHaltSource>.Instance).GetAsync())
            .Should().BeNull();
    }
}
