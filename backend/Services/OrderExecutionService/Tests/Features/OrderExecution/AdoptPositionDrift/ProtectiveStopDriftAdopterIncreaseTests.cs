using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution.AdoptPositionDrift;
using OrderExecutionService.Features.OrderExecution.AmendOrder;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-1009, FR-10, UC-06, #879, IADR-0424 決定2: **取り込みで建玉が生じる・増える・反転する形**のイベントを受けたら、
// 保護記録を作らず（損切りラインを導けない）、帳簿にもブローカーにも触らず、**Critical で「増えた分はシステムの保護を持たない」**と知らせる。
//
// この形は送り手（リスク管理）が発行しない契約の外の入力である（計画 ADR-0041 決定3・送り手の UnsupportedDirection。T-10-455 と
// T-10-1010 が送り手側で固定する）。受け手は受け取った値を無条件に信じない（IADR-0370 の既存の分岐）が、従来は Warning 1 行で
// 黙って終えていた。増えた分の約定価格はイベントに無いので、線を作って保護を作ると偽の保護になる（原則 A）。
public class ProtectiveStopDriftAdopterIncreaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 7, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // 取消・建玉照会を数えるだけのブローカー（本クラスの形ではどちらも起きてはならない）。
    private sealed class CountingBroker : IBrokerAdapter, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public int CancelCount { get; private set; }

        public int PositionQueryCount { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException("本テストは発注しない");

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            CancelCount++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default)
        {
            PositionQueryCount++;
            return Task.FromResult<IReadOnlyList<BrokerPositionSnapshot>?>([]);
        }
    }

    private sealed record Harness(
        ProtectiveStopDriftAdopter Adopter,
        CountingBroker Broker,
        InMemoryProtectiveStopOrderStore Stops,
        SoftwareStopLivenessReporterTests.RecordingLogger<ProtectiveStopDriftAdopter> Log);

    private static Harness NewHarness()
    {
        var broker = new CountingBroker();
        var stops = new InMemoryProtectiveStopOrderStore();
        var store = new InMemoryExecutedOrderStore();
        var log = new SoftwareStopLivenessReporterTests.RecordingLogger<ProtectiveStopDriftAdopter>();
        var amendments = new OrderAmendmentService(broker, store, new InMemoryOrderLifecycleStore(), new FakeClock());
        return new Harness(
            new ProtectiveStopDriftAdopter(stops, amendments, new FakeClock(), log, positions: broker), broker, stops, log);
    }

    // 既存の保護（S1・主張 15 株）。増える形でも削らない・作り足さないことを見るための基準。
    private static ProtectiveStopOrder AddSoftwareStop(Harness h, TradeSide entrySide)
    {
        var entry = Guid.NewGuid();
        var stop = new ProtectiveStopOrder(
            entry, Guid.NewGuid(), string.Empty, "AAPL", Market.UnitedStates, entrySide, ProductType.Cash,
            BrokerProvider.MoomooSimulate, 15, 950m, 1m, Attempt: 0, ProtectiveStopState.Active,
            Now.AddMinutes(-20), Now.AddMinutes(-20),
            Mechanism: StopLossExecutionMethod.SoftwareStop, RemainingProtected: 15);
        h.Stops.Save(stop);
        return stop;
    }

    private static PositionDriftAdopted Adopted(int before, int after) =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, before, after, after, Now.AddMinutes(-5), 1_000m,
            RealizedPnlRecorded: false, ReferencePrice: 1_010m, EstimatedPnlInBase: null,
            Actor: "owner", Reason: "テスト", AdoptedAt: Now);

    // 🔴 T-10-1009: 生む（0→50・0→−20）・増やす（10→15）・反転（10→−3・−10→3）。増えた株数は符号付きの前後から決まる。
    [Theory]
    [InlineData(0, 50, 50)]
    [InlineData(0, -20, 20)]
    [InlineData(10, 15, 5)]
    [InlineData(10, -3, 3)]
    [InlineData(-10, 3, 3)]
    public async Task 建玉が生じる増える形は保護を作らず帳簿もブローカーも変えずCriticalで知らせる(
        int before, int after, int added)
    {
        var h = NewHarness();
        var existing = AddSoftwareStop(h, before < 0 ? TradeSide.Sell : TradeSide.Buy);
        var adopted = Adopted(before, after);

        // 同じ取り込みの再配送（2 回）でも保護記録は増えない（何も書かないため）。
        var first = await h.Adopter.ApplyAsync(adopted);
        var second = await h.Adopter.ApplyAsync(adopted);

        foreach (var result in new[] { first, second })
        {
            result.Events.Should().BeEmpty("帳簿もブローカーも変えていないので、監査・通知へ出す事実は無い");
            result.Reduced.Should().Be(0);
            result.Scanned.Should().Be(0);
        }

        h.Stops.FindActive(100).Should().ContainSingle("保護記録を作り足さない（線を導けない）")
            .Which.Should().Be(existing, "既存の保護も削らない");
        h.Broker.CancelCount.Should().Be(0);
        h.Broker.PositionQueryCount.Should().Be(0);

        var critical = h.Log.Entries.Where(e => e.Level == LogLevel.Critical).Select(e => e.Message).ToList();
        critical.Should().HaveCount(2, "黙って飛ばさない（配送ごとに 1 行）");
        critical.Should().AllSatisfy(m => m.Should()
            .Contain($"増えた {added} 株")
            .And.Contain($"この {added} 株はシステムの保護を持ちません")
            .And.Contain("保護記録を作りません")
            .And.Contain(adopted.AdoptionId.ToString()));
        h.Log.Entries.Should().NotContain(e => e.Level == LogLevel.Warning, "Critical と Warning を二重に出さない");
    }

    // T-10-1009（続き）: 数量が変わらない形（減らない・増えない）は従来どおり Warning に留める（保護を持たない株は生じていない）。
    [Fact]
    public async Task 数量が変わらない形は従来どおりWarningに留める()
    {
        var h = NewHarness();
        AddSoftwareStop(h, TradeSide.Buy);

        var result = await h.Adopter.ApplyAsync(Adopted(10, 10));

        result.Events.Should().BeEmpty();
        h.Log.Entries.Should().NotContain(e => e.Level == LogLevel.Critical);
        h.Log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Message.Should().Contain("減少ではないため");
    }
}
