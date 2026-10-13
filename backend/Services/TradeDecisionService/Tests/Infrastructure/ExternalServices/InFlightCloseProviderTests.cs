extern alias OrderExecutionWorker;

using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OrderExecutionWorker::OrderExecutionService.Features.OrderExecution.QueryInFlightCloses;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using TradeDecisionService.Infrastructure.ExternalServices;
using Wolverine;
using Xunit;

namespace TradeDecisionService.Tests.Infrastructure.ExternalServices;

// T-10-2649, FR-04, FR-10, #1338, IADR-0535 決定2・3, IADR-0420: 判断側の処理中の決済の照会（Http / NoOp）と本番の配線。
// 🔴 **送り手（発注執行）の本物の型** `InFlightClosesView` を web 既定で直列化した本文を読ませる（手書きの JSON だけだと、送り手の項目名を
// 変えても両サービスの試験が緑のまま、実行時は既定値で読まれる）。🔴 失敗・不正な応答は **null（不明）**＝判断は LLM を呼ぶ。
// 「覆う」を読み違えて**見送る**向きの誤り（別銘柄・別方向の応答・欠落・未知の状態）を必ず不明へ倒す。
public class InFlightCloseProviderTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
                Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused"));
    }

    private static HttpInFlightCloseProvider Http(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://order-execution") }, NullLogger<HttpInFlightCloseProvider>.Instance);

    private static InFlightClosesView Covered() => new(
        "AAPL", Market.UnitedStates, TradeSide.Sell, InFlightClosesStatus.Covered, null, 13, 13, ["ORD-1"], [Guid.NewGuid()]);

    private static string Body(InFlightClosesView view) => JsonSerializer.Serialize(view, Web);

    [Fact]
    public async Task T_10_2649_送り手の本物の型を直列化した応答から覆うことを読み_銘柄市場方向を問いに載せる()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Body(Covered()));

        var read = await Http(handler).GetAsync("AAPL", Market.UnitedStates, TradeSide.Sell, TestContext.Current.CancellationToken);

        read.Should().NotBeNull();
        read!.Covered.Should().BeTrue();
        read.InFlightQuantity.Should().Be(13);
        read.OrderIds.Should().Equal("ORD-1");
        handler.Requests.Should().Equal("/order-execution/in-flight-closes?symbol=AAPL&market=1&closeSide=1");
    }

    [Fact]
    public async Task T_10_2649_覆わないは覆わないとして読み不明と取り違えない()
    {
        var view = Covered() with { Status = InFlightClosesStatus.NotCovered, InFlightQuantity = 5, ClosableQuantity = 13 };

        var read = await Http(new StubHandler(HttpStatusCode.OK, Body(view)))
            .GetAsync("AAPL", Market.UnitedStates, TradeSide.Sell, TestContext.Current.CancellationToken);

        read.Should().NotBeNull();
        read!.Covered.Should().BeFalse();
    }

    // 🔴 見送る向きの読み違いを不明へ倒す: 別の銘柄・市場・方向の答え、送り手の Unknown、項目の欠落、未知の状態、覆うのに株数・注文 ID が無い。
    [Theory]
    [InlineData("other-symbol")]
    [InlineData("other-market")]
    [InlineData("other-side")]
    [InlineData("unknown")]
    [InlineData("missing-status")]
    [InlineData("missing-closeSide")]
    [InlineData("unknown-status")]
    [InlineData("covered-without-quantity")]
    [InlineData("covered-without-orderIds")]
    public async Task T_10_2649_問いに答えていない応答は不明(string shape)
    {
        var node = JsonNode.Parse(Body(Covered()))!.AsObject();
        switch (shape)
        {
            case "other-symbol": node["symbol"] = "MSFT"; break;
            case "other-market": node["market"] = (int)Market.Japan; break;
            case "other-side": node["closeSide"] = (int)TradeSide.Buy; break;
            case "unknown": node["status"] = (int)InFlightClosesStatus.Unknown; node["unknownReason"] = "positions-indeterminate"; break;
            case "missing-status": node.Remove("status"); break;
            case "missing-closeSide": node.Remove("closeSide"); break;
            case "unknown-status": node["status"] = 99; break;
            case "covered-without-quantity": node.Remove("inFlightQuantity"); break;
            default: node.Remove("orderIds"); break;
        }

        var read = await Http(new StubHandler(HttpStatusCode.OK, node.ToJsonString()))
            .GetAsync("AAPL", Market.UnitedStates, TradeSide.Sell, TestContext.Current.CancellationToken);

        read.Should().BeNull(shape);
    }

    [Fact]
    public async Task T_10_2649_非2xxと例外は不明()
    {
        (await Http(new StubHandler(HttpStatusCode.ServiceUnavailable, "{}"))
            .GetAsync("AAPL", Market.UnitedStates, TradeSide.Sell, TestContext.Current.CancellationToken)).Should().BeNull();
        (await Http(new ThrowingHandler())
            .GetAsync("AAPL", Market.UnitedStates, TradeSide.Sell, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task T_10_2649_未結線のNoOpは常に不明()
    {
        (await new NoOpInFlightCloseProvider()
            .GetAsync("AAPL", Market.UnitedStates, TradeSide.Sell, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    // ---- 本番の配線（OrderExecution:BaseUrl → Http、無ければ NoOp。判断サービスへ届く） ----

    [Theory]
    [InlineData(null, typeof(NoOpInFlightCloseProvider))]
    [InlineData("not a url", typeof(NoOpInFlightCloseProvider))]
    [InlineData("http://order-execution", typeof(HttpInFlightCloseProvider))]
    public void T_10_2649_構成で実装を選び判断サービスへ届く(string? baseUrl, Type expected)
    {
        using var factory = new Factory(baseUrl);
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IInFlightCloseProvider>().Should().BeOfType(expected);

        // 🔴 登録だけでなく、判断サービスが実際に受け取っている（省略可能な引数のまま既定の NoOp に落ちていない）。
        var app = scope.ServiceProvider.GetRequiredService<TradeDecisionAppService>();
        var field = typeof(TradeDecisionAppService).GetField("_inFlightCloses", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.GetValue(app).Should().BeOfType(expected);
    }

    private sealed class Factory(string? orderExecutionBaseUrl) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["RabbitMq:ConnectionString"] = "amqp://localhost",
                    ["Otlp:Endpoint"] = "http://localhost:4317",
                };
                if (orderExecutionBaseUrl is not null)
                    settings["OrderExecution:BaseUrl"] = orderExecutionBaseUrl;
                cfg.AddInMemoryCollection(settings);
            });
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }
}
