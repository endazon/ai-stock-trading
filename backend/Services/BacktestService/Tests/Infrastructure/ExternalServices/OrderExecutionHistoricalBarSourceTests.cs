using System.Net;
using System.Text;
using BacktestService.Features.Backtest;
using BacktestService.Hosted;
using BacktestService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BacktestService.Tests;

// FR-15, ADR-0023 決定5, #1308, IADR-0528 決定8・9: 過去データ源 `order-execution`（発注執行の日足の照会）と、評価の窓の終端の構成。
//
// 利用者裁定 2026-10-13（#1219 のコメント）: Stage 0 の足は発注執行の `/daily-bars` 経由で取る（backtest の Pod に OpenD は配線しない）。
// 見るのは 3 点である。
//   1. 取れた足だけを使い、取れなかった銘柄は欠測に残す（半端に取れた分も捨てる）。🔴 欠測を無音にしない。
//   2. 選択は allow-list のまま（ベース URL が無い・不正なら no-op）。既定は none のまま。
//   3. 評価の窓の終端を構成で固定できる（空は今日・未来は今日・読めない値は今日）。
public class OrderExecutionHistoricalBarSourceTests
{
    private static readonly DateOnly From = new(2026, 9, 15);
    private static readonly DateOnly To = new(2026, 10, 9);

    [Fact]
    public async Task 銘柄ごとに日足の照会を呼び取れた足を使う()
    {
        var handler = new ScriptedHandler(req => Ok(Symbol(req), Available(Symbol(req),
            ("2026-09-15", 100m), ("2026-09-16", 101m), ("2026-08-01", 1m)))); // 期間外の足は捨てる
        var source = Source(handler);

        var load = await source.LoadBarsAsync(
            [("AAPL", Market.UnitedStates), ("MSFT", Market.UnitedStates)], From, To, TestContext.Current.CancellationToken);

        load.Gaps.Should().BeEmpty();
        load.Bars.Should().HaveCount(4);
        load.Bars.Where(b => b.Symbol == "AAPL").Select(b => b.Close).Should().Equal(100m, 101m);
        handler.Requests.Should().Equal(
            "/order-execution/daily-bars?symbol=AAPL&market=1&from=2026-09-15&to=2026-10-09",
            "/order-execution/daily-bars?symbol=MSFT&market=1&from=2026-09-15&to=2026-10-09");
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("http-500")]
    [InlineData("other-symbol")]
    [InlineData("missing-field")]
    [InlineData("no-status")]
    [InlineData("empty")]
    public async Task 取れなかった銘柄は欠測に残し他の銘柄は続ける(string mode)
    {
        var handler = new ScriptedHandler(req => Symbol(req) == "MSFT"
            ? Ok("MSFT", Available("MSFT", ("2026-09-15", 300m)))
            : mode switch
            {
                "unavailable" => Ok("AAPL", """{"symbol":"AAPL","market":1,"status":0,"unavailableReason":"broker-not-supported","bars":[]}"""),
                "http-500" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                "other-symbol" => Ok("AAPL", Available("NVDA", ("2026-09-15", 1m))),
                "missing-field" => Ok("AAPL", """{"symbol":"AAPL","market":1,"status":1,"bars":[{"date":"2026-09-15","open":1,"high":1,"low":1,"volume":1}]}"""),
                "no-status" => Ok("AAPL", """{"symbol":"AAPL","market":1,"bars":[]}"""),
                _ => Ok("AAPL", Available("AAPL")),
            });

        var load = await Source(handler).LoadBarsAsync(
            [("AAPL", Market.UnitedStates), ("MSFT", Market.UnitedStates)], From, To, TestContext.Current.CancellationToken);

        load.Gaps.Should().ContainSingle().Which.Symbol.Should().Be("AAPL");
        load.Bars.Should().ContainSingle().Which.Symbol.Should().Be("MSFT");
    }

    [Fact]
    public async Task 照会の上限を超える期間は分けて呼び_一部でも取れなければ銘柄ごと欠測にする()
    {
        var calls = 0;
        var handler = new ScriptedHandler(req =>
        {
            calls++;
            return calls == 1
                ? Ok("AAPL", Available("AAPL", ("2025-01-02", 10m)))
                : new HttpResponseMessage(HttpStatusCode.BadGateway);
        });

        var load = await Source(handler).LoadBarsAsync(
            [("AAPL", Market.UnitedStates)], new DateOnly(2025, 1, 1), new DateOnly(2026, 6, 30), TestContext.Current.CancellationToken);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Should().EndWith("from=2025-01-01&to=2026-02-04"); // 400 暦日（両端を含む）
        handler.Requests[1].Should().EndWith("from=2026-02-05&to=2026-06-30");
        load.Bars.Should().BeEmpty("半端に取れた分で評価しない");
        load.Gaps.Should().ContainSingle();
    }

    [Fact]
    public void 期間の分割は両端を含み隙間も重なりも無い()
    {
        var chunks = OrderExecutionHistoricalBarSource.Chunks(new DateOnly(2024, 1, 1), new DateOnly(2026, 12, 31)).ToList();

        chunks[0].From.Should().Be(new DateOnly(2024, 1, 1));
        chunks[^1].To.Should().Be(new DateOnly(2026, 12, 31));
        chunks.Should().OnlyContain(c => c.To.DayNumber - c.From.DayNumber + 1 <= OrderExecutionHistoricalBarSource.MaxRangeDays);
        chunks.Zip(chunks.Skip(1)).Should().OnlyContain(p => p.Second.From == p.First.To.AddDays(1));
        OrderExecutionHistoricalBarSource.Chunks(From, From).Should().Equal((From, From));
    }

    // ------------------------------------------------------------------------------------------------
    // 選択（allow-list）
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void provider_order_execution_はベースURLがあれば選ばれる()
    {
        var options = new BarDataOptions
        {
            Provider = "order-execution",
            OrderExecution = { BaseUrl = "http://order-execution-service:8080" },
        };

        HistoricalBarSourceFactory.ResolveProvider(options).Should().Be(HistoricalBarSourceFactory.OrderExecution);
        HistoricalBarSourceFactory.Create(
                options, new HttpClient(), TimeProvider.System, NullLoggerFactory.Instance, orderExecutionClient: new HttpClient())
            .Should().BeOfType<OrderExecutionHistoricalBarSource>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("ftp://order-execution-service")]
    public void ベースURLが無い_不正なら_no_op_へ倒す(string? baseUrl)
    {
        var options = new BarDataOptions { Provider = "order-execution", OrderExecution = { BaseUrl = baseUrl } };

        HistoricalBarSourceFactory.ResolveProvider(options).Should().Be(HistoricalBarSourceFactory.None);
        HistoricalBarSourceFactory.Create(options, new HttpClient(), TimeProvider.System, NullLoggerFactory.Instance)
            .Should().BeOfType<NoOpHistoricalBarSource>();
    }

    [Fact]
    public void provider_order_execution_なのにHttpClientが無ければ配線の誤りとして止める()
    {
        var options = new BarDataOptions { Provider = "order-execution", OrderExecution = { BaseUrl = "http://order-execution-service:8080" } };

        var act = () => HistoricalBarSourceFactory.Create(options, new HttpClient(), TimeProvider.System, NullLoggerFactory.Instance);

        act.Should().Throw<InvalidOperationException>();
    }

    // ------------------------------------------------------------------------------------------------
    // 評価の窓の終端（IADR-0528 決定9）
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "2026-10-13")] // 空は今日（従来どおり）
    [InlineData("", "2026-10-13")]
    [InlineData("2026-10-09", "2026-10-09")] // 記録の To に固定する
    [InlineData("2026-12-31", "2026-10-13")] // 未来は今日へ寄せる
    [InlineData("2026/10/09", "2026-10-13")] // 読めない値は今日へ倒す
    public void 評価の窓の終端を構成で固定できる(string? end, string expectedEnd)
    {
        var options = new Stage0EvaluationOptions { EvaluationEnd = end, LookbackDays = 24 };

        var (from, to) = options.EvaluationWindow(new DateOnly(2026, 10, 13));

        to.Should().Be(DateOnly.Parse(expectedEnd, System.Globalization.CultureInfo.InvariantCulture));
        from.Should().Be(to.AddDays(-24));
        options.HasUnparseableEvaluationEnd().Should().Be(end == "2026/10/09");
    }

    [Fact]
    public void 経路Bの想定値では窓は記録の窓と一致する()
    {
        var options = new Stage0EvaluationOptions { EvaluationEnd = "2026-10-09", LookbackDays = 24 };

        options.EvaluationWindow(new DateOnly(2026, 10, 20)).Should().Be((new DateOnly(2026, 9, 15), new DateOnly(2026, 10, 9)));
    }

    // ------------------------------------------------------------------------------------------------
    // 補助
    // ------------------------------------------------------------------------------------------------

    private static OrderExecutionHistoricalBarSource Source(ScriptedHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://order-execution-service:8080") },
            new NoWaitLimiter(),
            NullLogger<OrderExecutionHistoricalBarSource>.Instance);

    private static string Symbol(HttpRequestMessage request) =>
        System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["symbol"]!;

    private static string Available(string symbol, params (string Date, decimal Close)[] bars) =>
        $$"""{"symbol":"{{symbol}}","market":1,"status":1,"unavailableReason":null,"from":"2026-01-01","to":"2026-12-31","bars":[{{string.Join(",",
            bars.Select(b => $$"""{"date":"{{b.Date}}","open":{{b.Close}},"high":{{b.Close}},"low":{{b.Close}},"close":{{b.Close}},"volume":1000}"""))}}]}""";

    private static HttpResponseMessage Ok(string _, string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class NoWaitLimiter : IRateLimiter
    {
        public Task WaitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
