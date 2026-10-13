using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.QueryInFlightCloses;
using OrderExecutionService.Infrastructure.Persistence;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

namespace OrderExecutionService.Tests;

// 🔴 T-10-2646〜T-10-2648, FR-04, FR-10, #1338, IADR-0535 決定2: 処理中の決済の照会の口（取引判断が LLM の前に読む）。
// **判定は決済のゲートと同じ関数（InFlightCloses）**であり、ゲートが InFlightCloseCoversPosition で見送るときにだけ Covered を答える。
//   - 窓の両端（規則 11）: 発注の直後（P+）は覆う。約定の直後（P−1）・追跡の打ち切りの後（P−2）・終端（P0）は覆わない。
//   - 失敗はどれも Unknown／NotCovered（受け手は LLM を呼ぶ）へ倒す。処理中の決済が無ければブローカーへ照会しない。
public class InFlightClosesQueryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 13, 42, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // 建玉照会と注文照会を持つブローカー（moomoo 相当）。注文照会は Orders に登録した注文だけを返す（未登録は null＝不明）。
    private sealed class FakeBroker(IReadOnlyList<BrokerPositionSnapshot>? positions)
        : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } = positions;

        public Dictionary<string, BrokerOrder> Orders { get; } = [];

        public List<OrderIntent> Placed { get; } = [];

        public int PositionQueryCount { get; private set; }

        public int OrderQueryCount { get; private set; }

        public Action? DuringPositionQuery { get; set; }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default)
        {
            PositionQueryCount++;
            DuringPositionQuery?.Invoke();
            return Task.FromResult(Positions);
        }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            Placed.Add(intent);
            return Task.FromResult(new BrokerOrder("ORD-" + Placed.Count, intent, OrderStatus.Filled, intent.Quantity, intent.Price, Now, Now));
        }

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            throw new InvalidOperationException("本試験では逆指値を送らない");

        public Task<BrokerOrder> PlaceMarketOrderAsync(OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default) =>
            PlaceOrderAsync(closeIntent, ct);

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default)
        {
            OrderQueryCount++;
            return Task.FromResult(Orders.TryGetValue(orderId, out var order) ? order : null);
        }

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;

        public void Live(string orderId, OrderStatus status = OrderStatus.Accepted, int filled = 0) =>
            Orders[orderId] = new BrokerOrder(orderId, Close(0), status, filled, filled > 0 ? 347.93m : 0m, Now, null);
    }

    private static OrderIntent Close(int qty, TradeSide side = TradeSide.Sell) =>
        new("AAPL", Market.UnitedStates, side, ProductType.Cash, BrokerProvider.MoomooSimulate, qty, 347.93m, PositionEffect.Close);

    private static BrokerPositionSnapshot Position(int signedQuantity) => new("AAPL", Market.UnitedStates, signedQuantity, 330m);

    private static ExecutionRecord CloseRecord(
        string orderId, int qty, OrderStatus status = OrderStatus.Accepted, int filled = 0, Guid? decisionId = null,
        TradeSide side = TradeSide.Sell) =>
        new(decisionId ?? Guid.NewGuid(), orderId, "AAPL", Market.UnitedStates, side, ProductType.Cash, PositionEffect.Close,
            qty, 347.93m, filled, filled > 0 ? 347.93m : 0m, status, 0m, Now.AddMinutes(-5));

    private static ProtectiveStopOrder S0Leg(int qty, string stopOrderId, Guid stopDecisionId) =>
        new(Guid.NewGuid(), stopDecisionId, stopOrderId, "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, qty, 320m, 1m, Attempt: 1, ProtectiveStopState.Active,
            Now.AddDays(-1), Now.AddDays(-1), Mechanism: StopLossExecutionMethod.BrokerStopOrder);

    private sealed record Fixture(
        InFlightClosesQueryService Query, AppSvc Gate, FakeBroker Broker, InMemoryExecutedOrderStore Store, InMemoryProtectiveStopOrderStore Stops);

    private static Fixture NewFixture(IReadOnlyList<BrokerPositionSnapshot>? positions, bool brokerPositions = true, bool protectiveStops = true)
    {
        var broker = new FakeBroker(positions);
        var store = new InMemoryExecutedOrderStore();
        var stops = new InMemoryProtectiveStopOrderStore();
        var query = new InFlightClosesQueryService(
            store, broker, NullLogger<InFlightClosesQueryService>.Instance,
            protectiveStops ? stops : null, brokerPositions ? broker : null);
        var gate = new AppSvc(
            broker, store, new InMemoryOrderReservationStore(), new FakeClock(), protectiveStops ? stops : null, null,
            brokerPositions ? broker : null);
        return new Fixture(query, gate, broker, store, stops);
    }

    private static Task<InFlightClosesView> Ask(Fixture f, TradeSide closeSide = TradeSide.Sell) =>
        f.Query.QueryAsync("AAPL", Market.UnitedStates, closeSide, TestContext.Current.CancellationToken);

    // ---- T-10-2646: 窓の両端（規則 11 の表の P+・P−1・P−2・P0） ----

    // P+（増える側）: 決済を発注した直後（記録は Accepted・ブローカーも生きている）→ 覆う。注文 ID と判断 ID を返す。
    [Fact]
    public async Task T_10_2646_発注の直後は処理中の決済が建玉を覆う()
    {
        var f = NewFixture([Position(13)]);
        var record = CloseRecord("AAPL-SELL-1", 13);
        f.Store.Save(record);
        f.Broker.Live("AAPL-SELL-1");

        var view = await Ask(f);

        view.Status.Should().Be(InFlightClosesStatus.Covered);
        view.ClosableQuantity.Should().Be(13);
        view.InFlightQuantity.Should().Be(13);
        view.OrderIds.Should().Equal("AAPL-SELL-1");
        view.DecisionIds.Should().Equal(record.DecisionId);
    }

    // P−1（減る側）: ブローカーは約定（終端）と答えるが約定追跡がまだ記録を書いていない（記録は Accepted）→ 覆わない。
    // P−2（減る側）: 追跡を打ち切った記録（非終端のまま）だが、ブローカーは失効・取消と答える → 覆わない。照会できない（null）も同じ。
    [Theory]
    [InlineData("filled")]
    [InlineData("expired")]
    [InlineData("cancelled")]
    [InlineData("unknown-to-broker")]
    public async Task T_10_2646_約定や終端の直後は記録が古くても覆わない(string brokerAnswer)
    {
        var f = NewFixture([Position(13)]);
        f.Store.Save(CloseRecord("AAPL-SELL-1", 13));
        switch (brokerAnswer)
        {
            case "filled": f.Broker.Live("AAPL-SELL-1", OrderStatus.Filled, 13); break;
            case "expired": f.Broker.Live("AAPL-SELL-1", OrderStatus.Expired); break;
            case "cancelled": f.Broker.Live("AAPL-SELL-1", OrderStatus.Cancelled); break;
            default: break; // 未登録＝ブローカーは null を返す
        }

        var view = await Ask(f);

        view.Status.Should().Be(InFlightClosesStatus.NotCovered, brokerAnswer);
        view.InFlightQuantity.Should().Be(0);
    }

    // P0: 記録も終端 → 候補が無いので覆わない。建玉にも注文にも照会しない（処理中の決済が無い通常時に OpenD を叩かない）。
    [Fact]
    public async Task T_10_2646_終端の記録だけならブローカーへ照会せず覆わない()
    {
        var f = NewFixture([Position(13)]);
        f.Store.Save(CloseRecord("AAPL-SELL-1", 13, OrderStatus.Filled, 13));

        var view = await Ask(f);

        view.Status.Should().Be(InFlightClosesStatus.NotCovered);
        f.Broker.PositionQueryCount.Should().Be(0);
        f.Broker.OrderQueryCount.Should().Be(0);
    }

    // 🔴 S0 / S3 の保護レグ（全量・非終端の Close）は数えない（数えると S0 の建玉は常に覆うと読み、決済の判断を止める）。
    [Fact]
    public async Task T_10_2646_ブローカー側の保護レグは処理中の決済に数えない()
    {
        var f = NewFixture([Position(13)]);
        var legDecisionId = Guid.NewGuid();
        f.Stops.Save(S0Leg(13, "STOP-1", legDecisionId));
        f.Store.Save(CloseRecord("STOP-1", 13, decisionId: legDecisionId));
        f.Broker.Live("STOP-1");

        var view = await Ask(f);

        view.Status.Should().Be(InFlightClosesStatus.NotCovered);
        view.InFlightQuantity.Should().Be(0);
    }

    // 🔴 建玉照会の後に候補を読み直す（IADR-0461 決定4）。照会の最中に保存された決済も数える。
    [Fact]
    public async Task T_10_2646_建玉照会の最中に保存された決済も数える()
    {
        var f = NewFixture([Position(13)]);
        f.Store.Save(CloseRecord("AAPL-SELL-0", 5));
        f.Broker.Live("AAPL-SELL-0");
        f.Broker.DuringPositionQuery = () =>
        {
            f.Store.Save(CloseRecord("AAPL-SELL-1", 8));
            f.Broker.Live("AAPL-SELL-1");
        };

        var view = await Ask(f);

        view.Status.Should().Be(InFlightClosesStatus.Covered);
        view.InFlightQuantity.Should().Be(13);
    }

    // ---- T-10-2647: 倒し方（Unknown・NotCovered）と部分 ----

    [Theory]
    [InlineData("no-position-source")]
    [InlineData("no-protective-store")]
    public async Task T_10_2647_建玉照会の能力や保護記録ストアが無い構成はUnknown(string missing)
    {
        var f = NewFixture([Position(13)], brokerPositions: missing != "no-position-source", protectiveStops: missing != "no-protective-store");
        f.Store.Save(CloseRecord("AAPL-SELL-1", 13));
        f.Broker.Live("AAPL-SELL-1");

        var view = await Ask(f);

        view.Status.Should().Be(InFlightClosesStatus.Unknown);
        view.UnknownReason.Should().Be(InFlightClosesUnknownReasons.BrokerNotSupported);
        f.Broker.OrderQueryCount.Should().Be(0);
    }

    [Fact]
    public async Task T_10_2647_建玉を照会できなければUnknown()
    {
        var f = NewFixture(positions: null);
        f.Store.Save(CloseRecord("AAPL-SELL-1", 13));
        f.Broker.Live("AAPL-SELL-1");

        var view = await Ask(f);

        view.Status.Should().Be(InFlightClosesStatus.Unknown);
        view.UnknownReason.Should().Be(InFlightClosesUnknownReasons.PositionsIndeterminate);
    }

    // 🔴 部分（建玉 20 株・処理中 13 株）は覆わない＝残りの 7 株の決済（建玉を超える判断）を塞がない。決済方向の建玉 0 も覆わない（ゲートは別の理由）。
    [Theory]
    [InlineData(20, 13, InFlightClosesStatus.NotCovered)]
    [InlineData(13, 13, InFlightClosesStatus.Covered)]
    [InlineData(10, 13, InFlightClosesStatus.Covered)]
    [InlineData(0, 13, InFlightClosesStatus.NotCovered)]
    [InlineData(-13, 13, InFlightClosesStatus.NotCovered)] // 反対方向（ショート）の建玉は Sell の決済の建玉に数えない
    public async Task T_10_2647_部分は覆わない(int brokerSigned, int inFlight, InFlightClosesStatus expected)
    {
        var f = NewFixture([Position(brokerSigned)]);
        f.Store.Save(CloseRecord("AAPL-SELL-1", inFlight));
        f.Broker.Live("AAPL-SELL-1");

        var view = await Ask(f);

        view.Status.Should().Be(expected);
    }

    // 方向の区別: Buy の決済（ショートの買い戻し）を問われたら、Sell の処理中の決済は数えない（逆方向の処理中の決済を塞ぐ材料にしない）。
    [Fact]
    public async Task T_10_2647_逆方向の処理中の決済は数えない()
    {
        var f = NewFixture([Position(-13)]);
        f.Store.Save(CloseRecord("AAPL-SELL-1", 13));
        f.Broker.Live("AAPL-SELL-1");

        var view = await Ask(f, TradeSide.Buy);

        view.Status.Should().Be(InFlightClosesStatus.NotCovered);
        f.Broker.PositionQueryCount.Should().Be(0, "Buy の候補が無いので照会しない");
    }

    // ---- T-10-2648: ゲートと照会の口が同じ判定。ゲートの見送りは従来どおりで、処理中の決済の注文 ID を載せる ----

    [Theory]
    [InlineData(13, 13)]
    [InlineData(20, 13)]
    [InlineData(10, 13)]
    public async Task T_10_2648_ゲートの見送りと照会の口の答えが一致し見送りに注文IDが載る(int broker, int inFlight)
    {
        var f = NewFixture([Position(broker)]);
        f.Store.Save(CloseRecord("AAPL-SELL-1", inFlight));
        f.Broker.Live("AAPL-SELL-1");

        var view = await Ask(f);
        var result = await f.Gate.ExecuteAsync(new OrderApproved(Guid.NewGuid(), Close(broker), broker, Now), TestContext.Current.CancellationToken);

        var gateForgone = result.Forgone?.Reason == OrderDispatchForgoneReason.InFlightCloseCoversPosition;
        (view.Status == InFlightClosesStatus.Covered).Should().Be(gateForgone, "LLM を省くのはゲートが見送るときだけ");
        if (gateForgone)
            result.Forgone!.InFlightCloseOrderIds.Should().Equal("AAPL-SELL-1");
        else
            result.Forgone.Should().BeNull();
    }

    // ---- 口の守りと本文（本物の Program.cs・内蔵 paper の組み立て） ----

    private static WebApplicationFactory<Program> Wire(ExecutionWorkerWebApplicationFactory factory) =>
        factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { })));

    private const string OkPath = "/order-execution/in-flight-closes?symbol=AAPL&market=1&closeSide=1";

    // 内蔵 paper の本物の組み立て（建玉照会を登録しない）では Unknown（broker-not-supported）を web 既定で返す（受け手 T-10-2649 の前提）。
    [Fact]
    public async Task T_10_2647_内蔵paperの組み立てではUnknownを_web_既定で返す()
    {
        await using var factory = new ExecutionWorkerWebApplicationFactory();
        using var wired = Wire(factory);
        var client = wired.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-service");

        var res = await client.GetAsync(OkPath, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonNode.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        var expected = new InFlightClosesView(
            "AAPL", Market.UnitedStates, TradeSide.Sell, InFlightClosesStatus.Unknown, InFlightClosesUnknownReasons.BrokerNotSupported,
            null, 0, [], []);
        JsonNode.DeepEquals(body, JsonSerializer.SerializeToNode(expected, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            .Should().BeTrue($"本文が web 既定と異なる: {body.ToJsonString()}");
    }

    [Theory]
    [InlineData(OkPath, null, HttpStatusCode.Unauthorized)]
    [InlineData("/order-execution/in-flight-closes?market=1&closeSide=1", "trading-service", HttpStatusCode.BadRequest)]
    [InlineData("/order-execution/in-flight-closes?symbol=AAPL&closeSide=1", "trading-service", HttpStatusCode.BadRequest)]
    [InlineData("/order-execution/in-flight-closes?symbol=AAPL&market=7&closeSide=1", "trading-service", HttpStatusCode.BadRequest)]
    [InlineData("/order-execution/in-flight-closes?symbol=AAPL&market=1", "trading-service", HttpStatusCode.BadRequest)]
    [InlineData("/order-execution/in-flight-closes?symbol=AAPL&market=1&closeSide=9", "trading-service", HttpStatusCode.BadRequest)]
    [InlineData("/order-execution/in-flight-closes?symbol=ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456&market=1&closeSide=1", "trading-service", HttpStatusCode.BadRequest)]
    [InlineData(OkPath, "trading-owner", HttpStatusCode.OK)]
    public async Task T_10_2647_匿名は401で値域外は400(string path, string? roles, HttpStatusCode expected)
    {
        await using var factory = new ExecutionWorkerWebApplicationFactory();
        using var wired = Wire(factory);
        var client = wired.CreateClient();
        if (roles is not null)
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);

        var res = await client.GetAsync(path, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(expected);
    }
}
