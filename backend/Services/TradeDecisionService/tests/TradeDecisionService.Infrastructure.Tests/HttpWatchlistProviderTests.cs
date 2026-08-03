using System.Net;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.TradeDecision.Infrastructure.Tests;

// FR-02, FR-13, UC-06, SC-02, IADR-0088/0095: 権威源（市場監視 #10）の GET /monitor/watchlist を s2s 同期照会する実装の
// 写像とフェイルセーフを fake HttpMessageHandler で検証する（実ネットワーク不使用）。
// 供給不達（非 2xx・timeout・例外・不正応答）は fallback（既定 watchlist）へ委譲する。
public class HttpWatchlistProviderTests
{
    private static HttpWatchlistProvider Provider(HttpMessageHandler handler, IWatchlistProvider fallback) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://monitor") },
            fallback,
            NullLogger<HttpWatchlistProvider>.Instance);

    private static readonly IReadOnlyList<WatchedSymbol> FallbackSymbols =
        [new WatchedSymbol("FALLBACK", Market.Japan)];

    [Fact]
    public async Task 権威源の_watchlist_を_WatchedSymbol_に写像する()
    {
        // MonitoredSymbol（MarketMonitor.Domain）と WatchedSymbol は同形。web 既定（camelCase・列挙は数値）で往復する。
        var payload = new[] { new WatchedSymbol("7203", Market.Japan), new WatchedSymbol("AAPL", Market.UnitedStates) };
        var body = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var handler = new StubHandler(HttpStatusCode.OK, body);

        var result = await Provider(handler, new FakeFallback(FallbackSymbols)).GetWatchlistAsync();

        result.Should().HaveCount(2);
        result.Should().ContainEquivalentOf(new WatchedSymbol("7203", Market.Japan));
        result.Should().ContainEquivalentOf(new WatchedSymbol("AAPL", Market.UnitedStates));
        handler.LastPath.Should().Be("/monitor/watchlist");
    }

    [Fact]
    public async Task 空の_watchlist_をそのまま返す_フォールバックしない()
    {
        // 権威源が空（利用者が全削除）なら、その事実（何も判断しない）を尊重し fallback へ倒さない。
        var handler = new StubHandler(HttpStatusCode.OK, "[]");

        var result = await Provider(handler, new FakeFallback(FallbackSymbols)).GetWatchlistAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task 応答の_空_symbol_は除外する()
    {
        var body = """[{"symbol":"7203","market":0},{"symbol":"  ","market":0}]""";
        var handler = new StubHandler(HttpStatusCode.OK, body);

        var result = await Provider(handler, new FakeFallback(FallbackSymbols)).GetWatchlistAsync();

        result.Should().ContainSingle().Which.Symbol.Should().Be("7203");
    }

    [Fact]
    public async Task 未取得_404_は既定_watchlist_へフォールバックする()
    {
        var result = await Provider(new StubHandler(HttpStatusCode.NotFound, ""), new FakeFallback(FallbackSymbols))
            .GetWatchlistAsync();

        result.Should().BeEquivalentTo(FallbackSymbols);
    }

    [Fact]
    public async Task 非_2xx_403_は既定_watchlist_へフォールバックする()
    {
        var result = await Provider(new StubHandler(HttpStatusCode.Forbidden, ""), new FakeFallback(FallbackSymbols))
            .GetWatchlistAsync();

        result.Should().BeEquivalentTo(FallbackSymbols);
    }

    [Fact]
    public async Task 例外_不達_は既定_watchlist_へフォールバックする()
    {
        var result = await Provider(new ThrowingHandler(), new FakeFallback(FallbackSymbols)).GetWatchlistAsync();

        result.Should().BeEquivalentTo(FallbackSymbols);
    }

    [Fact]
    public async Task 不正_null応答の_200_は既定_watchlist_へフォールバックする()
    {
        var result = await Provider(new StubHandler(HttpStatusCode.OK, "null"), new FakeFallback(FallbackSymbols))
            .GetWatchlistAsync();

        result.Should().BeEquivalentTo(FallbackSymbols);
    }

    [Fact]
    public async Task タイムアウト_応答遅延_は既定_watchlist_へフォールバックする()
    {
        // HttpClient のタイムアウトを短くし、ハンドラを遅延させてタイムアウトを起こす（呼び出し側キャンセルではない）。
        var http = new HttpClient(new DelayingHandler(TimeSpan.FromSeconds(2)))
        {
            BaseAddress = new Uri("http://monitor"),
            Timeout = TimeSpan.FromMilliseconds(50),
        };
        var provider = new HttpWatchlistProvider(
            http, new FakeFallback(FallbackSymbols), NullLogger<HttpWatchlistProvider>.Instance);

        var result = await provider.GetWatchlistAsync();

        result.Should().BeEquivalentTo(FallbackSymbols);
    }

    private sealed class FakeFallback(IReadOnlyList<WatchedSymbol> symbols) : IWatchlistProvider
    {
        public Task<IReadOnlyList<WatchedSymbol>> GetWatchlistAsync(CancellationToken ct = default) =>
            Task.FromResult(symbols);
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
            throw new HttpRequestException("市場監視サービス不達");
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
