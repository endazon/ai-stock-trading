extern alias RiskManagementWorker;

using AiStockTrading.Shared.Contracts.Backtest;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Domain;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;
using TradeDecisionService.Infrastructure.ExternalServices;
using Xunit;
using static TradeDecisionService.Tests.DailyBarsTestData;
using FakeSource = TradeDecisionService.Tests.CachedDailyBarsProviderTests.FakeSource;

namespace TradeDecisionService.Tests.Features.TradeDecision.RecordStage0Decisions;

// FR-10, FR-15, ADR-0049 決定2, #1122, IADR-0486 決定4: Stage 0 の記録も、判断時点（AsOf）の前営業日までの確定足から本番と同じ計算で
// 損切り幅の下限（1.0 × ATR(14)）を求め、サイジングとプロンプトに使う。無効なら要求 0 回で従来（参照価格の 2%・プロンプトは同じ）。
public class Stage0StopWidthFloorTests
{
    // AsOf = 2026-09-29（火）。前営業日 = 2026-09-28（月）。
    private static readonly DateOnly AsOf = Tuesday;

    private static readonly DateTimeOffset FarFuture = new(2026, 12, 1, 15, 0, 0, TimeSpan.Zero);

    // 縮小係数 0.25（5 連敗 × DD 5% 以上）で 1 取引リスク側が効く構成: 予算 100,000 × 1% × 0.25 ＝ 250。
    private static readonly SizingContext Shrunk =
        new(100_000m, 50_000m, 20_000m, 5, 0.06m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());

    // 前営業日で終わる 15 本（TR ＝ 2.5 → ATR 2.5）。AsOf 当日（境界ちょうど）に巨大な値幅の足を混ぜる（先読みすれば値が変わる）。
    private static List<DailyBar> AtrBars(decimal range = 2.5m) =>
    [
        .. Bars(Monday, Repeat(1_000, 15)).Select(b => b with { Open = 100m, High = 100m + range / 2, Low = 100m - range / 2, Close = 100m }),
        new DailyBar(AsOf, 100m, 400m, 10m, 100m, 1),
    ];

    private static CachedDailyBarsProvider Cached(IDailyBarsSource source, DateTimeOffset now) =>
        new(source, new ManualTimeProvider(now), NullLogger<CachedDailyBarsProvider>.Instance);

    private static Atr14StopWidthFloorSource Atr(IDailyBarsProvider bars) =>
        new(bars, NullLogger<Atr14StopWidthFloorSource>.Instance);

    // T-10-2198, IADR-0486 決定4: 有効なら Stage 0 の下限は AsOf の前営業日までの確定足の ATR（2.5）。AI の幅 0.5 → 2.5 で 250 ÷ 2.5 ＝ 100 株
    // （2% の退避なら 125 株）。プロンプトには本番と同じ下限の行が載る。AsOf 当日の足（値幅 390）は使わない。各票の生の幅は AI の値のまま。
    [Fact]
    public async Task T_10_2198_有効ならStage0もATRの下限でサイジングしプロンプトに書く()
    {
        var source = new FakeSource(_ => AtrBars());
        using var bars = Cached(source, FarFuture);
        var (recorder, llm, sink) = Build(inner => Decorate(inner, Atr(bars)));

        await recorder.RunAsync(Options(), TestContext.Current.CancellationToken);

        var record = sink.Saved!.Records.Should().ContainSingle().Subject;
        record.SignedQuantity.Should().Be(100, "250 ÷ ATR 2.5");
        record.RawDecisions[0].StopLossDistancePerShare.Should().Be(0.5m, "生の判断は AI の幅のまま");
        llm.Prompts.Should().ContainSingle().Which.Should().Contain("- 損切り幅の下限: 2.5（1 株あたり。");
        source.Requests.Should().ContainSingle().Which.To.Should().Be(Monday, "AsOf の前日まで");
    }

    // T-10-2198: 🔴 無効なら要求 0 回。下限は記録の参照価格の 2%（125 株）で、プロンプトはデコレータの無い組み立てと一字一句同じ。
    [Fact]
    public async Task T_10_2198_無効なら要求せず2パーセントでプロンプトは従来と同じ()
    {
        var source = new FakeSource(_ => throw new InvalidOperationException("呼ばない"));
        using var bars = Cached(source, FarFuture);
        var (withDecorator, llmA, sinkA) = Build(inner => Decorate(inner, new NoAtrStopWidthFloorSource()));
        var (baseline, llmB, sinkB) = Build(inner => inner);
        var ct = TestContext.Current.CancellationToken;

        await withDecorator.RunAsync(Options(), ct);
        await baseline.RunAsync(Options(), ct);

        source.Requests.Should().BeEmpty();
        sinkA.Saved!.Records[0].SignedQuantity.Should().Be(125, "250 ÷ 2（参照価格 100 の 2%）");
        llmA.Prompts.Should().Equal(llmB.Prompts);
        llmA.Prompts.Single().Should().NotContain("損切り幅の下限:");
        sinkA.Saved.Records[0].InputFingerprint.Should().Be(sinkB.Saved!.Records[0].InputFingerprint);
    }

    // T-10-2198: 有効でも得られない（取れない・古い・足りない・例外）なら 2% へ退避し（125 株）、プロンプトは「未提供」の行。記録は止めない。
    [Theory]
    [InlineData("null")]
    [InlineData("stale")]
    [InlineData("short")]
    [InlineData("throw")]
    public async Task T_10_2198_得られない日は2パーセントで未提供と書く(string kind)
    {
        var source = new FakeSource(_ => kind switch
        {
            "null" => null,
            "stale" => Bars(new DateOnly(2026, 9, 25), Repeat(1_000, 15)),
            "short" => AtrBars().Skip(2).ToList(),
            _ => throw new HttpRequestException("down"),
        });
        using var bars = Cached(source, FarFuture);
        var (recorder, llm, sink) = Build(inner => Decorate(inner, Atr(bars)));

        var outcome = await recorder.RunAsync(Options(), TestContext.Current.CancellationToken);

        outcome.Status.Should().Be(Stage0RecordingStatus.Completed);
        sink.Saved!.Records[0].SignedQuantity.Should().Be(125);
        llm.Prompts.Single().Should().Contain($"- {TradeDecisionPromptBuilder.StopWidthFloorAtrUnavailableLine}");
    }

    // T-10-2198: デコレータは口の例外を「未提供」にし（キャンセルは伝える）、内側が既に下限を渡していれば読まない。組み直しで他の入力を落とさない。
    [Fact]
    public async Task T_10_2198_デコレータは例外を未提供にし既存の値を上書きしない()
    {
        var ct = TestContext.Current.CancellationToken;
        var throwing = new ScriptedFloor(_ => throw new InvalidOperationException("boom"));
        var decorated = await new StopWidthFloorAsOfDecisionInputProvider(
                new StubInputs(), throwing, NullLogger<StopWidthFloorAsOfDecisionInputProvider>.Instance)
            .GetAsync("AAPL", Market.UnitedStates, AsOf, ct);
        decorated!.StopFloor.Should().Be(StopWidthFloorContext.Unavailable);
        throwing.AsOfDays.Should().Equal(AsOf);

        var preset = new StopWidthFloorContext(new StopWidthFloor(9m, StopWidthFloorSource.Atr14, 9m));
        var counting = new ScriptedFloor(_ => throw new InvalidOperationException("呼ばない"));
        (await new StopWidthFloorAsOfDecisionInputProvider(new StubInputs(preset), counting, NullLogger<StopWidthFloorAsOfDecisionInputProvider>.Instance)
            .GetAsync("AAPL", Market.UnitedStates, AsOf, ct))!.StopFloor.Should().Be(preset);
        counting.AsOfDays.Should().BeEmpty();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancelling = new ScriptedFloor(_ => throw new OperationCanceledException(cts.Token));
        await new StopWidthFloorAsOfDecisionInputProvider(new StubInputs(), cancelling, NullLogger<StopWidthFloorAsOfDecisionInputProvider>.Instance)
            .Invoking(d => d.GetAsync("AAPL", Market.UnitedStates, AsOf, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        // 組み直し: 下限・出来高・監視銘柄はそれぞれの With で落ちない。
        var volume = new DailyVolumeContext(Monday, 2_000, 1_050m, 2_000m / 1_050m);
        var watched = new List<WatchedSymbol> { new("META", Market.UnitedStates) };
        var full = Input(preset).WithVolume(volume).WithWatchlist(watched, null);
        full.StopFloor.Should().Be(preset);
        full.Volume.Should().Be(volume);
        Input().WithStopFloor(preset).WithVolume(volume).StopFloor.Should().Be(preset);
        Input(watchlist: watched).WithStopFloor(preset).Watchlist.Should().Equal(watched);
    }

    // T-10-2199, ADR-0049 決定2「Stage 0 では同じ値を計算する」, IADR-0486 決定4, IADR-0479 の同じ口の原則: 本番（取引日 D の場中）の下限と
    // as-of（AsOf = D）の下限は、同じ取得元から同じ値になる（期間・切り方・計算を共有する）。
    [Fact]
    public async Task T_10_2199_本番の下限とasofの下限は同じ値()
    {
        var source = new FakeSource(_ => AtrBars(1.7m));
        using var production = Cached(source, TuesdayMorning);
        using var replay = Cached(source, FarFuture);
        var ct = TestContext.Current.CancellationToken;

        var live = await Atr(production).GetFloorAsync("AAPL", Market.UnitedStates, ct);
        var asOf = await Atr(replay).GetFloorAsOfAsync("AAPL", Market.UnitedStates, AsOf, ct);

        live.Should().Be(new StopWidthFloor(1.7m, StopWidthFloorSource.Atr14, 1.7m));
        asOf.Should().Be(live);
        source.Requests.Should().HaveCount(2);
        source.Requests[1].Should().Be(source.Requests[0], "期間の求め方を共有する");
    }

    // ------------------------------------------------------------------------------------------------

    private static IAsOfDecisionInputProvider Decorate(IAsOfDecisionInputProvider inner, IStopWidthFloorSource floor) =>
        new StopWidthFloorAsOfDecisionInputProvider(inner, floor, NullLogger<StopWidthFloorAsOfDecisionInputProvider>.Instance);

    private static AsOfDecisionInput Input(StopWidthFloorContext? stopFloor = null, IReadOnlyList<WatchedSymbol>? watchlist = null) =>
        new(AsOf,
            new DailyPolicy(AsOf, "当日の方針"),
            Shrunk,
            new DatedPrice(AsOf, 100m),
            watchlist: watchlist,
            previousClose: new DatedPrice(Monday, 99m),
            stopFloor: stopFloor);

    private static Stage0RecordingOptions Options() => new()
    {
        Enabled = true,
        From = "2026-09-29",
        To = "2026-09-29",
        LlmTrainingCutoff = "2026-03-31",
        Symbols = [new Stage0RecordingOptions.SymbolEntry { Symbol = "AAPL", Market = Market.UnitedStates }],
        VoteCount = 1,
        DecisionsPerDay = 1,
        InputTokensPerDecision = 1_000,
        OutputTokensPerDecision = 1_000,
        // #1196, IADR-0498: 記録は二段（一次 1 回＋本判断 1 回）で走る。見積りは 1 判断時点 ×（1 ＋ 1）× 2 円。
        ScreeningInputTokensPerDecision = 1_000,
        ScreeningOutputTokensPerDecision = 1_000,
        ApprovedVoteCount = 1,
        ApprovedEstimateJpy = 4m,
        OutputPath = "records.json",
        Model = "claude-sonnet-5",
    };

    private static (Stage0DecisionRecorder Recorder, FakeLlm Llm, CapturingSink Sink) Build(
        Func<IAsOfDecisionInputProvider, IAsOfDecisionInputProvider> wrap)
    {
        var collector = new Stage0RecordingUsageCollector(new NullReporter());
        var llm = new FakeLlm(collector);
        var sink = new CapturingSink();
        var recorder = new Stage0DecisionRecorder(
            llm, DecisionOrchestrationOptions.Default, wrap(new StubInputs()), sink, collector,
            LlmPriceTable.From([("claude-sonnet-5", "1", "1")], "1", "1"),
            new ManualTimeProvider(FarFuture), NullLogger<Stage0DecisionRecorder>.Instance);
        return (recorder, llm, sink);
    }

    private sealed class StubInputs(StopWidthFloorContext? stopFloor = null) : IAsOfDecisionInputProvider
    {
        public Task<AsOfDecisionInput?> GetAsync(
            string symbol, Market market, DateOnly asOf, CancellationToken cancellationToken = default) =>
            Task.FromResult<AsOfDecisionInput?>(Input(stopFloor));
    }

    private sealed class ScriptedFloor(Func<DateOnly, StopWidthFloor?> asOfAnswer) : IStopWidthFloorSource
    {
        public List<DateOnly> AsOfDays { get; } = [];

        public bool IsEnabled => true;

        public ValueTask<StopWidthFloor?> GetFloorAsync(string symbol, Market market, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Stage 0 は本番の今日の口を呼ばない");

        public ValueTask<StopWidthFloor?> GetFloorAsOfAsync(
            string symbol, Market market, DateOnly tradingDay, CancellationToken cancellationToken = default)
        {
            AsOfDays.Add(tradingDay);
            return ValueTask.FromResult(asOfAnswer(tradingDay));
        }
    }

    private sealed class FakeLlm(Stage0RecordingUsageCollector usage) : ILlmCompletionClient
    {
        public List<string> Prompts { get; } = [];

        public async Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken cancellationToken = default)
        {
            // #1196, IADR-0498: 記録は二段で走る。一次は関心あり（本判断へ進める）を返し、本判断のプロンプトだけを集める。
            await usage.ReportAsync(new LlmUsage(purpose ?? LlmPurposes.TradeDecision, 1_000, 1_000, model), cancellationToken);
            if (purpose == LlmPurposes.TradeDecisionScreening)
                return """{"action":"Buy","rationale":"関心あり"}""";
            Prompts.Add(prompt);
            return """{"action":"Buy","rationale":"根拠","referencePrice":100,"stopLossDistancePerShare":0.5}""";
        }
    }

    private sealed class CapturingSink : IStage0DecisionRecordSink
    {
        public Stage0DecisionRecordSet? Saved { get; private set; }

        public Task<bool> SaveAsync(Stage0DecisionRecordSet recordSet, CancellationToken cancellationToken = default)
        {
            Saved = recordSet;
            return Task.FromResult(true);
        }
    }

    private sealed class NullReporter : ILlmUsageReporter
    {
        public Task ReportAsync(LlmUsage usage, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
