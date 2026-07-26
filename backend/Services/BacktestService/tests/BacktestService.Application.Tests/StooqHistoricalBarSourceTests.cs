using System.Net;
using AiStockTrading.Backtest.Application;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.Backtest.Application.Tests;

// FR-15, ADR-0004, #208, IADR-0105: Stooq 実過去データ源アダプタを fake HttpMessageHandler で検証する。
// 実 Stooq は叩かない（CI 緑と実基盤依存の切り分け・IADR-0049）。欠測は無音破棄せず Gaps に残ることを固定する。
public class StooqHistoricalBarSourceTests
{
    private static readonly (string Symbol, Market Market)[] TwoSymbols =
    [
        ("AAPL", Market.UnitedStates),
        ("7203", Market.Japan),
    ];

    private const string AppleCsv = """
        Date,Open,High,Low,Close,Volume
        2024-01-04,185.52,186.06,182.73,181.91,71983570
        """;

    private const string ToyotaCsv = """
        Date,Open,High,Low,Close,Volume
        2024-01-04,2600,2650,2580,2620,10000000
        """;

    [Fact]
    public async Task 複数銘柄の日足を取得する()
    {
        var handler = new RoutingHandler
        {
            ["aapl.us"] = (HttpStatusCode.OK, AppleCsv),
            ["7203.jp"] = (HttpStatusCode.OK, ToyotaCsv),
        };
        var source = Create(handler);

        var load = await source.LoadBarsAsync(TwoSymbols, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        load.Gaps.Should().BeEmpty();
        load.Bars.Should().HaveCount(2);
        load.Bars.Should().ContainSingle(b => b.Symbol == "AAPL" && b.Market == Market.UnitedStates && b.Close == 181.91m);
        load.Bars.Should().ContainSingle(b => b.Symbol == "7203" && b.Market == Market.Japan && b.Close == 2620m);
    }

    [Fact]
    public async Task 要求URLに市場記法と期間を載せる()
    {
        var handler = new RoutingHandler { ["aapl.us"] = (HttpStatusCode.OK, AppleCsv) };
        var source = Create(handler);

        await source.LoadBarsAsync([("AAPL", Market.UnitedStates)], new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        handler.Urls.Should().ContainSingle();
        handler.Urls[0].Should().Contain("s=aapl.us").And.Contain("d1=20240101").And.Contain("d2=20240131").And.Contain("i=d");
    }

    [Fact]
    public async Task 期間外のバーは返さない()
    {
        // 期間指定を無視した応答（データ源の揺れ）を素通しさせない。
        const string csv = """
            Date,Open,High,Low,Close,Volume
            2023-12-29,180,181,179,180.5,100
            2024-01-04,185.52,186.06,182.73,181.91,71983570
            2024-02-01,190,191,189,190.5,100
            """;
        var source = Create(new RoutingHandler { ["aapl.us"] = (HttpStatusCode.OK, csv) });

        var load = await source.LoadBarsAsync(
            [("AAPL", Market.UnitedStates)], new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        load.Bars.Should().ContainSingle();
        load.Bars[0].Date.Should().Be(new DateOnly(2024, 1, 4));
    }

    [Fact]
    public async Task 非成功応答は欠測として記録し他銘柄を続行する()
    {
        var handler = new RoutingHandler
        {
            ["aapl.us"] = (HttpStatusCode.TooManyRequests, "rate limited"),
            ["7203.jp"] = (HttpStatusCode.OK, ToyotaCsv),
        };
        var source = Create(handler);

        var load = await source.LoadBarsAsync(TwoSymbols, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        load.Bars.Should().ContainSingle(b => b.Symbol == "7203");
        load.Gaps.Should().ContainSingle();
        load.Gaps[0].Symbol.Should().Be("AAPL");
        load.Gaps[0].Market.Should().Be(Market.UnitedStates);
        load.Gaps[0].Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task 解析できない応答は欠測として記録する()
    {
        var handler = new RoutingHandler
        {
            ["aapl.us"] = (HttpStatusCode.OK, "No data"),
            ["7203.jp"] = (HttpStatusCode.OK, ToyotaCsv),
        };
        var source = Create(handler);

        var load = await source.LoadBarsAsync(TwoSymbols, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        load.Bars.Should().ContainSingle(b => b.Symbol == "7203");
        load.Gaps.Should().ContainSingle(g => g.Symbol == "AAPL");
    }

    [Fact]
    public async Task 写像できない銘柄は外部へ要求せず欠測として記録する()
    {
        var handler = new RoutingHandler();
        var source = Create(handler);

        var load = await source.LoadBarsAsync(
            [("", Market.UnitedStates)], new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        handler.Urls.Should().BeEmpty();
        load.Bars.Should().BeEmpty();
        load.Gaps.Should().ContainSingle();
    }

    [Fact]
    public async Task 送信前にレート制御を通す()
    {
        // 429 を受けてから対処するのでは規約違反そのものを防げない（IADR-0064）。取得回数＝銘柄数。
        var limiter = new CountingRateLimiter();
        var handler = new RoutingHandler
        {
            ["aapl.us"] = (HttpStatusCode.OK, AppleCsv),
            ["7203.jp"] = (HttpStatusCode.OK, ToyotaCsv),
        };
        var source = Create(handler, limiter);

        await source.LoadBarsAsync(TwoSymbols, new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        limiter.Waits.Should().Be(2);
    }

    [Fact]
    public async Task 通信例外は握りつぶさず送出する()
    {
        // 取得できなければバックテストは完走せず verdict も出ない＝昇格しない（fail-safe）。
        var source = Create(new ThrowingHandler());

        var act = async () => await source.LoadBarsAsync(
            [("AAPL", Market.UnitedStates)], new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task 銘柄が空なら外部へ要求しない()
    {
        var handler = new RoutingHandler();
        var source = Create(handler);

        var load = await source.LoadBarsAsync([], new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 31));

        handler.Urls.Should().BeEmpty();
        load.Bars.Should().BeEmpty();
        load.Gaps.Should().BeEmpty();
    }

    private static StooqHistoricalBarSource Create(HttpMessageHandler handler, IRateLimiter? limiter = null) =>
        new(new HttpClient(handler),
            limiter ?? new CountingRateLimiter(),
            NullLogger<StooqHistoricalBarSource>.Instance,
            "https://stooq.example");

    // 銘柄記法（クエリの s=）で応答を切り替えるスタブ。実ネットワークへは一切出ない。
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses = [];

        public List<string> Urls { get; } = [];

        public (HttpStatusCode Status, string Body) this[string stooqSymbol]
        {
            set => _responses[stooqSymbol] = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Urls.Add(url);

            var match = _responses.FirstOrDefault(r => url.Contains($"s={r.Key}", StringComparison.Ordinal));
            var (status, body) = match.Key is null ? (HttpStatusCode.NotFound, "No data") : match.Value;

            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("network down");
    }

    private sealed class CountingRateLimiter : IRateLimiter
    {
        public int Waits { get; private set; }

        public Task WaitAsync(CancellationToken cancellationToken = default)
        {
            Waits++;
            return Task.CompletedTask;
        }
    }
}
