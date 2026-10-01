using System.Diagnostics;
using AwesomeAssertions;
using InformationCollectionService.Common.Abstractions;
using InformationCollectionService.Infrastructure.ExternalServices;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InformationCollectionService.Tests;

// FR-01, ADR-0020 決定3, #1133, IADR-0469: Finnhub が応答を返さないときの情報収集の巡回。
// 打ち切りはソース単位の欠測（従来の通信失敗と同じ経路）であり、呼び出し側の停止要求だけは巡回ごと伝える。
public class FinnhubCollectionTimeoutTests
{
    // 🔴 T-10-1866: 応答が返らない Finnhub は、打ち切りの時間で「このソースは欠測」になり、他のソースの取得へ進む。
    [Fact]
    public async Task T_10_1866_応答が返らないFinnhubは打ち切りの時間で欠測になる()
    {
        using var handler = new HangingHandler();
        var runner = Runner(handler, TimeSpan.FromMilliseconds(200));

        var watch = Stopwatch.StartNew();
        var result = await runner.FetchAllAsync(TestContext.Current.CancellationToken);
        watch.Stop();

        result.Outcomes.Should().ContainSingle(o => o.Name == "finnhub").Which.Succeeded.Should().BeFalse();
        result.Items.Should().BeEmpty();
        handler.Requests.Should().BeGreaterThan(0);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "打ち切り（200 ms）で返り、既定の 100 秒は待たない");
    }

    // 🔴 T-10-1867: 応答を待つ間に呼び出し側が止めたら、欠測に化けず OperationCanceledException として巡回ごと伝わる。
    [Fact]
    public async Task T_10_1867_待つ間の呼び出し側の停止は欠測に化けず伝わる()
    {
        using var handler = new HangingHandler();
        var runner = Runner(handler, TimeSpan.FromSeconds(30));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var call = runner.FetchAllAsync(cts.Token);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        var act = async () => await call;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static SourceFetchRunner Runner(HttpMessageHandler handler, TimeSpan timeout) =>
        new(
            InformationSourceFactory.Create(
                new CollectionSourceOptions
                {
                    Provider = "finnhub",
                    Finnhub = new FinnhubOptions { ApiKey = "key", Symbols = ["AAPL"] },
                },
                new HttpClient(handler) { Timeout = timeout },
                new FixedClock(),
                TimeProvider.System,
                NullLoggerFactory.Instance),
            NullLogger<SourceFetchRunner>.Instance);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    }

    /// <summary>応答を返さない（取り消されるまで待ち続ける）ハンドラ。TLS ハンドシェイクで止まる相手の代わり。</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("到達しない");
        }
    }
}
