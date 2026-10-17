extern alias OrderExecutionWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using AwesomeAssertions;
using BacktestService.Features.Backtest;
using BacktestService.Infrastructure.ExternalServices;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using DailyBarsStatus = OrderExecutionWorker::OrderExecutionService.Features.OrderExecution.QueryDailyBars.DailyBarsStatus;
using DailyBarsUnavailableReasons = OrderExecutionWorker::OrderExecutionService.Features.OrderExecution.QueryDailyBars.DailyBarsUnavailableReasons;
using DailyBarsView = OrderExecutionWorker::OrderExecutionService.Features.OrderExecution.QueryDailyBars.DailyBarsView;
using DailyBarView = OrderExecutionWorker::OrderExecutionService.Features.OrderExecution.QueryDailyBars.DailyBarView;
using OrderExecutionReadWireMapping = OrderExecutionWorker::OrderExecutionService.Features.OrderExecution.OrderExecutionReadWireMapping;
using Proto = AiStockTrading.Shared.Grpc.OrderExecution.V1;

namespace BacktestService.Tests;

// T-10-2910, NFR, FR-15, ADR-0023 決定5, MSP:ADR-0029, IADR-0528 決定8, IADR-0559 決定 2〜4, #1391 (#753):
// バックテストの過去データ源 `order-execution` を gRPC（`OrderExecutionRead/GetDailyBars`）で読むときの**契約**と**失敗の意味**。
// 🔴 銘柄ごとの照会・期間の分割・欠測の作り方・応答の解釈は REST（HttpOrderExecutionHistoricalBarSource）と同じ 1 つ。
// 🔴 失敗の分け方も REST と同じ: 送り手が答えた失敗（REST の非 2xx）はその銘柄だけの欠測、届かない・待ちきれない失敗（REST の通信例外・
// タイムアウト）は送出する。送り手の本物の型を REST と gRPC の両方へ出し、同じ読み込み結果になることを実 Kestrel の h2c で見る。
public class GrpcOrderExecutionHistoricalBarSourceTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateOnly From = new(2026, 9, 15);
    private static readonly DateOnly To = new(2026, 10, 9);

    private static DailyBarsView Available(string symbol) => new(
        symbol, Market.UnitedStates, DailyBarsStatus.Available, null, From, To,
        [
            new DailyBarView(new DateOnly(2026, 9, 15), 250.5m, 252m, 249.25m, 251.75m, 41_234_567),
            new DailyBarView(new DateOnly(2026, 9, 16), 251.75m, 255.5m, 250m, 254.125m, 52_345_678),
            new DailyBarView(new DateOnly(2026, 8, 1), 1m, 1m, 1m, 1m, 1), // 期間外の足は捨てる（REST と同じ）
        ]);

    private static ServiceProvider Compose(string address)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingOrderExecutionGrpc(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OrderExecution:Grpc"] = address }).Build());
        return services.BuildServiceProvider();
    }

    private static Task<HistoricalBarLoad> LoadGrpcAsync(
        Func<int, CancellationToken, Task<Proto.GetDailyBarsResponse>> respond,
        IReadOnlyList<(string Symbol, Market Market)> symbols) =>
        LoadGrpcAsync(new OrderExecutionReadStubBehavior { DailyBars = respond }, symbols);

    private static async Task<HistoricalBarLoad> LoadGrpcAsync(
        OrderExecutionReadStubBehavior behavior, IReadOnlyList<(string Symbol, Market Market)> symbols)
    {
        await using var host = await OrderExecutionReadStubHost.StartAsync(behavior);
        await using var sp = Compose(host.Address);
        var source = new GrpcOrderExecutionHistoricalBarSource(
            sp.GetRequiredService<OrderExecutionGrpcTransport>(), new NoWaitLimiter(), NullLogger<GrpcOrderExecutionHistoricalBarSource>.Instance);
        return await source.LoadBarsAsync(symbols, From, To, TestContext.Current.CancellationToken);
    }

    private static Task<HistoricalBarLoad> LoadRestAsync(Func<string, DailyBarsView> answer, IReadOnlyList<(string Symbol, Market Market)> symbols) =>
        new HttpOrderExecutionHistoricalBarSource(
                new HttpClient(new JsonByRequest(answer)) { BaseAddress = new Uri("http://order-execution-service:8080") },
                new NoWaitLimiter(),
                NullLogger<HttpOrderExecutionHistoricalBarSource>.Instance)
            .LoadBarsAsync(symbols, From, To, TestContext.Current.CancellationToken);

    [Fact]
    public async Task T_10_2910_送り手の型の日足を_REST_と同じ読み込み結果で読む()
    {
        IReadOnlyList<(string, Market)> symbols = [("AAPL", Market.UnitedStates), ("MSFT", Market.UnitedStates)];

        OrderExecutionReadStubBehavior? behavior = null;
        behavior = new OrderExecutionReadStubBehavior
        {
            // 要求の銘柄の答えを返す（HandleDailyBars は応答を作る前に要求を記録する）。
            DailyBars = (_, _) => Task.FromResult(OrderExecutionReadWireMapping.ToProto(Available(behavior!.LastDailyBars!.Symbol))),
        };
        var grpc = await LoadGrpcAsync(behavior, symbols);
        var rest = await LoadRestAsync(Available, symbols);

        grpc.Gaps.Should().BeEmpty();
        grpc.Bars.Should().HaveCount(4);
        grpc.Bars.Should().Equal(rest.Bars, "解釈は REST と同じ 1 つ（期間外の足は捨てる・価格も日付も同じ値）");
        behavior.DailyBarsCalls.Should().Be(2, "銘柄ごとに 1 回");
        behavior.LastDailyBars!.Should().Be(new Proto.GetDailyBarsRequest
        {
            Symbol = "MSFT",
            Market = Proto.Market.UnitedStates,
            From = "2026-09-15",
            To = "2026-10-09",
        });
    }

    // 送り手が答えた「取れない」・契約の食い違いは、その銘柄だけの欠測（他の銘柄は続ける）。REST と同じ。
    [Theory]
    [InlineData("Unavailable")]
    [InlineData("状態の未指定")]
    [InlineData("欄の欠けた足")]
    [InlineData("INVALID_ARGUMENT")]
    [InlineData("PERMISSION_DENIED")]
    public async Task T_10_2910_送り手が答えた失敗はその銘柄だけの欠測(string kind)
    {
        IReadOnlyList<(string, Market)> symbols = [("AAPL", Market.UnitedStates), ("MSFT", Market.UnitedStates)];
        OrderExecutionReadStubBehavior? behavior = null;
        behavior = new OrderExecutionReadStubBehavior
        {
            DailyBars = (_, _) =>
            {
                // AAPL だけを壊す。
                if (behavior!.LastDailyBars!.Symbol != "AAPL")
                    return Task.FromResult(OrderExecutionReadWireMapping.ToProto(Available(behavior.LastDailyBars.Symbol)));

                var ok = OrderExecutionReadWireMapping.ToProto(Available("AAPL"));
                switch (kind)
                {
                    case "Unavailable":
                        return Task.FromResult(OrderExecutionReadWireMapping.ToProto(new DailyBarsView(
                            "AAPL", Market.UnitedStates, DailyBarsStatus.Unavailable, DailyBarsUnavailableReasons.RateLimited, From, To, [])));
                    case "状態の未指定":
                        ok.Status = Proto.DailyBarsState.Unspecified;
                        return Task.FromResult(ok);
                    case "欄の欠けた足":
                        ok.Bars[0].ClearClose();
                        return Task.FromResult(ok);
                    case "INVALID_ARGUMENT":
                        throw new RpcException(new Status(StatusCode.InvalidArgument, "stub"));
                    default:
                        throw new RpcException(new Status(StatusCode.PermissionDenied, "stub"));
                }
            },
        };

        var load = await LoadGrpcAsync(behavior, symbols);

        load.Gaps.Should().ContainSingle().Which.Symbol.Should().Be("AAPL");
        load.Bars.Should().HaveCount(2).And.OnlyContain(b => b.Symbol == "MSFT", "他の銘柄は続ける");
    }

    // 🔴 届かない・待ちきれない失敗は送出する（REST の通信例外・タイムアウトと同じ。欠測へ畳まない）。
    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.DeadlineExceeded)]
    public async Task T_10_2910_届かない失敗は送出する(StatusCode status)
    {
        var act = () => LoadGrpcAsync(
            OrderExecutionReadStubBehavior.Fails<Proto.GetDailyBarsResponse>(status), [("AAPL", Market.UnitedStates)]);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(status);
    }

    // 選択（allow-list は崩さない）: 宣言があれば BaseUrl が空でも order-execution を満たす。provider の明示は今までどおり要る。
    [Fact]
    public void T_10_2910_宣言は_BaseUrl_の代わりになるが_provider_の明示は要る()
    {
        var withoutBaseUrl = new BarDataOptions { Provider = "order-execution" };
        HistoricalBarSourceFactory.ResolveProvider(withoutBaseUrl).Should().Be(HistoricalBarSourceFactory.None);
        HistoricalBarSourceFactory.ResolveProvider(withoutBaseUrl, orderExecutionGrpcDeclared: true)
            .Should().Be(HistoricalBarSourceFactory.OrderExecution);
        HistoricalBarSourceFactory.ResolveProvider(new BarDataOptions(), orderExecutionGrpcDeclared: true)
            .Should().Be(HistoricalBarSourceFactory.None, "宣言だけでは過去データ源を選ばない");
    }

    private sealed class NoWaitLimiter : IRateLimiter
    {
        public Task WaitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class JsonByRequest(Func<string, DailyBarsView> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var symbol = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["symbol"]!;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(answer(symbol), Web), Encoding.UTF8, "application/json"),
            });
        }
    }
}
