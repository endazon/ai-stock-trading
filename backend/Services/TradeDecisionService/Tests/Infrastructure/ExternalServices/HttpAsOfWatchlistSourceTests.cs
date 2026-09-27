using System.Net;
using System.Text;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;
using TradeDecisionService.Infrastructure.ExternalServices;
using Xunit;

namespace TradeDecisionService.Tests;

// 🔴 T-10-1628, FR-04, FR-15, ADR-0044 決定 3, ADR-0046 決定 1, #1049, IADR-0442 決定 3: 当時の監視銘柄の照会（HTTP）。
// 読めなかったことを空の一覧へ倒さない（原則 A）。実ネットワークは使わない（偽のハンドラ）。
public class HttpAsOfWatchlistSourceTests
{
    private static readonly DateTimeOffset At = new DateTimeOffset(2026, 9, 25, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_999);

    private static HttpAsOfWatchlistSource Source(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://monitor") }, NullLogger<HttpAsOfWatchlistSource>.Instance);

    [Fact]
    public async Task 再構成できた一覧を読み時刻をZ付きで送る()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"reconstructed":true,"symbols":[{"symbol":"META","market":1},{"symbol":"7203","market":0}],"basis":"after-change","basisChangedAt":"2026-09-25T18:10:00+00:00","seededAt":"2026-09-15T16:30:52+00:00","reason":null}""");

        var result = await Source(handler).GetWatchlistAtAsync(At);

        result.Symbols.Should().Equal(new WatchedSymbol("META", Market.UnitedStates), new WatchedSymbol("7203", Market.Japan));
        result.Reason.Should().BeNull();
        handler.Requested!.AbsolutePath.Should().Be("/monitor/watchlist/as-of");
        Uri.UnescapeDataString(handler.Requested.Query).Should().Be("?at=2026-09-25T23:59:59.9999999Z");
    }

    [Fact]
    public async Task 当時0件は空の一覧として読む()
    {
        var result = await Source(new StubHandler(HttpStatusCode.OK, """{"reconstructed":true,"symbols":[]}""")).GetWatchlistAtAsync(At);

        result.Symbols.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task 再構成できないと答えたら理由つきでできないを返す()
    {
        var result = await Source(new StubHandler(HttpStatusCode.OK,
            """{"reconstructed":false,"symbols":null,"seededAt":null,"reason":"SeededAt が記録されていません。"}""")).GetWatchlistAtAsync(At);

        result.Symbols.Should().BeNull();
        result.Reason.Should().Be("SeededAt が記録されていません。");
    }

    // 🔴 否定形: 読めない・欠けた応答はすべて「できない」（空の一覧にしない）。
    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"reconstructed":true,"symbols":[]}""")]
    [InlineData(HttpStatusCode.Forbidden, "")]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    [InlineData(HttpStatusCode.OK, "null")]
    [InlineData(HttpStatusCode.OK, """{"symbols":[{"symbol":"META","market":1}]}""")] // reconstructed の欠落
    [InlineData(HttpStatusCode.OK, """{"reconstructed":true}""")] // 一覧の欠落
    [InlineData(HttpStatusCode.OK, """{"reconstructed":true,"symbols":[null]}""")]
    [InlineData(HttpStatusCode.OK, """{"reconstructed":true,"symbols":[{"symbol":"","market":1}]}""")]
    [InlineData(HttpStatusCode.OK, """{"reconstructed":true,"symbols":[{"symbol":"META"}]}""")] // 市場の欠落
    [InlineData(HttpStatusCode.OK, """{"reconstructed":true,"symbols":[{"symbol":"META","market":99}]}""")] // 値域外
    [InlineData(HttpStatusCode.OK, "壊れた JSON")]
    public async Task 読めないか欠けた応答はできないを返す(HttpStatusCode status, string body)
    {
        var result = await Source(new StubHandler(status, body)).GetWatchlistAtAsync(At);

        result.Symbols.Should().BeNull();
        result.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task 例外と供給口自身の打ち切りはできないを返す()
    {
        (await Source(new ThrowingHandler(new HttpRequestException("接続できない"))).GetWatchlistAtAsync(At)).Symbols.Should().BeNull();
        (await Source(new ThrowingHandler(new TaskCanceledException("打ち切り"))).GetWatchlistAtAsync(At)).Reason.Should().Contain("タイムアウト");
    }

    [Fact]
    public async Task 呼び出し側のキャンセルは伝播する()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => Source(new ThrowingHandler(new TaskCanceledException("止めた"))).GetWatchlistAtAsync(At, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task 未結線の供給は常にできないを返す()
    {
        var result = await new UnwiredAsOfWatchlistSource().GetWatchlistAtAsync(At);

        result.Symbols.Should().BeNull();
        result.Reason.Should().Be(UnwiredAsOfWatchlistSource.Reason);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? Requested { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException<HttpResponseMessage>(exception);
        }
    }
}
