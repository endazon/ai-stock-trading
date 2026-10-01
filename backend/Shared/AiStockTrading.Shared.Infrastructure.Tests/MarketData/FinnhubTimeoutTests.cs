using System.Diagnostics;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests.MarketData;

// FR-02, FR-10, #1133, IADR-0469: Finnhub が応答を返さない（TLS ハンドシェイクが止まる等）ときの現在値の照会。
// 打ち切り（HttpClient.Timeout）は「取得できない」（null）であり、呼び出し側の停止要求だけを OperationCanceledException で伝える。
public class FinnhubTimeoutTests
{
    // T-10-1862: 打ち切りの値は 1 か所で持つ（現在値 5 秒・情報収集 15 秒）。各サービスの組み立ての試験はこの値を引く。
    [Fact]
    public void T_10_1862_打ち切りの値は現在値5秒_情報収集15秒()
    {
        FinnhubHttpTimeouts.Quote.Should().Be(TimeSpan.FromSeconds(5));
        FinnhubHttpTimeouts.Collection.Should().Be(TimeSpan.FromSeconds(15));
        FinnhubHttpTimeouts.Quote.Should().BeLessThan(FinnhubHttpTimeouts.Collection, "判断の経路は収集より短く待つ");
    }

    // 🔴 T-10-1860: 応答が返らない Finnhub への照会は、打ち切りの時間で null（取得できない）になり、例外を投げない。
    [Fact]
    public async Task T_10_1860_応答が返らない照会は打ち切りの時間で取得不可になり例外を投げない()
    {
        using var handler = new HangingHandler();
        var source = Create(handler, TimeSpan.FromMilliseconds(200));

        var watch = Stopwatch.StartNew();
        var quote = await source.GetLatestQuoteAsync("AAPL", Market.UnitedStates, TestContext.Current.CancellationToken);
        watch.Stop();

        quote.Should().BeNull("打ち切りは「取得できない」であり、例外として呼び出し側（判断・巡回）へ渡さない");
        handler.Requests.Should().Be(1);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "打ち切り（200 ms）で返り、既定の 100 秒は待たない");
    }

    // 🔴 T-10-1861: 応答を待つ間に呼び出し側が止めたら、取得不可に化けず OperationCanceledException として伝わる。
    [Fact]
    public async Task T_10_1861_待つ間の呼び出し側の停止は取得不可に化けず伝わる()
    {
        using var handler = new HangingHandler();
        var source = Create(handler, TimeSpan.FromSeconds(30));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var call = source.GetLatestQuoteAsync("AAPL", Market.UnitedStates, cts.Token);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        var act = async () => await call;
        await act.Should().ThrowAsync<OperationCanceledException>("監視・判断の停止を「価格が取れない」と読み替えない");
    }

    private static FinnhubMarketDataSource Create(HttpMessageHandler handler, TimeSpan timeout) =>
        new(
            new FinnhubQuoteClient(
                new HttpClient(handler) { Timeout = timeout }, "key", new PassThroughRateLimiter(), NullLogger.Instance),
            NullLogger<FinnhubMarketDataSource>.Instance);

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

    private sealed class PassThroughRateLimiter : IRateLimiter
    {
        public Task WaitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
