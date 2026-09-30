extern alias OrderExecutionWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using OrderExecutionWorker::OrderExecutionService.Features.OrderExecution.QueryDailyBars;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-04, FR-15, ADR-0048 決定 2, #1118, IADR-0420, IADR-0467 決定 2（T-10-1836）: 日足の受け手（HttpDailyBarsSource）。
//
// 🔴 **送り手（発注執行）の本物の型** `DailyBarsView` を送り手の web 既定（camelCase・列挙は数値・日付は yyyy-MM-dd）で直列化した本文を読ませる
// （手書きの JSON だけだと、送り手の項目名を変えても両サービスの試験が緑のまま、実行時は既定値で読まれる＝#940 / #943）。
// 送り手がその設定で出していることは発注執行側の T-10-1837（DailyBarsReadContractTests）が本物の Program.cs で固定する。
public class HttpDailyBarsSourceTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateOnly From = new(2026, 8, 14);
    private static readonly DateOnly To = new(2026, 9, 28);

    private static readonly DailyBarView Bar1 = new(new DateOnly(2026, 9, 25), 250.5m, 252m, 249.25m, 251.75m, 41_234_567);
    private static readonly DailyBarView Bar2 = new(new DateOnly(2026, 9, 28), 251.75m, 255.5m, 250m, 254.125m, 52_345_678);

    private static HttpDailyBarsSource Source(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://order-execution") }, NullLogger<HttpDailyBarsSource>.Instance);

    private static DailyBarsView Available() =>
        new("AAPL", Market.UnitedStates, DailyBarsStatus.Available, null, From, To, [Bar1, Bar2]);

    /// <summary>T-10-1836: 送り手の本物の型を直列化した応答から OHLCV を読む。要求は銘柄・市場の数値・期間（yyyy-MM-dd）を運ぶ。</summary>
    [Fact]
    public async Task 送り手の本物の型を直列化した応答から日足を読む()
    {
        var handler = new Stub(HttpStatusCode.OK, JsonSerializer.Serialize(Available(), Web));

        var bars = await Source(handler).FetchAsync("AAPL", Market.UnitedStates, From, To, TestContext.Current.CancellationToken);

        bars.Should().Equal(
            new DailyBar(Bar1.Date, Bar1.Open, Bar1.High, Bar1.Low, Bar1.Close, Bar1.Volume),
            new DailyBar(Bar2.Date, Bar2.Open, Bar2.High, Bar2.Low, Bar2.Close, Bar2.Volume));
        handler.Requests.Should().ContainSingle()
            .Which.Should().Be("/order-execution/daily-bars?symbol=AAPL&market=1&from=2026-08-14&to=2026-09-28");
    }

    /// <summary>T-10-1836: 送り手の型の項目が 1 つ欠けても（改名の窓）、既定値で読まず null（取得できない）にする。</summary>
    [Theory]
    [InlineData("symbol")]
    [InlineData("market")]
    [InlineData("status")]
    [InlineData("bars")]
    [InlineData("bars[0].date")]
    [InlineData("bars[0].volume")]
    [InlineData("bars[1].low")]
    public async Task 送り手の項目が欠ければ取得できないとして読む(string missing)
    {
        var body = JsonSerializer.SerializeToNode(Available(), Web)!.AsObject();
        if (missing.StartsWith("bars[", StringComparison.Ordinal))
        {
            var index = missing[5] - '0';
            body["bars"]![index]!.AsObject().Remove(missing[(missing.IndexOf('.', StringComparison.Ordinal) + 1)..])
                .Should().BeTrue($"送り手の web 既定の項目名に {missing} がある");
        }
        else
        {
            body.Remove(missing).Should().BeTrue($"送り手の web 既定の項目名に {missing} がある");
        }

        var bars = await Source(new Stub(HttpStatusCode.OK, body.ToJsonString()))
            .FetchAsync("AAPL", Market.UnitedStates, From, To, TestContext.Current.CancellationToken);

        bars.Should().BeNull("欠けた足を捨てると本数が減って比の分母が変わる。1 本でも欠ければ全体を取得できないとする");
    }

    /// <summary>T-10-1836: 送り手の Unavailable・未定義の状態・別の銘柄／市場・非 2xx・壊れた本文・例外・タイムアウトはすべて null。</summary>
    [Theory]
    [InlineData("Unavailable")]
    [InlineData("未定義の状態")]
    [InlineData("別の銘柄")]
    [InlineData("別の市場")]
    [InlineData("500")]
    [InlineData("401")]
    [InlineData("壊れた本文")]
    [InlineData("例外")]
    [InlineData("タイムアウト")]
    public async Task 照会の失敗と契約の食い違いは取得できないとして読む(string kind)
    {
        var ok = Available();
        HttpMessageHandler handler = kind switch
        {
            "Unavailable" => new Stub(HttpStatusCode.OK, JsonSerializer.Serialize(
                ok with { Status = DailyBarsStatus.Unavailable, UnavailableReason = DailyBarsUnavailableReasons.QueryFailed, Bars = [] }, Web)),
            "未定義の状態" => new Stub(HttpStatusCode.OK, JsonSerializer.Serialize(ok with { Status = (DailyBarsStatus)9 }, Web)),
            "別の銘柄" => new Stub(HttpStatusCode.OK, JsonSerializer.Serialize(ok with { Symbol = "MSFT" }, Web)),
            "別の市場" => new Stub(HttpStatusCode.OK, JsonSerializer.Serialize(ok with { Market = Market.Japan }, Web)),
            "500" => new Stub(HttpStatusCode.InternalServerError, "{}"),
            "401" => new Stub(HttpStatusCode.Unauthorized, ""),
            "壊れた本文" => new Stub(HttpStatusCode.OK, "{not json"),
            "例外" => new Throwing(new HttpRequestException("connection refused")),
            "タイムアウト" => new Throwing(new TaskCanceledException("timeout")),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var bars = await Source(handler).FetchAsync("AAPL", Market.UnitedStates, From, To, TestContext.Current.CancellationToken);

        bars.Should().BeNull();
    }

    private sealed class Stub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Throwing(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}
