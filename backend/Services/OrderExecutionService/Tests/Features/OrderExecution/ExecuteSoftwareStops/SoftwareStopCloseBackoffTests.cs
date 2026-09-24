using Microsoft.Extensions.Logging;
using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 FR-10, FR-12, UC-02, ADR-0040 決定1（S1）, #833 項目2, IADR-0344 追記(14):
// **S1 の決済が続けて売れない行の、行ごとの待ち時間**（T-10-790..T-10-794）。
//
// 開場中に拒否が続くと、ガード（30 秒）と市場監視の到達（60 秒）が成行を撃ち続けた（毎分 3 件前後）。
// かつての歯止めは「到達 1 回あたり 3 試行で到達の記録を消す」打ち切りで、**価格が戻ると二度と撃たなかった**（出口を塞ぐ）。
// 是正: 到達の記録は消さず、連続失敗 n 回目の後 min(30 秒 × 2^(n−1), 15 分) の待ち時間を置いて撃ち直しを続ける。
//
// 🔴 時計は注入した可変の値のみ（壁時計の待ちを作らない）。配置は稼働 PoC と同じ AAPL・ライン 338.51。
public class SoftwareStopCloseBackoffTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 14, 0, 0, TimeSpan.Zero);

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class FakeBroker : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; }

        public OrderStatus CloseStatus { get; set; } = OrderStatus.Rejected;

        public List<(OrderIntent Intent, Guid DecisionId, DateTimeOffset At)> MarketCloses { get; } = [];

        public Func<DateTimeOffset> Clock { get; set; } = () => T0;

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException("発動は通常発注を行わない");

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            throw new NotSupportedException("S1 はブローカーへ逆指値を出さない");

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default)
        {
            MarketCloses.Add((closeIntent, decisionId, Clock()));
            return Task.FromResult(new BrokerOrder(
                $"close-{MarketCloses.Count}", closeIntent, CloseStatus, 0, 0m, Clock(),
                OrderStatusLifecycle.IsTerminal(CloseStatus) ? Clock() : null));
        }

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult(Positions);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IEnumerable<string> Errors => Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed record Fixture(
        SoftwareStopExecutor Executor, SoftwareStopReArmer ReArmer, FakeBroker Broker,
        InMemoryProtectiveStopOrderStore Stops, InMemoryExecutedOrderStore Store, MutableClock Clock,
        RecordingLogger<SoftwareStopExecutor> Log);

    private static Fixture NewFixture()
    {
        var clock = new MutableClock(T0);
        var broker = new FakeBroker { Clock = () => clock.UtcNow };
        var stops = new InMemoryProtectiveStopOrderStore();
        var store = new InMemoryExecutedOrderStore();
        var log = new RecordingLogger<SoftwareStopExecutor>();
        return new Fixture(
            new SoftwareStopExecutor(broker, broker, stops, store, new InMemoryOrderReservationStore(), clock, log),
            new SoftwareStopReArmer(stops, store, clock),
            broker, stops, store, clock, log);
    }

    private static BrokerPositionSnapshot Long(int qty) => new("AAPL", Market.UnitedStates, qty, 340m);

    // S1 の行（エントリーは約定済み・未到達）を置く。
    private static ProtectiveStopOrder Stop(Fixture f, int quantity, decimal line = 338.51m, int minutesOld = 360)
    {
        var id = Guid.NewGuid();
        var at = T0.AddMinutes(-minutesOld);
        var stop = new ProtectiveStopOrder(
            id, ProtectiveStopIds.SoftwareStopId(id), string.Empty, "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, quantity, line, 1m, 0, ProtectiveStopState.Active, at, at,
            StopLossExecutionMethod.SoftwareStop);
        f.Stops.Save(stop);
        f.Store.Save(new ExecutionRecord(
            id, $"entry-{id:N}", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            PositionEffect.Open, quantity, 340m, quantity, 340m, OrderStatus.Filled, 0m, at));
        return stop;
    }

    private static StopLossTriggered Trigger(DateTimeOffset detectedAt, decimal price = 338.20m) =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, 707, price, 338.51m, detectedAt);

    // ガードの巡回と同じ入口（行を引き直して TryCloseAsync）。
    private static Task<SoftwareStopCloseOutcome> Guard(Fixture f, ProtectiveStopOrder stop) =>
        f.Executor.TryCloseAsync(f.Stops.Find(stop.EntryDecisionId)!, snapshot: null);

    // T-10-790: 連続失敗 n 回目の後 min(30 秒 × 2^(n−1), 15 分) が過ぎるまで、**ハンドラからもガードからも**成行を送らない。
    [Fact]
    public async Task T_10_790_続けて拒否された行は待ち時間が過ぎるまでハンドラからもガードからも撃たない()
    {
        var f = NewFixture();
        f.Broker.Positions = [Long(707)];
        var stop = Stop(f, 707);

        await f.Executor.OnTriggeredAsync(Trigger(T0));
        f.Broker.MarketCloses.Should().ContainSingle("最初の到達ではすぐ撃つ");

        // 期待する待ち時間の列（秒）。上限 15 分で頭打ちになる。
        int[] expected = [30, 60, 120, 240, 480, 900, 900];
        var lastSent = T0;
        for (var n = 1; n <= expected.Length; n++)
        {
            var row = f.Stops.Find(stop.EntryDecisionId)!;
            row.CloseFailures.Should().Be(n);
            row.NextCloseAttemptAt.Should().Be(lastSent.AddSeconds(expected[n - 1]), $"連続失敗 {n} 回目の後の待ち時間");
            SoftwareStopExecutor.CloseBackoff(n).Should().Be(TimeSpan.FromSeconds(expected[n - 1]));

            // 待ち時間の 1 秒前: ガードも、前回から 60 秒間隔で続く到達（新しい窓ではない）も撃たない。
            f.Clock.UtcNow = lastSent.AddSeconds(expected[n - 1] - 1);
            var sentBefore = f.Broker.MarketCloses.Count;
            (await Guard(f, stop)).Kind.Should().Be(SoftwareStopCloseKind.Deferred);
            var seen = f.Stops.Find(stop.EntryDecisionId)!.LastTriggerSeenAt!.Value;
            await f.Executor.OnTriggeredAsync(Trigger(seen.AddSeconds(60)));
            f.Broker.MarketCloses.Should().HaveCount(sentBefore, $"待ち時間中は撃たない（{n} 回目の後）");

            // 待ち時間が過ぎた: ガードが撃つ。
            f.Clock.UtcNow = lastSent.AddSeconds(expected[n - 1]);
            await Guard(f, stop);
            f.Broker.MarketCloses.Should().HaveCount(sentBefore + 1, $"待ち時間の後は撃ち直す（{n} 回目の後）");
            lastSent = f.Clock.UtcNow;
        }

        // 試行ごとに別の DecisionId（予約・記録の再送防止はそのまま効いている）。
        f.Broker.MarketCloses.Select(c => c.DecisionId).Should().OnlyHaveUniqueItems();
    }

    // T-10-791: 🔴 拒否が何回続いても到達の記録は消えず、**価格が戻って到達が途絶えても**ガードが撃ち直して決済が通る。
    [Fact]
    public async Task T_10_791_拒否が続いても到達の記録は消えず価格が戻ってもガードが撃ち直して決済が通る()
    {
        var f = NewFixture();
        f.Broker.Positions = [Long(707)];
        var stop = Stop(f, 707);

        await f.Executor.OnTriggeredAsync(Trigger(T0));
        for (var n = 1; n <= 5; n++)
        {
            f.Clock.UtcNow = f.Stops.Find(stop.EntryDecisionId)!.NextCloseAttemptAt!.Value;
            await Guard(f, stop);
        }

        var row = f.Stops.Find(stop.EntryDecisionId)!;
        row.CloseFailures.Should().Be(6);
        row.TriggeredAt.Should().Be(T0, "打ち切りで到達の記録を消さない（消すと価格が戻ったとき二度と撃たない）");
        row.TriggeredPrice.Should().Be(338.20m);
        row.State.Should().Be(ProtectiveStopState.Active);

        // 価格はラインの内側へ戻った（市場監視はもう到達を出さない）。ガードだけが撃ち直す。
        f.Broker.CloseStatus = OrderStatus.Accepted;
        f.Clock.UtcNow = row.NextCloseAttemptAt!.Value;
        var outcome = await Guard(f, stop);

        outcome.Kind.Should().Be(SoftwareStopCloseKind.Completed);
        outcome.Event!.Outcome.Should().Be(SoftwareStopOutcome.ClosePlaced);
        f.Broker.MarketCloses.Last().Intent.Quantity.Should().Be(707);
        f.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // T-10-792: Critical（CloseRejected）は連続失敗 3 回目で出て以後 4 回ごと。失敗のたびに LogError が出る（抑止しない）。
    [Fact]
    public async Task T_10_792_Criticalは連続失敗3回目と以後4回ごとに出て失敗のたびにエラーログが出る()
    {
        var f = NewFixture();
        f.Broker.Positions = [Long(707)];
        var stop = Stop(f, 707);

        var criticalAt = new List<int>();
        var first = await f.Executor.OnTriggeredAsync(Trigger(T0));
        if (first.Events.OfType<SoftwareStopExecuted>().Any(e => e.Outcome == SoftwareStopOutcome.CloseRejected))
            criticalAt.Add(1);

        for (var n = 2; n <= 11; n++)
        {
            f.Clock.UtcNow = f.Stops.Find(stop.EntryDecisionId)!.NextCloseAttemptAt!.Value;
            var outcome = await Guard(f, stop);
            outcome.Kind.Should().Be(SoftwareStopCloseKind.Rejected);
            if (outcome.Event is { Outcome: SoftwareStopOutcome.CloseRejected } critical)
            {
                criticalAt.Add(n);
                critical.Attempt.Should().Be(n);
                critical.Quantity.Should().Be(707);
            }
        }

        criticalAt.Should().Equal([3, 7, 11], "3 回目で初めて鳴らし、以後 4 回ごと（上限 15 分に達した後はおよそ 1 時間ごと）");
        f.Log.Errors.Count(m => m.Contains("受理されませんでした")).Should().Be(11, "失敗のたびにエラーログを残す");
        f.Stops.Find(stop.EntryDecisionId)!.TriggeredAt.Should().Be(T0);
    }

    // T-10-793: 前回の到達から 5 分以上空いた到達（閉場を挟んだ・価格が一度戻った）では数えと待ち時間をやり直してすぐ撃つ。
    // 60 秒間隔で続く到達では戻さない（戻すと待ち時間が毎分消えて拒否連発が再発する）。
    [Fact]
    public async Task T_10_793_間が空いた到達では待ち時間をやり直し60秒間隔の到達では戻さない()
    {
        var f = NewFixture();
        f.Broker.Positions = [Long(707)];
        var stop = Stop(f, 707);

        // 開場中・ラインを越えたまま 60 秒ごとに到達（20 分＝21 回）。ハンドラだけで撃つ。
        for (var minute = 0; minute <= 20; minute++)
        {
            f.Clock.UtcNow = T0.AddMinutes(minute);
            await f.Executor.OnTriggeredAsync(Trigger(f.Clock.UtcNow));
        }

        // 待ち時間 30s / 60s / 120s / 240s / 480s の後にだけ撃つ（0・1・2・4・8・16 分）。待ち時間が無ければ 21 本。
        f.Broker.MarketCloses.Select(c => (int)(c.At - T0).TotalMinutes).Should().Equal([0, 1, 2, 4, 8, 16]);
        var row = f.Stops.Find(stop.EntryDecisionId)!;
        row.CloseFailures.Should().Be(6);
        row.NextCloseAttemptAt.Should().Be(T0.AddMinutes(16).AddMinutes(15));
        row.LastTriggerSeenAt.Should().Be(T0.AddMinutes(20));

        // 遅れて届いた古い到達（再配送）は時刻を巻き戻さず、窓も開かない。
        f.Clock.UtcNow = T0.AddMinutes(21);
        await f.Executor.OnTriggeredAsync(Trigger(T0.AddMinutes(3)));
        f.Broker.MarketCloses.Should().HaveCount(6);
        f.Stops.Find(stop.EntryDecisionId)!.LastTriggerSeenAt.Should().Be(T0.AddMinutes(20));

        // 5 分の空白（閉場を挟んだ・価格が一度戻った）の後の到達: 待ち時間（T0+31 分まで）の途中でもすぐ撃つ。
        f.Clock.UtcNow = T0.AddMinutes(25);
        await f.Executor.OnTriggeredAsync(Trigger(f.Clock.UtcNow));
        f.Broker.MarketCloses.Should().HaveCount(7, "新しい窓では待ち時間を持ち越さない");
        row = f.Stops.Find(stop.EntryDecisionId)!;
        row.CloseFailures.Should().Be(1, "数えは 0 からやり直し、この拒否で 1");
        row.NextCloseAttemptAt.Should().Be(T0.AddMinutes(25).AddSeconds(30));
    }

    // T-10-794: 再武装は 0 約定なら失敗として数えて待ち時間を置き、1 株でも約定していれば数えを 0 へ戻す。
    // 🔴 同じ銘柄の別の行（AAPL 715 株 / 713 株＝稼働 PoC の 2 行配置）の待ち時間には影響しない。
    [Fact]
    public async Task T_10_794_0約定の再武装は待ち時間を置き同じ銘柄の別の行は待たない()
    {
        var f = NewFixture();
        f.Broker.Positions = [Long(715 + 713)];
        f.Broker.CloseStatus = OrderStatus.Accepted;
        var a = Stop(f, 715, line: 330.88m, minutesOld: 400);
        var b = Stop(f, 713, line: 331.67m, minutesOld: 390);

        // 両方の行がラインを割った。B の決済は受理された（A も受理）。
        await f.Executor.OnTriggeredAsync(Trigger(T0, price: 329.00m));
        f.Broker.MarketCloses.Should().HaveCount(2);
        f.Stops.Find(b.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);

        // B の決済だけが 0 約定で失効 → 約定追跡が再武装する。
        f.Clock.UtcNow = T0.AddMinutes(5);
        var bLeg = f.Store.FindByDecisionId(ProtectiveStopIds.SoftwareCloseDecisionId(b.EntryDecisionId, 1))!;
        f.Store.UpdateOutcome(bLeg.OrderId, OrderStatus.Expired, 0, 0m, 0m, f.Clock.UtcNow);
        f.ReArmer.OnCloseTerminalized(bLeg, OrderStatus.Expired, filledQuantity: 0)!
            .Outcome.Should().Be(SoftwareStopOutcome.CloseUnfilled);

        var reArmed = f.Stops.Find(b.EntryDecisionId)!;
        reArmed.State.Should().Be(ProtectiveStopState.Active);
        reArmed.CloseFailures.Should().Be(1);
        reArmed.NextCloseAttemptAt.Should().Be(T0.AddMinutes(5).AddSeconds(30));

        // A は影響を受けない（別の行の待ち時間を共有しない）。
        var aRow = f.Stops.Find(a.EntryDecisionId)!;
        aRow.CloseFailures.Should().Be(0);
        aRow.NextCloseAttemptAt.Should().BeNull();

        // 建玉照会は B の 713 株が戻ったことを映している（A の 715 株は売れた）。
        f.Broker.Positions = [Long(713)];
        f.Clock.UtcNow = T0.AddMinutes(5).AddSeconds(10);
        (await Guard(f, b)).Kind.Should().Be(SoftwareStopCloseKind.Deferred, "0 約定の再武装の直後は待つ");
        f.Broker.MarketCloses.Should().HaveCount(2);

        f.Clock.UtcNow = T0.AddMinutes(5).AddSeconds(30);
        (await Guard(f, b)).Event!.Outcome.Should().Be(SoftwareStopOutcome.ClosePlaced);
        f.Broker.MarketCloses.Should().HaveCount(3);
        f.Broker.MarketCloses.Last().Intent.Quantity.Should().Be(713);

        // 2 本目も 200 株だけ約定して失効 → 前進した。数えを 0 へ戻し、残り 513 株はすぐ撃ってよい。
        f.Clock.UtcNow = T0.AddMinutes(10);
        var bLeg2 = f.Store.FindByDecisionId(ProtectiveStopIds.SoftwareCloseDecisionId(b.EntryDecisionId, 2))!;
        f.Store.UpdateOutcome(bLeg2.OrderId, OrderStatus.Expired, 200, 331m, 0m, f.Clock.UtcNow);
        f.ReArmer.OnCloseTerminalized(bLeg2, OrderStatus.Expired, filledQuantity: 200);
        var progressed = f.Stops.Find(b.EntryDecisionId)!;
        progressed.RemainingProtected.Should().Be(513);
        progressed.CloseFailures.Should().Be(0, "1 株でも約定した再武装は前進であり失敗として数えない");
        progressed.NextCloseAttemptAt.Should().BeNull();
    }
}
