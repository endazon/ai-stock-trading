using System.Net;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests.MarketData;

// #158, FR-10, IADR-0068: Finnhub の実市況アダプタ（IMarketDataSource 実装）を fake HttpMessageHandler で検証する。
// 実 Finnhub API は叩かない（CI 緑と実基盤依存の切り分け・IADR-0049。実 API の確認は手動 opt-in の live 検証）。
public class FinnhubMarketDataSourceTests
{
    private const string OkBody = """{"c":150.25,"h":151.0,"l":149.0,"o":149.5,"pc":148.0,"t":1720000000}""";

    [Fact]
    public async Task 応答の現在値を_Quote_に写像する()
    {
        var handler = new StubHandler(HttpStatusCode.OK, OkBody);
        var source = Create(handler);

        var quote = await source.GetLatestQuoteAsync("AAPL", Market.UnitedStates);

        quote.Should().NotBeNull();
        quote!.Symbol.Should().Be("AAPL");
        quote.Market.Should().Be(Market.UnitedStates);
        quote.Price.Should().Be(150.25m);
        quote.AsOf.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1720000000));
    }

    [Fact]
    public async Task APIキーはURLではなくヘッダーで渡す()
    {
        // URL クエリに載せると OTel の HttpClient 計装がキーをトレースへ出力してしまう。
        var handler = new StubHandler(HttpStatusCode.OK, OkBody);
        var source = Create(handler);

        await source.GetLatestQuoteAsync("AAPL", Market.UnitedStates);

        handler.LastUrl.Should().Contain("symbol=AAPL");
        handler.LastUrl.Should().NotContain("key");
        handler.LastToken.Should().Be("key");
    }

    [Fact]
    public async Task 非成功応答は取得不可()
    {
        var source = Create(new StubHandler(HttpStatusCode.TooManyRequests, "rate limited"));

        var quote = await source.GetLatestQuoteAsync("AAPL", Market.UnitedStates);

        quote.Should().BeNull();
    }

    [Fact]
    public async Task 現在値ゼロは取得不可として扱う()
    {
        // Finnhub は未知の銘柄に 200＋全項目 0 を返す。0 を現在値として通すと建玉が全損として評価される。
        var source = Create(new StubHandler(HttpStatusCode.OK, """{"c":0,"h":0,"l":0,"o":0,"pc":0,"t":0}"""));

        var quote = await source.GetLatestQuoteAsync("NOPE", Market.UnitedStates);

        quote.Should().BeNull();
    }

    [Fact]
    public async Task 解析できない応答は取得不可として扱う()
    {
        var source = Create(new StubHandler(HttpStatusCode.OK, "<html>error page</html>"));

        var quote = await source.GetLatestQuoteAsync("AAPL", Market.UnitedStates);

        quote.Should().BeNull();
    }

    [Fact]
    public async Task 通信エラーは取得不可として扱い巡回を止めない()
    {
        // 市場監視は 1 銘柄の取得失敗を null＝スキップとして扱う契約（例外だと巡回全体＝損切り検知が落ちる）。
        var source = Create(new ThrowingHandler());

        var quote = await source.GetLatestQuoteAsync("AAPL", Market.UnitedStates);

        quote.Should().BeNull();
    }

    [Fact]
    public async Task 米国以外の市場は要求を出さずに取得不可()
    {
        // Finnhub 無料枠の /quote は米国株のみ。要求を出せばレート枠を無駄に消費する（IADR-0068 決定 5）。
        var handler = new StubHandler(HttpStatusCode.OK, OkBody);
        var source = Create(handler);

        var quote = await source.GetLatestQuoteAsync("7203", Market.Japan);

        quote.Should().BeNull();
        handler.Requests.Should().Be(0);
    }

    [Fact]
    public async Task 銘柄ごとに送信前レート制限を消費する()
    {
        var limiter = new CountingRateLimiter();
        var source = Create(new StubHandler(HttpStatusCode.OK, OkBody), limiter);

        await source.GetLatestQuoteAsync("AAPL", Market.UnitedStates);
        await source.GetLatestQuoteAsync("MSFT", Market.UnitedStates);

        limiter.Waits.Should().Be(2, "Finnhub Free の 60回/分 制限を送信前に自制する（IADR-0064）");
    }

    [Fact]
    public async Task キャンセルはそのまま伝播する()
    {
        // 停止要求は「取得不可」ではない（no-op へ倒すと停止が遅れる）。
        var source = Create(new StubHandler(HttpStatusCode.OK, OkBody));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => source.GetLatestQuoteAsync("AAPL", Market.UnitedStates, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // 🔴 T-10-838, FR-03, FR-10, #957, IADR-0399 決定3: 銘柄が無い照会は出さずに取得不可（null）を返し、例外を投げない。
    // FinnhubQuoteClient は銘柄 null で Uri.EscapeDataString が ArgumentNullException を投げ、それは本クラスの catch の対象外だった
    // （市場監視の巡回全体が落ち、全建玉の損切り検知が止まった。PR #959 監査の実測）。空・空白も照会しない（レート枠を消費しない）。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task T_10_838_銘柄が無い照会は要求を出さずに取得不可とし例外を投げない(string? symbol)
    {
        var handler = new StubHandler(HttpStatusCode.OK, OkBody);
        var limiter = new CountingRateLimiter();
        var source = Create(handler, limiter);

        var quote = await source.GetLatestQuoteAsync(symbol!, Market.UnitedStates);

        quote.Should().BeNull();
        handler.Requests.Should().Be(0);
        limiter.Waits.Should().Be(0, "レート枠を消費しない");
    }

    private static FinnhubMarketDataSource Create(HttpMessageHandler handler, IRateLimiter? limiter = null) =>
        new(
            new FinnhubQuoteClient(
                new HttpClient(handler), "key", limiter ?? new CountingRateLimiter(), NullLogger.Instance),
            NullLogger<FinnhubMarketDataSource>.Instance);

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastUrl { get; private set; }

        public string? LastToken { get; private set; }

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastUrl = request.RequestUri?.ToString();
            LastToken = request.Headers.TryGetValues("X-Finnhub-Token", out var values) ? values.FirstOrDefault() : null;
            Requests++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("接続できません");
    }

    private sealed class CountingRateLimiter : IRateLimiter
    {
        public int Waits { get; private set; }

        public Task WaitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Waits++;
            return Task.CompletedTask;
        }
    }
}
