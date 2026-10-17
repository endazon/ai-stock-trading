extern alias OrderExecutionWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using Xunit;
using DailyBarsStatus = OrderExecutionWorker::OrderExecutionService.Features.OrderExecution.QueryDailyBars.DailyBarsStatus;
using DailyBarsUnavailableReasons = OrderExecutionWorker::OrderExecutionService.Features.OrderExecution.QueryDailyBars.DailyBarsUnavailableReasons;
using DailyBarsView = OrderExecutionWorker::OrderExecutionService.Features.OrderExecution.QueryDailyBars.DailyBarsView;
using DailyBarView = OrderExecutionWorker::OrderExecutionService.Features.OrderExecution.QueryDailyBars.DailyBarView;
using InFlightClosesStatus = OrderExecutionWorker::OrderExecutionService.Features.OrderExecution.QueryInFlightCloses.InFlightClosesStatus;
using InFlightClosesView = OrderExecutionWorker::OrderExecutionService.Features.OrderExecution.QueryInFlightCloses.InFlightClosesView;
using OrderExecutionReadWireMapping = OrderExecutionWorker::OrderExecutionService.Features.OrderExecution.OrderExecutionReadWireMapping;
using Proto = AiStockTrading.Shared.Grpc.OrderExecution.V1;

namespace TradeDecisionService.Tests;

// T-10-2907・T-10-2908, NFR, FR-04, FR-10, FR-15, MSP:ADR-0029, IADR-0467 決定 2, IADR-0535 決定 2・3, IADR-0559 決定 2〜4, #1391 (#753):
// 取引判断が gRPC（`OrderExecutionRead/GetDailyBars`・`GetInFlightCloses`）で読む日足・処理中の決済の**契約**と**失敗の意味**。
// 🔴 解釈は REST（HttpDailyBarsSource.Interpret・HttpInFlightCloseProvider.Interpret）と同じ 1 つ。送り手の本物の型を、REST へは送り手の
// web 既定で、gRPC へは送り手の写し（OrderExecutionReadWireMapping）で出し、**両方の受け手が同じ答えを返す**ことを実 Kestrel の h2c で見る。
public class GrpcOrderExecutionReadTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateOnly From = new(2026, 8, 14);
    private static readonly DateOnly To = new(2026, 9, 28);
    private static readonly DailyBarView Bar1 = new(new DateOnly(2026, 9, 25), 250.5m, 252m, 249.25m, 251.75m, 41_234_567);
    private static readonly DailyBarView Bar2 = new(new DateOnly(2026, 9, 28), 251.75m, 255.5m, 250m, 254.125m, 52_345_678);

    private static ServiceProvider Compose(string address, Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?> { ["OrderExecution:Grpc"] = address };
        foreach (var (k, v) in extra ?? [])
            values[k] = v;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingOrderExecutionGrpc(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return services.BuildServiceProvider();
    }

    private static HttpClient RestClient(object view) =>
        new(new JsonStub(JsonSerializer.Serialize(view, Web))) { BaseAddress = new Uri("http://order-execution") };

    // ---- 日足（T-10-2907） ----

    private static async Task<(IReadOnlyList<DailyBar>? Bars, OrderExecutionReadStubBehavior Behavior)> FetchGrpcAsync(
        Func<int, CancellationToken, Task<Proto.GetDailyBarsResponse>> respond, Dictionary<string, string?>? extra = null)
    {
        var behavior = new OrderExecutionReadStubBehavior { DailyBars = respond };
        await using var host = await OrderExecutionReadStubHost.StartAsync(behavior);
        await using var sp = Compose(host.Address, extra);
        var source = new GrpcDailyBarsSource(sp.GetRequiredService<OrderExecutionGrpcTransport>(), NullLogger<GrpcDailyBarsSource>.Instance);
        return (await source.FetchAsync("AAPL", Market.UnitedStates, From, To, TestContext.Current.CancellationToken), behavior);
    }

    private static Task<IReadOnlyList<DailyBar>?> FetchRestAsync(DailyBarsView view) =>
        new HttpDailyBarsSource(RestClient(view), NullLogger<HttpDailyBarsSource>.Instance)
            .FetchAsync("AAPL", Market.UnitedStates, From, To, TestContext.Current.CancellationToken);

    [Fact]
    public async Task T_10_2907_送り手の型の日足を_REST_と同じ値で読み_要求は期間を運ぶ()
    {
        var view = new DailyBarsView("AAPL", Market.UnitedStates, DailyBarsStatus.Available, null, From, To, [Bar1, Bar2]);

        var (grpc, behavior) = await FetchGrpcAsync(OrderExecutionReadStubBehavior.Returns(OrderExecutionReadWireMapping.ToProto(view)));
        var rest = await FetchRestAsync(view);

        grpc.Should().NotBeNull().And.HaveCount(2);
        grpc.Should().Equal(rest!, "解釈は REST と同じ 1 つ（価格の 10 進も日付も同じ値）");
        behavior.LastDailyBars!.Should().Be(new Proto.GetDailyBarsRequest
        {
            Symbol = "AAPL",
            Market = Proto.Market.UnitedStates,
            From = "2026-08-14",
            To = "2026-09-28",
        });
    }

    // 「取得できない」の意味は REST と同じ: Unavailable・状態の未指定・別の銘柄・欄の欠けた足（読めない価格を含む）はどれも null。
    [Theory]
    [InlineData("Unavailable")]
    [InlineData("状態の未指定")]
    [InlineData("別の銘柄")]
    [InlineData("市場の未指定")]
    [InlineData("欄の欠けた足")]
    [InlineData("読めない価格")]
    public async Task T_10_2907_送り手が答えても読めない応答は未提供(string kind)
    {
        var ok = OrderExecutionReadWireMapping.ToProto(
            new DailyBarsView("AAPL", Market.UnitedStates, DailyBarsStatus.Available, null, From, To, [Bar1, Bar2]));
        var response = kind switch
        {
            "Unavailable" => OrderExecutionReadWireMapping.ToProto(new DailyBarsView(
                "AAPL", Market.UnitedStates, DailyBarsStatus.Unavailable, DailyBarsUnavailableReasons.RateLimited, From, To, [])),
            "状態の未指定" => Mutate(ok, r => r.Status = Proto.DailyBarsState.Unspecified),
            "別の銘柄" => Mutate(ok, r => r.Symbol = "MSFT"),
            "市場の未指定" => Mutate(ok, r => r.Market = Proto.Market.Unspecified),
            "欄の欠けた足" => Mutate(ok, r => r.Bars[1].ClearVolume()),
            "読めない価格" => Mutate(ok, r => r.Bars[0].Low = "not-a-number"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        (await FetchGrpcAsync(OrderExecutionReadStubBehavior.Returns(response))).Bars.Should().BeNull();
    }

    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.DeadlineExceeded)]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.InvalidArgument)]
    public async Task T_10_2907_照会の失敗は未提供で既定は再試行しない(StatusCode status)
    {
        var (bars, behavior) = await FetchGrpcAsync(OrderExecutionReadStubBehavior.Fails<Proto.GetDailyBarsResponse>(status));

        bars.Should().BeNull();
        behavior.DailyBarsCalls.Should().Be(1);
    }

    [Fact]
    public async Task T_10_2907_既定の_deadline_は_REST_と同じ8秒()
    {
        await using var sp = Compose("http://order-execution-service:8081");
        var transport = sp.GetRequiredService<OrderExecutionGrpcTransport>();

        transport.Timeout.Should().Be(HttpDailyBarsSource.RequestTimeout).And.Be(TimeSpan.FromSeconds(8));
        transport.MaxAttempts.Should().Be(1);
    }

    // ---- 処理中の決済（T-10-2908） ----

    private static async Task<(InFlightCloseCoverage? Coverage, OrderExecutionReadStubBehavior Behavior)> GetGrpcAsync(
        Func<int, CancellationToken, Task<Proto.GetInFlightClosesResponse>> respond)
    {
        var behavior = new OrderExecutionReadStubBehavior { InFlightCloses = respond };
        await using var host = await OrderExecutionReadStubHost.StartAsync(behavior);
        await using var sp = Compose(host.Address);
        var provider = new GrpcInFlightCloseProvider(
            sp.GetRequiredService<OrderExecutionGrpcTransport>(), NullLogger<GrpcInFlightCloseProvider>.Instance);
        return (await provider.GetAsync("AAPL", Market.UnitedStates, TradeSide.Sell, TestContext.Current.CancellationToken), behavior);
    }

    private static Task<InFlightCloseCoverage?> GetRestAsync(InFlightClosesView view) =>
        new HttpInFlightCloseProvider(RestClient(view), NullLogger<HttpInFlightCloseProvider>.Instance)
            .GetAsync("AAPL", Market.UnitedStates, TradeSide.Sell, TestContext.Current.CancellationToken);

    // 契約: 覆う（Covered）・覆わない（NotCovered）・分からない（Unknown）を REST と同じ答えで読む。要求は方向を名前で運ぶ。
    [Theory]
    [InlineData(InFlightClosesStatus.Covered, true)]
    [InlineData(InFlightClosesStatus.NotCovered, false)]
    [InlineData(InFlightClosesStatus.Unknown, null)]
    public async Task T_10_2908_送り手の型の処理中の決済を_REST_と同じ答えで読む(InFlightClosesStatus status, bool? covered)
    {
        var view = new InFlightClosesView(
            "AAPL", Market.UnitedStates, TradeSide.Sell, status,
            status == InFlightClosesStatus.Unknown ? "records-unavailable" : null,
            ClosableQuantity: 10,
            InFlightQuantity: status == InFlightClosesStatus.Covered ? 10 : 0,
            OrderIds: status == InFlightClosesStatus.Covered ? ["o-1"] : [],
            DecisionIds: status == InFlightClosesStatus.Covered ? [Guid.NewGuid()] : []);

        var (grpc, behavior) = await GetGrpcAsync(OrderExecutionReadStubBehavior.Returns(OrderExecutionReadWireMapping.ToProto(view)));
        var rest = await GetRestAsync(view);

        if (covered is null)
        {
            grpc.Should().BeNull("不明は LLM を呼ぶ（見送らない）");
            rest.Should().BeNull();
        }
        else
        {
            grpc.Should().BeEquivalentTo(rest, "解釈は REST と同じ 1 つ");
            grpc!.Covered.Should().Be(covered.Value);
        }

        behavior.LastInFlightCloses!.CloseSide.Should().Be(Proto.TradeSide.Sell, "C# の Sell（1）を線上の名前で運ぶ");
    }

    // 🔴 「覆う」を読み違えて見送る向きの誤りを作らない: 状態の未指定・別の方向・覆うのに株数の無い応答・照会の失敗はどれも不明。
    [Theory]
    [InlineData("状態の未指定")]
    [InlineData("別の方向")]
    [InlineData("方向の未指定")]
    [InlineData("覆うのに株数が無い")]
    [InlineData("失敗")]
    public async Task T_10_2908_読めない応答と照会の失敗は不明(string kind)
    {
        var covered = OrderExecutionReadWireMapping.ToProto(new InFlightClosesView(
            "AAPL", Market.UnitedStates, TradeSide.Sell, InFlightClosesStatus.Covered, null, 10, 10, ["o-1"], [Guid.NewGuid()]));
        var respond = kind switch
        {
            "状態の未指定" => OrderExecutionReadStubBehavior.Returns(Mutate(covered, r => r.Status = Proto.InFlightClosesState.Unspecified)),
            "別の方向" => OrderExecutionReadStubBehavior.Returns(Mutate(covered, r => r.CloseSide = Proto.TradeSide.Buy)),
            "方向の未指定" => OrderExecutionReadStubBehavior.Returns(Mutate(covered, r => r.CloseSide = Proto.TradeSide.Unspecified)),
            "覆うのに株数が無い" => OrderExecutionReadStubBehavior.Returns(Mutate(covered, r => r.ClearInFlightQuantity())),
            _ => OrderExecutionReadStubBehavior.Fails<Proto.GetInFlightClosesResponse>(StatusCode.Unavailable),
        };

        (await GetGrpcAsync(respond)).Coverage.Should().BeNull();
    }

    private static T Mutate<T>(T message, Action<T> change)
        where T : Google.Protobuf.IMessage<T>
    {
        var copy = message.Clone();
        change(copy);
        return copy;
    }

    private sealed class JsonStub(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
