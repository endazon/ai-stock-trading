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

// 🔴 FR-10, #1176, IADR-0495 決定1・2: **最小の名目額（equity × しきい値。既定 1%）に満たない新規建ては見送る**（オーナー裁定 2026-10-07）。
//   - LLM の後（統制の本体）: サイジングの名目額（数量 × 参照価格・基準通貨）が最小に満たなければ SizedBelowMinimumNotional。ちょうど等しいときは通す。
//   - LLM の前（費用）: 保有が既知で 0・未約定が既知で空・資金と残枠が既知で、新規建てに使える金額の上限（1 注文上限・段階残枠・日次残枠の最小）
//     が最小に届かなければ、LLM を呼ばずに EntryCapacityBelowMinimumNotional。
//   - 決済は対象外。しきい値 0 は統制を外す。構成を渡さなければ既定（1%）で効く。
public class MinimumEntryNotionalDecisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 19, 47, 0, TimeSpan.Zero); // 2026-10-07 04:47 JST
    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 10, 6), "押し目買いの方針");

    // 2026-10-07 04:47 JST の実例: AAPL 13 株 @334.11（約 $4.3k・equity の約 0.45%）。段階資金の残枠が約 $4.3〜4.7k まで減っていた。
    private const decimal AaplPrice = 334.11m;
    private const decimal PocEquity = 970_000m;
    private const string AaplBuyJson =
        """{"action":"Buy","rationale":"押し目","referencePrice":334.11,"stopLossDistancePerShare":10}""";
    private const string AaplSellJson =
        """{"action":"Sell","rationale":"利確","referencePrice":334.11,"stopLossDistancePerShare":null}""";
    private const string HoldJson = """{"action":"Hold","rationale":"様子見"}""";

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

    // 保有照会（実結線）。working が true なら未約定の新規建てあり。
    private sealed class FakeHeld(int held, bool working = false) : IHeldPositionProvider
    {
        public bool IsEnabled => true;

        public Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<int?>(held);

        public Task<HeldPosition?> GetPositionAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<HeldPosition?>(held == 0 ? HeldPosition.None : new HeldPosition(held, 320m, 310m));

        public Task<WorkingEntryOrders?> GetWorkingEntryOrdersAsync(
            string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<WorkingEntryOrders?>(working
                ? new WorkingEntryOrders([new WorkingEntryOrder(TradeSide.Buy, 5, 330m, Now.AddMinutes(-1))])
                : WorkingEntryOrders.None);
    }

    // 非基準通貨（日本株・JPY）の換算レート。鮮度は判定しない供給元（既定実装の Unknown＝新規建てに使える）。
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

    private static SizingContext Context(decimal? equity, decimal? stageRemaining, decimal? dailyRemaining = 1_000_000m) =>
        new(equity, stageRemaining, dailyRemaining, 0, 0m, BrokerProvider.MoomooSimulate, TradingDefaults.CreateRiskLimits());

    // options が null なら**構成を渡さない**（既定で効くことを確かめる経路）。
    private static Probe Create(
        SizingContext context, IHeldPositionProvider held, decimal price = AaplPrice, string llmOutput = AaplBuyJson,
        MinimumEntryNotionalOptions? options = null, IFxRateProvider? fxRate = null)
    {
        var llm = new FixedLlm(llmOutput);
        var forgone = new RecordingForgone();
        var heldReporter = new RecordingHeldReporter();
        var skips = new RecordingSkips();
        var service = new AppSvc(
            llm, new FakePolicy(), new FakeSizing(context), new FakeClock(), NullLogger<AppSvc>.Instance,
            currentPrice: new FakeCurrentPrice(price), heldPosition: held, skipReporter: skips, heldReporter: heldReporter,
            forgoneReporter: forgone, minimumEntryNotional: options, fxRate: fxRate);
        return new Probe(service, llm, forgone, heldReporter, skips);
    }

    private static DecisionTrigger Trigger(string symbol = "AAPL") => DecisionTrigger.Scheduled(symbol, Market.UnitedStates, Now);

    // T-10-2312: 境界（サイジングの後）。equity 100,000 の 1%＝1,000。残枠 1,500（LLM の前の下界には掛からない）で 1 株だけ買える価格を動かす。
    // 1 株 × 999.99 は見送り、1,000 ちょうどと 1,000.01 は発注意図を作る。
    [Theory]
    [InlineData("999.99", false)]
    [InlineData("1000", true)]
    [InlineData("1000.01", true)]
    public async Task T_10_2312_サイジングの名目額がequityの1パーセント未満なら見送りちょうどは通す(string priceText, bool expectsOrder)
    {
        var price = decimal.Parse(priceText, System.Globalization.CultureInfo.InvariantCulture);
        var json = $$"""{"action":"Buy","rationale":"押し目","referencePrice":{{priceText}},"stopLossDistancePerShare":30}""";
        var probe = Create(Context(100_000m, 1_500m), new FakeHeld(0), price, json);

        var decision = await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        probe.Llm.Calls.Should().BeGreaterThan(0, "残枠は最小以上なので LLM の前には省かない");
        probe.Forgone.Reports.Should().BeEmpty();
        if (expectsOrder)
        {
            decision.Should().NotBeNull();
            decision!.Intent.Quantity.Should().Be(1);
            decision.Intent.PositionEffect.Should().Be(PositionEffect.Open);
            probe.Skips.Reasons.Should().BeEmpty();
        }
        else
        {
            decision.Should().BeNull("名目額 999.99 は equity の 1%（1,000）に満たない");
            probe.Skips.Reasons.Should().Equal(DecisionSkipReason.SizedBelowMinimumNotional);
            probe.Held.Reports.Should().ContainSingle().Which.Reason.Should().Be(nameof(DecisionSkipReason.SizedBelowMinimumNotional));
        }
    }

    // T-10-2312: 価格で割る端数（LLM の前には分からない）。残枠 9,800 ≥ 最小 9,700 だが、334.11 で 29 株＝9,689.19 ＜ 9,700 で見送る。
    [Fact]
    public async Task T_10_2312_残枠が最小以上でも株数の端数で最小を割ればLLMの後に見送る()
    {
        var probe = Create(Context(PocEquity, 9_800m), new FakeHeld(0));

        (await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken)).Should().BeNull();

        probe.Llm.Calls.Should().BeGreaterThan(0);
        probe.Skips.Reasons.Should().Equal(DecisionSkipReason.SizedBelowMinimumNotional);
    }

    // T-10-2313: 🔴 issue の実例（AAPL 13 株 @334.11・equity 約 $970k・段階残枠 $4.5k）。保有 0・未約定なしでは、残枠（4,500）が最小（9,700）に
    // 届かないので LLM を呼ばずに見送る。構成を渡さない（既定の 1% が効く）。
    [Fact]
    public async Task T_10_2313_AAPLの13株の実例は保有0ならLLMを呼ばずに見送る()
    {
        var probe = Create(Context(PocEquity, 4_500m), new FakeHeld(0));

        var decision = await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        decision.Should().BeNull();
        probe.Llm.Calls.Should().Be(0, "LLM の費用を消費しない（裁定 1）");
        probe.Forgone.Reports.Should().ContainSingle().Which.Reason
            .Should().Be(DecisionForgoneBeforeLlmReason.EntryCapacityBelowMinimumNotional);
        probe.Skips.Reasons.Should().Equal(DecisionSkipReason.EntryCapacityBelowMinimumNotional);
        probe.Held.Reports.Should().BeEmpty("判断をしていない見送りで急変の基準値を進めない（IADR-0452 決定1）");
    }

    // T-10-2313: 同じ AAPL の 13 株でも、保有中（買い増し）・未約定ありでは LLM を呼ぶ（決済の判断を残す）。買いの結論は LLM の後に見送る。
    [Theory]
    [InlineData(10, false)]
    [InlineData(0, true)]
    public async Task T_10_2313_保有中や未約定ありではLLMを呼び買いはサイジングの後に見送る(int held, bool working)
    {
        var probe = Create(Context(PocEquity, 4_500m), new FakeHeld(held, working));

        (await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken)).Should().BeNull();

        probe.Llm.Calls.Should().BeGreaterThan(0);
        probe.Forgone.Reports.Should().BeEmpty();
        probe.Skips.Reasons.Should().Equal(DecisionSkipReason.SizedBelowMinimumNotional);
    }

    // T-10-2313: 資金・残枠が未供給（null）なら「届かない」とは読まず LLM を呼ぶ（従来どおり LLM の後に数量 0 で見送る）。
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task T_10_2313_資金か残枠が未供給ならLLMの前には省かない(bool equityUnknown, bool stageUnknown)
    {
        var probe = Create(Context(equityUnknown ? null : PocEquity, stageUnknown ? null : 4_500m), new FakeHeld(0));

        (await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken)).Should().BeNull();

        probe.Llm.Calls.Should().BeGreaterThan(0);
        probe.Forgone.Reports.Should().BeEmpty();
        probe.Skips.Reasons.Should().Equal(DecisionSkipReason.SizingZeroQuantity);
    }

    // T-10-2313: しきい値 0 は統制を外す（AAPL の 13 株がそのまま発注意図になる＝是正前と同じ）。
    [Fact]
    public async Task T_10_2313_しきい値0なら最小の名目額で見送らない()
    {
        var probe = Create(Context(PocEquity, 4_500m), new FakeHeld(0), options: new MinimumEntryNotionalOptions(0m));

        var decision = await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        decision.Should().NotBeNull();
        decision!.Intent.Quantity.Should().Be(13, "floor(4,500 ÷ 334.11)＝13（issue の実例と同じ株数）");
        probe.Skips.Reasons.Should().BeEmpty();
    }

    // T-10-2313: しきい値は構成どおりに効く（0.4% なら 13 株 ≈ 4,343 ≥ 3,880 で通る）。
    [Fact]
    public async Task T_10_2313_構成したしきい値で判定する()
    {
        var probe = Create(Context(PocEquity, 4_500m), new FakeHeld(0), options: new MinimumEntryNotionalOptions(0.004m));

        (await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken))!.Intent.Quantity.Should().Be(13);
    }

    // T-10-2314: 🔴 決済は名目額で止めない（FR-10「手仕舞いは止めない」）。保有 13 株（約 0.45%）の利確の売りは全量の決済になる。
    [Fact]
    public async Task T_10_2314_極小の保有の決済は最小の名目額で止めない()
    {
        var probe = Create(Context(PocEquity, 4_500m), new FakeHeld(13), llmOutput: AaplSellJson);

        var decision = await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        decision.Should().NotBeNull();
        decision!.Intent.PositionEffect.Should().Be(PositionEffect.Close);
        decision.Intent.Quantity.Should().Be(13);
        probe.Skips.Reasons.Should().BeEmpty();
    }

    // T-10-2314: LLM の前に省いた銘柄でも Hold は従来どおり（ここでは LLM を呼ばないので結論は無い）。保有中の Hold は LlmHold のまま。
    [Fact]
    public async Task T_10_2314_保有中のHoldは従来どおり()
    {
        var probe = Create(Context(PocEquity, 4_500m), new FakeHeld(13), llmOutput: HoldJson);

        (await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken)).Should().BeNull();

        probe.Skips.Reasons.Should().Equal(DecisionSkipReason.LlmHold);
    }

    // T-10-2335: 🔴 非基準通貨（日本株・JPY）は**基準通貨（USD）へ換算した名目額**で比べる。レート 0.0064（≈ 1/156）・equity $100,000 の 1%＝$1,000。
    // 段階残枠 $1,500（LLM の前の下界には掛からない）で 1 株だけ買える価格を動かす。¥156,249 × 0.0064 ＝ $999.9936 は見送り、
    // ¥156,250 × 0.0064 ＝ $1,000 ちょうどは通す。ローカル通貨の名目額（¥156,249）で比べると桁で誤り、見送りが消える。
    [Theory]
    [InlineData("156249", false)]
    [InlineData("156250", true)]
    public async Task T_10_2335_日本株は基準通貨へ換算した名目額で最小と比べる(string priceText, bool expectsOrder)
    {
        const decimal JpyToUsd = 0.0064m;
        var price = decimal.Parse(priceText, System.Globalization.CultureInfo.InvariantCulture);
        var json = $$"""{"action":"Buy","rationale":"押し目","referencePrice":{{priceText}},"stopLossDistancePerShare":4000}""";
        var probe = Create(Context(100_000m, 1_500m), new FakeHeld(0), price, json, fxRate: new FixedFxRate(JpyToUsd));

        var decision = await probe.Service.DecideAsync(
            DecisionTrigger.Scheduled("7203", Market.Japan, Now), TestContext.Current.CancellationToken);

        probe.Llm.Calls.Should().BeGreaterThan(0, "残枠 $1,500 は最小 $1,000 以上なので LLM の前には省かない");
        probe.Forgone.Reports.Should().BeEmpty();
        if (expectsOrder)
        {
            decision.Should().NotBeNull("¥156,250 × 0.0064 ＝ $1,000 は equity の 1% ちょうど（ちょうどは通す）");
            decision!.Intent.Quantity.Should().Be(1);
            decision.Intent.Price.Should().Be(price, "発注意図の価格はローカル通貨（JPY）のまま");
            decision.Intent.NotionalInBase.Should().Be(1_000m);
            probe.Skips.Reasons.Should().BeEmpty();
        }
        else
        {
            decision.Should().BeNull("¥156,249 × 0.0064 ＝ $999.9936 は equity の 1%（$1,000）に満たない");
            probe.Skips.Reasons.Should().Equal(DecisionSkipReason.SizedBelowMinimumNotional);
        }
    }

    // T-10-2336: 🔴 LLM の前の下界の境界。新規建てに使える金額の上限が最小の名目額**ちょうど**（段階残枠 $1,000＝equity $100,000 の 1%）なら
    // 届かないとは言えないので LLM を呼ぶ（下界は「満たない」＝厳密な不等号）。参照価格 $1,000 で 1 株＝名目 $1,000 ちょうどは発注意図になる。
    [Fact]
    public async Task T_10_2336_使える金額の上限が最小ちょうどならLLMを呼ぶ()
    {
        const string json = """{"action":"Buy","rationale":"押し目","referencePrice":1000,"stopLossDistancePerShare":30}""";
        var probe = Create(Context(100_000m, 1_000m), new FakeHeld(0), 1_000m, json);

        var decision = await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        probe.Llm.Calls.Should().BeGreaterThan(0, "上限 $1,000 は最小 $1,000 に届いている");
        probe.Forgone.Reports.Should().BeEmpty();
        decision.Should().NotBeNull();
        decision!.Intent.Quantity.Should().Be(1);
        probe.Skips.Reasons.Should().BeEmpty();
    }

    // T-10-2336: 🔴 日次の発注残枠が段階残枠より小さく、日次だけが最小に届かない（段階 $5,000 ≥ 最小 $1,000 ＞ 日次 $900）なら、
    // LLM を呼ばずに見送る。上限は段階残枠・日次残枠の**小さい方**で読む（段階残枠だけで読むと LLM を呼んでしまう）。
    [Fact]
    public async Task T_10_2336_日次の残枠だけが最小に届かなくてもLLMを呼ばずに見送る()
    {
        var probe = Create(Context(100_000m, 5_000m, dailyRemaining: 900m), new FakeHeld(0));

        (await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken)).Should().BeNull();

        probe.Llm.Calls.Should().Be(0, "日次の残枠 $900 は最小 $1,000 に届かない");
        probe.Forgone.Reports.Should().ContainSingle().Which.Reason
            .Should().Be(DecisionForgoneBeforeLlmReason.EntryCapacityBelowMinimumNotional);
        probe.Skips.Reasons.Should().Equal(DecisionSkipReason.EntryCapacityBelowMinimumNotional);
    }
}
