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

    // FR-02, FR-04, ADR-0044 決定1, #1035, IADR-0451: 同じ応答の日中文脈（前日終値・始値 o・高値・安値）も Quote へ写す。
    [Fact]
    public async Task 応答の前日終値と始値と高安を_Quote_に写像する()
    {
        var source = Create(new StubHandler(HttpStatusCode.OK, OkBody));

        var quote = await source.GetLatestQuoteAsync("AAPL", Market.UnitedStates);

        quote.Should().NotBeNull();
        quote!.PreviousClose.Should().Be(148.0m);
        quote.Open.Should().Be(149.5m);
        quote.High.Should().Be(151.0m);
        quote.Low.Should().Be(149.0m);
    }

    // #1035, IADR-0451: 場前は始値・高安が 0 で返る。0 は値ではなく「無い」の表現であり null（不明）へ倒す（前日比が -100% になる）。
    // 始値 o が応答に無い場合も不明。
    [Theory]
    [InlineData("""{"c":150.25,"h":0,"l":0,"o":0,"pc":148.0,"t":1720000000}""")]
    [InlineData("""{"c":150.25,"h":0,"l":0,"pc":148.0,"t":1720000000}""")]
    public async Task 場前の0や欠けた項目は不明として写像する_否定形(string body)
    {
        var source = Create(new StubHandler(HttpStatusCode.OK, body));

        var quote = await source.GetLatestQuoteAsync("AAPL", Market.UnitedStates);

        quote.Should().NotBeNull();
        quote!.Price.Should().Be(150.25m);
        quote.PreviousClose.Should().Be(148.0m);
        quote.Open.Should().BeNull();
        quote.High.Should().BeNull();
        quote.Low.Should().BeNull();
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

    // NFR, FR-10, #1532, IADR-0614: 応答の時刻（Unix 秒）が表せる範囲の外なら、例外を呼び出し側（巡回・判断）へ抜けさせず取得不可にする。
    [Theory]
    [InlineData(253402300800L)] // 9999-12-31T23:59:59Z の次の秒（DateTimeOffset で表せない）
    [InlineData(9223372036854775807L)]
    [InlineData(-62135596801L)] // 0001-01-01 より前
    public async Task 範囲外の時刻の応答は取得不可として扱い例外を投げない(long t)
    {
        var source = Create(new StubHandler(HttpStatusCode.OK, $$"""{"c":150.25,"h":151.0,"l":149.0,"o":149.5,"pc":148.0,"t":{{t}}}"""));

        var act = async () => await source.GetLatestQuoteAsync("AAPL", Market.UnitedStates);

        (await act.Should().NotThrowAsync()).Subject.Should().BeNull();
    }

    // NFR, FR-10, #1532, IADR-0614: 未来の時刻の応答は鮮度の判定を素通りする（古い値が新しく見える）。許容（5 分）を超えたら取得不可。
    [Fact]
    public async Task 未来の時刻の応答は取得不可として扱う()
    {
        var clock = new FixedTime(DateTimeOffset.FromUnixTimeSeconds(1_780_000_000));
        var future = clock.GetUtcNow().Add(FinnhubQuoteClient.FutureTimestampTolerance).AddSeconds(1).ToUnixTimeSeconds();
        var source = Create(new StubHandler(HttpStatusCode.OK, $$"""{"c":150.25,"pc":148.0,"t":{{future}}}"""), timeProvider: clock);

        (await source.GetLatestQuoteAsync("AAPL", Market.UnitedStates)).Should().BeNull();
    }

    [Fact]
    public async Task 許容の内の未来の時刻はそのまま受け付ける_時計のずれを取得不可にしない()
    {
        var clock = new FixedTime(DateTimeOffset.FromUnixTimeSeconds(1_780_000_000));
        var near = clock.GetUtcNow().Add(FinnhubQuoteClient.FutureTimestampTolerance).ToUnixTimeSeconds();
        var source = Create(new StubHandler(HttpStatusCode.OK, $$"""{"c":150.25,"pc":148.0,"t":{{near}}}"""), timeProvider: clock);

        var quote = await source.GetLatestQuoteAsync("AAPL", Market.UnitedStates);

        quote.Should().NotBeNull();
        quote!.AsOf.Should().Be(DateTimeOffset.FromUnixTimeSeconds(near));
    }

    // NFR, #1532, IADR-0614: レート制限の応答のヘッダの時刻が範囲の外でも、例外を抜けさせず従来どおりスキップ（null）にする。
    [Fact]
    public async Task レート制限のヘッダの時刻が範囲外でも例外を投げない()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
        response.Headers.Add(FinnhubRateLimitClassifier.RemainingHeader, "0");
        response.Headers.Add(FinnhubRateLimitClassifier.ResetHeader, "9223372036854775807");
        var source = Create(new FixedResponseHandler(response));

        var act = async () => await source.GetLatestQuoteAsync("AAPL", Market.UnitedStates);

        (await act.Should().NotThrowAsync()).Subject.Should().BeNull();
    }

    // NFR, #1532, IADR-0614: 応答の本文が上限（ExternalHttpResponseLimits）を超えたら、その回の取得の失敗（null）にする。
    [Fact]
    public async Task 上限を超える応答は取得不可として扱う()
    {
        var http = new HttpClient(new StubHandler(HttpStatusCode.OK, OkBody.PadRight(4096)))
        {
            MaxResponseContentBufferSize = 1024,
        };
        var source = new FinnhubMarketDataSource(
            new FinnhubQuoteClient(http, "key", new CountingRateLimiter(), NullLogger.Instance),
            NullLogger<FinnhubMarketDataSource>.Instance);

        (await source.GetLatestQuoteAsync("AAPL", Market.UnitedStates)).Should().BeNull();
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

    private static FinnhubMarketDataSource Create(
        HttpMessageHandler handler, IRateLimiter? limiter = null, TimeProvider? timeProvider = null) =>
        new(
            new FinnhubQuoteClient(
                new HttpClient(handler), "key", limiter ?? new CountingRateLimiter(), NullLogger.Instance,
                timeProvider: timeProvider),
            NullLogger<FinnhubMarketDataSource>.Instance);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
    }

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
