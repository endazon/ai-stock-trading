extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using TradeDecisionService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;

namespace TradeDecisionService.Tests;

// T-10-2942, T-10-2943, FR-04, FR-10, #1397, IADR-0562 決定 3: 判断のプロンプト（本判断・一次）に保有中の全銘柄（銘柄・方向・数量）を渡す。
// 実測（PoC 2026-10-16）: MARA の保有中に COIN を判断しても AI は MARA の保有を知らず、方針の「同時に 1 銘柄まで」を評価できなかった。実 LLM は呼ばない。
public class PortfolioInPromptTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 15, 0, 0, TimeSpan.Zero);

    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 10, 16), "COIN・MARA・MSTR・HOOD は同時に保有するのは 1 銘柄までとする。");

    private static readonly SizingContext Context =
        new(100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());

    [Fact]
    public void T_10_2942_保有中の全銘柄を銘柄と方向と数量で並べる()
    {
        var section = TradeDecisionPromptBuilder.PortfolioSection(new PortfolioHoldingsContext(
        [
            new HeldHolding("MARA", Market.UnitedStates, 150),
            new HeldHolding("NVDA", Market.UnitedStates, -10),
            new HeldHolding("7203", Market.Japan, 100),
        ]));

        section.Should().StartWith(TradeDecisionPromptBuilder.PortfolioSectionTitle)
            .And.Contain("- MARA（米国）: ロング 150 株")
            .And.Contain("- NVDA（米国）: ショート 10 株")
            .And.Contain("- 7203（東証）: ロング 100 株")
            .And.Contain(TradeDecisionPromptBuilder.PortfolioRule);
    }

    [Fact]
    public void T_10_2942_保有なしと不明と材料なしを書き分ける()
    {
        TradeDecisionPromptBuilder.PortfolioSection(new PortfolioHoldingsContext([])).Should().Contain(TradeDecisionPromptBuilder.PortfolioNoneLine);
        TradeDecisionPromptBuilder.PortfolioSection(new PortfolioHoldingsContext(null)).Should().Contain(TradeDecisionPromptBuilder.PortfolioUnknownLine)
            .And.NotContain(TradeDecisionPromptBuilder.PortfolioNoneLine);
        TradeDecisionPromptBuilder.PortfolioSection(null).Should().BeEmpty();
    }

    // 縮退の見積り: 並べる上限いっぱい（桁の多い数量・長い銘柄）でも保護分の予約に収まり、超えた分は件数で書く。
    [Fact]
    public void T_10_2942_最悪長は縮退の保護分の予約に収まる()
    {
        var holdings = Enumerable.Range(0, 15)
            .Select(i => new HeldHolding($"SYMBOL{i:00}.AB", Market.UnitedStates, -9_999_999))
            .ToList();

        var section = TradeDecisionPromptBuilder.PortfolioSection(new PortfolioHoldingsContext(holdings));

        section.Length.Should().BeLessThanOrEqualTo(ScreeningContextAssembler.PortfolioReserveChars);
        section.Should().Contain("- ほか 5 銘柄");
    }

    [Fact]
    public async Task T_10_2943_本判断と一次の両方に他銘柄の保有が載る()
    {
        var llm = new RecordingLlm();
        var service = new AppSvc(llm, new FakePolicy(), new FakeSizing(), new FakeClock(), NullLogger<AppSvc>.Instance,
            options: DecisionOrchestrationOptions.Default with { EnableScreening = true },
            currentPrice: new FakeCurrentPrice(new CurrentPriceReading(300m, IntradayPriceContext.Unknown)),
            holdings: new FakeHoldings([new HeldHolding("MARA", Market.UnitedStates, 150)]));

        await service.DecideAsync(DecisionTrigger.Scheduled("COIN", Market.UnitedStates, Now), TestContext.Current.CancellationToken);

        llm.Prompts.Should().HaveCount(2, "一次＋本判断");
        llm.Prompts.Should().OnlyContain(p => p.Contains("- MARA（米国）: ロング 150 株"));
    }

    [Fact]
    public async Task T_10_2943_照会できなければ不明と書き判断は止めない()
    {
        var llm = new RecordingLlm();
        var service = new AppSvc(llm, new FakePolicy(), new FakeSizing(), new FakeClock(), NullLogger<AppSvc>.Instance,
            currentPrice: new FakeCurrentPrice(new CurrentPriceReading(300m, IntradayPriceContext.Unknown)),
            holdings: new FakeHoldings(null));

        await service.DecideAsync(DecisionTrigger.Scheduled("COIN", Market.UnitedStates, Now), TestContext.Current.CancellationToken);

        llm.Prompts.Should().ContainSingle().Which.Should().Contain(TradeDecisionPromptBuilder.PortfolioUnknownLine);
    }

    [Fact]
    public void T_10_2943_応答の解釈は数量を符号付きで畳み0を除く()
    {
        var rows = new List<HttpHeldPositionProvider.OpenPositionDto>
        {
            new("MARA", Market.UnitedStates, TradeSide.Buy, 150, 20m, null),
            new("NVDA", Market.UnitedStates, TradeSide.Sell, 10, 180m, null),
        };

        HttpHeldPositionProvider.InterpretHoldings(rows, NullLogger.Instance).Should().Equal(
            new HeldHolding("MARA", Market.UnitedStates, 150), new HeldHolding("NVDA", Market.UnitedStates, -10));
        HttpHeldPositionProvider.InterpretHoldings(
            [new HttpHeldPositionProvider.OpenPositionDto("MARA", Market.UnitedStates, null, 150, 20m, null)], NullLogger.Instance).Should().BeNull();
    }

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class FakeHoldings(IReadOnlyList<HeldHolding>? holdings) : IHeldSymbolsProvider
    {
        public Task<IReadOnlyList<WatchedSymbol>?> GetHeldSymbolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WatchedSymbol>?>(holdings?.Select(h => new WatchedSymbol(h.Symbol, h.Market)).ToList());

        public Task<IReadOnlyList<HeldHolding>?> GetHoldingsAsync(CancellationToken ct = default) => Task.FromResult(holdings);
    }

    private sealed class RecordingLlm : ILlmCompletionClient
    {
        public List<string> Prompts { get; } = [];

        public Task<string> CompleteAsync(string prompt, string? model = null, string? purpose = null, CancellationToken ct = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult("""{"action":"Buy","rationale":"押し目","referencePrice":300,"stopLossDistancePerShare":10}""");
        }
    }

    private sealed class FakePolicy : IDailyPolicyProvider
    {
        public Task<DailyPolicy?> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult<DailyPolicy?>(Policy);
    }

    private sealed class FakeSizing : ISizingContextProvider
    {
        public Task<SizingContext> GetContextAsync(CancellationToken ct = default) => Task.FromResult(Context);
    }

    private sealed class FakeCurrentPrice(CurrentPriceReading? reading) : ICurrentPriceProvider
    {
        public bool IsEnabled => true;

        public Task<CurrentPriceReading?> GetCurrentPriceAsync(DecisionTrigger trigger, CancellationToken ct = default) =>
            Task.FromResult(reading);
    }
}
