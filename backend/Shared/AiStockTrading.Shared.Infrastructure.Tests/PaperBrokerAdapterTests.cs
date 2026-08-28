using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests;

// FR-12: ペーパートレードモード（実発注せず、判断・記録・報告のフローは実発注と同一）
public class PaperBrokerAdapterTests
{
    private static OrderIntent Intent(int quantity = 10, decimal price = 3000m) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, quantity, price);

    [Fact]
    public async Task 発注すると現在値で即時全量約定する()
    {
        var broker = new PaperBrokerAdapter();

        var order = await broker.PlaceOrderAsync(Intent(quantity: 10, price: 3000m));

        order.OrderId.Should().NotBeNullOrWhiteSpace();
        order.Status.Should().Be(OrderStatus.Filled);
        order.FilledQuantity.Should().Be(10);
        order.AveragePrice.Should().Be(3000m);
    }

    [Fact]
    public async Task 発注済み注文を注文IDで照会できる()
    {
        var broker = new PaperBrokerAdapter();
        var placed = await broker.PlaceOrderAsync(Intent());

        var fetched = await broker.GetOrderAsync(placed.OrderId);

        fetched.Should().NotBeNull();
        fetched!.OrderId.Should().Be(placed.OrderId);
        fetched.Status.Should().Be(OrderStatus.Filled);
    }

    [Fact]
    public async Task 未知の注文IDの照会はnullを返す()
    {
        var broker = new PaperBrokerAdapter();

        var fetched = await broker.GetOrderAsync("unknown-order-id");

        fetched.Should().BeNull();
    }

    [Fact]
    public async Task 約定済み注文の取消は失敗する()
    {
        var broker = new PaperBrokerAdapter();
        var placed = await broker.PlaceOrderAsync(Intent());

        var act = () => broker.CancelOrderAsync(placed.OrderId);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(0, 3000)]   // 数量ゼロ
    [InlineData(-5, 3000)]  // 数量が負
    [InlineData(10, 0)]     // 価格ゼロ
    [InlineData(10, -1)]    // 価格が負
    public async Task 数量や価格が不正な注文は約定せず証券会社拒否になる(int quantity, decimal price)
    {
        // Issue #30 / FR-05, FR-12: 実ブローカーが拒否する不正注文はペーパーでも約定させない。
        var broker = new PaperBrokerAdapter();

        var order = await broker.PlaceOrderAsync(Intent(quantity: quantity, price: price));

        order.Status.Should().Be(OrderStatus.Rejected);
        order.FilledQuantity.Should().Be(0);
        order.AveragePrice.Should().Be(0m);
    }

    [Fact]
    public async Task 証券会社拒否の注文は取消できない()
    {
        // Issue #30: Rejected は終端状態。取消は失敗する。
        var broker = new PaperBrokerAdapter();
        var rejected = await broker.PlaceOrderAsync(Intent(quantity: 0));

        var act = () => broker.CancelOrderAsync(rejected.OrderId);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // --- #154, IADR-0067: 非終端状態（immediateFill=false）と訂正・取消 ---
    // 既定（immediateFill=true）の挙動は上のテスト群が固定する。ここでは既定を明示指定しても現挙動と同一であること、
    // および opt-in したときだけ非終端になり訂正・取消が成立することを検証する。

    [Fact]
    public async Task 即時約定を明示的に有効化しても既定と同じく即時全量約定する()
    {
        var broker = new PaperBrokerAdapter(immediateFill: true);

        var order = await broker.PlaceOrderAsync(Intent(quantity: 10, price: 3000m));

        order.Status.Should().Be(OrderStatus.Filled);
        order.FilledQuantity.Should().Be(10);
        order.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task 即時約定を無効化すると発注は非終端の受付状態に留まる()
    {
        // IADR-0067: 訂正・取消の配管を検証可能にするための最小の仕組み。本番配線の既定では有効にしない。
        var broker = new PaperBrokerAdapter(immediateFill: false);

        var order = await broker.PlaceOrderAsync(Intent(quantity: 10, price: 3000m));

        order.Status.Should().Be(OrderStatus.Accepted);
        order.FilledQuantity.Should().Be(0);
        order.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task 即時約定を無効化しても不正な注文は証券会社拒否になる()
    {
        // 不正注文の拒否（Issue #30）は約定モードに依らない安全側判定として維持する。
        var broker = new PaperBrokerAdapter(immediateFill: false);

        var order = await broker.PlaceOrderAsync(Intent(quantity: 0));

        order.Status.Should().Be(OrderStatus.Rejected);
    }

    [Fact]
    public async Task 受付状態の注文を取消できる()
    {
        var broker = new PaperBrokerAdapter(immediateFill: false);
        var placed = await broker.PlaceOrderAsync(Intent());

        await broker.CancelOrderAsync(placed.OrderId);

        var fetched = await broker.GetOrderAsync(placed.OrderId);
        fetched!.Status.Should().Be(OrderStatus.Cancelled);
        fetched.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task 受付状態の注文を訂正できる()
    {
        var broker = new PaperBrokerAdapter(immediateFill: false);
        var placed = await broker.PlaceOrderAsync(Intent(quantity: 10, price: 3000m));

        var modified = await broker.ModifyOrderAsync(placed.OrderId, quantity: 5, price: 2900m);

        modified.Intent.Quantity.Should().Be(5);
        modified.Intent.Price.Should().Be(2900m);
        modified.Status.Should().Be(OrderStatus.Accepted);

        var fetched = await broker.GetOrderAsync(placed.OrderId);
        fetched!.Intent.Quantity.Should().Be(5);
        fetched.Intent.Price.Should().Be(2900m);
    }

    [Fact]
    public async Task 約定済み注文の訂正は失敗する()
    {
        var broker = new PaperBrokerAdapter();
        var placed = await broker.PlaceOrderAsync(Intent());

        var act = () => broker.ModifyOrderAsync(placed.OrderId, quantity: 5, price: 2900m);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task 未知の注文IDの訂正は失敗する()
    {
        var broker = new PaperBrokerAdapter(immediateFill: false);

        var act = () => broker.ModifyOrderAsync("unknown-order-id", quantity: 5, price: 2900m);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task 未知の注文IDの取消は失敗する()
    {
        var broker = new PaperBrokerAdapter(immediateFill: false);

        var act = () => broker.CancelOrderAsync("unknown-order-id");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(0, 2900)]   // 数量ゼロ
    [InlineData(-5, 2900)]  // 数量が負
    [InlineData(5, 0)]      // 価格ゼロ
    [InlineData(5, -1)]     // 価格が負
    public async Task 不正な内容への訂正は失敗し元の注文を壊さない(int quantity, decimal price)
    {
        // 発注時（Issue #30）と同じ妥当性判定を訂正にも適用する。実ブローカーが拒否する内容へは訂正しない。
        var broker = new PaperBrokerAdapter(immediateFill: false);
        var placed = await broker.PlaceOrderAsync(Intent(quantity: 10, price: 3000m));

        var act = () => broker.ModifyOrderAsync(placed.OrderId, quantity, price);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var fetched = await broker.GetOrderAsync(placed.OrderId);
        fetched!.Intent.Quantity.Should().Be(10);
        fetched.Intent.Price.Should().Be(3000m);
    }

    // --- FR-10, #331, IADR-0210: 保護注文（IProtectiveOrderBroker）---

    [Fact]
    public async Task 逆指値は滞留Acceptedで受理される()
    {
        // paper は板を持たないため発火を模擬しない。受理＝非終端の Accepted（建玉を保護している状態）。
        var broker = new PaperBrokerAdapter();
        var closeIntent = Intent(quantity: 10, price: 3000m);

        var stop = await broker.PlaceStopOrderAsync(closeIntent, triggerPrice: 2900m, Guid.NewGuid());

        stop.Status.Should().Be(OrderStatus.Accepted);
        stop.FilledQuantity.Should().Be(0);
        stop.CompletedAt.Should().BeNull("非終端＝滞留中");
        (await broker.GetOrderAsync(stop.OrderId)).Should().NotBeNull("照会・取消の対象になる");
    }

    [Theory]
    [InlineData(0, 2900)]   // 数量ゼロ
    [InlineData(10, 0)]     // 発火価格ゼロ
    [InlineData(10, -1)]    // 発火価格が負
    public async Task 不正な逆指値は_Rejected_で受ける(int quantity, decimal trigger)
    {
        // 実ブローカーの未受理と同じ分岐（建玉を持たない側）へ呼び出し側を導く。例外にしない。
        var broker = new PaperBrokerAdapter();

        var stop = await broker.PlaceStopOrderAsync(Intent(quantity: quantity, price: 3000m), trigger, Guid.NewGuid());

        stop.Status.Should().Be(OrderStatus.Rejected);
    }

    [Fact]
    public async Task 成行手仕舞いは参照価格で即時全量約定する()
    {
        var broker = new PaperBrokerAdapter();

        var close = await broker.PlaceMarketOrderAsync(Intent(quantity: 7, price: 2950m), Guid.NewGuid());

        close.Status.Should().Be(OrderStatus.Filled);
        close.FilledQuantity.Should().Be(7);
        close.AveragePrice.Should().Be(2950m);
    }
}
