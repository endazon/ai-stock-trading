using System.Net;
using System.Text.Json;
using MarketMonitorService.Domain;
using MarketMonitorService.Infrastructure.Adapters;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MarketMonitorService.Infrastructure.Tests;

// FR-03, FR-10, IADR-0030: リスク管理の GET /risk-controls/open-positions を同期照会する実装の写像とフェイルセーフを
// fake HttpMessageHandler で検証する（実ネットワーク不使用）。未取得系は空列（＝損切り検知対象なし）へ倒す。
public class HttpPositionStoreTests
{
    private static HttpPositionStore Store(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://risk") },
            NullLogger<HttpPositionStore>.Instance);

    [Fact]
    public async Task 応答を_HeldPosition_列に写像する()
    {
        // web 既定（camelCase・列挙は数値）で往復させる JSON を用意する。
        var payload = new[]
        {
            new HeldPosition("AAPL", Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 970m),
            new HeldPosition("MSFT", Market.UnitedStates, TradeSide.Sell, 5, 2_000m, 2_060m),
        };
        var body = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var handler = new StubHandler(HttpStatusCode.OK, body);

        var positions = await Store(handler).GetOpenPositionsAsync();

        positions.Should().HaveCount(2);
        var aapl = positions.Single(p => p.Symbol == "AAPL");
        aapl.Side.Should().Be(TradeSide.Buy);
        aapl.Quantity.Should().Be(10);
        aapl.StopLossPrice.Should().Be(970m);
        handler.LastPath.Should().Be("/risk-controls/open-positions");
    }

    [Fact]
    public async Task 未取得_404_は空列_損切り検知対象なし()
    {
        (await Store(new StubHandler(HttpStatusCode.NotFound, "")).GetOpenPositionsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task 非_2xx_は空列_損切り検知対象なし()
    {
        (await Store(new StubHandler(HttpStatusCode.Unauthorized, "")).GetOpenPositionsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task 例外_不達_は空列_損切り検知対象なし()
    {
        (await Store(new ThrowingHandler()).GetOpenPositionsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task 不正_空ボディの_200_は空列_損切り検知対象なし()
    {
        (await Store(new StubHandler(HttpStatusCode.OK, "")).GetOpenPositionsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task タイムアウト_応答遅延_は空列_損切り検知対象なし()
    {
        // HttpClient のタイムアウトを短くし、ハンドラを遅延させてタイムアウトを起こす（呼び出し側キャンセルではない）。
        var http = new HttpClient(new DelayingHandler(TimeSpan.FromSeconds(2)))
        {
            BaseAddress = new Uri("http://risk"),
            Timeout = TimeSpan.FromMilliseconds(50),
        };
        var store = new HttpPositionStore(http, NullLogger<HttpPositionStore>.Instance);

        (await store.GetOpenPositionsAsync()).Should().BeEmpty();
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
            throw new HttpRequestException("リスク管理サービス不達");
    }

    // 指定時間待機してから応答するハンドラ（HttpClient のタイムアウトを起こすため）。
    private sealed class DelayingHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        }
    }
}
