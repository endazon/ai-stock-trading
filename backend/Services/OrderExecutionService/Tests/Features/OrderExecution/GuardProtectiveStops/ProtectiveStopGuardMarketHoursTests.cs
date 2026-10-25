using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-2995〜T-10-3000・T-10-3002・T-10-3003, FR-10, NFR, #1444（親 #1423 M-1）, IADR-0574: **閉場中は保護逆指値ガードと S1 が新しい注文を送らない。**
//
// 是正前: ガードは時間帯を見ずに 30 秒ごとに巡回し、引けで失効した当日限りの逆指値（保護レグは有効期限を載せない）を
// 閉場中に見つけると張り直し、拒否されれば成行で手仕舞った。場外の成行は翌寄りで約定し、**損切りラインに達していない建玉を
// 寄り値で手仕舞う**。S1 の到達済み行の成行の撃ち直しも同じく閉場中に出た。
//
// 時計は注入した可変の値で、判定は本番と同じ MarketHoursSessionGate（共有カーネルの MarketHours）を使う。
// 2026-10 は米国東部夏時間（UTC−4）、2026-11 は標準時（UTC−5）。
public class ProtectiveStopGuardMarketHoursTests
{
    // 2026-10-22（木）16:00:00 EDT ＝引けちょうど（閉場）。
    private static readonly DateTimeOffset AtClose = new(2026, 10, 22, 20, 0, 0, TimeSpan.Zero);

    // 2026-10-23（金）09:30:00 EDT ＝次の寄り付き（場中）。
    private static readonly DateTimeOffset NextOpen = new(2026, 10, 23, 13, 30, 0, TimeSpan.Zero);

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed record Harness(
        ProtectiveStopGuard Guard,
        StopLegScriptedBroker Broker,
        InMemoryProtectiveStopOrderStore Stops,
        InMemoryExecutedOrderStore Store,
        MutableClock Clock,
        SoftwareStopExecutor Executor);

    private static Harness NewHarness(DateTimeOffset now, IMarketSessionGate? gate = null)
    {
        gate ??= MarketHoursSessionGate.Instance;
        var clock = new MutableClock(now);
        var broker = new StopLegScriptedBroker();
        var stops = new InMemoryProtectiveStopOrderStore();
        var store = new InMemoryExecutedOrderStore();
        var reservations = new InMemoryOrderReservationStore();
        var executor = new SoftwareStopExecutor(
            broker, broker, stops, store, reservations, clock, gate);
        var guard = new ProtectiveStopGuard(
            broker, broker, stops, store, reservations, clock, gate, softwareStops: executor);
        return new Harness(guard, broker, stops, store, clock, executor);
    }

    // S0（ブローカー側の逆指値）。元の逆指値 "stop-1" は照会で失効（Cancelled）を返す＝毎巡回 ReplaceOrClose へ入る。
    private static ProtectiveStopOrder LapsedS0(Harness h, DateTimeOffset createdAt)
    {
        var stop = new ProtectiveStopOrder(
            Guid.NewGuid(), Guid.NewGuid(), StopLegScriptedBroker.LapsedStopOrderId, "AAPL", Market.UnitedStates,
            TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, Attempt: 1,
            ProtectiveStopState.Active, createdAt, createdAt);
        h.Stops.Save(stop);
        return stop;
    }

    // S1（ソフトウェア逆指値・エントリーは約定済み・未到達）。
    private static ProtectiveStopOrder S1(Harness h, DateTimeOffset createdAt)
    {
        var id = Guid.NewGuid();
        var stop = new ProtectiveStopOrder(
            id, ProtectiveStopIds.SoftwareStopId(id), string.Empty, "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, 0, ProtectiveStopState.Active,
            createdAt, createdAt, StopLossExecutionMethod.SoftwareStop);
        h.Stops.Save(stop);
        h.Store.Save(new ExecutionRecord(
            id, $"entry-{id:N}", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            PositionEffect.Open, 10, 1_000m, 10, 1_000m, OrderStatus.Filled, 0m, createdAt));
        return stop;
    }

    private static StopLossTriggered Trigger(DateTimeOffset detectedAt) =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, TradeSide.Buy, 10, 940m, 950m, detectedAt);

    // T-10-2995: 🔴 本 issue そのもの。引けで失効した逆指値を閉場中に見つけても、張り直しも成行の手仕舞いも送らない。
    // 行は Active のまま失効した元の逆指値を指し続ける（次の開場で張り直す状態）。不明・失敗にも数えない。
    [Fact]
    public async Task T_10_2995_閉場中に失効した逆指値を見つけても張り直しも成行の手仕舞いも送らない()
    {
        var h = NewHarness(AtClose);
        h.Broker.Stop = StopLegScriptedBroker.StopBehavior.Reject; // 是正前は拒否 → 成行へ倒れていた経路
        var stop = LapsedS0(h, AtClose.AddHours(-6));

        // 閉場中の巡回を何回回しても（30 秒ごと × 一晩の一部）注文は 1 本も出ない。
        for (var i = 0; i < 5; i++)
        {
            h.Clock.UtcNow = AtClose.AddMinutes(30 * i);
            var result = await h.Guard.RunOnceAsync(10);
            result.AwaitingMarketOpen.Should().Be(1, "閉場で待つ件数として数える");
            result.Unknown.Should().Be(0, "不明（据え置き）に混ぜない");
            result.CloseFailed.Should().Be(0);
            result.Replaced.Should().Be(0);
            result.ClosedOut.Should().Be(0);
            result.Events.Should().BeEmpty("閉場で待つのは毎日の平常であり、保護喪失の通知を出さない");
        }

        h.Broker.StopPlaceCount.Should().Be(0, "閉場中は逆指値を張り直さない");
        h.Broker.MarketCloseCount.Should().Be(0, "🔴 閉場中は成行で手仕舞わない（翌寄りで損切りラインに達していない建玉を手仕舞う）");
        var row = h.Stops.Find(stop.EntryDecisionId)!;
        row.State.Should().Be(ProtectiveStopState.Active, "次の開場の巡回の対象に残る");
        row.StopOrderId.Should().Be(StopLegScriptedBroker.LapsedStopOrderId);
        row.Attempt.Should().Be(1, "試行を進めない（張り直しは次の開場の巡回で行う）");
    }

    // T-10-2996: 次の開場後の最初の巡回で張り直す（受理されれば Replaced、不可なら場中に成行で手仕舞う）。
    [Theory]
    [InlineData(StopLegScriptedBroker.StopBehavior.Accept, 1, 0)]
    [InlineData(StopLegScriptedBroker.StopBehavior.Reject, 1, 1)]
    public async Task T_10_2996_次の開場後の最初の巡回で張り直し不可なら場中に成行で手仕舞う(
        StopLegScriptedBroker.StopBehavior behavior, int expectedStops, int expectedMarket)
    {
        var h = NewHarness(AtClose.AddHours(3));
        h.Broker.Stop = behavior;
        var stop = LapsedS0(h, AtClose.AddHours(-6));

        (await h.Guard.RunOnceAsync(10)).AwaitingMarketOpen.Should().Be(1);

        // 寄り付きの 1 秒前はまだ閉場。
        h.Clock.UtcNow = NextOpen.AddSeconds(-1);
        (await h.Guard.RunOnceAsync(10)).AwaitingMarketOpen.Should().Be(1);
        h.Broker.StopPlaceCount.Should().Be(0);

        h.Clock.UtcNow = NextOpen;
        var opened = await h.Guard.RunOnceAsync(10);

        opened.AwaitingMarketOpen.Should().Be(0);
        h.Broker.StopPlaceCount.Should().Be(expectedStops, "開場後の最初の巡回で張り直す");
        h.Broker.MarketCloseCount.Should().Be(expectedMarket, "張り直せなければ場中に成行で手仕舞う（従来どおり）");
        if (behavior == StopLegScriptedBroker.StopBehavior.Accept)
        {
            opened.Replaced.Should().Be(1);
            opened.Events.OfType<ProtectiveStopPlaced>().Should().ContainSingle().Which.Attempt.Should().Be(2);
            h.Stops.Find(stop.EntryDecisionId)!.StopOrderId.Should().Be("stop-new-1");
        }
        else
        {
            opened.ClosedOut.Should().Be(1);
        }
    }

    // T-10-2997: 場中は従来どおり（失効を見たその巡回で張り直し、拒否なら成行で手仕舞う）。ゲートの有無で挙動が変わらない。
    [Fact]
    public async Task T_10_2997_場中は従来どおり失効を見た巡回で張り直し拒否なら成行で手仕舞う()
    {
        var inSession = AtClose.AddSeconds(-1); // 15:59:59 EDT
        var gated = NewHarness(inSession);
        var legacy = NewHarness(inSession, AlwaysOpenSessionGate.Instance);
        foreach (var h in new[] { gated, legacy })
        {
            h.Broker.Stop = StopLegScriptedBroker.StopBehavior.Reject;
            LapsedS0(h, inSession.AddHours(-6));
            var result = await h.Guard.RunOnceAsync(10);
            result.AwaitingMarketOpen.Should().Be(0);
            result.ClosedOut.Should().Be(1);
            h.Broker.StopPlaceCount.Should().Be(1);
            h.Broker.MarketCloseCount.Should().Be(1);
        }
    }

    // T-10-2998: 境界（寄り付き・引けの前後 1 秒）・半日取引日・休場日・週末。張り直したかどうかで場中判定を読む。
    [Theory]
    [InlineData("2026-10-23T13:29:59Z", false)] // 09:29:59 EDT 寄り付きの直前
    [InlineData("2026-10-23T13:30:00Z", true)]  // 09:30:00 EDT 寄り付き
    [InlineData("2026-10-23T19:59:59Z", true)]  // 15:59:59 EDT 引けの直前
    [InlineData("2026-10-23T20:00:00Z", false)] // 16:00:00 EDT 引け
    [InlineData("2026-10-24T15:00:00Z", false)] // 土曜 11:00 EDT
    [InlineData("2026-11-26T19:00:00Z", false)] // 感謝祭（休場日）14:00 EST
    [InlineData("2026-11-27T17:59:59Z", true)]  // 感謝祭翌日（半日取引日）12:59:59 EST
    [InlineData("2026-11-27T18:00:00Z", false)] // 同 13:00 EST（半日の引け）
    [InlineData("2026-12-25T15:00:00Z", false)] // クリスマス（休場日）10:00 EST
    public async Task T_10_2998_境界と休場日と半日取引日では場中だけ張り直す(string instant, bool open)
    {
        var now = DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture);
        var h = NewHarness(now);
        LapsedS0(h, now.AddHours(-30));

        var result = await h.Guard.RunOnceAsync(10);

        h.Broker.StopPlaceCount.Should().Be(open ? 1 : 0);
        h.Broker.MarketCloseCount.Should().Be(0);
        result.AwaitingMarketOpen.Should().Be(open ? 0 : 1);
    }

    // T-10-2999: 到達済みの S1 で決済が売れていない行は、閉場中はガードから成行を撃ち直さない。失敗に数えず（待ち時間を進めない）、
    // 到達の記録は残し、次の開場の巡回で撃つ。
    [Fact]
    public async Task T_10_2999_到達済みのS1は閉場中に成行を撃ち直さず次の開場の巡回で撃つ()
    {
        var h = NewHarness(AtClose.AddMinutes(-10)); // 15:50 EDT
        h.Broker.MarketUnavailable = true;           // 場中の 1 本目は確実に未発注で失敗（据え置き）
        var stop = S1(h, AtClose.AddHours(-6));

        await h.Executor.OnTriggeredAsync(Trigger(h.Clock.UtcNow));
        h.Broker.MarketCloseCount.Should().Be(1, "場中の到達ではすぐ撃つ");
        var armed = h.Stops.Find(stop.EntryDecisionId)!;
        armed.TriggeredAt.Should().NotBeNull();
        var failures = armed.CloseFailures;

        h.Broker.MarketUnavailable = false;
        for (var i = 0; i < 4; i++)
        {
            h.Clock.UtcNow = AtClose.AddMinutes(30 * i);
            var closed = await h.Guard.RunOnceAsync(10);
            closed.AwaitingMarketOpen.Should().Be(1);
            closed.Unknown.Should().Be(0);
        }

        h.Broker.MarketCloseCount.Should().Be(1, "🔴 閉場中は S1 の成行を撃ち直さない");
        var waiting = h.Stops.Find(stop.EntryDecisionId)!;
        waiting.State.Should().Be(ProtectiveStopState.Active);
        waiting.TriggeredAt.Should().Be(armed.TriggeredAt, "到達の記録は消さない");
        waiting.CloseFailures.Should().Be(failures, "閉場で待つことを失敗に数えない");

        h.Clock.UtcNow = NextOpen;
        var opened = await h.Guard.RunOnceAsync(10);

        h.Broker.MarketCloseCount.Should().Be(2, "次の開場の巡回で撃つ（一度到達したら価格が戻っても決済する）");
        opened.ClosedOut.Should().Be(1);
        h.Stops.Find(stop.EntryDecisionId)!.State.Should().Be(ProtectiveStopState.Completed);
    }

    // T-10-3000: 引けの直前に検知された到達が閉場後に届いても（配送の遅れ）、成行を送らない。到達は記録し、開場で撃つ。
    [Fact]
    public async Task T_10_3000_引けの直前の到達が閉場後に届いても成行を送らず開場の巡回で撃つ()
    {
        var h = NewHarness(AtClose.AddSeconds(10));
        var stop = S1(h, AtClose.AddHours(-6));

        var run = await h.Executor.OnTriggeredAsync(Trigger(AtClose.AddSeconds(-20)));

        run.Matched.Should().Be(1);
        run.Deferred.Should().Be(1, "閉場で待つ到達は据え置きに数える");
        h.Broker.MarketCloseCount.Should().Be(0, "🔴 閉場後に届いた到達で成行を送らない");
        h.Broker.CancelCount.Should().Be(0, "送らない回に判断の手仕舞いやエントリーを取り消さない");
        h.Stops.Find(stop.EntryDecisionId)!.TriggeredAt.Should().NotBeNull("到達は記録する（開場で撃つ）");

        h.Clock.UtcNow = NextOpen;
        await h.Guard.RunOnceAsync(10);
        h.Broker.MarketCloseCount.Should().Be(1);
    }

    private static int Stalled(ProtectiveStopGuardResult result) =>
        result.Events.OfType<SoftwareStopExecuted>().Count(e => e.Outcome == SoftwareStopOutcome.CloseStalled);

    // T-10-3002: 🔴 引け際に到達した S1 が閉場をまたいで決済を待つだけなら「決済できていない」通知（CloseStalled）を出さない。
    // 閉場の間は猶予の計時を進めず、到達の後の最初の開場から数え直す——開場後も猶予（15 分）を過ぎて決済できなければ 1 回だけ出す。
    [Fact]
    public async Task T_10_3002_閉場をまたいで待つだけのS1は決済できていない通知を出さず開場から猶予を数え直す()
    {
        var h = NewHarness(AtClose.AddMinutes(-5)); // 15:55 EDT
        h.Broker.MarketUnavailable = true;          // 場中も開場後も確実に未発注（据え置き）が続く
        var stop = S1(h, AtClose.AddHours(-6));
        await h.Executor.OnTriggeredAsync(Trigger(h.Clock.UtcNow));

        var stalled = 0;
        // 閉場中（引け〜翌寄り）に何度巡回しても出さない。
        foreach (var at in new[] { AtClose.AddMinutes(1), AtClose.AddMinutes(30), AtClose.AddHours(6), NextOpen.AddMinutes(-1) })
        {
            h.Clock.UtcNow = at;
            stalled += Stalled(await h.Guard.RunOnceAsync(10));
        }

        stalled.Should().Be(0, "閉場で待つことは「決済できていない」ではない（到達から 17 時間以上たっても出さない）");

        // 開場の後: 猶予（15 分）の内は出さない。
        h.Clock.UtcNow = NextOpen.AddMinutes(14);
        Stalled(await h.Guard.RunOnceAsync(10)).Should().Be(0, "猶予は到達の後の最初の開場から数え直す");

        // 開場から猶予を過ぎて、なお決済できない: 1 回だけ出す。
        h.Clock.UtcNow = NextOpen.AddMinutes(16);
        Stalled(await h.Guard.RunOnceAsync(10)).Should().Be(1);
        h.Clock.UtcNow = NextOpen.AddMinutes(30);
        Stalled(await h.Guard.RunOnceAsync(10)).Should().Be(0, "通知は 1 回だけ（従来どおり）");
        h.Stops.Find(stop.EntryDecisionId)!.StalledNotifiedAt.Should().Be(NextOpen.AddMinutes(16));
    }

    // T-10-3003: 場中に到達して決済できないまま猶予を過ぎたら、従来どおり「決済できていない」通知を 1 回出す。
    [Fact]
    public async Task T_10_3003_場中に到達して猶予を過ぎても決済できなければ従来どおり通知する()
    {
        var triggeredAt = new DateTimeOffset(2026, 10, 22, 14, 0, 0, TimeSpan.Zero); // 10:00 EDT
        var h = NewHarness(triggeredAt);
        h.Broker.MarketUnavailable = true;
        S1(h, triggeredAt.AddHours(-1));
        await h.Executor.OnTriggeredAsync(Trigger(triggeredAt));

        h.Clock.UtcNow = triggeredAt.AddMinutes(14);
        Stalled(await h.Guard.RunOnceAsync(10)).Should().Be(0);
        h.Clock.UtcNow = triggeredAt.AddMinutes(16);
        Stalled(await h.Guard.RunOnceAsync(10)).Should().Be(1, "同じ場の中で猶予を過ぎた（到達の時刻から数える）");
    }
}
