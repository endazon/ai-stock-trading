using AiStockTrading.OrderExecution.Application.Adapters;
using AiStockTrading.OrderExecution.Application.Ports;
using AiStockTrading.OrderExecution.Application.Reconciliation;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.OrderExecution.Application.Tests;

// #141, FR-05, IADR-0074: リコンサイルの下支え（滞留走査・解放ガード・no-op プローブ・滞留閾値ポリシー）。
public class OrderReservationReconcileStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FindStalledReservedは閾値より古いReservedのみをReservedAt昇順で返す()
    {
        var store = new InMemoryOrderReservationStore();
        var old1 = Guid.NewGuid();
        var old2 = Guid.NewGuid();
        var recent = Guid.NewGuid();
        store.TryReserve(old2, Now.AddHours(-48));
        store.TryReserve(old1, Now.AddHours(-72)); // 最古
        store.TryReserve(recent, Now.AddHours(-1)); // 閾値内

        var stalled = store.FindStalledReserved(Now.AddHours(-24), batchSize: 50);

        stalled.Select(r => r.DecisionId).Should().Equal(old1, old2);
    }

    [Fact]
    public void FindStalledReservedはCompletedを含めない()
    {
        var store = new InMemoryOrderReservationStore();
        var done = Guid.NewGuid();
        store.TryReserve(done, Now.AddHours(-48));
        store.MarkCompleted(done, "BRK-1", Now.AddHours(-48));

        store.FindStalledReserved(Now.AddHours(-24), batchSize: 50).Should().BeEmpty();
    }

    [Fact]
    public void ReleaseはReservedを削除しtrueを返す()
    {
        var store = new InMemoryOrderReservationStore();
        var id = Guid.NewGuid();
        store.TryReserve(id, Now.AddHours(-48));

        store.Release(id).Should().BeTrue();
        store.Find(id).Should().BeNull();
    }

    [Fact]
    public void ReleaseはCompletedを削除せずfalseを返す()
    {
        // 安全ガード: 終端行は決して消さない（消せば再配送で二重発注・実弾では実損）。
        var store = new InMemoryOrderReservationStore();
        var id = Guid.NewGuid();
        store.TryReserve(id, Now.AddHours(-48));
        store.MarkCompleted(id, "BRK-1", Now.AddHours(-48));

        store.Release(id).Should().BeFalse();
        store.Find(id)!.State.Should().Be(OrderDispatchState.Completed);
    }

    [Fact]
    public void Releaseは未知のIdでfalseを返す()
    {
        new InMemoryOrderReservationStore().Release(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public async Task noopプローブは常にIndeterminateを返す()
    {
        var reservation = new OrderDispatchReservation(
            Guid.NewGuid(), OrderDispatchState.Reserved, DateTimeOffset.UtcNow, BrokerOrderId: null);
        var result = await new IndeterminateReservationBrokerProbe().ProbeAsync(reservation);
        result.Outcome.Should().Be(ReservationProbeOutcome.Indeterminate);
        result.Order.Should().BeNull();
    }

    [Theory]
    [InlineData(24, 24)]
    [InlineData(1, 1)]
    [InlineData(0, 1)] // 下限クランプ
    [InlineData(-5, 1)] // 下限クランプ
    public void 滞留閾値は下限1時間にクランプされる(int configured, int effective)
    {
        ReconciliationPolicy.EffectiveStallThresholdHours(configured).Should().Be(effective);
    }

    [Fact]
    public void StallCutoffForは閾値ぶん過去を返す()
    {
        ReconciliationPolicy.StallCutoffFor(Now, 24).Should().Be(Now.AddHours(-24));
    }

    [Fact]
    public void StallCutoffForは極端な設定でMinValueへ安全側に倒す()
    {
        // 溢れる設定では「何も対象にしない」= どの予約も境界より古くならない MinValue を返す。
        ReconciliationPolicy.StallCutoffFor(Now, int.MaxValue).Should().Be(DateTimeOffset.MinValue);
    }

    [Fact]
    public void ReconciliationOptionsの既定は無効かつ安全側()
    {
        var options = new ReconciliationOptions();
        options.Enabled.Should().BeFalse("fail-safe 既定は無効");
        options.StallThresholdHours.Should().Be(ReconciliationPolicy.DefaultStallThresholdHours);
        options.Interval.Should().Be(TimeSpan.FromHours(6));
        options.EffectiveBatchSize.Should().Be(200);
    }

    [Theory]
    [InlineData(0, 1)] // 間隔は下限1時間
    [InlineData(6, 6)]
    public void 巡回間隔は下限1時間にクランプされる(int hours, int effectiveHours)
    {
        new ReconciliationOptions { IntervalHours = hours }.Interval.Should().Be(TimeSpan.FromHours(effectiveHours));
    }

    [Theory]
    [InlineData(0, 1)] // 下限1
    [InlineData(999999, 10000)] // 上限1万
    public void バッチ上限はクランプされる(int batch, int effective)
    {
        new ReconciliationOptions { BatchSize = batch }.EffectiveBatchSize.Should().Be(effective);
    }
}
