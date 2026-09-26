using System.Net;
using System.Text;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using InformationCollectionService.Common.Abstractions;
using InformationCollectionService.Features.InformationCollection;
using InformationCollectionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InformationCollectionService.Tests;

// FR-01, FR-13, #1015, IADR-0435: 取得の前に 1 回だけ対象銘柄を決め直す装飾（T-10-1469）と、
// 同じ鍵の現在値・企業ニュースが 1 つのバケットを共有すること（T-10-1470）。
public class WatchlistFollowingSourceFetcherTests
{
    // T-10-1469: 取得の前に 1 回だけ照会し、取得（Finnhub のソース）はその集合を見る。巡回ごとに照会し直す。
    [Fact]
    public async Task 取得の前に1回だけ照会しソースは決め直した集合を見る()
    {
        var reader = new CountingReader(
            [new WatchedSymbol("MSFT", Market.UnitedStates)],
            [new WatchedSymbol("NVDA", Market.UnitedStates), new WatchedSymbol("META", Market.UnitedStates)]);
        using var metrics = new BusinessMetrics();
        var selector = new FinnhubSymbolSelector(
            reader, ["AAPL"], maxSymbolsPerCycle: 100, metrics, NullLogger<FinnhubSymbolSelector>.Instance);
        var inner = new SnapshotFetcher(selector);
        var fetcher = new WatchlistFollowingSourceFetcher(inner, selector);

        await fetcher.FetchAllAsync();
        await fetcher.FetchAllAsync();

        reader.Calls.Should().Be(2);
        inner.Seen.Should().HaveCount(2);
        inner.Seen[0].Should().Equal("MSFT");
        inner.Seen[1].Should().Equal("NVDA", "META");
    }

    // T-10-1470: `finnhub` と `finnhub-news` は 1 つのバケットを共有する（容量 1 回/分なら、現在値の 1 要求の後の企業ニュースは待たされる）。
    // 🔴 別々のバケットだと、同じ鍵へ自制値の 2 倍を送り得る（プロセスの自制レートが 1 つに定まらない）。
    // 待ちは実時間の遅延（1 分）なので、待たされていることを「送られていない・完了していない」で観測し、中止して畳む（壁時計の競争にしない）。
    [Fact]
    public async Task 現在値と企業ニュースは1つのバケットを共有する()
    {
        var http = new RoutingHandler();
        var sources = InformationSourceFactory.Create(
            new CollectionSourceOptions
            {
                Provider = "finnhub,finnhub-news",
                Finnhub = new FinnhubOptions { ApiKey = "key", Symbols = ["AAPL"], RateLimitPerMinute = 1 },
            },
            new HttpClient(http),
            new FixedClock(),
            TimeProvider.System,
            NullLoggerFactory.Instance);
        var quote = sources.Single(s => s.Name == "finnhub").Source;
        var news = sources.Single(s => s.Name == "finnhub-news").Source;

        (await quote.FetchAsync()).Should().ContainSingle();
        http.Requests.Should().Be(1);

        using var cts = new CancellationTokenSource();
        var pending = news.FetchAsync(cts.Token);

        pending.IsCompleted.Should().BeFalse("企業ニュースは現在値と同じバケットの次のトークンを待つ");
        http.Requests.Should().Be(1, "待っている間は送らない");

        await cts.CancelAsync();
        var act = async () => await pending;
        await act.Should().ThrowAsync<OperationCanceledException>();
        http.Requests.Should().Be(1);
    }

    // 呼ぶたびに台本の次の応答を返し、呼ばれた回数を数える。
    private sealed class CountingReader(params IReadOnlyList<WatchedSymbol>[] script) : IWatchlistReader
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<WatchedSymbol>?> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WatchedSymbol>?>(script[Math.Min(Calls++, script.Length - 1)]);
    }

    // 取得の時点の集合を記録するだけの取得器。
    private sealed class SnapshotFetcher(IFinnhubSymbolSet symbols) : ISourceFetcher
    {
        public List<IReadOnlyList<string>> Seen { get; } = [];

        public Task<SourceFetchResult> FetchAllAsync(CancellationToken cancellationToken = default)
        {
            Seen.Add(symbols.Current);
            return Task.FromResult(SourceFetchResult.Empty);
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
    }

    // 現在値と企業ニュースを URL で答え分ける（実ネットワーク不使用）。
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            var body = request.RequestUri!.AbsolutePath.EndsWith("/quote", StringComparison.Ordinal)
                ? """{"c":150.25,"h":151.0,"l":149.0,"o":149.5,"pc":148.0,"t":1720000000}"""
                : "[]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
