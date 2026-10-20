extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;

namespace TradeDecisionService.Tests;

// T-10-2932, FR-04, FR-10, #1396, IADR-0561 決定 5: 取引判断のプロンプト（本判断・一次）に市場の現地時刻と閉場までの残り時間を渡す。
// 実測（PoC 2026-10-19）: プロンプトに時刻が無く、方針の「取引終盤は新規建てを控えて手仕舞いを優先する」を AI が評価できなかった。実 LLM は呼ばない。
public class MarketClockInPromptTests
{
    // 2026-10-19 15:02 ET（閉場の 58 分前）＝19:02 UTC。
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 19, 2, 0, TimeSpan.Zero);

    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 10, 19), "取引終盤は新規建てを控えて手仕舞いを優先する。");

    private static readonly SizingContext Context =
        new(100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());

    private static string Line(Market market, DateTimeOffset now) =>
        TradeDecisionPromptBuilder.MarketClockLine(
            new MarketClockContext(market, now, AiStockTrading.Shared.Kernel.Trading.MarketHours.SessionCloseOn(market, now)));

    [Fact]
    public void T_10_2932_米国の現地時刻と閉場までの残り分を書く()
    {
        Line(Market.UnitedStates, Now).Should().Be(
            $"{TradeDecisionPromptBuilder.MarketClockLinePrefix}2026-10-19 15:02（米国東部時間） / 本日の閉場: 16:00（残り 58 分）{Environment.NewLine}");
    }

    [Fact]
    public void T_10_2932_半日取引日は前倒しの閉場を書く()
    {
        Line(Market.UnitedStates, new DateTimeOffset(2026, 11, 27, 17, 30, 0, TimeSpan.Zero))
            .Should().Contain("12:30（米国東部時間） / 本日の閉場: 13:00（残り 30 分）");
    }

    [Fact]
    public void T_10_2932_休場日と閉場の後と東証()
    {
        Line(Market.UnitedStates, new DateTimeOffset(2026, 11, 26, 18, 0, 0, TimeSpan.Zero)).Should().Contain("本日の閉場: なし（休場日）");
        Line(Market.UnitedStates, new DateTimeOffset(2026, 10, 19, 20, 30, 0, TimeSpan.Zero)).Should().Contain("本日の閉場: 16:00（閉場後）");
        Line(Market.Japan, new DateTimeOffset(2026, 10, 20, 5, 0, 0, TimeSpan.Zero))
            .Should().Contain("2026-10-20 14:00（日本時間） / 本日の閉場: 15:30（残り 90 分）");
    }

    [Fact]
    public void T_10_2932_材料が無ければ行を出さない()
    {
        TradeDecisionPromptBuilder.MarketClockLine(null).Should().BeEmpty();
    }

    // 縮退の見積り: 行の最長が保護分の予約に収まる。
    [Fact]
    public void T_10_2932_行の最長は縮退の保護分の予約に収まる()
    {
        var longest = new[]
        {
            Line(Market.UnitedStates, new DateTimeOffset(2026, 10, 19, 13, 30, 0, TimeSpan.Zero)),
            Line(Market.UnitedStates, new DateTimeOffset(2026, 11, 26, 18, 0, 0, TimeSpan.Zero)),
            Line(Market.Japan, new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero)),
            Line(Market.UnitedStates, new DateTimeOffset(2026, 10, 19, 23, 0, 0, TimeSpan.Zero)),
        }.Max(l => l.Length);

        longest.Should().BeLessThanOrEqualTo(ScreeningContextAssembler.MarketClockReserveChars);
    }

    [Fact]
    public async Task T_10_2932_本判断と一次の両方に同じ行が載る()
    {
        var llm = new RecordingLlm();
        var service = new AppSvc(llm, new FakePolicy(), new FakeSizing(), new FakeClock(), NullLogger<AppSvc>.Instance,
            options: DecisionOrchestrationOptions.Default with { EnableScreening = true },
            currentPrice: new FakeCurrentPrice(new CurrentPriceReading(20m, IntradayPriceContext.Unknown)));

        await service.DecideAsync(DecisionTrigger.Scheduled("MARA", Market.UnitedStates, Now), TestContext.Current.CancellationToken);

        llm.Prompts.Should().HaveCount(2, "一次＋本判断");
        llm.Prompts.Should().OnlyContain(p => p.Contains("2026-10-19 15:02（米国東部時間） / 本日の閉場: 16:00（残り 58 分）"));
    }

    [Fact]
    public async Task T_10_2932_構成の臨時の半日取引日は市場カレンダーから引く()
    {
        var llm = new RecordingLlm();
        var calendar = new TradeDecisionService.Infrastructure.ExternalServices.MarketCalendar(
            new Dictionary<Market, IReadOnlySet<DateOnly>>(),
            new Dictionary<Market, IReadOnlySet<DateOnly>> { [Market.UnitedStates] = new HashSet<DateOnly> { new(2026, 10, 19) } });
        var service = new AppSvc(llm, new FakePolicy(), new FakeSizing(), new FakeClock(), NullLogger<AppSvc>.Instance,
            currentPrice: new FakeCurrentPrice(new CurrentPriceReading(20m, IntradayPriceContext.Unknown)), marketCalendar: calendar);

        await service.DecideAsync(DecisionTrigger.Scheduled("MARA", Market.UnitedStates, Now), TestContext.Current.CancellationToken);

        llm.Prompts.Should().OnlyContain(p => p.Contains("本日の閉場: 13:00（閉場後）"));
    }

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class RecordingLlm : ILlmCompletionClient
    {
        public List<string> Prompts { get; } = [];

        public Task<string> CompleteAsync(string prompt, string? model = null, string? purpose = null, CancellationToken ct = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult("""{"action":"Buy","rationale":"押し目","referencePrice":20,"stopLossDistancePerShare":1}""");
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
