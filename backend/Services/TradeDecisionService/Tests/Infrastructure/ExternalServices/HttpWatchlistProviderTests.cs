using System.Net;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-02, FR-13, UC-06, SC-02, IADR-0088/0095: 権威源（市場監視 #10）の GET /monitor/watchlist を s2s 同期照会する実装の
// 写像とフェイルセーフを fake HttpMessageHandler で検証する（実ネットワーク不使用）。
// 供給不達（非 2xx・timeout・例外・不正応答）は fallback（既定 watchlist）へ委譲する。
public class HttpWatchlistProviderTests
{
    // 打ち切りが効かなくなったときに、黙って固まる代わりに理由付きで赤くするための上限（合否の基準ではない）。
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static HttpWatchlistProvider Provider(HttpMessageHandler handler, IWatchlistProvider fallback) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://monitor") },
            fallback,
            NullLogger<HttpWatchlistProvider>.Instance);

    private static readonly IReadOnlyList<WatchedSymbol> FallbackSymbols =
        [new WatchedSymbol("FALLBACK", Market.Japan)];

    [Fact]
    public async Task 権威源の_watchlist_を_WatchedSymbol_に写像する()
    {
        // MonitoredSymbol（MarketMonitorService.Domain）と WatchedSymbol は同形。web 既定（camelCase・列挙は数値）で往復する。
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
        // #885, IADR-0379: 従来は「壁時計 50 ms の HttpClient.Timeout」対「壁時計 2 秒のハンドラ遅延」という
        // **時刻どうしの競争**で合否が決まっていた（#900 / #901 と同型。機序は IADR-0367）。
        // 🔴 遅延が勝つと 200 応答（本文 `[]`）が採られて既定へ倒れず**実際に赤くなる**（変異注入で実測）。
        // 応答が返らない上流に変え、打ち切りで終わったことを観測して確定させる。**上限値（50 ms）は動かしていない。**
        var handler = new NeverRespondingHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://monitor"),
            Timeout = TimeSpan.FromMilliseconds(50),
        };
        var provider = new HttpWatchlistProvider(
            http, new FakeFallback(FallbackSymbols), NullLogger<HttpWatchlistProvider>.Instance);

        // Guard は「打ち切りが効かない」ときに黙って固まらないための上限であり、合否の基準ではない。
        var result = await provider.GetWatchlistAsync().WaitAsync(Guard);

        result.Should().BeEquivalentTo(FallbackSymbols);
        (await handler.Cancellation.WaitAsync(Guard)).Should()
            .BeTrue("上限に達した要求は打ち切られる（応答は返っていない）");
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

    // #885, IADR-0379: 時間では応答しない上流。終わり方は打ち切り（＝要求トークンの発火）だけであり、
    // 「遅延が上限に勝つ」という競争そのものが存在しない。
    private sealed class NeverRespondingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _cancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // 要求が打ち切られたか（true＝上限で切られた）。テストはこれで「応答で終わっていない」ことを確定させる。
        public Task<bool> Cancellation => _cancellation.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _cancellation.TrySetResult(cancellationToken.IsCancellationRequested);
            }

            throw new InvalidOperationException("到達しない（無期限待ちは打ち切りでしか終わらない）。");
        }
    }
}
