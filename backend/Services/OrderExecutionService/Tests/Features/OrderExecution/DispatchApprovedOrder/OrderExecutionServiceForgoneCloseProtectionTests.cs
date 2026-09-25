using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Infrastructure.Persistence;
using Xunit;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

namespace OrderExecutionService.Tests;

// 🔴 T-10-1000〜T-10-1003, FR-10, FR-09, UC-06, #879, IADR-0424 決定1:
// **建玉照会の不明で決済を見送ったとき、その建玉の保護の記録を見送りに載せる**（不明・無し・有りを混ぜない）。
//
// 見送りの根拠のひとつ「損切りはブローカー側の逆指値が担うので、見送っても損切りは消えない」（IADR-0355 決定3）は、
// ブローカー側の注文を持つ建玉にしか当てはまらない。S2 で建てた建玉は保護記録を持たず、S1 の決済も照会の不明のあいだは
// 据え置かれる。通知が「保護レグを持たない（可能性がある）」を書き分けられるよう、発注執行が記録を読んで分類する。
public class OrderExecutionServiceForgoneCloseProtectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 6, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // 建玉照会の能力を持つブローカー（moomoo 相当）。snapshot が null なら「照会不能（不明）」を返す。発注は数えるだけ。
    private sealed class FakePositionAwareBroker(IReadOnlyList<BrokerPositionSnapshot>? snapshot)
        : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public int PlaceCount { get; private set; }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult(snapshot);

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default)
        {
            PlaceCount++;
            return Task.FromResult(new BrokerOrder(
                "ORD-" + PlaceCount, intent, OrderStatus.Filled, intent.Quantity, intent.Price, Now, Now));
        }

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            throw new NotSupportedException("本テストは逆指値を発注しない");

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default) =>
            throw new NotSupportedException("本テストは成行を発注しない");

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;
    }

    // Active の読み取りが必ず落ちる記録ストア（T-10-1002）。
    private sealed class ThrowingFindActiveStore(IProtectiveStopOrderStore inner) : IProtectiveStopOrderStore
    {
        public void Save(ProtectiveStopOrder stop) => inner.Save(stop);

        public ProtectiveStopOrder? Find(Guid entryDecisionId) => inner.Find(entryDecisionId);

        public IReadOnlyList<ProtectiveStopOrder> FindActive(int batchSize) =>
            throw new InvalidOperationException("保護記録の読み取りに失敗（テスト）");

        public IReadOnlyList<ProtectiveStopOrder> FindActiveSoftwareStops(
            string symbol, Market market, TradeSide entrySide) => inner.FindActiveSoftwareStops(symbol, market, entrySide);

        public IReadOnlyList<ProtectiveStopOrder> FindCompletedSoftwareStops(
            string symbol, Market market, TradeSide entrySide, int limit) =>
            inner.FindCompletedSoftwareStops(symbol, market, entrySide, limit);

        public IReadOnlyList<ProtectiveStopOrder> FindUnattributedNotified(int limit) =>
            inner.FindUnattributedNotified(limit);
    }

    private static OrderIntent CloseIntent(int qty = 300, TradeSide side = TradeSide.Sell, string symbol = "AAPL") =>
        new(symbol, Market.UnitedStates, side, ProductType.Cash, BrokerProvider.MoomooSimulate,
            qty, 100m, PositionEffect.Close);

    private static OrderApproved Approved(OrderIntent intent) => new(Guid.NewGuid(), intent, intent.Quantity, Now);

    private static AppSvc NewService(FakePositionAwareBroker broker, IProtectiveStopOrderStore? stops) =>
        new(broker, new InMemoryExecutedOrderStore(), new InMemoryOrderReservationStore(), new FakeClock(),
            stops, null, broker);

    // S0（ブローカー側の逆指値）の行。RemainingProtected を指定しなければ Quantity を主張する。
    private static ProtectiveStopOrder BrokerStop(
        int quantity, TradeSide entrySide = TradeSide.Buy, string symbol = "AAPL", Market market = Market.UnitedStates,
        ProtectiveStopState state = ProtectiveStopState.Active, int? remaining = null)
    {
        var entry = Guid.NewGuid();
        return new ProtectiveStopOrder(
            entry, ProtectiveStopIds.StopDecisionId(entry, 1), "STOP-" + entry.ToString("N")[..6], symbol, market,
            entrySide, ProductType.Cash, BrokerProvider.MoomooSimulate, quantity, 95m, 1m, 1, state,
            Now.AddHours(-1), Now.AddHours(-1), RemainingProtected: remaining);
    }

    // S1（ソフトウェア逆指値）の行。主張は確定済みの残保護数量。
    private static ProtectiveStopOrder SoftwareStop(int remaining, TradeSide entrySide = TradeSide.Buy)
    {
        var entry = Guid.NewGuid();
        return new ProtectiveStopOrder(
            entry, ProtectiveStopIds.StopDecisionId(entry, 1), string.Empty, "AAPL", Market.UnitedStates,
            entrySide, ProductType.Cash, BrokerProvider.MoomooSimulate, remaining, 95m, 1m, 0,
            ProtectiveStopState.Active, Now.AddHours(-1), Now.AddHours(-1),
            Mechanism: StopLossExecutionMethod.SoftwareStop, RemainingProtected: remaining);
    }

    // 🔴 T-10-1000: 照会不明の決済で、その建玉の Active な保護記録が 1 件も無い（S2 で建てた建玉と同じ状態）
    // → **NoneRecorded**（0/0）。「無い」と断定できるときに「不明」と書かせない。
    [Fact]
    public async Task 照会不明の決済で保護記録が無ければ_NoneRecorded_を載せる()
    {
        var broker = new FakePositionAwareBroker(null);
        var stops = new InMemoryProtectiveStopOrderStore();
        // 別銘柄の行は数えない（同じ銘柄の記録だけが、この建玉について何かを言える）。
        stops.Save(BrokerStop(100, symbol: "MSFT"));

        var result = await NewService(broker, stops).ExecuteAsync(Approved(CloseIntent()));

        broker.PlaceCount.Should().Be(0, "見送りの判定そのものは変えない（IADR-0355 決定3）");
        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.BrokerPositionsIndeterminate);
        result.Forgone.Protection.Should().Be(
            new ForgoneCloseProtection(ForgoneCloseProtectionStatus.NoneRecorded, 0, 0));
    }

    // 🔴 T-10-1001: S0 と S1 の Active 行 → **Recorded**。ブローカー側の注文（S0）とソフトウェア逆指値（S1）を分けて主張を合計する
    // （照会の不明のあいだに効くのはブローカー側だけなので、混ぜると通知が「守られている」と言い過ぎる）。
    // 数えない行: 他の市場・決済と同じ方向（＝エントリーが逆の建玉）・完了済み。S0 の主張は残保護数量があればそれを使う。
    [Fact]
    public async Task 照会不明の決済で保護記録があれば_ブローカー側とソフトウェア逆指値を分けて主張を載せる()
    {
        var broker = new FakePositionAwareBroker(null);
        var stops = new InMemoryProtectiveStopOrderStore();
        stops.Save(BrokerStop(100));                                   // 数える: S0・主張 100
        stops.Save(BrokerStop(80, remaining: 30));                     // 数える: S0・主張 30（残保護数量）
        stops.Save(SoftwareStop(50));                                  // 数える: S1・主張 50
        stops.Save(BrokerStop(70, market: Market.Japan));              // 数えない: 他の市場
        stops.Save(BrokerStop(60, entrySide: TradeSide.Sell));         // 数えない: ショートの保護（決済が売りなのでロングだけ）
        stops.Save(BrokerStop(40, state: ProtectiveStopState.Completed)); // 数えない: 完了済み

        var result = await NewService(broker, stops).ExecuteAsync(Approved(CloseIntent(qty: 300)));

        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.BrokerPositionsIndeterminate);
        result.Forgone.Protection.Should().Be(
            new ForgoneCloseProtection(ForgoneCloseProtectionStatus.Recorded, 130, 50));
        result.Forgone.Intent.Quantity.Should().Be(300, "見送りは承認が運んだ数量のまま（通知が覆われていない株数を計算する）");
    }

    // T-10-1001（続き）: ショートの買い戻し（決済が Buy）はエントリーが Sell の行を数える。
    [Fact]
    public async Task 照会不明の買い戻しはショートの保護記録を数える()
    {
        var broker = new FakePositionAwareBroker(null);
        var stops = new InMemoryProtectiveStopOrderStore();
        stops.Save(BrokerStop(60, entrySide: TradeSide.Sell));
        stops.Save(BrokerStop(100)); // ロングの保護は数えない

        var result = await NewService(broker, stops).ExecuteAsync(Approved(CloseIntent(qty: 60, side: TradeSide.Buy)));

        result.Forgone!.Protection.Should().Be(new ForgoneCloseProtection(ForgoneCloseProtectionStatus.Recorded, 60, 0));
    }

    // 🔴 T-10-1002: 記録ストアの無い構成／読み取りの例外 → **Unknown**（「分からない」を「無い」と言わない）。
    // 見送りそのものは従来どおり行う（保護の読み取りの失敗で決済を送る側へ倒さない）。
    [Fact]
    public async Task 記録ストアが無い構成では_Unknown_を載せ見送りは従来どおり()
    {
        var broker = new FakePositionAwareBroker(null);

        var result = await NewService(broker, stops: null).ExecuteAsync(Approved(CloseIntent()));

        broker.PlaceCount.Should().Be(0);
        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.BrokerPositionsIndeterminate);
        result.Forgone.Protection.Should().Be(new ForgoneCloseProtection(ForgoneCloseProtectionStatus.Unknown, 0, 0));
    }

    [Fact]
    public async Task 保護記録の読み取りが落ちたら_Unknown_を載せ見送りは従来どおり()
    {
        var broker = new FakePositionAwareBroker(null);
        var stops = new ThrowingFindActiveStore(new InMemoryProtectiveStopOrderStore());

        var result = await NewService(broker, stops).ExecuteAsync(Approved(CloseIntent()));

        broker.PlaceCount.Should().Be(0);
        result.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.BrokerPositionsIndeterminate);
        result.Forgone.Protection.Should().Be(new ForgoneCloseProtection(ForgoneCloseProtectionStatus.Unknown, 0, 0));
    }

    // T-10-1003: 照会不明以外の見送りは保護を載せない（null）。発注執行が判別を試みていない理由で「無い」と読ませない。
    [Fact]
    public async Task 照会不明以外の見送りは保護を載せない()
    {
        var stops = new InMemoryProtectiveStopOrderStore();

        // 建玉なし（空の一覧＝照会は成功・0 株）
        var absent = await NewService(new FakePositionAwareBroker([]), stops).ExecuteAsync(Approved(CloseIntent()));
        absent.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.BrokerPositionAbsent);
        absent.Forgone.Protection.Should().BeNull();

        // 損切り価格の無い新規建て
        var open = new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            BrokerProvider.MoomooSimulate, 10, 100m, PositionEffect.Open);
        var missing = await NewService(new FakePositionAwareBroker(null), stops).ExecuteAsync(Approved(open));
        missing.Forgone!.Reason.Should().Be(OrderDispatchForgoneReason.StopLossPriceMissing);
        missing.Forgone.Protection.Should().BeNull();
    }
}
