extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using TradeDecisionService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;

namespace TradeDecisionService.Tests;

// FR-10, FR-04, FR-11, ADR-0003, ADR-0049 決定1〜5, #1120, IADR-0465: 新規建ての損切り幅に下限を掛ける。
// 下限 ＝ 1.0 × ATR(14)。ATR が得られない間（今は常に）は参照価格（アンカー後）の 2%。下限を割った幅は下限まで広げ、見送らない。
// 広げた幅でサイジング・ライン・発注意図・監査・観測ログを計算する。1 注文上限（25%）は緩めない。実 LLM は呼ばない。
public class StopWidthFloorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 20, 0, 0, TimeSpan.Zero);

    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 9, 30), "META は押し目で新規買いを検討する。");

    // equity 100,000・段階残枠 50,000・当日残枠 20,000 → 1 取引リスク 1,000・1 注文上限 25,000・残枠 20,000。
    private static readonly SizingContext Context =
        new(100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());

    private static string Buy(decimal referencePrice, decimal width) =>
        $$"""{"action":"Buy","rationale":"押し目","referencePrice":{{referencePrice}},"stopLossDistancePerShare":{{width}}}""";

    private static string Sell(decimal referencePrice, decimal width) =>
        $$"""{"action":"Sell","rationale":"戻り売り","referencePrice":{{referencePrice}},"stopLossDistancePerShare":{{width}}}""";

    private static DecisionTrigger ScheduledMeta() => DecisionTrigger.Scheduled("META", Market.UnitedStates, Now);

    private static AppSvc Service(
        string llmOutput,
        ICurrentPriceProvider? currentPrice = null,
        IStopWidthFloorSource? floor = null,
        IHeldPositionProvider? held = null,
        SizingContext? context = null,
        StateLogger? logger = null) =>
        new(new FixedLlm(llmOutput), new FakePolicy(), new FakeSizing(context ?? Context), new FakeClock(),
            logger ?? new StateLogger(),
            currentPrice: currentPrice, heldPosition: held, stopWidthFloor: floor);

    // ================================================================================================
    // 純関数
    // ================================================================================================

    // T-10-1797, FR-10, ADR-0049 決定3, IADR-0465 決定1: 適用する幅 ＝ max(AI の幅, 下限)。広げたのは AI の幅が下限を**割った**ときだけ。
    [Theory]
    [InlineData(0.5, 2, 2, true)]    // 割った → 下限まで広げる
    [InlineData(1.99, 2, 2, true)]
    [InlineData(3, 2, 3, false)]     // 下限以上 → そのまま
    [InlineData(2, 2, 2, false)]     // ちょうど下限 → そのまま（広げたとは書かない）
    public void 下限を割った幅だけを下限まで広げる(double ai, double floor, double applied, bool widened)
    {
        var result = StopWidthFloorPolicy.Apply(
            (decimal)ai, new StopWidthFloor((decimal)floor, StopWidthFloorSource.Fallback2Pct));

        result.AiWidthPerShare.Should().Be((decimal)ai);
        result.FloorPerShare.Should().Be((decimal)floor);
        result.FloorSource.Should().Be(StopWidthFloorSource.Fallback2Pct);
        result.AppliedWidthPerShare.Should().Be((decimal)applied);
        result.Widened.Should().Be(widened);
    }

    // T-10-1798, FR-10, ADR-0049 決定2, §5「損切り幅の下限」: 退避の下限 ＝ 参照価格 × 2%（端数は丸めない）。出所は Fallback2Pct。
    [Theory]
    [InlineData(100, 2)]
    [InlineData(724.85, 14.497)]  // PoC の META（アンカー 724.85）→ 下限 14.497（AI の幅 4.50 は約 0.6%）
    [InlineData(0.5, 0.01)]
    public void 退避の下限は参照価格の2パーセント(double price, double expected)
    {
        var floor = StopWidthFloorPolicy.Fallback((decimal)price);

        floor.PerShare.Should().Be((decimal)expected);
        floor.Source.Should().Be(StopWidthFloorSource.Fallback2Pct);
    }

    // T-10-1803: 供給口の答えを検める純関数。正の値で出所が指定されていれば採り、それ以外は 2% へ退避する。
    [Fact]
    public void 供給口の答えは正の値で出所があるときだけ採る_否定形()
    {
        StopWidthFloorPolicy.Resolve(new StopWidthFloor(5m, StopWidthFloorSource.Atr14), 100m)
            .Should().Be(new StopWidthFloor(5m, StopWidthFloorSource.Atr14));

        var fallback = new StopWidthFloor(2m, StopWidthFloorSource.Fallback2Pct);
        StopWidthFloorPolicy.Resolve(null, 100m).Should().Be(fallback);
        StopWidthFloorPolicy.Resolve(new StopWidthFloor(0m, StopWidthFloorSource.Atr14), 100m).Should().Be(fallback);
        StopWidthFloorPolicy.Resolve(new StopWidthFloor(-1m, StopWidthFloorSource.Atr14), 100m).Should().Be(fallback);
        StopWidthFloorPolicy.Resolve(new StopWidthFloor(5m, StopWidthFloorSource.Unspecified), 100m).Should().Be(fallback);
    }

    // ================================================================================================
    // 判断サービス
    // ================================================================================================

    // T-10-1800, FR-10, ADR-0049 決定3, IADR-0465 決定1・2: 幅 0.5 ＜ 下限 2（100 の 2%）→ 下限まで広げる。見送らない。
    // ライン・発注意図・監査の値はすべて広げた幅から。株数は 1 注文上限／残枠が効く（幅 4% 以下）ので 200 株。
    [Fact]
    public async Task 下限を割った幅は下限まで広げてラインと監査に使う()
    {
        var decision = await Service(Buy(100m, 0.5m)).DecideAsync(ScheduledMeta());

        decision.Should().NotBeNull("狭い幅は見送る理由にしない（ADR-0049 決定3）");
        decision!.Intent.PositionEffect.Should().Be(PositionEffect.Open);
        decision.Intent.StopLossPrice.Should().Be(98m, "100 − 2（AI の 0.5 ではなく下限の 2）");
        decision.Intent.Quantity.Should().Be(200, "残枠 20,000 ÷ 100。幅 2%（4% 以下）では 1 取引リスク側は効かない");
        decision.StopWidth.Should().Be(
            new StopWidthFloorApplication(0.5m, 2m, StopWidthFloorSource.Fallback2Pct, 2m, Widened: true));
    }

    // T-10-1800: 🔴 サイジングも広げた幅で行う。下限が 4% を超える（ATR 8）と 1 取引リスク側が効き、株数 ＝ 1,000 ÷ 8 ＝ 125。
    // AI の幅 0.5 のままなら 2,000 株 → 上限・残枠で 200 株になる。
    [Fact]
    public async Task サイジングは広げた幅で行う()
    {
        var decision = await Service(Buy(100m, 0.5m), floor: new FixedFloor(new StopWidthFloor(8m, StopWidthFloorSource.Atr14)))
            .DecideAsync(ScheduledMeta());

        decision.Should().NotBeNull();
        decision!.Intent.Quantity.Should().Be(125, "floor(100,000 × 1% ÷ 8)");
        decision.Intent.StopLossPrice.Should().Be(92m);
        decision.StopWidth.Should().Be(
            new StopWidthFloorApplication(0.5m, 8m, StopWidthFloorSource.Atr14, 8m, Widened: true));
    }

    // T-10-1800: 🔴 退避の 2% でも、縮小係数（5 連敗 × DD 5% 以上＝0.25）で 1 取引リスク側が効くときは広げた幅が株数を減らす。
    // 予算 100,000 × 1% × 0.25 ＝ 250 → 250 ÷ 2 ＝ 125 株（AI の幅 0.5 のままなら 500 → 残枠で 200 株）。
    [Fact]
    public async Task 縮小係数が掛かると広げた幅が株数を減らす()
    {
        var shrunk = Context with { ConsecutiveLosses = 5, DrawdownRatio = 0.06m };

        var decision = await Service(Buy(100m, 0.5m), context: shrunk).DecideAsync(ScheduledMeta());

        decision.Should().NotBeNull();
        decision!.Intent.Quantity.Should().Be(125);
        decision.Intent.StopLossPrice.Should().Be(98m);
    }

    // T-10-1801, IADR-0465 決定1: 🔴 否定形。下限以上の幅はそのまま（広げない）。ちょうど下限も広げない。
    [Theory]
    [InlineData(3, 97)]
    [InlineData(2, 98)]
    public async Task 下限以上の幅はそのまま使う_否定形(double width, double expectedLine)
    {
        var decision = await Service(Buy(100m, (decimal)width)).DecideAsync(ScheduledMeta());

        decision.Should().NotBeNull();
        decision!.Intent.StopLossPrice.Should().Be((decimal)expectedLine);
        decision.StopWidth.Should().Be(new StopWidthFloorApplication(
            (decimal)width, 2m, StopWidthFloorSource.Fallback2Pct, (decimal)width, Widened: false));
    }

    // T-10-1799, ADR-0049 決定2, IADR-0465 決定1（窓の表）: 下限はアンカー後の価格で求める。
    // 増える側: LLM 100 → 現在値 110。下限 2.2（110 の 2%）→ ライン 107.8（LLM の価格で求めると 2.0 → 108 で下限を割る）。
    // 減る側: LLM 110 → 現在値 100。下限 2.0 → ライン 98（LLM の価格で求めると 2.2 → 97.8 で定義より広い）。
    [Theory]
    [InlineData(100, 110, 2.2, 107.8)]
    [InlineData(110, 100, 2.0, 98)]
    public async Task 下限はアンカー後の価格で求める(double llmPrice, double current, double floor, double line)
    {
        var decision = await Service(
                Buy((decimal)llmPrice, 1m), currentPrice: new FakeCurrentPrice((decimal)current))
            .DecideAsync(ScheduledMeta());

        decision.Should().NotBeNull();
        decision!.Intent.Price.Should().Be((decimal)current);
        decision.Intent.StopLossPrice.Should().Be((decimal)line);
        decision.StopWidth!.FloorPerShare.Should().Be((decimal)floor);
        decision.StopWidth.AppliedWidthPerShare.Should().Be((decimal)floor);
    }

    // T-10-1802, ADR-0049 決定1「方向を問わない」: 空売りの建て増し（保有 −10 株・売り）も対称。ライン ＝ 価格 ＋ 下限。
    [Fact]
    public async Task 空売りも対称に下限を掛ける()
    {
        var decision = await Service(Sell(100m, 0.5m), held: new Held(-10)).DecideAsync(ScheduledMeta());

        decision.Should().NotBeNull();
        decision!.Intent.Side.Should().Be(TradeSide.Sell);
        decision.Intent.PositionEffect.Should().Be(PositionEffect.Open);
        decision.Intent.StopLossPrice.Should().Be(102m, "100 ＋ 2（AI の 0.5 ではなく下限の 2）");
        decision.StopWidth.Should().Be(
            new StopWidthFloorApplication(0.5m, 2m, StopWidthFloorSource.Fallback2Pct, 2m, Widened: true));
    }

    // T-10-1803, ADR-0049 決定2, IADR-0465 決定1: 🔴 否定形。供給口が得られない（null・0・負・出所未指定）・例外なら 2% へ退避し、
    // 見送らない（下限が得られないことは見送る理由にならない）。出所は Fallback2Pct と書く（ATR と取り違えない）。
    [Theory]
    [MemberData(nameof(UnavailableFloors))]
    public async Task 下限が得られなければ2パーセントへ退避する_否定形(IStopWidthFloorSource floor)
    {
        var decision = await Service(Buy(100m, 0.5m), floor: floor).DecideAsync(ScheduledMeta());

        decision.Should().NotBeNull();
        decision!.StopWidth.Should().Be(
            new StopWidthFloorApplication(0.5m, 2m, StopWidthFloorSource.Fallback2Pct, 2m, Widened: true));
        decision.Intent.StopLossPrice.Should().Be(98m);
    }

    public static TheoryData<IStopWidthFloorSource> UnavailableFloors() => new()
    {
        new FixedFloor(null),
        new FixedFloor(new StopWidthFloor(0m, StopWidthFloorSource.Atr14)),
        new FixedFloor(new StopWidthFloor(-1m, StopWidthFloorSource.Atr14)),
        new FixedFloor(new StopWidthFloor(5m, StopWidthFloorSource.Unspecified)),
        new ThrowingFloor(new InvalidOperationException("日足の取得に失敗")),
    };

    // T-10-1803, ADR-0049 決定5: 本番の組み立てが結線する本物（NoAtr）は ATR を返さず、判断は 2% を下限とする（配備までの暫定手段）。
    [Fact]
    public async Task 本番の既定の供給口はATRを返さず2パーセントが効く()
    {
        var real = new NoAtrStopWidthFloorSource();

        real.IsEnabled.Should().BeFalse();
        (await real.GetFloorAsync("META", Market.UnitedStates)).Should().BeNull();
        (await real.GetFloorAsOfAsync("META", Market.UnitedStates, new DateOnly(2026, 9, 30))).Should().BeNull();

        var decision = await Service(Buy(724.85m, 4.5m), floor: real).DecideAsync(ScheduledMeta());

        decision!.StopWidth.Should().Be(
            new StopWidthFloorApplication(4.5m, 14.497m, StopWidthFloorSource.Fallback2Pct, 14.497m, Widened: true));
        decision.Intent.StopLossPrice.Should().Be(710.353m, "PoC の META（幅 4.50 ≈ 0.6%）は 724.85 − 14.497 まで広がる");
    }

    // T-10-1803: 供給口が得た値（ATR）はその出所で記録する。
    // ［2026-10-03 改 / #1122, IADR-0486 決定2］ATR は価格に依らないため、供給口へ価格を渡さない（判断ごとにプロンプトの前に 1 回だけ読む）。
    // 2% の退避はアンカー後の価格で求める（T-10-1799）。
    [Fact]
    public async Task 供給口の下限は出所つきで使う()
    {
        var floor = new FixedFloor(new StopWidthFloor(3m, StopWidthFloorSource.Atr14));

        var decision = await Service(Buy(100m, 0.5m), currentPrice: new FakeCurrentPrice(105m), floor: floor)
            .DecideAsync(ScheduledMeta());

        floor.Calls.Should().ContainSingle().Which.Should().Be(("META", Market.UnitedStates));
        decision!.StopWidth.Should().Be(new StopWidthFloorApplication(0.5m, 3m, StopWidthFloorSource.Atr14, 3m, Widened: true));
        decision.Intent.StopLossPrice.Should().Be(102m);
    }

    // T-10-1803: 本判断のキャンセルは伝える（退避へ倒して発注意図を作らない）。
    [Fact]
    public async Task 下限の供給口のキャンセルは伝える()
    {
        var act = () => Service(Buy(100m, 0.5m), floor: new ThrowingFloor(new OperationCanceledException()))
            .DecideAsync(ScheduledMeta());

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // T-10-1804, IADR-0465 決定1: 🔴 否定形（極端）。下限で広げた幅が参照価格以上ならラインが成立しない（ロングは 0 以下）ので見送る。
    // 幅を価格未満へ縮めない（下限を割る）。2% の退避では起こらない。
    [Theory]
    [InlineData(100)]
    [InlineData(150)]
    public async Task 広げた幅が参照価格以上なら見送る_否定形(double floor)
    {
        var logger = new StateLogger();
        var decision = await Service(
                Buy(100m, 0.5m), floor: new FixedFloor(new StopWidthFloor((decimal)floor, StopWidthFloorSource.Atr14)),
                logger: logger)
            .DecideAsync(ScheduledMeta());

        decision.Should().BeNull();
        logger.Entries.Should().Contain(e => e.Message.StartsWith("損切り幅の下限が現在値以上のため見送り", StringComparison.Ordinal));
    }

    // T-10-1804: AI の幅そのものが参照価格以上（壊れた出力）は、下限を掛ける前に従来どおり見送る。
    // ［2026-10-03 改 / #1122, IADR-0486 決定2］下限の供給口はプロンプトの前に 1 回だけ読む（ATR と下限をプロンプトへ出すため）。読み直さない。
    [Fact]
    public async Task AIの幅が参照価格以上なら下限を掛ける前に見送る_否定形()
    {
        var floor = new FixedFloor(new StopWidthFloor(3m, StopWidthFloorSource.Atr14));

        var decision = await Service(Buy(300m, 150m), currentPrice: new FakeCurrentPrice(102m), floor: floor)
            .DecideAsync(ScheduledMeta());

        decision.Should().BeNull();
        floor.Calls.Should().ContainSingle("下限はプロンプトの前に 1 回だけ読み、見送りで読み直さない");
    }

    // T-10-1805, ADR-0049 決定3・決定4: 🔴 1 注文上限（equity の 25%）は緩めない。equity 3,000・価格 100 → 上限 750 → 7 株。
    // 幅 4% 以下（AI の 0.5 → 下限 2・幅 2・幅 4）は上限が効いて株数は変わらない（7 株）。幅 5（> 4%）は 1 取引リスク側で 6 株。
    [Theory]
    [InlineData(0.5, 7)]
    [InlineData(2, 7)]
    [InlineData(4, 7)]
    [InlineData(5, 6)]
    public async Task 一注文上限は緩めず幅4パーセント以下では株数が変わらない(double width, int expected)
    {
        var small = new SizingContext(3_000m, 3_000m, 4_500m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());

        var decision = await Service(Buy(100m, (decimal)width), context: small).DecideAsync(ScheduledMeta());

        decision.Should().NotBeNull();
        decision!.Intent.Quantity.Should().Be(expected);
        decision.Intent.Notional.Should().BeLessThanOrEqualTo(750m, "1 注文上限 ＝ equity 3,000 × 25%");
    }

    // T-10-1805: 純関数でも確かめる（PositionSizer は下限を知らない。幅 ≦ 4% では 25% 側の株数）。
    [Theory]
    [InlineData(2, 7)]
    [InlineData(4, 7)]
    [InlineData(5, 6)]
    public void 幅4パーセント以下では1注文上限が株数を決める(double width, int expected)
    {
        PositionSizer.CalculateCappedQuantity(3_000m, 0.01m, (decimal)width, 100m, 750m, 3_000m)
            .Should().Be(expected);
    }

    // T-10-1807, IADR-0460 決定3, IADR-0465 決定3: 観測ログに下限・出所・適用した幅・広げたかを出す。stopWidth・比率は AI の幅のまま。
    [Fact]
    public async Task 観測ログに下限と適用した幅を出す()
    {
        var logger = new StateLogger();

        var decision = await Service(Buy(100m, 0.5m), logger: logger).DecideAsync(ScheduledMeta());

        var entry = logger.Entries.Should().ContainSingle(e => e.Message.StartsWith("損切り幅の観測", StringComparison.Ordinal)).Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Values["StopWidthPerShare"].Should().Be(0.5m, "AI の幅（AI の提案の傾向を測る）");
        entry.Values["StopWidthPercent"].Should().Be(0.5m, "AI の幅 ÷ 100 × 100");
        entry.Values["StopWidthFloor"].Should().Be(2m);
        entry.Values["FloorSource"].Should().Be(StopWidthFloorSource.Fallback2Pct);
        entry.Values["AppliedStopWidth"].Should().Be(2m);
        entry.Values["Widened"].Should().Be(true);
        entry.Values["StopLossPrice"].Should().Be(decision!.Intent.StopLossPrice).And.Be(98m);
        entry.Message.Should().Contain("floorSource=Fallback2Pct").And.Contain("widened=True");
    }

    // T-10-1807: 🔴 否定形。広げない判断では widened=False・適用した幅＝AI の幅。
    [Fact]
    public async Task 広げない判断は観測ログでも広げないと書く_否定形()
    {
        var logger = new StateLogger();

        await Service(Buy(100m, 3m), logger: logger).DecideAsync(ScheduledMeta());

        var entry = logger.Entries.Should().ContainSingle(e => e.Message.StartsWith("損切り幅の観測", StringComparison.Ordinal)).Subject;
        entry.Values["AppliedStopWidth"].Should().Be(3m);
        entry.Values["Widened"].Should().Be(false);
    }

    // T-10-1806: 🔴 否定形。決済（保有 10 株の売り）は損切りラインを作らないので、監査の幅（StopWidth）を持たない（null）。
    // ［2026-10-03 改 / #1122, IADR-0486 決定2・決定5］供給口はプロンプトの前に 1 回だけ読む。決済の発注意図は下限の印を持たない（null）。
    [Fact]
    public async Task 決済の判断は下限の結果を持たない_否定形()
    {
        var floor = new FixedFloor(new StopWidthFloor(3m, StopWidthFloorSource.Atr14));

        var decision = await Service(Sell(100m, 0.5m), held: new Held(10), floor: floor).DecideAsync(ScheduledMeta());

        decision.Should().NotBeNull();
        decision!.Intent.PositionEffect.Should().Be(PositionEffect.Close);
        decision.StopWidth.Should().BeNull();
        decision.Intent.StopFloorSource.Should().BeNull();
        floor.Calls.Should().ContainSingle();
    }

    // ------------------------------------------------------------------------------------------------

    private sealed class FixedFloor(StopWidthFloor? floor) : IStopWidthFloorSource
    {
        public List<(string Symbol, Market Market)> Calls { get; } = [];

        public bool IsEnabled => true;

        public ValueTask<StopWidthFloor?> GetFloorAsync(
            string symbol, Market market, CancellationToken cancellationToken = default)
        {
            Calls.Add((symbol, market));
            return ValueTask.FromResult(floor);
        }

        public ValueTask<StopWidthFloor?> GetFloorAsOfAsync(
            string symbol, Market market, DateOnly tradingDay, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(floor);

        public override string ToString() => $"FixedFloor({floor?.ToString() ?? "null"})";
    }

    private sealed class ThrowingFloor(Exception exception) : IStopWidthFloorSource
    {
        public bool IsEnabled => true;

        public ValueTask<StopWidthFloor?> GetFloorAsync(
            string symbol, Market market, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<StopWidthFloor?>(exception);

        public ValueTask<StopWidthFloor?> GetFloorAsOfAsync(
            string symbol, Market market, DateOnly tradingDay, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<StopWidthFloor?>(exception);

        public override string ToString() => $"ThrowingFloor({exception.GetType().Name})";
    }

    private sealed class Held(int signedQuantity) : IHeldPositionProvider
    {
        public bool IsEnabled => true;

        public Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(signedQuantity);

        public Task<HeldPosition?> GetPositionAsync(string symbol, Market market, CancellationToken cancellationToken = default) =>
            Task.FromResult<HeldPosition?>(new HeldPosition(signedQuantity, 100m, null));

        public Task<WorkingEntryOrders?> GetWorkingEntryOrdersAsync(
            string symbol, Market market, CancellationToken cancellationToken = default) =>
            Task.FromResult<WorkingEntryOrders?>(WorkingEntryOrders.None);
    }

    private sealed record LogEntry(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Values);

    private sealed class StateLogger : ILogger<AppSvc>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(p => p.Key, p => p.Value)
                : new Dictionary<string, object?>();
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), values));
        }
    }

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class FixedLlm(string output) : ILlmCompletionClient
    {
        public Task<string> CompleteAsync(string prompt, string? model = null, string? purpose = null, CancellationToken ct = default) =>
            Task.FromResult(output);
    }

    private sealed class FakePolicy : IDailyPolicyProvider
    {
        public Task<DailyPolicy?> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult<DailyPolicy?>(Policy);
    }

    private sealed class FakeSizing(SizingContext context) : ISizingContextProvider
    {
        public Task<SizingContext> GetContextAsync(CancellationToken ct = default) => Task.FromResult(context);
    }

    private sealed class FakeCurrentPrice(decimal price) : ICurrentPriceProvider
    {
        public bool IsEnabled => true;

        public Task<CurrentPriceReading?> GetCurrentPriceAsync(DecisionTrigger trigger, CancellationToken ct = default) =>
            Task.FromResult<CurrentPriceReading?>(new CurrentPriceReading(price, IntradayPriceContext.Unknown));
    }
}
