extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;

namespace TradeDecisionService.Tests;

// 🔴 FR-10, ADR-0063 決定1〜5, #1291, IADR-0527 決定3・決定4: 高ボラティリティ銘柄（明示指定、または ATR(14) ÷ 参照価格 ≥ 4%）の 1 注文上限
// （equity の 5%。区分外の 25% との小さい方）を、**サイジング・LLM の前の見送り・審査の 3 か所で同じ判定と同じ上限**にする。
//   - サイジング: 区分の銘柄の数量は equity × 5% ÷ 参照価格で抑えられ、その発注意図は審査（RiskEvaluator）を必ず通る（1 株多ければ落ちる）。
//   - 発注意図は判断で読んだ ATR(14) を運び、審査は同じ ATR で同じ区分を判定する。ATR が得られなければ明示指定だけ。
//   - LLM の前: 区分の上限が最小の名目額に届かなければ LLM を呼ばずに見送る。ATR は判断ごとに 1 回だけ読む。
public class HighVolatilityOrderCapDecisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 13, 42, 0, TimeSpan.Zero);
    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 10, 9), "追加した高ボラティリティ銘柄は数量を控えめに");
    private const decimal Equity = 100_000m;
    private const decimal Price = 100m;

    // 損切り幅 0.5（参照価格の 0.5%。下限 2%＝2 まで広がる）→ リスク基準 1,000 ÷ 2 = 500 株。金額の上限が決める。
    private const string BuyJson = """{"action":"Buy","rationale":"押し目","referencePrice":100,"stopLossDistancePerShare":0.5}""";

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class FixedLlm(string output) : ILlmCompletionClient
    {
        public int Calls { get; private set; }

        public Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(output);
        }
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

    private sealed class FlatHeld : IHeldPositionProvider
    {
        public bool IsEnabled => true;

        public Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<int?>(0);

        public Task<HeldPosition?> GetPositionAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<HeldPosition?>(HeldPosition.None);

        public Task<WorkingEntryOrders?> GetWorkingEntryOrdersAsync(
            string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<WorkingEntryOrders?>(WorkingEntryOrders.None);
    }

    // ATR(14) の供給口（有効な構成）。atr が null なら「有効だが得られない」。何回読まれたかを数える。
    private sealed class FakeAtr(decimal? atr) : IStopWidthFloorSource
    {
        public int Calls { get; private set; }

        public bool IsEnabled => true;

        public ValueTask<StopWidthFloor?> GetFloorAsync(string symbol, Market market, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(atr is { } a ? new StopWidthFloor(a, StopWidthFloorSource.Atr14, a) : null);
        }

        public ValueTask<StopWidthFloor?> GetFloorAsOfAsync(
            string symbol, Market market, DateOnly tradingDay, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<StopWidthFloor?>(null);
    }

    private sealed class RecordingForgone : IDecisionForgoneBeforeLlmReporter
    {
        public List<TradeDecisionForgoneBeforeLlm> Reports { get; } = [];

        public Task ReportAsync(TradeDecisionForgoneBeforeLlm forgone, CancellationToken cancellationToken = default)
        {
            Reports.Add(forgone);
            return Task.CompletedTask;
        }
    }

    private sealed record Probe(AppSvc Service, FixedLlm Llm, RecordingForgone Forgone);

    private static HighVolatilitySettings Designated(params string[] symbols) =>
        TradingDefaults.CreateHighVolatilitySettings() with
        {
            DesignatedSymbols = [.. symbols.Select(s => new HighVolatilitySymbol(s, Market.UnitedStates))],
        };

    private static SizingContext Context(HighVolatilitySettings? highVolatility, decimal stageRemaining = 1_000_000m) =>
        new(Equity, stageRemaining, 1_000_000m, 0, 0m, BrokerProvider.MoomooSimulate, TradingDefaults.CreateRiskLimits(),
            HighVolatility: highVolatility);

    private static Probe Create(
        SizingContext context, IStopWidthFloorSource? atr = null, MinimumEntryNotionalOptions? minimum = null)
    {
        var llm = new FixedLlm(BuyJson);
        var forgone = new RecordingForgone();
        var service = new AppSvc(
            llm, new FakePolicy(), new FakeSizing(context), new FakeClock(), NullLogger<AppSvc>.Instance,
            currentPrice: new FakeCurrentPrice(Price), heldPosition: new FlatHeld(), forgoneReporter: forgone,
            stopWidthFloor: atr, minimumEntryNotional: minimum);
        return new Probe(service, llm, forgone);
    }

    private static DecisionTrigger Trigger(string symbol = "TSLA") => DecisionTrigger.Scheduled(symbol, Market.UnitedStates, Now);

    // 審査（リスク管理）の判定。設定は取引判断へ渡したのと同じ統制値（明示指定）を持つ。
    private static OrderScreeningResult Screen(OrderIntent intent, HighVolatilitySettings? highVolatility) =>
        RiskEvaluator.Evaluate(
            intent,
            TradingDefaults.CreateSettings() with { HighVolatility = highVolatility ?? TradingDefaults.CreateHighVolatilitySettings() },
            new PortfolioSnapshot { Capital = Equity });

    // T-10-2553, ADR-0063 決定1・決定2・フォローアップ 3: 明示指定の銘柄はサイジングで equity の 5%（5,000＝50 株）に抑えられ、その数量は審査を通る。
    // 1 株多い（51 株＝5,100）は審査で拒否される＝サイジングと審査の上限が一致している（#29 の形のループにならない）。明示指定の無い銘柄は 25%（250 株）。
    [Theory]
    [InlineData("TSLA", 50)]
    [InlineData("AAPL", 250)]
    public async Task T_10_2553_明示指定の銘柄はサイジングで5パーセントに抑えられ審査と一致する(string symbol, int expected)
    {
        var highVolatility = Designated("TSLA");
        var probe = Create(Context(highVolatility));

        var decision = await probe.Service.DecideAsync(Trigger(symbol), TestContext.Current.CancellationToken);

        decision!.Intent.Quantity.Should().Be(expected);
        decision.Intent.Atr14.Should().BeNull("ATR の経路が無効（既定）なら運ばない＝審査も明示指定だけで判定する");
        Screen(decision.Intent, highVolatility).Reasons.Should().NotContain(RejectionReason.PerOrderAmountExceeded);
        Screen(decision.Intent with { Quantity = expected + 1 }, highVolatility).Reasons
            .Should().Contain(RejectionReason.PerOrderAmountExceeded, "サイジングの上限と審査の上限は同じ値");
    }

    // T-10-2554, ADR-0063 決定1: 明示指定が無くても ATR(14) ÷ 参照価格 ≥ 4% なら区分（ちょうど 4% を含む）。発注意図は ATR を運び、
    // 審査は同じ ATR で同じ区分を判定する（1 株多ければ拒否）。3.99% は区分外（25%）。
    [Theory]
    [InlineData("4", 50)]     // ちょうど 4%（幅も 4 へ広がり、リスク基準 1,000 ÷ 4 = 250 株 → 区分の 5% で 50 株）
    [InlineData("3.99", 250)] // 直下 → 区分外（リスク基準 250 株・25% も 250 株）
    public async Task T_10_2554_ATR比4パーセント以上なら区分に入り審査と一致する(string atrText, int expected)
    {
        var atr = decimal.Parse(atrText, System.Globalization.CultureInfo.InvariantCulture);
        var probe = Create(Context(Designated()), new FakeAtr(atr));

        var decision = await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        decision!.Intent.Quantity.Should().Be(expected);
        decision.Intent.Atr14.Should().Be(atr, "審査が同じ ATR で区分を判定する");
        Screen(decision.Intent, null).Reasons.Should().NotContain(RejectionReason.PerOrderAmountExceeded);
        if (expected == 50)
        {
            Screen(decision.Intent with { Quantity = 51 }, null).Reasons.Should().Contain(RejectionReason.PerOrderAmountExceeded);
        }
    }

    // T-10-2555, ADR-0063 決定1 🔴: ATR が得られない（有効だが欠損）銘柄は明示指定だけで判定する。指定が無ければ区分外（緩い側）、あれば 5%。
    [Theory]
    [InlineData(false, 250)] // 区分外: リスク基準 500 株（幅は退避の 2%）を 25%（25,000 ÷ 100）が抑える
    [InlineData(true, 50)]   // 明示指定: 5%（5,000 ÷ 100）
    public async Task T_10_2555_ATRが欠損していれば明示指定だけで判定する(bool designated, int expected)
    {
        var probe = Create(Context(designated ? Designated("TSLA") : Designated()), new FakeAtr(null));

        var decision = await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        decision!.Intent.Atr14.Should().BeNull();
        decision.Intent.Quantity.Should().Be(expected);
    }

    // T-10-2556, ADR-0063 決定2 🔴・IADR-0527 決定4: 区分の上限が最小の名目額に届かない構成（最小 6% ＞ 区分 5%）では、
    // 区分の銘柄（明示指定）は LLM を呼ばずに見送る。区分外の銘柄は 25% が最小に届くので LLM を呼ぶ（LLM の前の見送りも同じ上限を使う）。
    [Theory]
    [InlineData("TSLA", true)]
    [InlineData("AAPL", false)]
    public async Task T_10_2556_区分の上限が最小の名目額に届かなければLLMの前に見送る(string symbol, bool forgone)
    {
        var probe = Create(Context(Designated("TSLA")), minimum: new MinimumEntryNotionalOptions(0.06m));

        var decision = await probe.Service.DecideAsync(Trigger(symbol), TestContext.Current.CancellationToken);

        if (forgone)
        {
            decision.Should().BeNull();
            probe.Llm.Calls.Should().Be(0, "LLM の費用を消費しない");
            probe.Forgone.Reports.Should().ContainSingle().Which.Reason
                .Should().Be(DecisionForgoneBeforeLlmReason.EntryCapacityBelowMinimumNotional);
        }
        else
        {
            probe.Llm.Calls.Should().BeGreaterThan(0);
            probe.Forgone.Reports.Should().BeEmpty();
            decision!.Intent.Quantity.Should().Be(250);
        }
    }

    // T-10-2557, IADR-0527 決定4: 自動判定の銘柄でも LLM の前に見送る（ATR をその場で読む）。ATR は判断ごとに 1 回だけ読む
    // （見送らなかったときも、下限の適用・プロンプトのために読み直さない）。ATR が欠損なら明示指定だけ＝見送らない。
    [Theory]
    [InlineData("5", true)]
    [InlineData("3", false)]
    [InlineData(null, false)]
    public async Task T_10_2557_自動判定の銘柄もLLMの前に見送りATRは判断ごとに1回だけ読む(string? atrText, bool forgone)
    {
        var atr = atrText is null ? (decimal?)null : decimal.Parse(atrText, System.Globalization.CultureInfo.InvariantCulture);
        var source = new FakeAtr(atr);
        var probe = Create(Context(Designated()), source, new MinimumEntryNotionalOptions(0.06m));

        var decision = await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        source.Calls.Should().Be(1, "判断ごとに 1 回（IADR-0486 決定2）");
        if (forgone)
        {
            decision.Should().BeNull();
            probe.Llm.Calls.Should().Be(0);
            probe.Forgone.Reports.Should().ContainSingle().Which.Reason
                .Should().Be(DecisionForgoneBeforeLlmReason.EntryCapacityBelowMinimumNotional);
        }
        else
        {
            probe.Llm.Calls.Should().BeGreaterThan(0);
            decision.Should().NotBeNull();
        }
    }

    // T-10-2558, ADR-0063 決定1・IADR-0527 決定3: サイジング文脈に高ボラティリティ銘柄の統制値が無い（旧応答・安全既定）ときは既定（5%・明示指定なし）で効く。
    // 区分の上限そのものは外れない（ATR 5% の銘柄は 50 株）。
    [Fact]
    public async Task T_10_2558_統制値が未供給でも既定の5パーセントで効く()
    {
        var probe = Create(Context(null), new FakeAtr(5m));

        var decision = await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        decision!.Intent.Quantity.Should().Be(50);
    }
}
