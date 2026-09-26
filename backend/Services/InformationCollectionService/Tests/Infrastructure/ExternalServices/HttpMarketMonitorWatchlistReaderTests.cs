using System.Net;
using System.Text;
using AiStockTrading.Shared.Contracts.Trading;
using InformationCollectionService.Features.InformationCollection;
using InformationCollectionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InformationCollectionService.Tests;

// T-10-1467, FR-01, FR-13, #1015, IADR-0435: 市場監視の監視銘柄の読み口。🔴 どの失敗も「分からない」（null）で返し、空の一覧へ倒さない。
// 項目の欠けた行・値域外の市場が 1 つでもあれば一覧ごと「分からない」（既定値〔市場 0＝日本〕で読むと米国の銘柄が黙って外れる）。
public class HttpMarketMonitorWatchlistReaderTests
{
    [Fact]
    public async Task 読めた一覧を返し_パスは監視銘柄の照会口()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK,
            """[{"symbol":"AAPL","market":1},{"symbol":"7203","market":0}]""");

        var read = await Reader(handler).ReadAsync();

        read.Should().Equal(new WatchedSymbol("AAPL", Market.UnitedStates), new WatchedSymbol("7203", Market.Japan));
        handler.LastPath.Should().Be("/monitor/watchlist");
        handler.LastMethod.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task 空の一覧は空として返す()
    {
        var read = await Reader(new RecordingHandler(HttpStatusCode.OK, "[]")).ReadAsync();

        read.Should().NotBeNull().And.BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "[]")]
    [InlineData(HttpStatusCode.Forbidden, "[]")]
    [InlineData(HttpStatusCode.InternalServerError, "[]")]
    [InlineData(HttpStatusCode.OK, "null")]
    [InlineData(HttpStatusCode.OK, "not json")]
    // 欠けた行（銘柄・市場）・空の銘柄・null の行・値域外の市場。
    [InlineData(HttpStatusCode.OK, """[{"symbol":"AAPL","market":1},{"market":1}]""")]
    [InlineData(HttpStatusCode.OK, """[{"symbol":"AAPL"}]""")]
    [InlineData(HttpStatusCode.OK, """[{"symbol":"  ","market":1}]""")]
    [InlineData(HttpStatusCode.OK, """[null]""")]
    [InlineData(HttpStatusCode.OK, """[{"symbol":"AAPL","market":7}]""")]
    public async Task 失敗と読めない応答は不明であり空へ倒さない(HttpStatusCode status, string body)
    {
        var read = await Reader(new RecordingHandler(status, body)).ReadAsync();

        read.Should().BeNull();
    }

    [Fact]
    public async Task 例外は不明()
    {
        var read = await Reader(new ThrowingHandler(new HttpRequestException("down"))).ReadAsync();

        read.Should().BeNull();
    }

    [Fact]
    public async Task 呼び出し側の中止でないキャンセル_タイムアウト_は不明()
    {
        var read = await Reader(new ThrowingHandler(new TaskCanceledException("timeout"))).ReadAsync();

        read.Should().BeNull();
    }

    [Fact]
    public async Task 呼び出し側の中止は伝播する()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => Reader(new ThrowingHandler(new TaskCanceledException("cancelled"))).ReadAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static HttpMarketMonitorWatchlistReader Reader(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://market-monitor") },
            NullLogger<HttpMarketMonitorWatchlistReader>.Instance);

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastPath { get; private set; }

        public HttpMethod? LastMethod { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            LastMethod = request.Method;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}
