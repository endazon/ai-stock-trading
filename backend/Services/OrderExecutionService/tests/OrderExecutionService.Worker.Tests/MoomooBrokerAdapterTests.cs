using AiStockTrading.OrderExecution.Worker.Composable.Adapters;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.OrderExecution.Worker.Tests;

// #13, FR-05, ADR-0002, IADR-0016: moomoo アダプタの写像・状態変換・SIMULATE 限定・fail-safe を fake client で検証する
// （実 OpenD 不使用）。実結合（MMApiMoomooTradeClient）は live 検証。
public class MoomooBrokerAdapterTests
{
    private sealed class FakeClient : IMoomooTradeClient
    {
        public MoomooOrderRequest? LastRequest { get; private set; }
        public MoomooOrderResult Result { get; set; } = new("mo-1", MoomooOrderState.FilledAll, 10, 100m);
        public Func<Exception>? ThrowOnPlace { get; set; }
        public MoomooOrderResult? QueryResult { get; set; }
        public string? CancelledId { get; private set; }

        public Task<MoomooOrderResult> PlaceOrderAsync(MoomooOrderRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            if (ThrowOnPlace is not null) throw ThrowOnPlace();
            return Task.FromResult(Result);
        }

        public Task<MoomooOrderResult?> QueryOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult(QueryResult);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            CancelledId = orderId;
            return Task.CompletedTask;
        }

        public Task<MoomooOrderSnapshot?> FindOrderByClientIdAsync(
            string clientOrderId, DateTimeOffset reservedAtUtc, CancellationToken ct = default) =>
            Task.FromResult<MoomooOrderSnapshot?>(null);

        // #292, IADR-0118: 建玉照会。既定は空列（建玉なし）。例外側は Throw で差し替える。
        public Func<Exception>? PositionsThrow { get; set; }

        public IReadOnlyList<MoomooPositionSnapshot> Positions { get; set; } = [];

        public Task<IReadOnlyList<MoomooPositionSnapshot>> GetPositionsAsync(CancellationToken ct = default)
        {
            if (PositionsThrow is not null) throw PositionsThrow();
            return Task.FromResult(Positions);
        }
    }

    // #141, IADR-0092: DecisionId を remark（client order id相当）として発注リクエストに載せることを検証する。
    [Fact]
    public async Task client_order_id_発注は_DecisionId_を_remark_に載せる()
    {
        var client = new FakeClient();
        var decisionId = Guid.NewGuid();

        await new MoomooBrokerAdapter(client).PlaceOrderAsync(Intent(), decisionId);

        client.LastRequest!.Remark.Should().Be(decisionId.ToString("N"));
    }

    [Fact]
    public async Task 通常発注は_remark_を付けない()
    {
        var client = new FakeClient();
        await new MoomooBrokerAdapter(client).PlaceOrderAsync(Intent());
        client.LastRequest!.Remark.Should().BeNull();
    }

    private static OrderIntent Intent(int qty = 10, decimal price = 100m, Market market = Market.UnitedStates,
        TradeSide side = TradeSide.Buy, TradeMode mode = TradeMode.Paper) =>
        new("AAPL", market, side, ProductType.Cash, mode, qty, price);

    [Fact]
    public async Task 発注を_SIMULATE_リクエストへ写像し結果を_BrokerOrder_へ変換する()
    {
        var client = new FakeClient { Result = new("mo-9", MoomooOrderState.FilledAll, 10, 101m) };
        var order = await new MoomooBrokerAdapter(client).PlaceOrderAsync(
            Intent(qty: 10, price: 100m, market: Market.Japan, side: TradeSide.Sell));

        client.LastRequest.Should().Be(new MoomooOrderRequest("AAPL", MoomooMarket.Japan, MoomooSide.Sell, 10, 100m));
        order.OrderId.Should().Be("mo-9");
        order.Status.Should().Be(OrderStatus.Filled);
        order.FilledQuantity.Should().Be(10);
        order.AveragePrice.Should().Be(101m);
    }

    [Fact]
    public async Task Live_指定でも_SIMULATE_で発注する_実弾を撃たない()
    {
        // moomoo アダプタは Mode に依らず SIMULATE（client 実装が固定）で発注する。Live でも拒否せず送信する。
        var client = new FakeClient();
        var order = await new MoomooBrokerAdapter(client).PlaceOrderAsync(Intent(mode: TradeMode.Live));
        client.LastRequest.Should().NotBeNull();
        order.Status.Should().Be(OrderStatus.Filled);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(10, 0)]
    public async Task 不正注文_数量や価格が0以下_は送信せず_Rejected(int qty, decimal price)
    {
        var client = new FakeClient();
        var order = await new MoomooBrokerAdapter(client).PlaceOrderAsync(Intent(qty, price));
        order.Status.Should().Be(OrderStatus.Rejected);
        client.LastRequest.Should().BeNull(); // 送信していない
    }

    [Fact]
    public async Task client_例外_OpenD不達_は_Rejected_に倒す_fail_safe()
    {
        var client = new FakeClient { ThrowOnPlace = () => new InvalidOperationException("OpenD 不達") };
        var order = await new MoomooBrokerAdapter(client).PlaceOrderAsync(Intent());
        order.Status.Should().Be(OrderStatus.Rejected);
    }

    [Fact]
    public void 注文状態を_OrderStatus_へ写像する()
    {
        MoomooBrokerAdapter.MapState(MoomooOrderState.Submitting).Should().Be(OrderStatus.Accepted);
        MoomooBrokerAdapter.MapState(MoomooOrderState.Submitted).Should().Be(OrderStatus.Accepted);
        MoomooBrokerAdapter.MapState(MoomooOrderState.Filling).Should().Be(OrderStatus.PartiallyFilled);
        MoomooBrokerAdapter.MapState(MoomooOrderState.FilledPart).Should().Be(OrderStatus.PartiallyFilled);
        MoomooBrokerAdapter.MapState(MoomooOrderState.FilledAll).Should().Be(OrderStatus.Filled);
        MoomooBrokerAdapter.MapState(MoomooOrderState.Cancelled).Should().Be(OrderStatus.Cancelled);
        MoomooBrokerAdapter.MapState(MoomooOrderState.Failed).Should().Be(OrderStatus.Rejected);
    }

    [Fact]
    public async Task 状態照会は_client_結果を返し_未知は_null()
    {
        var client = new FakeClient { QueryResult = new("mo-2", MoomooOrderState.Filling, 3, 99m) };
        var found = await new MoomooBrokerAdapter(client).GetOrderAsync("mo-2");
        found!.Status.Should().Be(OrderStatus.PartiallyFilled);
        found.FilledQuantity.Should().Be(3);

        client.QueryResult = null;
        (await new MoomooBrokerAdapter(client).GetOrderAsync("none")).Should().BeNull();
    }

    [Fact]
    public async Task 取消は_client_へ委譲する()
    {
        var client = new FakeClient();
        await new MoomooBrokerAdapter(client).CancelOrderAsync("mo-3");
        client.CancelledId.Should().Be("mo-3");
    }

    // --- #292, IADR-0118: 建玉照会（突合の入力） ---

    [Fact]
    public async Task 建玉を符号付きで共有契約へ写す()
    {
        var client = new FakeClient
        {
            Positions =
            [
                new MoomooPositionSnapshot("AAPL", MoomooMarket.UnitedStates, 4072, 20.5m),
                new MoomooPositionSnapshot("7203", MoomooMarket.Japan, -100, 2500m),
            ],
        };
        var adapter = new MoomooBrokerAdapter(client);

        var positions = await adapter.GetPositionsAsync();

        positions.Should().NotBeNull();
        positions!.Should().HaveCount(2);
        positions[0].Should().Be(new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 4072, 20.5m));
        positions[1].Should().Be(new BrokerPositionSnapshot("7203", Market.Japan, -100, 2500m));
    }

    [Fact]
    public async Task 建玉が無ければ空列を返す()
    {
        // 空列（建玉ゼロ）は観測事実。null（不明）と取り違えない。
        var positions = await new MoomooBrokerAdapter(new FakeClient()).GetPositionsAsync();

        positions.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task 照会に失敗したら不明として_null_を返す()
    {
        // 空列に倒すと「ブローカは何も持っていない」と誤断定し、台帳の全建玉が乖離として報告される。
        var client = new FakeClient { PositionsThrow = () => new InvalidOperationException("OpenD 不達") };

        var positions = await new MoomooBrokerAdapter(client).GetPositionsAsync();

        positions.Should().BeNull();
    }
}
