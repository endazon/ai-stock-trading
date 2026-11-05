extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;

namespace TradeDecisionService.Tests;

// 🔴 FR-04, FR-10, ADR-0003, #1529, IADR-0617: 権威ある現在値の供給が無い構成（no-op）では、LLM が返した参照価格で新規建てを作らない。
// 手仕舞い（Close）は止めない（FR-10）。従前の挙動は明示の opt-in（UnanchoredEntryPriceOptions.Allow=true）でだけ。
public class UnanchoredEntryPriceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 1, 0, 0, TimeSpan.Zero);
    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 7, 10), "米国株の押し目買い方針");

    private const string BuyJson =
        """{"action":"Buy","rationale":"押し目","referencePrice":1000,"stopLossDistancePerShare":30}""";
    private const string SellJson =
        """{"action":"Sell","rationale":"利確","referencePrice":1000,"stopLossDistancePerShare":30}""";

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class FakeLlm(string output) : ILlmCompletionClient
    {
        public Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken ct = default) =>
            Task.FromResult(output);
    }

    private sealed class FakePolicy : IDailyPolicyProvider
    {
        public Task<DailyPolicy?> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult<DailyPolicy?>(Policy);
    }

    private sealed class FakeSizing : ISizingContextProvider
    {
        public Task<SizingContext> GetContextAsync(CancellationToken ct = default) =>
            Task.FromResult(new SizingContext(100_000m, 50_000m, 20_000m, 0, 0m,
                BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits()));
    }

    private sealed class FakeHeld(int signedQuantity) : IHeldPositionProvider
    {
        public bool IsEnabled => true;

        public Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<int?>(signedQuantity);

        public Task<HeldPosition?> GetPositionAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<HeldPosition?>(signedQuantity == 0 ? HeldPosition.None : new HeldPosition(signedQuantity, 1_000m, 970m));

        public Task<WorkingEntryOrders?> GetWorkingEntryOrdersAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<WorkingEntryOrders?>(WorkingEntryOrders.None);
    }

    private sealed class FakeCurrentPrice(decimal price) : ICurrentPriceProvider
    {
        public bool IsEnabled => true;

        public Task<CurrentPriceReading?> GetCurrentPriceAsync(DecisionTrigger trigger, CancellationToken ct = default) =>
            Task.FromResult<CurrentPriceReading?>(new CurrentPriceReading(price, IntradayPriceContext.Unknown));
    }

    private sealed class RecordingSkipReporter : IDecisionSkipReporter
    {
        public List<DecisionSkipReason> Reasons { get; } = [];

        public void Report(string trigger, DecisionSkipReason reason) => Reasons.Add(reason);
    }

    private static AppSvc Create(
        string llmOutput, int held, UnanchoredEntryPriceOptions? options, RecordingSkipReporter reporter,
        ICurrentPriceProvider? currentPrice = null) =>
        new(new FakeLlm(llmOutput), new FakePolicy(), new FakeSizing(), new FakeClock(), NullLogger<AppSvc>.Instance,
            currentPrice: currentPrice, heldPosition: new FakeHeld(held), skipReporter: reporter,
            unanchoredEntryPrice: options);

    private static PriceMovementDetected Trigger() =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, 1_040m, 1_000m, 0.04m, Now);

    [Fact]
    public async Task 現在値の供給が無い構成で_既定は新規建てを見送る()
    {
        var reporter = new RecordingSkipReporter();

        var decision = await Create(BuyJson, held: 0, new UnanchoredEntryPriceOptions(Allow: false), reporter)
            .DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        decision.Should().BeNull("LLM の参照価格だけで新規建てのサイジング・損切り・発注をしない");
        reporter.Reasons.Should().Equal(DecisionSkipReason.ReferencePriceInvalid);
    }

    [Fact]
    public async Task 現在値の供給が無い構成でも手仕舞いは止めない()
    {
        var reporter = new RecordingSkipReporter();

        var decision = await Create(SellJson, held: 10, new UnanchoredEntryPriceOptions(Allow: false), reporter)
            .DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        decision.Should().NotBeNull();
        decision!.Intent.PositionEffect.Should().Be(PositionEffect.Close);
        decision.Intent.Quantity.Should().Be(10);
    }

    [Fact]
    public async Task 権威ある現在値があれば新規建ては従前どおり()
    {
        var reporter = new RecordingSkipReporter();

        var decision = await Create(BuyJson, held: 0, new UnanchoredEntryPriceOptions(Allow: false), reporter,
                currentPrice: new FakeCurrentPrice(1_000m))
            .DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        decision.Should().NotBeNull();
        decision!.Intent.PositionEffect.Should().Be(PositionEffect.Open);
        decision.Intent.Price.Should().Be(1_000m);
    }

    [Fact]
    public async Task 明示の_opt_in_では従前どおり_LLM_の参照価格で新規建てを作る()
    {
        var reporter = new RecordingSkipReporter();

        var decision = await Create(BuyJson, held: 0, new UnanchoredEntryPriceOptions(Allow: true), reporter)
            .DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        decision.Should().NotBeNull();
        decision!.Intent.PositionEffect.Should().Be(PositionEffect.Open);
    }
}
