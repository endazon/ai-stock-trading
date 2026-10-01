extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static TradeDecisionService.Tests.DailyBarsTestData;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;

namespace TradeDecisionService.Tests;

// FR-04, FR-02, ADR-0048 決定 2・3, ADR-0020, #1118, IADR-0467 決定 4・6: 判断のプロンプトへ前営業日の出来高と 20 日平均比を渡す。
// 🔴 既定（無効）は要求 0 回・プロンプトは従来の「出来高: 未提供」の行と一字一句同じ。取得できないことで判断を止めない。
public class DailyVolumeInPromptTests
{
    private static readonly DailyPolicy Policy = new(Tuesday, "AAPL は押し目で新規買いを検討する。");

    private static readonly SizingContext Context =
        new(100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());

    private static readonly IntradayPriceContext Known = new(100m, 103m, 104m, 99.5m);

    private static readonly DailyVolumeContext Full = new(Monday, 12_345_678, 10_000_000m, 1.2345678m);

    private static DecisionTrigger ScheduledAapl() => DecisionTrigger.Scheduled("AAPL", Market.UnitedStates, TuesdayMorning);

    // ---- T-10-1834: 出来高の行（値あり／比は不明／取得できない）と最悪長 ----
    [Fact]
    public void 値ありなら前営業日の出来高と20日平均比をシステムの計算として出す()
    {
        var line = TradeDecisionPromptBuilder.VolumeLine(Full);

        line.Should().Be(
            "出来高: 前営業日（2026-09-28）の確定値 12345678 株 / 20 日平均比: 1.23 倍（前営業日までの確定した日足 20 本の単純平均 "
            + "10000000 株に対する比）。出来高と比はシステムが日足（分割調整済み）から計算した値です。当日の出来高は含みません");
    }

    [Fact]
    public void 比が計算できなければ比だけ不明と書き0を書かない()
    {
        var line = TradeDecisionPromptBuilder.VolumeLine(new DailyVolumeContext(Monday, 5_000, null, null));

        line.Should().Contain("確定値 5000 株");
        line.Should().Contain($"20 日平均比: {TradeDecisionPromptBuilder.VolumeRatioUnknownText}");
        line.Should().NotContain("0.00 倍");
    }

    [Fact]
    public void 取得できなければ未提供と書き_無効の構成の文とは別の文にする()
    {
        TradeDecisionPromptBuilder.VolumeLine(DailyVolumeContext.Unavailable).Should().Be(TradeDecisionPromptBuilder.VolumeUnavailableLine);
        TradeDecisionPromptBuilder.VolumeUnavailableLine.Should().StartWith("出来高: 未提供")
            .And.NotBe(TradeDecisionPromptBuilder.VolumeNotProvidedLine);
        TradeDecisionPromptBuilder.VolumeLine(null).Should().Be(TradeDecisionPromptBuilder.VolumeNotProvidedLine);
    }

    [Fact]
    public void 本判断の定時_急変と一次に同じ出来高の行が出る()
    {
        var movement = DecisionTrigger.FromPriceMovement(
            new AiStockTrading.Shared.Contracts.Events.PriceMovementDetected(
                Guid.NewGuid(), "AAPL", Market.UnitedStates, 105m, 100m, 0.05m, TuesdayMorning));
        var expected = $"- {TradeDecisionPromptBuilder.VolumeLine(Full)}";

        TradeDecisionPromptBuilder.Build(ScheduledAapl(), Policy, Context, currentPrice: 102m, intraday: Known, volume: Full)
            .Should().Contain(expected).And.NotContain(TradeDecisionPromptBuilder.VolumeNotProvidedLine);
        TradeDecisionPromptBuilder.Build(movement, Policy, Context, currentPrice: 102m, intraday: Known, volume: Full)
            .Should().Contain(expected);
        TradeDecisionPromptBuilder.BuildScreening(ScheduledAapl(), Policy, Context, currentPrice: 102m, intraday: Known, volume: Full)
            .Should().Contain(expected);
    }

    // 縮退の見積り: 出来高の値の行を含む値動きの行の最悪長（桁の多い価格・通貨表記・13 桁の出来高）が保護分の予約に収まる。
    [Fact]
    public void 出来高の値の行を含む値動きの行の最悪長は縮退の保護分の予約に収まる()
    {
        var longest = new IntradayPriceContext(1m, 1m, 999_999_999.999999m, 999_999_999.999999m);
        var volume = new DailyVolumeContext(Monday, 9_999_999_999_999, 9_999_999_999_999m, 99_999.99m);
        var lines = new[]
        {
            TradeDecisionPromptBuilder.PriceContextLines(999_999_999.999999m, longest, " JPY", volume),
            TradeDecisionPromptBuilder.PriceContextLines(999_999_999.999999m, longest, " JPY", DailyVolumeContext.Unavailable),
            TradeDecisionPromptBuilder.PriceContextLines(999_999_999.999999m, longest, " JPY", new DailyVolumeContext(Monday, 9_999_999_999_999, null, null)),
        };

        lines.Should().OnlyContain(l => l.Length <= ScreeningContextAssembler.PriceContextReserveChars);
    }

    // ---- T-10-1833: 無効（既定）は要求 0 回・プロンプトは従来と一字一句同じ ----
    [Fact]
    public async Task 無効の構成は日足を要求せずプロンプトは従来と同じ()
    {
        var llm = new RecordingLlm();
        var bars = new CountingProvider(enabled: false, _ => Confirmed(Bars(Monday, Repeat(9_000, 21))));
        var withNoOp = new RecordingLlm();

        await Service(llm, bars).DecideAsync(ScheduledAapl());
        await Service(withNoOp, dailyBars: null).DecideAsync(ScheduledAapl());

        bars.Calls.Should().Be(0, "無効なら取得枠に触れない");
        llm.Prompts.Should().Equal(withNoOp.Prompts, "無効の構成のプロンプトは未指定（従来）と一字一句同じ");
        llm.Prompts.Should().HaveCount(2).And.OnlyContain(p => p.Contains($"- {TradeDecisionPromptBuilder.VolumeNotProvidedLine}"));
    }

    // ---- T-10-1835: 有効なら 1 回引いて一次・本判断へ載せる／見送る判断では引かない／失敗で判断を止めない ----
    [Fact]
    public async Task 有効なら日足を1回引いて一次と本判断の両方へ出来高を渡す()
    {
        var llm = new RecordingLlm();
        var bars = new CountingProvider(enabled: true, _ => Confirmed(Bars(Monday, Then(Repeat(1_000, 19), 2_000))));

        var decision = await Service(llm, bars).DecideAsync(ScheduledAapl());

        decision.Should().NotBeNull();
        bars.Calls.Should().Be(1);
        llm.Prompts.Should().HaveCount(2).And.OnlyContain(p =>
            p.Contains("出来高: 前営業日（2026-09-28）の確定値 2000 株 / 20 日平均比: 1.90 倍"));
    }

    [Fact]
    public async Task 日報の方針が無く見送る判断では日足を引かない()
    {
        var bars = new CountingProvider(enabled: true, _ => Confirmed(Bars(Monday, Repeat(9_000, 21))));
        var service = new AppSvc(
            new RecordingLlm(), new NoPolicy(), new FakeSizing(), new FakeClock(), NullLogger<AppSvc>.Instance,
            options: DecisionOrchestrationOptions.Default with { EnableScreening = true },
            currentPrice: new FakeCurrentPrice(), dailyBars: bars);

        (await service.DecideAsync(ScheduledAapl())).Should().BeNull();
        bars.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("例外")]
    public async Task 取得できなくても判断は止めず出来高は未提供と書く(string kind)
    {
        var llm = new RecordingLlm();
        var bars = new CountingProvider(enabled: true,
            _ => kind == "例外" ? throw new InvalidOperationException("boom") : null);

        var decision = await Service(llm, bars).DecideAsync(ScheduledAapl());

        decision.Should().NotBeNull("K 線が取れないことを理由に判断を止めない（ADR-0048 決定 2）");
        llm.Prompts.Should().HaveCount(2).And.OnlyContain(p => p.Contains($"- {TradeDecisionPromptBuilder.VolumeUnavailableLine}"));
    }

    [Fact]
    public async Task 判断のキャンセルは伝える()
    {
        using var cts = new CancellationTokenSource();
        var bars = new CountingProvider(enabled: true, _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        var act = () => Service(new RecordingLlm(), bars).DecideAsync(ScheduledAapl(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static AppSvc Service(RecordingLlm llm, IDailyBarsProvider? dailyBars) =>
        new(llm, new FakePolicy(), new FakeSizing(), new FakeClock(), NullLogger<AppSvc>.Instance,
            options: DecisionOrchestrationOptions.Default with { EnableScreening = true },
            currentPrice: new FakeCurrentPrice(), dailyBars: dailyBars);

    private sealed class CountingProvider(bool enabled, Func<string, ConfirmedDailyBars?> answer) : IDailyBarsProvider
    {
        public int Calls { get; private set; }

        public bool IsEnabled => enabled;

        public Task<ConfirmedDailyBars?> GetConfirmedBarsAsync(string symbol, Market market, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(answer(symbol));
        }

        public Task<ConfirmedDailyBars?> GetConfirmedBarsAsOfAsync(
            string symbol, Market market, DateOnly tradingDay, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("本番の判断は as-of の口を呼ばない");
    }

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => TuesdayMorning; }

    private sealed class RecordingLlm : ILlmCompletionClient
    {
        public List<string> Prompts { get; } = [];

        public Task<string> CompleteAsync(string prompt, string? model = null, string? purpose = null, CancellationToken ct = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult("""{"action":"Buy","rationale":"押し目","referencePrice":102,"stopLossDistancePerShare":2}""");
        }
    }

    private sealed class FakePolicy : IDailyPolicyProvider
    {
        public Task<DailyPolicy?> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult<DailyPolicy?>(Policy);
    }

    private sealed class NoPolicy : IDailyPolicyProvider
    {
        public Task<DailyPolicy?> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult<DailyPolicy?>(null);
    }

    private sealed class FakeSizing : ISizingContextProvider
    {
        public Task<SizingContext> GetContextAsync(CancellationToken ct = default) => Task.FromResult(Context);
    }

    private sealed class FakeCurrentPrice : ICurrentPriceProvider
    {
        public bool IsEnabled => true;

        public Task<CurrentPriceReading?> GetCurrentPriceAsync(DecisionTrigger trigger, CancellationToken ct = default) =>
            Task.FromResult<CurrentPriceReading?>(new CurrentPriceReading(102m, Known));
    }
}
