using Microsoft.EntityFrameworkCore;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using OrderExecutionService.Features.OrderExecution.RetrofitStopWidthFloor;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-10, FR-11, ADR-0049, #1136（オーナー裁定 2026-10-01）, IADR-0472: 損切り幅の下限（取得単価 × 2%）を、
// 下限の導入前に建てた Active・未到達の S1 の損切りラインへ遡及する（広げる向きだけ・冪等・監査に残す）。
public class SoftwareStopFloorRetrofitTests
{
    private static readonly DateTimeOffset Now = StopLegScriptedBroker.Now;

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed record Fixture(
        SoftwareStopFloorRetrofitter Retrofit, StopLegScriptedBroker Broker,
        IProtectiveStopOrderStore Stops, InMemoryExecutedOrderStore Store);

    private static Fixture NewFixture(IProtectiveStopOrderStore? stops = null)
    {
        var broker = new StopLegScriptedBroker();
        var store = new InMemoryExecutedOrderStore();
        stops ??= new InMemoryProtectiveStopOrderStore();
        return new Fixture(new SoftwareStopFloorRetrofitter(stops, store, broker, new FakeClock()), broker, stops, store);
    }

    private static ProtectiveStopOrder S1(
        decimal line, TradeSide side = TradeSide.Buy, string symbol = "NVDA", DateTimeOffset? triggeredAt = null,
        ProtectiveStopState state = ProtectiveStopState.Active)
    {
        var id = Guid.NewGuid();
        return new ProtectiveStopOrder(
            id, ProtectiveStopIds.SoftwareStopId(id), string.Empty, symbol, Market.UnitedStates, side,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 10, line, 1m, 0, state,
            Now.AddDays(-1), Now.AddDays(-1), StopLossExecutionMethod.SoftwareStop,
            triggeredAt, triggeredAt is null ? null : line, RemainingProtected: 10);
    }

    private static ProtectiveStopOrder BrokerSide(
        decimal line, StopLossExecutionMethod method, string symbol = "NVDA", string stopOrderId = "stop-s0")
    {
        var id = Guid.NewGuid();
        return new ProtectiveStopOrder(
            id, ProtectiveStopIds.StopDecisionId(id, 1), stopOrderId, symbol, Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 10, line, 1m, 1, ProtectiveStopState.Active,
            Now.AddDays(-1), Now.AddDays(-1), method);
    }

    private static void Entry(Fixture f, ProtectiveStopOrder stop, decimal averagePrice, int filled = 10) =>
        f.Store.Save(new ExecutionRecord(
            stop.EntryDecisionId, $"entry-{stop.EntryDecisionId:N}", stop.Symbol, stop.Market, stop.EntrySide,
            ProductType.Cash, PositionEffect.Open, 10, averagePrice, filled, filled > 0 ? averagePrice : 0m,
            filled >= 10 ? OrderStatus.Filled : OrderStatus.PartiallyFilled, 0m, Now.AddDays(-1)));

    private static Task<IReadOnlyList<SoftwareStopLineWidened>> Apply(Fixture f) =>
        f.Retrofit.ApplyAsync(f.Stops.FindActive(100));

    // T-10-1920, FR-10, ADR-0049 決定2・決定3, IADR-0472 決定2: 純関数。下限は取得単価 × 2%、広げる向きだけ。等しい・狭める向きは null。
    [Theory]
    [InlineData(TradeSide.Buy, 99, 100, 98.0)]       // 幅 1% → 2% へ広げる
    [InlineData(TradeSide.Sell, 101, 100, 102.0)]    // 空売りは上へ
    [InlineData(TradeSide.Buy, 97, 100, null)]     // 既に広い（3%）→ 変えない
    [InlineData(TradeSide.Sell, 103, 100, null)]
    [InlineData(TradeSide.Buy, 98, 100, null)]     // ちょうど下限 → 変えない
    [InlineData(TradeSide.Sell, 102, 100, null)]
    public void T_10_1920_下限は取得単価の2パーセントで広げる向きだけ(TradeSide side, double line, double entry, double? expected)
    {
        StopWidthFloorRetrofitPolicy.Widen(side, (decimal)line, (decimal)entry)
            .Should().Be(expected is null ? null : (decimal)expected.Value);
        StopWidthFloorRetrofitPolicy.FloorPerShare(100m).Should().Be(100m * StopWidthFloorDefaults.FallbackRatio);
        StopWidthFloorDefaults.FallbackRatio.Should().Be(0.02m);
    }

    // T-10-1921, FR-10, #1136: 稼働中の 3 例（9/30）。取得単価は裁定の「約」の値から逆算（新ライン ÷ 0.98）。端数は丸めない。
    [Theory]
    [InlineData("NVDA", 230.82, 226.52, 226.2036)]
    [InlineData("AMZN", 248.01, 245.14, 243.0498)]
    [InlineData("MSFT", 511.91, 503.98, 501.6718)]
    public async Task T_10_1921_稼働中の3例は裁定の値へ広がる(string symbol, double entry, double line, double expected)
    {
        var f = NewFixture();
        var stop = S1((decimal)line, symbol: symbol);
        f.Stops.Save(stop);
        Entry(f, stop, (decimal)entry);

        var events = await Apply(f);

        f.Stops.Find(stop.EntryDecisionId)!.TriggerPrice.Should().Be((decimal)expected);
        events.Should().ContainSingle().Which.StopLossPrice.Should().Be((decimal)expected);
        // 裁定の「約」の値（小数 2 桁）と一致する。
        Math.Round((decimal)expected, 2).Should().Be(Math.Round((decimal)entry * 0.98m, 2));
    }

    // T-10-1922, FR-10, FR-11, IADR-0472 決定2・決定5: 買い建て・売り建てを広げて保存し、事実（旧・新・取得単価・下限・出所）を返す。
    [Fact]
    public async Task T_10_1922_買い建てと売り建てを広げて保存し事実を返す()
    {
        var f = NewFixture();
        var longStop = S1(99m, TradeSide.Buy, "AAPL");
        var shortStop = S1(101m, TradeSide.Sell, "TSLA");
        f.Stops.Save(longStop);
        f.Stops.Save(shortStop);
        Entry(f, longStop, 100m);
        Entry(f, shortStop, 100m);

        var events = await Apply(f);

        var longSaved = f.Stops.Find(longStop.EntryDecisionId)!;
        longSaved.TriggerPrice.Should().Be(98m);
        longSaved.UpdatedAt.Should().Be(Now);
        longSaved.TriggeredAt.Should().BeNull("広げるだけで到達の記録は作らない");
        longSaved.RemainingProtected.Should().Be(10, "ライン以外は変えない");
        f.Stops.Find(shortStop.EntryDecisionId)!.TriggerPrice.Should().Be(102m);

        events.Should().HaveCount(2);
        events.Should().ContainEquivalentOf(new SoftwareStopLineWidened(
            longStop.EntryDecisionId, "AAPL", Market.UnitedStates, TradeSide.Buy, 100m, 99m, 98m, 2m,
            StopWidthFloorSource.Fallback2Pct, Now));
        events.Should().ContainEquivalentOf(new SoftwareStopLineWidened(
            shortStop.EntryDecisionId, "TSLA", Market.UnitedStates, TradeSide.Sell, 100m, 101m, 102m, 2m,
            StopWidthFloorSource.Fallback2Pct, Now));
        f.Broker.CancelCount.Should().Be(0);
        f.Broker.MarketCloseCount.Should().Be(0, "ラインを動かすだけで、注文は 1 本も出さない");
    }

    // T-10-1923, FR-10, #1136 の裁定, IADR-0472 決定3: 触らないもの——既に広い・到達済み・完了・S0・S3・取得単価が分からない。
    [Fact]
    public async Task T_10_1923_対象外の記録は触らない()
    {
        var f = NewFixture();
        var wider = S1(97m, symbol: "A");
        var triggered = S1(99m, symbol: "B", triggeredAt: Now.AddMinutes(-5));
        var completed = S1(99m, symbol: "C", state: ProtectiveStopState.Completed);
        var s0 = BrokerSide(99m, StopLossExecutionMethod.BrokerStopOrder, "D");
        var s3 = BrokerSide(99m, StopLossExecutionMethod.AlternativeBrokerOrderType, "E");
        var noRecord = S1(99m, symbol: "F");
        var unfilled = S1(99m, symbol: "G");
        foreach (var s in new[] { wider, triggered, completed, s0, s3, noRecord, unfilled })
            f.Stops.Save(s);
        foreach (var s in new[] { wider, triggered, completed, s0, s3 })
            Entry(f, s, 100m);
        Entry(f, unfilled, 100m, filled: 0);
        var versionBefore = f.Stops.Find(wider.EntryDecisionId)!.Version;

        // 完了の行は FindActive に載らないので、巡回の写しとしてわざと渡しても書かない（最新の行で判定し直す）。
        var events = await f.Retrofit.ApplyAsync([wider, triggered, completed, s0, s3, noRecord, unfilled]);

        events.Should().BeEmpty();
        f.Stops.Find(wider.EntryDecisionId)!.TriggerPrice.Should().Be(97m, "狭めない");
        f.Stops.Find(triggered.EntryDecisionId)!.TriggerPrice.Should().Be(99m, "到達済み（決済を続ける行）は動かさない");
        f.Stops.Find(completed.EntryDecisionId)!.TriggerPrice.Should().Be(99m);
        f.Stops.Find(s0.EntryDecisionId)!.TriggerPrice.Should().Be(99m, "S0 はブローカー側の注文");
        f.Stops.Find(s3.EntryDecisionId)!.TriggerPrice.Should().Be(99m, "S3 はブローカー側の代替注文");
        f.Stops.Find(noRecord.EntryDecisionId)!.TriggerPrice.Should().Be(99m, "取得単価が分からなければ当てない");
        f.Stops.Find(unfilled.EntryDecisionId)!.TriggerPrice.Should().Be(99m);
        f.Stops.Find(wider.EntryDecisionId)!.Version.Should().Be(versionBefore, "書かない（版が進まない）");
    }

    // T-10-1924, FR-10, #1136 の裁定, IADR-0472 決定3（IADR-0461 決定1・4 と同じ見分け方）: 決済が処理中の群は触らない。
    // 判断の手仕舞いがブローカーで生きている → 触らない／終端・照会 null → 当てる／S0 の保護レグだけ → 当てる。
    [Theory]
    [InlineData("live", false)]
    [InlineData("terminal", true)]
    [InlineData("unknown", true)]
    [InlineData("s0-leg-only", true)]
    public async Task T_10_1924_決済が処理中の群は触らない(string closeState, bool widened)
    {
        var f = NewFixture();
        var stop = S1(99m, symbol: "AAPL");
        f.Stops.Save(stop);
        Entry(f, stop, 100m);

        var closeIntent = new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            BrokerProvider.MoomooSimulate, 10, 99.5m, PositionEffect.Close);
        if (closeState == "s0-leg-only")
        {
            var s0 = BrokerSide(90m, StopLossExecutionMethod.BrokerStopOrder, "AAPL", "s0-order");
            f.Stops.Save(s0);
            f.Store.Save(new ExecutionRecord(s0.StopDecisionId, "s0-order", "AAPL", Market.UnitedStates, TradeSide.Sell,
                ProductType.Cash, PositionEffect.Close, 10, 90m, 0, 0m, OrderStatus.Accepted, 0m, Now));
            f.Broker.Orders["s0-order"] = new BrokerOrder("s0-order", closeIntent, OrderStatus.Accepted, 0, 0m, Now, null);
        }
        else
        {
            f.Store.Save(new ExecutionRecord(Guid.NewGuid(), "decision-close", "AAPL", Market.UnitedStates, TradeSide.Sell,
                ProductType.Cash, PositionEffect.Close, 10, 99.5m, 0, 0m, OrderStatus.Accepted, 0m, Now));
            if (closeState == "live")
                f.Broker.Orders["decision-close"] = new BrokerOrder("decision-close", closeIntent, OrderStatus.Accepted, 0, 0m, Now, null);
            else if (closeState == "terminal")
                f.Broker.Orders["decision-close"] = new BrokerOrder("decision-close", closeIntent, OrderStatus.Cancelled, 0, 0m, Now, Now);
        }

        var events = await Apply(f);

        f.Stops.Find(stop.EntryDecisionId)!.TriggerPrice.Should().Be(widened ? 98m : 99m);
        events.Should().HaveCount(widened ? 1 : 0);
        f.Broker.CancelCount.Should().Be(0, "処理中の決済は取り消さない（見るだけ）");
    }

    // T-10-1925, FR-10, IADR-0472 決定1・決定2: 冪等。2 回目は書かず事実も出さない。取得単価が下がれば広げ、上がっても狭めない（窓 C）。
    [Fact]
    public async Task T_10_1925_冪等で2回目は書かず取得単価の変化には広い方へ収束する()
    {
        var f = NewFixture();
        var stop = S1(99m, symbol: "AAPL");
        f.Stops.Save(stop);
        Entry(f, stop, 100m);

        (await Apply(f)).Should().ContainSingle();
        var version = f.Stops.Find(stop.EntryDecisionId)!.Version;

        (await Apply(f)).Should().BeEmpty("同じ下限のラインでは何もしない");
        f.Stops.Find(stop.EntryDecisionId)!.Version.Should().Be(version, "書かない");

        // 平均の変化は約定追跡が同じ記録へ書く（UpdateOutcome）。
        void Average(decimal price) => f.Store.UpdateOutcome(
            $"entry-{stop.EntryDecisionId:N}", OrderStatus.Filled, 10, price, 0m, Now.AddDays(-1)).Should().BeTrue();

        Average(101m); // 平均が上がる（減る側）→ 下限のライン 98.98 は今の 98 より内側。狭めない。
        (await Apply(f)).Should().BeEmpty();
        f.Stops.Find(stop.EntryDecisionId)!.TriggerPrice.Should().Be(98m);

        Average(99m);  // 平均が下がる（増える側）→ 97.02 まで広げる。
        var events = await Apply(f);
        events.Should().ContainSingle().Which.PreviousStopLossPrice.Should().Be(98m);
        f.Stops.Find(stop.EntryDecisionId)!.TriggerPrice.Should().Be(97.02m);
    }

    // T-10-1927, FR-10, IADR-0472 決定1: 永続化。EF（別のコンテキストから読める）とインメモリの両方で、広げたラインが残る。
    [Fact]
    public async Task T_10_1927_広げたラインはEFとインメモリの両方で永続化される()
    {
        var dbName = Guid.NewGuid().ToString();
        OrderExecutionDbContext NewContext() => new(new DbContextOptionsBuilder<OrderExecutionDbContext>()
            .UseInMemoryDatabase(dbName).Options);

        var stop = S1(245.14m, symbol: "AMZN");
        using (var db = NewContext())
        {
            var f = NewFixture(new EfProtectiveStopOrderStore(db));
            f.Stops.Save(stop);
            Entry(f, stop, 248.01m);
            (await Apply(f)).Should().ContainSingle();
        }

        using (var db = NewContext())
        {
            var found = new EfProtectiveStopOrderStore(db).Find(stop.EntryDecisionId)!;
            found.TriggerPrice.Should().Be(243.0498m);
            found.TriggeredAt.Should().BeNull();
        }

        var memory = NewFixture();
        memory.Stops.Save(stop);
        Entry(memory, stop, 248.01m);
        await Apply(memory);
        memory.Stops.Find(stop.EntryDecisionId)!.TriggerPrice.Should().Be(243.0498m);
    }

    // T-10-1928, FR-10, IADR-0472 決定1: ガードの巡回の先頭で遡及し、事実を巡回の結果に載せる（建玉照会が不明な巡回でも）。
    // 遡及の口を渡さない組み立ては従来どおり（ラインを動かさない）。
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task T_10_1928_ガードは巡回の先頭で遡及し事実を結果に載せる(bool withRetrofit, bool positionsKnown)
    {
        var f = NewFixture();
        var stop = S1(226.52m, symbol: "NVDA");
        f.Stops.Save(stop);
        Entry(f, stop, 230.82m);
        f.Broker.Positions = positionsKnown ? [new BrokerPositionSnapshot("NVDA", Market.UnitedStates, 10, 230.82m)] : null;
        var reservations = new InMemoryOrderReservationStore();
        var clock = new FakeClock();
        var executor = new SoftwareStopExecutor(f.Broker, f.Broker, f.Stops, f.Store, reservations, clock);
        var guard = new ProtectiveStopGuard(
            f.Broker, f.Broker, f.Stops, f.Store, reservations, clock, softwareStops: executor,
            floorRetrofit: withRetrofit ? f.Retrofit : null);

        var result = await guard.RunOnceAsync(10);

        f.Stops.Find(stop.EntryDecisionId)!.TriggerPrice.Should().Be(withRetrofit ? 226.2036m : 226.52m);
        result.Events.OfType<SoftwareStopLineWidened>().Should().HaveCount(withRetrofit ? 1 : 0);
        f.Broker.MarketCloseCount.Should().Be(0);
    }

    // ---- 窓 A（遡及の書き込みと到達の武装の競合）。IADR-0472 決定4・作業仕様書の窓の表 ----

    // 候補の一覧を読んだ直後に割り込みを走らせる保護記録ストア（到達の購読が候補を読んだ後に、ガードが遡及した状況を作る）。
    private sealed class InterleavingStore(InMemoryProtectiveStopOrderStore inner) : IProtectiveStopOrderStore
    {
        public Action? AfterCandidatesRead { get; set; }

        public void Save(ProtectiveStopOrder stop) => inner.Save(stop);

        public bool TrySave(ProtectiveStopOrder stop) => inner.TrySave(stop);

        public ProtectiveStopOrder? Find(Guid entryDecisionId) => inner.Find(entryDecisionId);

        public IReadOnlyList<ProtectiveStopOrder> FindActive(int batchSize) => inner.FindActive(batchSize);

        public IReadOnlyList<ProtectiveStopOrder> FindActiveSoftwareStops(string symbol, Market market, TradeSide entrySide)
        {
            var result = inner.FindActiveSoftwareStops(symbol, market, entrySide);
            var hook = AfterCandidatesRead;
            AfterCandidatesRead = null;
            hook?.Invoke();
            return result;
        }

        public IReadOnlyList<ProtectiveStopOrder> FindActiveFor(string symbol, Market market, TradeSide entrySide) =>
            inner.FindActiveFor(symbol, market, entrySide);

        public IReadOnlyList<ProtectiveStopOrder> FindRecentFor(string symbol, Market market, TradeSide entrySide, int limit) =>
            inner.FindRecentFor(symbol, market, entrySide, limit);

        public IReadOnlyList<ProtectiveStopOrder> FindCompletedSoftwareStops(
            string symbol, Market market, TradeSide entrySide, int limit) =>
            inner.FindCompletedSoftwareStops(symbol, market, entrySide, limit);

        public IReadOnlyList<ProtectiveStopOrder> FindUnattributedNotified(int limit) => inner.FindUnattributedNotified(limit);
    }

    private static (Fixture F, InterleavingStore Stops, SoftwareStopExecutor Executor, ProtectiveStopOrder Stop) RaceFixture()
    {
        var stops = new InterleavingStore(new InMemoryProtectiveStopOrderStore());
        var f = NewFixture(stops);
        f.Broker.Positions = [new BrokerPositionSnapshot("NVDA", Market.UnitedStates, 10, 230.82m)];
        var stop = S1(226.52m, symbol: "NVDA");
        stops.Save(stop);
        Entry(f, stop, 230.82m);
        var executor = new SoftwareStopExecutor(
            f.Broker, f.Broker, stops, f.Store, new InMemoryOrderReservationStore(), new FakeClock());
        return (f, stops, executor, stop);
    }

    private static StopLossTriggered Trigger(decimal price) =>
        new(Guid.NewGuid(), "NVDA", Market.UnitedStates, TradeSide.Buy, 10, price, 226.52m, Now);

    // T-10-1930, FR-10, IADR-0472 決定4（窓 A の P1・増える側）: 候補（旧ライン 226.52）を読んだ後にガードが 226.2036 へ広げた。
    // 価格 226.40 は旧ラインなら到達・新ラインなら未到達 → 武装しない（最新の行のラインで判定し直す）。
    [Fact]
    public async Task T_10_1930_候補を読んだ後に広げたら旧ラインでの到達で武装しない()
    {
        var (f, stops, executor, stop) = RaceFixture();
        stops.AfterCandidatesRead = () => f.Retrofit.ApplyAsync(stops.FindActive(10)).GetAwaiter().GetResult();

        var result = await executor.OnTriggeredAsync(Trigger(226.40m));

        var saved = stops.Find(stop.EntryDecisionId)!;
        saved.TriggerPrice.Should().Be(226.2036m);
        saved.TriggeredAt.Should().BeNull("新ラインが正であり、旧ラインでの到達は武装しない");
        result.Events.Should().BeEmpty();
        f.Broker.MarketCloseCount.Should().Be(0);
    }

    // T-10-1931, FR-10, IADR-0472 決定3・決定4（窓 A の P2・減る側）: 武装が先に書かれた後、ガードが古い写し（未到達）で遡及しようとする
    // → 到達の記録を巻き戻さず、ラインも動かさない（最新の行で対象かを判定し直す）。
    [Fact]
    public async Task T_10_1931_武装の後に古い写しで遡及しても到達の記録を巻き戻さない()
    {
        var (f, stops, executor, stop) = RaceFixture();
        var staleSnapshot = stops.FindActive(10); // 巡回の写し（未到達）
        f.Broker.MarketUnavailable = true;        // 決済は確実に未発注で終わり、行は Active・到達済みのまま残る

        await executor.OnTriggeredAsync(Trigger(226.40m));
        var armed = stops.Find(stop.EntryDecisionId)!;
        armed.TriggeredAt.Should().NotBeNull();
        armed.State.Should().Be(ProtectiveStopState.Active);

        var events = await f.Retrofit.ApplyAsync(staleSnapshot);

        events.Should().BeEmpty();
        var after = stops.Find(stop.EntryDecisionId)!;
        after.TriggeredAt.Should().Be(armed.TriggeredAt);
        after.TriggerPrice.Should().Be(226.52m, "到達済みの行のラインは動かさない");
        after.Version.Should().Be(armed.Version);
    }

    // T-10-1932, FR-10, IADR-0472 決定4（窓 A の P3・対照）: 新ラインも割った到達（226.00）は、遡及が割り込んでも武装して決済する。
    [Fact]
    public async Task T_10_1932_新ラインも割った到達は遡及が割り込んでも武装して決済する()
    {
        var (f, stops, executor, stop) = RaceFixture();
        stops.AfterCandidatesRead = () => f.Retrofit.ApplyAsync(stops.FindActive(10)).GetAwaiter().GetResult();

        var result = await executor.OnTriggeredAsync(Trigger(226.00m));

        f.Broker.MarketCloseCount.Should().Be(1);
        result.Events.OfType<SoftwareStopExecuted>().Should().ContainSingle()
            .Which.StopLossPrice.Should().Be(226.2036m, "決済は広げた後のラインで発動した");
    }
}
