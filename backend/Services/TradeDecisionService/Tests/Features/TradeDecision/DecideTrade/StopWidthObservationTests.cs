extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;

namespace TradeDecisionService.Tests;

// FR-10, FR-04, FR-02, #1104, IADR-0460: 新規建ての損切り幅の観測（ログ）。本試験は観測値の計算とログの有無を固定する。
// #1120, IADR-0465: 幅には下限（ATR が得られない間は参照価格の 2%）が掛かる。下限そのものの試験は StopWidthFloorTests。
// 観測の stopWidth・比率・倍率は AI の幅のまま、ラインは下限を掛けた幅から引く。実 LLM は呼ばない。
public class StopWidthObservationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 14, 0, 0, TimeSpan.Zero);

    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 9, 30), "AAPL は押し目で新規買いを検討する。");

    private static readonly SizingContext Context =
        new(100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());

    // 現在値 102・高値 104・安値 99.5 → 日中の値幅 4.5。
    private static readonly IntradayPriceContext Known = new(100m, 103m, 104m, 99.5m);

    private const string BuyJson =
        """{"action":"Buy","rationale":"押し目","referencePrice":100,"stopLossDistancePerShare":2}""";

    private const string ObservationPrefix = "損切り幅の観測";

    // #1120: 観測の計算は下限の結果も受け取る。ここでは退避の 2%（アンカー後の価格）を掛けた結果を渡す（本番の経路と同じ）。
    private static StopWidthObservation Observe(
        decimal llmReference, decimal anchored, decimal width, IntradayPriceContext? intraday) =>
        StopWidthObservation.Of(
            llmReference, anchored, width, intraday,
            StopWidthFloorPolicy.Apply(width, StopWidthFloorPolicy.Fallback(anchored)));

    private static DecisionTrigger ScheduledAapl() => DecisionTrigger.Scheduled("AAPL", Market.UnitedStates, Now);

    // ================================================================================================
    // 計算
    // ================================================================================================

    // T-10-1747, FR-10, #1104, IADR-0460 決定3: 差＝アンカリング済み − LLM、比率＝幅 ÷ アンカリング済み × 100、倍率＝幅 ÷ 値幅。
    [Fact]
    public void 観測値は差と比率と日中の値幅に対する倍率を計算する()
    {
        var observed = Observe(100m, 102m, 2m, Known);

        observed.LlmReferencePrice.Should().Be(100m);
        observed.AnchoredPrice.Should().Be(102m);
        observed.AnchorDifference.Should().Be(2m);
        observed.WidthPerShare.Should().Be(2m);
        observed.WidthPercentOfAnchored.Should().Be(1.9608m, "2 ÷ 102 × 100 = 1.96078…（LLM の参照価格 100 で割ると 2.0000）");
        observed.IntradayRange.Should().Be(4.5m);
        observed.WidthToIntradayRange.Should().Be(0.4444m, "2 ÷ 4.5 = 0.4444…");
    }

    // T-10-1747: 差は符号つき（LLM の参照価格が現在値より高いときは負）。比率は小数 4 桁の四捨五入。
    [Theory]
    [InlineData(110, 100, 3, -10, 3)]
    [InlineData(100, 300, 1, 200, 0.3333)]
    [InlineData(100, 30, 1, -70, 3.3333)]
    // 丸めは四捨五入（中間は 0 から遠い側）: 1 ÷ 128 × 100 = 0.78125 → 0.7813（偶数丸めなら 0.7812）。
    [InlineData(128, 128, 1, 0, 0.7813)]
    public void 差は符号つきで比率は小数4桁に丸める(
        decimal llmReference, decimal anchored, decimal width, decimal expectedDiff, decimal expectedPercent)
    {
        var observed = Observe(llmReference, anchored, width, intraday: null);

        observed.AnchorDifference.Should().Be(expectedDiff);
        observed.WidthPercentOfAnchored.Should().Be(expectedPercent);
    }

    // T-10-1748, #1104, IADR-0460 決定3: 🔴 否定形。日中の値幅が分からないときは値幅も倍率も不明（null）。0 にしない・0 除算しない。
    [Theory]
    [MemberData(nameof(UnknownRanges))]
    public void 日中の値幅が分からなければ値幅も倍率も不明_否定形(IntradayPriceContext intraday)
    {
        AssertRangeUnknown(Observe(100m, 102m, 2m, intraday));
    }

    // T-10-1748: 現在値の供給なし（日中文脈そのものが無い）。
    [Fact]
    public void 日中文脈が無ければ値幅も倍率も不明_否定形()
    {
        AssertRangeUnknown(Observe(100m, 102m, 2m, intraday: null));
    }

    private static void AssertRangeUnknown(StopWidthObservation observed)
    {

        observed.IntradayRange.Should().BeNull();
        observed.WidthToIntradayRange.Should().BeNull();
        observed.WidthPercentOfAnchored.Should().Be(1.9608m, "比率は日中文脈に依らない");
    }

    public static TheoryData<IntradayPriceContext> UnknownRanges() => new()
    {
        IntradayPriceContext.Unknown,            // すべて不明
        new IntradayPriceContext(100m, 103m, null, 99.5m),  // 高値不明
        new IntradayPriceContext(100m, 103m, 104m, null),   // 安値不明
        new IntradayPriceContext(100m, 103m, 101m, 101m),   // 高値＝安値（値幅 0）
        new IntradayPriceContext(100m, 103m, 99m, 101m),    // 高値＜安値（不整合）
    };

    [Fact]
    public void アンカリング済みの価格が0以下なら例外()
    {
        var act = () => StopWidthObservation.Of(
            100m, 0m, 2m, Known, new StopWidthFloorApplication(2m, 2m, StopWidthFloorSource.Fallback2Pct, 2m, false));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ================================================================================================
    // 判断サービスのログ
    // ================================================================================================

    // T-10-1749, FR-10, #1104, IADR-0460 決定1/2/3: 新規建てで観測ログが Information で 1 件出て、構造化値が計算と一致し、
    // 損切り価格と株数は発注意図と一致する（ログは判断を変えない）。
    [Fact]
    public async Task 新規建てで損切り幅の観測ログを構造化値つきで出す()
    {
        var logger = new StateLogger();
        var service = new AppSvc(
            new FixedLlm(BuyJson), new FakePolicy(), new FakeSizing(), new FakeClock(), logger,
            currentPrice: new FakeCurrentPrice(new CurrentPriceReading(102m, Known)));

        var decision = await service.DecideAsync(ScheduledAapl());

        decision.Should().NotBeNull();
        // #1120, IADR-0465: AI の幅 2 は下限 2.04（アンカー後 102 の 2%）を割るため、下限まで広げてから引く。
        decision!.Intent.StopLossPrice.Should().Be(99.96m, "102 − 2.04（アンカリング済みの価格から、下限を掛けた幅を引く）");

        var entry = logger.Entries.Should().ContainSingle(e => e.Message.StartsWith(ObservationPrefix, StringComparison.Ordinal))
            .Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Values["Symbol"].Should().Be("AAPL");
        entry.Values["Side"].Should().Be(TradeSide.Buy);
        entry.Values["Quantity"].Should().Be(decision.Intent.Quantity);
        entry.Values["LlmReferencePrice"].Should().Be(100m);
        entry.Values["AnchoredPrice"].Should().Be(102m);
        entry.Values["AnchorDifference"].Should().Be(2m);
        entry.Values["StopWidthPerShare"].Should().Be(2m);
        entry.Values["StopWidthPercent"].Should().Be(1.9608m);
        entry.Values["IntradayRange"].Should().Be(4.5m);
        entry.Values["StopWidthToIntradayRange"].Should().Be(0.4444m);
        entry.Values["StopLossPrice"].Should().Be(decision.Intent.StopLossPrice);
    }

    // T-10-1750, #1104, IADR-0460 決定3: 🔴 否定形。現在値の供給なし（既定 NoOp）では、アンカリング済み＝LLM の参照価格・差 0、
    // 日中の値幅と倍率は「不明」と書く（0 と書かない）。
    [Fact]
    public async Task 現在値の供給が無ければ差は0で値幅と倍率は不明と書く_否定形()
    {
        var logger = new StateLogger();
        var service = new AppSvc(new FixedLlm(BuyJson), new FakePolicy(), new FakeSizing(), new FakeClock(), logger);

        var decision = await service.DecideAsync(ScheduledAapl());

        decision.Should().NotBeNull();
        var entry = logger.Entries.Should().ContainSingle(e => e.Message.StartsWith(ObservationPrefix, StringComparison.Ordinal))
            .Subject;
        entry.Values["AnchoredPrice"].Should().Be(100m);
        entry.Values["AnchorDifference"].Should().Be(0m);
        entry.Values["StopWidthPercent"].Should().Be(2m);
        entry.Values["IntradayRange"].Should().Be(AppSvc.Unknown);
        entry.Values["StopWidthToIntradayRange"].Should().Be(AppSvc.Unknown);
        entry.Message.Should().Contain("intradayRange=不明").And.Contain("stopWidthToRange=不明");
    }

    // T-10-1751, #1104, IADR-0460 決定2: 🔴 否定形。Hold・損切り幅の不正で見送る判断では観測ログを出さない（損切りラインを作らない）。
    [Theory]
    [InlineData("""{"action":"Hold","rationale":"様子見","referencePrice":null,"stopLossDistancePerShare":null}""")]
    // LLM の参照価格 300 に対しては妥当な幅 150 だが、現在値 102 に対しては幅 ≧ 価格で見送る（再検証）。
    [InlineData("""{"action":"Buy","rationale":"押し目","referencePrice":300,"stopLossDistancePerShare":150}""")]
    public async Task 見送る判断では観測ログを出さない_否定形(string llmOutput)
    {
        var logger = new StateLogger();
        var service = new AppSvc(
            new FixedLlm(llmOutput), new FakePolicy(), new FakeSizing(), new FakeClock(), logger,
            currentPrice: new FakeCurrentPrice(new CurrentPriceReading(102m, Known)));

        var decision = await service.DecideAsync(ScheduledAapl());

        decision.Should().BeNull();
        logger.Entries.Should().NotContain(e => e.Message.StartsWith(ObservationPrefix, StringComparison.Ordinal));
    }

    // T-10-1751, #1104, IADR-0460 決定2: 🔴 否定形。損切り幅が妥当でもサイジングで数量 0 になって見送る判断では観測ログを出さない
    // （ログは数量 0 判定・採算ゲートの後、発注意図を作る直前に置く）。
    [Fact]
    public async Task サイジングで数量0の見送りでは観測ログを出さない_否定形()
    {
        var logger = new StateLogger();
        var service = new AppSvc(
            new FixedLlm(BuyJson), new FakePolicy(), new FakeSizing(Context with { StageCapitalRemaining = 0m }),
            new FakeClock(), logger,
            currentPrice: new FakeCurrentPrice(new CurrentPriceReading(102m, Known)));

        var decision = await service.DecideAsync(ScheduledAapl());

        decision.Should().BeNull();
        logger.Entries.Should().Contain(e => e.Message.StartsWith("サイジングで数量 0 のため見送り", StringComparison.Ordinal));
        logger.Entries.Should().NotContain(e => e.Message.StartsWith(ObservationPrefix, StringComparison.Ordinal));
    }

    // T-10-1751, #1104, IADR-0460 決定2: 🔴 否定形。保有の決済（Close）は損切りラインを作らないので、LLM が幅を返しても観測ログを出さない。
    [Fact]
    public async Task 決済の判断では観測ログを出さない_否定形()
    {
        var logger = new StateLogger();
        var service = new AppSvc(
            new FixedLlm("""{"action":"Sell","rationale":"利益確定","referencePrice":102,"stopLossDistancePerShare":2}"""),
            new FakePolicy(), new FakeSizing(), new FakeClock(), logger,
            currentPrice: new FakeCurrentPrice(new CurrentPriceReading(102m, Known)),
            heldPosition: new LongHeld(10));

        var decision = await service.DecideAsync(ScheduledAapl());

        decision.Should().NotBeNull("保有の決済は判断として出る（見送りではない）");
        decision!.Intent.PositionEffect.Should().Be(PositionEffect.Close);
        logger.Entries.Should().NotContain(e => e.Message.StartsWith(ObservationPrefix, StringComparison.Ordinal));
    }

    private sealed class LongHeld(int quantity) : IHeldPositionProvider
    {
        public bool IsEnabled => true;

        public Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(quantity);

        public Task<HeldPosition?> GetPositionAsync(string symbol, Market market, CancellationToken cancellationToken = default) =>
            Task.FromResult<HeldPosition?>(new HeldPosition(quantity, 100m, 98m));

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

    private sealed class FakeSizing(SizingContext? context = null) : ISizingContextProvider
    {
        public Task<SizingContext> GetContextAsync(CancellationToken ct = default) => Task.FromResult(context ?? Context);
    }

    private sealed class FakeCurrentPrice(CurrentPriceReading? reading) : ICurrentPriceProvider
    {
        public bool IsEnabled => true;

        public Task<CurrentPriceReading?> GetCurrentPriceAsync(DecisionTrigger trigger, CancellationToken ct = default) =>
            Task.FromResult(reading);
    }
}
