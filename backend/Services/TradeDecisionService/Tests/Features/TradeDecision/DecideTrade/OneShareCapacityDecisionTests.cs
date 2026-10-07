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

// 🔴 FR-10, #1174, IADR-0500 決定1・2: **段階残枠と日次残枠の小さい方が現在値 × 1 株（基準通貨）に満たない新規建ては、LLM を呼ばずに見送る**
// （オーナー裁定 2026-10-07）。是正前は GOOGL で 1 セッション 48 回、一次・二次の LLM を経たうえでサイジングの数量 0 で見送っていた。
//   - 線引きは #1176 と同じ（保有が既知で 0・未約定が既知で空）。現在値が無い・残枠が未供給なら省かない（LLM を呼ぶ）。
//   - ちょうど 1 株の価格は省かない（1 株買える）。日本株は基準通貨へ換算して比べる。
//   - 残枠が最小の名目額（equity の 1%）にも届かないときは #1176 の EntryCapacityBelowMinimumNotional が先に当たる。
public class OneShareCapacityDecisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 17, 0, 0, TimeSpan.Zero); // 2026-10-05 US セッション
    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 10, 5), "押し目買いの方針");

    // GOOGL の形: equity $100,000（最小の名目額 $1,000）・残枠 $2,000（最小以上）・現在値 $2,500（1 株に届かない）。
    private const decimal Equity = 100_000m;
    private const decimal GooglPrice = 2_500m;
    private const string GooglBuyJson =
        """{"action":"Buy","rationale":"押し目","referencePrice":2500,"stopLossDistancePerShare":30}""";

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

    // enabled=false は現在値ソースが未有効の構成（サイジングは LLM の参照価格を使う）。
    private sealed class FakeCurrentPrice(decimal price, bool enabled = true) : ICurrentPriceProvider
    {
        public bool IsEnabled => enabled;

        public Task<CurrentPriceReading?> GetCurrentPriceAsync(DecisionTrigger trigger, CancellationToken ct = default) =>
            Task.FromResult<CurrentPriceReading?>(
                enabled ? new CurrentPriceReading(price, IntradayPriceContext.Unknown) : null);
    }

    // 保有照会（実結線）。working が true なら未約定の新規建てあり。
    private sealed class FakeHeld(int held, bool working = false) : IHeldPositionProvider
    {
        public bool IsEnabled => true;

        public Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<int?>(held);

        public Task<HeldPosition?> GetPositionAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<HeldPosition?>(held == 0 ? HeldPosition.None : new HeldPosition(held, 2_400m, 2_300m));

        public Task<WorkingEntryOrders?> GetWorkingEntryOrdersAsync(
            string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<WorkingEntryOrders?>(working
                ? new WorkingEntryOrders([new WorkingEntryOrder(TradeSide.Buy, 1, 2_450m, Now.AddMinutes(-1))])
                : WorkingEntryOrders.None);
    }

    // 非基準通貨（日本株・JPY）の換算レート。鮮度は判定しない供給元（新規建てに使える）。
    private sealed class FixedFxRate(decimal rate) : IFxRateProvider
    {
        public Task<decimal?> GetRateToBaseAsync(Market market, CancellationToken ct = default) =>
            Task.FromResult<decimal?>(market == Market.UnitedStates ? 1m : rate);
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

    private sealed class RecordingHeldReporter : IDecisionHeldReporter
    {
        public List<TradeDecisionHeld> Reports { get; } = [];

        public Task ReportAsync(TradeDecisionHeld held, CancellationToken cancellationToken = default)
        {
            Reports.Add(held);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSkips : IDecisionSkipReporter
    {
        public List<DecisionSkipReason> Reasons { get; } = [];

        public void Report(string trigger, DecisionSkipReason reason) => Reasons.Add(reason);
    }

    private sealed record Probe(
        AppSvc Service, FixedLlm Llm, RecordingForgone Forgone, RecordingHeldReporter Held, RecordingSkips Skips);

    private static SizingContext Context(
        decimal? stageRemaining, decimal? dailyRemaining = 1_000_000m, decimal? equity = Equity) =>
        new(equity, stageRemaining, dailyRemaining, 0, 0m, BrokerProvider.MoomooSimulate, TradingDefaults.CreateRiskLimits());

    private static Probe Create(
        SizingContext context, IHeldPositionProvider held, ICurrentPriceProvider? currentPrice = null,
        string llmOutput = GooglBuyJson, MinimumEntryNotionalOptions? options = null, IFxRateProvider? fxRate = null)
    {
        var llm = new FixedLlm(llmOutput);
        var forgone = new RecordingForgone();
        var heldReporter = new RecordingHeldReporter();
        var skips = new RecordingSkips();
        var service = new AppSvc(
            llm, new FakePolicy(), new FakeSizing(context), new FakeClock(), NullLogger<AppSvc>.Instance,
            currentPrice: currentPrice ?? new FakeCurrentPrice(GooglPrice), heldPosition: held, skipReporter: skips,
            heldReporter: heldReporter, forgoneReporter: forgone, minimumEntryNotional: options, fxRate: fxRate);
        return new Probe(service, llm, forgone, heldReporter, skips);
    }

    private static DecisionTrigger Trigger(string symbol = "GOOGL") => DecisionTrigger.Scheduled(symbol, Market.UnitedStates, Now);

    private static void ShouldBeForgoneBeforeLlm(Probe probe, DecisionForgoneBeforeLlmReason reason)
    {
        probe.Llm.Calls.Should().Be(0, "結果が決まっている LLM 呼び出しをしない（裁定）");
        probe.Forgone.Reports.Should().ContainSingle().Which.Reason.Should().Be(reason);
        probe.Skips.Reasons.Should().Equal(AppSvc.ToSkipReason(reason));
        probe.Held.Reports.Should().BeEmpty("判断をしていない見送りで急変の基準値を進めない（IADR-0452 決定1）");
    }

    // T-10-2371: 🔴 issue の形（GOOGL）。残枠 $2,000 は最小の名目額 $1,000 以上だが、現在値 $2,500 × 1 株に満たない → LLM を呼ばずに見送る。
    [Fact]
    public async Task T_10_2371_残枠が現在値の1株に満たない銘柄はLLMを呼ばずに見送る()
    {
        var probe = Create(Context(stageRemaining: 2_000m), new FakeHeld(0));

        var decision = await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        decision.Should().BeNull();
        ShouldBeForgoneBeforeLlm(probe, DecisionForgoneBeforeLlmReason.EntryCapacityBelowOneShare);
        probe.Forgone.Reports[0].Symbol.Should().Be("GOOGL");
    }

    // T-10-2372: 🔴 境界。残枠 ＝ 現在値（1 株ちょうど買える）は LLM を呼び、1 株の発注意図になる。1 セント足りなければ LLM を呼ばずに見送る。
    // 日次の発注残枠だけが足りない（段階残枠は十分）ときも見送る（小さい方で読む）。
    [Theory]
    [InlineData("2500", "1000000", true)]
    [InlineData("2499.99", "1000000", false)]
    [InlineData("1000000", "2499.99", false)]
    [InlineData("1000000", "2500", true)]
    public async Task T_10_2372_残枠がちょうど1株の価格ならLLMを呼び1セント足りなければ見送る(
        string stageText, string dailyText, bool expectsOrder)
    {
        var stage = decimal.Parse(stageText, System.Globalization.CultureInfo.InvariantCulture);
        var daily = decimal.Parse(dailyText, System.Globalization.CultureInfo.InvariantCulture);
        var probe = Create(Context(stage, daily), new FakeHeld(0));

        var decision = await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        if (expectsOrder)
        {
            probe.Llm.Calls.Should().BeGreaterThan(0, "残枠で 1 株ちょうど買える");
            probe.Forgone.Reports.Should().BeEmpty();
            decision.Should().NotBeNull();
            decision!.Intent.Quantity.Should().Be(1);
            decision.Intent.Price.Should().Be(GooglPrice, "参照価格は LLM の前に読んだ現在値（同じ値で判定した）");
            decision.Intent.PositionEffect.Should().Be(PositionEffect.Open);
            probe.Skips.Reasons.Should().BeEmpty();
        }
        else
        {
            decision.Should().BeNull();
            ShouldBeForgoneBeforeLlm(probe, DecisionForgoneBeforeLlmReason.EntryCapacityBelowOneShare);
        }
    }

    // T-10-2373: 🔴 日本株（JPY）は**基準通貨（USD）へ換算した 1 株の価格**で比べる。レート 0.0064・残枠 $2,000。
    // ¥312,500 × 0.0064 ＝ $2,000 ちょうどは LLM を呼んで 1 株、¥312,501（$2,000.0064）は LLM を呼ばずに見送る。
    // 円の価格（312,500）のまま比べると常に足りないと読み、日本株の判断が全部消える。
    [Theory]
    [InlineData("312500", true)]
    [InlineData("312501", false)]
    public async Task T_10_2373_日本株は基準通貨へ換算した1株の価格で残枠と比べる(string priceText, bool expectsOrder)
    {
        const decimal JpyToUsd = 0.0064m;
        var price = decimal.Parse(priceText, System.Globalization.CultureInfo.InvariantCulture);
        var json = $$"""{"action":"Buy","rationale":"押し目","referencePrice":{{priceText}},"stopLossDistancePerShare":4000}""";
        var probe = Create(
            Context(stageRemaining: 2_000m), new FakeHeld(0), new FakeCurrentPrice(price), json, fxRate: new FixedFxRate(JpyToUsd));

        var decision = await probe.Service.DecideAsync(
            DecisionTrigger.Scheduled("7203", Market.Japan, Now), TestContext.Current.CancellationToken);

        if (expectsOrder)
        {
            probe.Llm.Calls.Should().BeGreaterThan(0, "¥312,500 × 0.0064 ＝ $2,000 は残枠ちょうど");
            probe.Forgone.Reports.Should().BeEmpty();
            decision.Should().NotBeNull();
            decision!.Intent.Quantity.Should().Be(1);
            decision.Intent.Price.Should().Be(price, "発注意図の価格はローカル通貨（JPY）のまま");
            decision.Intent.NotionalInBase.Should().Be(2_000m);
        }
        else
        {
            decision.Should().BeNull("¥312,501 × 0.0064 ＝ $2,000.0064 は残枠 $2,000 に満たない");
            ShouldBeForgoneBeforeLlm(probe, DecisionForgoneBeforeLlmReason.EntryCapacityBelowOneShare);
        }
    }

    // T-10-2374: 🔴 省かない経路（LLM を呼ぶ）。保有中（決済の判断を残す。買い増しは LLM の後に数量 0）・未約定あり・現在値ソースが未有効
    // （サイジングは LLM の参照価格を使うので LLM の前には分からない）・段階残枠または日次残枠が未供給（「分からない」を「足りない」と読まない）。
    // どれも従来どおり LLM の後にサイジングの数量 0 で見送る。
    [Theory]
    [InlineData("held")]
    [InlineData("working")]
    [InlineData("noCurrentPrice")]
    [InlineData("stageUnknown")]
    [InlineData("dailyUnknown")]
    public async Task T_10_2374_保有中や未約定ありや現在値なしや残枠の未供給ではLLMを呼ぶ(string kind)
    {
        var probe = kind switch
        {
            "held" => Create(Context(stageRemaining: 2_000m), new FakeHeld(10)),
            "working" => Create(Context(stageRemaining: 2_000m), new FakeHeld(0, working: true)),
            "noCurrentPrice" => Create(
                Context(stageRemaining: 2_000m), new FakeHeld(0), new FakeCurrentPrice(GooglPrice, enabled: false)),
            "stageUnknown" => Create(Context(stageRemaining: null, dailyRemaining: 2_000m), new FakeHeld(0)),
            "dailyUnknown" => Create(Context(stageRemaining: 2_000m, dailyRemaining: null), new FakeHeld(0)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        (await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken)).Should().BeNull();

        probe.Llm.Calls.Should().BeGreaterThan(0, "LLM の前には結論が決まらない（{0}）", kind);
        probe.Forgone.Reports.Should().BeEmpty();
        probe.Skips.Reasons.Should().Equal(DecisionSkipReason.SizingZeroQuantity);
    }

    // T-10-2375: 🔴 2 つの LLM 前の金額の判定の順序。残枠 $500 は最小の名目額（$1,000）にも 1 株（$2,500）にも届かない → 資金の枯渇として
    // #1176 の EntryCapacityBelowMinimumNotional が勝つ。しきい値 0（#1176 の統制を外す）・equity の未供給（#1176 の判定が働かない）では本件の理由。
    [Theory]
    [InlineData("default")]
    [InlineData("ratioZero")]
    [InlineData("equityUnknown")]
    public async Task T_10_2375_残枠が最小の名目額にも届かなければ最小の名目額の理由が先に当たる(string kind)
    {
        var probe = kind switch
        {
            "default" => Create(Context(stageRemaining: 500m), new FakeHeld(0)),
            "ratioZero" => Create(
                Context(stageRemaining: 500m), new FakeHeld(0), options: new MinimumEntryNotionalOptions(0m)),
            "equityUnknown" => Create(Context(stageRemaining: 500m, equity: null), new FakeHeld(0)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        (await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken)).Should().BeNull();

        ShouldBeForgoneBeforeLlm(
            probe,
            kind == "default"
                ? DecisionForgoneBeforeLlmReason.EntryCapacityBelowMinimumNotional
                : DecisionForgoneBeforeLlmReason.EntryCapacityBelowOneShare);
    }
}
