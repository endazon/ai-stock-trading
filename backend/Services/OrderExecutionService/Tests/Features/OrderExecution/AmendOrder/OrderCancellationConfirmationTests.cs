using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.AmendOrder;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-585, FR-05, FR-10, FR-11, UC-06, #847, #768, IADR-0357, IADR-0117（2026-09-19 追記・改定 1/4）:
// **「確実に取り消せた」と「取消を送ったが結果が不明」を混同しない。**
//
// OrderCancelled は取引台帳で MarkTerminal → **在庫の押さえを解く引き金**である。
// ブローカーが取消**要求**を受理しても注文はまだ生きていることがある（moomoo は Cancelling_Part/All（12/13）を
// 経て Cancelled_All（15）になり、その間に約定し得る。12/13 を非終端へ倒す配慮は改定 3 が入れている）。
// 不明のまま在庫を戻すと、同じ建玉に 2 本目の決済が並ぶ（**二重決済で意図しないショート化**）。
public class OrderCancellationConfirmationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 6, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private static OrderIntent Intent() =>
        new("SOXL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
            3381, 334.09m, PositionEffect.Close);

    // 取消の送信は成功するが、そのあとの照会が返す状態を注入できるブローカー（moomoo の実挙動を模す）。
    private sealed class ScriptedCancelBroker(
        OrderStatus? afterCancel, bool cancelThrows = false, int filledAfterCancel = 0) : IBrokerAdapter
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public int CancelCount { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            Task.FromResult(new BrokerOrder("order-1", intent, OrderStatus.Accepted, 0, 0m, Now, null));

        // #847: 取消を確認する照会は**累積約定数も返す**。既定 0 のままだと「その値が運ばれているか」を
        // 観測できず、発行側（OrderAmendmentService）の是正がテストで固定されない（監査 B-1）。
        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(afterCancel is { } status
                ? new BrokerOrder(orderId, Intent(), status, filledAfterCancel, 0m, Now, null)
                : null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            CancelCount++;
            return cancelThrows
                ? throw new InvalidOperationException("取消に失敗（テスト）")
                : Task.CompletedTask;
        }
    }

    private static (OrderAmendmentService Service, Guid DecisionId, InMemoryOrderLifecycleStore Lifecycle)
        Build(IBrokerAdapter broker)
    {
        var executedOrders = new InMemoryExecutedOrderStore();
        var lifecycle = new InMemoryOrderLifecycleStore();
        var decisionId = Guid.NewGuid();
        executedOrders.Save(new OrderExecutionService.Domain.ExecutionRecord(
            decisionId, "order-1", "SOXL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            PositionEffect.Close, 3381, 334.09m, 0, 0m, OrderStatus.Accepted, 0m, Now));
        return (new OrderAmendmentService(broker, executedOrders, lifecycle, new FakeClock(), null),
            decisionId, lifecycle);
    }

    // 🔴 確認できた取消（ブローカーが終端の Cancelled を返す）だけが在庫を戻すイベントになる。
    [Fact]
    public async Task 確認できた取消だけが_OrderCancelled_になる()
    {
        var (service, decisionId, _) = Build(new ScriptedCancelBroker(OrderStatus.Cancelled));

        var outcome = await service.CancelAsync(decisionId, "指値が置いていかれたので取り消す");

        outcome.Confirmed.Should().BeTrue();
        outcome.Event.Should().NotBeNull();
        outcome.Event!.DecisionId.Should().Be(decisionId);
        outcome.Event.Reason.Should().Be("指値が置いていかれたので取り消す");
        outcome.Event.CancelledAt.Should().Be(Now);
    }

    // 🔴 T-10-589, #847, IADR-0357（発行側の固定・監査 B-1）:
    // **取消を確認した照会が返した累積約定数を、そのままイベントへ載せる。**
    //
    // これが無いと、失効通知の「未決済 N 株」を正しくした是正そのものを**リファクタで黙って元へ戻せる**
    // （実測: `snapshot!.FilledQuantity` を `0` に変えても 6 テストプロジェクトで赤が 1 件も出なかった）。
    // 受け手側（PositionCloseAbandonedTests）は値を**手で渡して**いるため、運搬経路は観測できていなかった。
    // テスト仕様書自身の規律「不変条件は、それを壊す経路そのものを通すテストでしか固定できない」に従う。
    [Fact]
    public async Task 取消を確認した時点の約定数をイベントへ載せる()
    {
        // 3,381 株の手仕舞いのうち 1,000 株が約定済みのまま取り消された（#847 の実測に寄せた形）。
        var (service, decisionId, _) = Build(
            new ScriptedCancelBroker(OrderStatus.Cancelled, filledAfterCancel: 1_000));

        var outcome = await service.CancelAsync(decisionId, "取消");

        outcome.Confirmed.Should().BeTrue();
        outcome.Event!.ObservedFilledQuantity.Should().Be(
            1_000,
            "取消は部分約定を追い越して台帳へ着く。ここで運ばなければ、受け手は残数量を 3,381 と水増しする");
    }

    // 対（否定形）: 約定していなければ 0 を載せる（「不明」も 0 で、受け手が台帳の累計へフォールバックする）。
    [Fact]
    public async Task 約定が無ければ約定数はゼロで載る()
    {
        var (service, decisionId, _) = Build(new ScriptedCancelBroker(OrderStatus.Cancelled));

        (await service.CancelAsync(decisionId, "取消")).Event!.ObservedFilledQuantity.Should().Be(0);
    }

    // 🔴 否定形・最重要: **取消の結果が不明なときに在庫を戻さない。**
    [Fact]
    public async Task 照会できないときは在庫を戻すイベントを作らない()
    {
        var (service, decisionId, lifecycle) = Build(new ScriptedCancelBroker(afterCancel: null));

        var outcome = await service.CancelAsync(decisionId, "取消");

        outcome.Confirmed.Should().BeFalse("照会できない＝取り消せたか分からない。不明を確定として扱わない");
        outcome.Event.Should().BeNull();
        lifecycle.GetByOrderId("order-1").Should().ContainSingle("送ったこと自体は監査に残す");
    }

    // 🔴 否定形・最重要: 取消進行中（moomoo の Cancelling_* → OrderStatus.Accepted へ写る）は**終端ではない**。
    [Theory]
    [InlineData(OrderStatus.Accepted)]
    [InlineData(OrderStatus.PartiallyFilled)]
    public async Task 非終端のままなら在庫を戻すイベントを作らない(OrderStatus status)
    {
        var (service, decisionId, _) = Build(new ScriptedCancelBroker(status));

        var outcome = await service.CancelAsync(decisionId, "取消");

        outcome.Confirmed.Should().BeFalse("取消進行中の注文はまだ約定し得る。押さえを解くと二重決済になる");
        outcome.Event.Should().BeNull();
    }

    // 🔴 否定形: 取消を送ったのに全量約定していた場合も OrderCancelled にしない
    // （未約定残は無い。約定は約定の経路〔OrderExecuted〕が台帳へ運ぶ）。
    [Fact]
    public async Task 全量約定していたら在庫を戻すイベントを作らない()
    {
        var (service, decisionId, _) = Build(new ScriptedCancelBroker(OrderStatus.Filled));

        (await service.CancelAsync(decisionId, "取消")).Confirmed.Should().BeFalse();
    }

    // 失効・拒否も「未約定残が二度と約定しない」終端であり、確認できたなら在庫を戻してよい。
    [Theory]
    [InlineData(OrderStatus.Expired)]
    [InlineData(OrderStatus.Rejected)]
    public async Task 失効と拒否も確認できた終端として扱う(OrderStatus status)
    {
        var (service, decisionId, _) = Build(new ScriptedCancelBroker(status));

        (await service.CancelAsync(decisionId, "取消")).Confirmed.Should().BeTrue();
    }

    // 取消そのものが失敗したら記録もイベントも作らない（IADR-0067 の既存規律）。
    [Fact]
    public async Task 取消の送信に失敗したら記録もイベントも作らない()
    {
        var (service, decisionId, lifecycle) =
            Build(new ScriptedCancelBroker(OrderStatus.Cancelled, cancelThrows: true));

        var act = () => service.CancelAsync(decisionId, "取消");

        await act.Should().ThrowAsync<InvalidOperationException>();
        lifecycle.GetByOrderId("order-1").Should().BeEmpty();
    }

    // #847: 取消は IBrokerAdapter 経由であり、**moomoo でも使える**
    // （IOrderAmendmentBroker〔ペーパー専用〕は訂正のためだけに残す）。
    [Fact]
    public async Task 訂正の口が無い構成でも取消はできる()
    {
        var (service, decisionId, _) = Build(new ScriptedCancelBroker(OrderStatus.Cancelled));

        (await service.CancelAsync(decisionId, "取消")).Confirmed.Should().BeTrue();

        var modify = () => service.ModifyAsync(decisionId, 10, 100m, "訂正");
        await modify.Should().ThrowAsync<NotSupportedException>("訂正の口は実ブローカーへ配線していない");
    }
}
