extern alias RiskManagementWorker;

using System.Security.Cryptography;
using System.Text;
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

// FR-04, FR-15, ADR-0048 決定 2, #1139, IADR-0479: Stage 0 の記録へも、判断時点（AsOf）の前営業日までの確定足から
// 本番と同じ計算で出来高と 20 日平均比を渡す。無効なら要求 0 回で、プロンプト・指紋は従来と一字一句同じ。
// 🔴 窓の両端（作業仕様書の窓の表）: AsOf 当日（境界ちょうど）以降の足を使わない（P1）・それでも前営業日の値は出す（P2）・
// 古い足を前日と書かない（P3）・前営業日の足を落とさない（P4）。
public class Stage0DecisionVolumeTests
{
    // AsOf = 2026-09-29（火）。前営業日 = 2026-09-28（月）。
    private static readonly DateOnly AsOf = Tuesday;

    // 本番の「今」とは無関係な時刻（as-of の取得は今の取引日に依らない）。
    private static readonly DateTimeOffset FarFuture = new(2026, 12, 1, 15, 0, 0, TimeSpan.Zero);

    private static CachedDailyBarsProvider Cached(IDailyBarsSource source, DateTimeOffset now) =>
        new(source, new ManualTimeProvider(now), NullLogger<CachedDailyBarsProvider>.Instance);

    // 前営業日 2,000・その前 19 本 1,000 → 平均 1,050・比 1.90。AsOf 当日（境界ちょうど）と翌日に巨大な出来高を混ぜる。
    private static List<DailyBar> BarsWithFuture() =>
    [
        .. Bars(Monday, Then(Repeat(1_000, 19), 2_000)),
        new DailyBar(AsOf, 100m, 100m, 100m, 100m, 77_777_777),
        new DailyBar(AsOf.AddDays(1), 100m, 100m, 100m, 100m, 88_888_888),
    ];

    // ---- T-10-2030: 有効なら Stage 0 のプロンプトに本番と同じ出来高の行が載り、指紋は同じ入力の本番の組み立てと一致する ----
    [Fact]
    public async Task 有効ならStage0のプロンプトと指紋は本番と同じ出来高の2値を持つ()
    {
        var source = new FakeSource(_ => BarsWithFuture());
        using var bars = Cached(source, FarFuture);
        var (recorder, llm, sink) = Build(inner => new DailyVolumeAsOfDecisionInputProvider(
            inner, bars, NullLogger<DailyVolumeAsOfDecisionInputProvider>.Instance));

        var outcome = await recorder.RunAsync(Options(), TestContext.Current.CancellationToken);

        outcome.Status.Should().Be(Stage0RecordingStatus.Completed);
        llm.Prompts.Should().ContainSingle()
            .Which.Should().Contain("出来高: 前営業日（2026-09-28）の確定値 2000 株 / 20 日平均比: 1.90 倍");
        var expectedVolume = DailyVolumeContext.From(new ConfirmedDailyBars(AsOf, Monday, Bars(Monday, Then(Repeat(1_000, 19), 2_000))));
        sink.Saved!.Records[0].InputFingerprint.Should().Be(Fingerprint(ExpectedPrompt(expectedVolume)),
            "本番と同じ組み立て（Build(volume:)）に同じ 2 値を渡したプロンプトである");
    }

    // ---- T-10-2031: 本番（取引日 D の場中）と as-of（AsOf=D）が、同じ取得元から同じ値を作る ----
    [Fact]
    public async Task 本番の取得とasofの取得は同じ取得元から同じ値を作る()
    {
        var source = new FakeSource(_ => BarsWithFuture());
        using var production = Cached(source, TuesdayMorning);
        using var asOf = Cached(source, FarFuture);
        var ct = TestContext.Current.CancellationToken;

        var live = DailyVolumeContext.From(await production.GetConfirmedBarsAsync("AAPL", Market.UnitedStates, ct));
        var replay = DailyVolumeContext.From(await asOf.GetConfirmedBarsAsOfAsync("AAPL", Market.UnitedStates, AsOf, ct));

        replay.Should().Be(live);
        replay.PreviousDayVolume.Should().Be(2_000);
        source.Requests.Should().HaveCount(2);
        source.Requests[1].Should().Be(source.Requests[0], "期間の求め方を共有する");
    }

    // ---- T-10-2032: 先読みなし（AsOf ちょうど・後の足を捨てる）・前営業日の値は出す・要求は前営業日の 45 暦日前〜AsOf の前日 ----
    [Fact]
    public async Task asofちょうどと後の足を使わず前営業日の値を出す()
    {
        var source = new FakeSource(_ => [.. Enumerable.Reverse(BarsWithFuture())]);
        using var provider = Cached(source, FarFuture);

        var confirmed = await provider.GetConfirmedBarsAsOfAsync("AAPL", Market.UnitedStates, AsOf, TestContext.Current.CancellationToken);

        confirmed!.TradingDay.Should().Be(AsOf);
        confirmed.ExpectedPreviousTradingDay.Should().Be(Monday);
        confirmed.Bars.Should().OnlyContain(b => b.Date < AsOf, "AsOf 当日（境界ちょうど）以降の足は判断時点では確定していない");
        confirmed.Bars[^1].Date.Should().Be(Monday, "前営業日の足は落とさない");
        var volume = DailyVolumeContext.From(confirmed);
        volume.PreviousDayVolume.Should().Be(2_000, "AsOf の 77,777,777 を前日として読まない");
        volume.RatioToAverage20.Should().Be(2_000m / 1_050m);
        source.Requests.Should().ContainSingle().Which.Should().Be(
            ("AAPL", Market.UnitedStates, Monday.AddDays(-CachedDailyBarsProvider.LookbackCalendarDays), Monday));
    }

    [Fact]
    public async Task 先読みの足はStage0のプロンプトにも出ない()
    {
        var source = new FakeSource(_ => BarsWithFuture());
        using var bars = Cached(source, FarFuture);
        var (recorder, llm, _) = Build(inner => new DailyVolumeAsOfDecisionInputProvider(
            inner, bars, NullLogger<DailyVolumeAsOfDecisionInputProvider>.Instance));

        await recorder.RunAsync(Options(), TestContext.Current.CancellationToken);

        llm.Prompts.Should().ContainSingle().Which.Should().NotContain("77777777").And.NotContain("88888888");
    }

    // ---- T-10-2033: 型の側で as-of を守る（前営業日が AsOf ちょうど・後なら例外） ----
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void 出来高の前営業日がasof以降なら例外(int daysAfterAsOf)
    {
        var day = AsOf.AddDays(daysAfterAsOf);

        var act = () => Input(volume: new DailyVolumeContext(day, 1_000, null, null));

        act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("volume");
    }

    [Fact]
    public void 出来高の前営業日がasofより前なら受けWithVolumeでも同じ規律()
    {
        Input(volume: new DailyVolumeContext(Monday, 1_000, null, null)).Volume!.PreviousDay.Should().Be(Monday);
        Input(volume: DailyVolumeContext.Unavailable).Volume.Should().Be(DailyVolumeContext.Unavailable);

        var act = () => Input().WithVolume(new DailyVolumeContext(AsOf, 1_000, null, null));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- T-10-2034: 無効なら要求 0 回・プロンプトと指紋はデコレータの無い組み立て（develop）と一字一句同じ ----
    [Fact]
    public async Task 無効なら要求せずプロンプトと指紋は従来と同じ()
    {
        var disabled = new CountingDailyBars(enabled: false, (_, _) => throw new InvalidOperationException("呼ばない"));
        var (withDecorator, llmA, sinkA) = Build(inner => new DailyVolumeAsOfDecisionInputProvider(
            inner, disabled, NullLogger<DailyVolumeAsOfDecisionInputProvider>.Instance));
        var (baseline, llmB, sinkB) = Build(inner => inner);
        var ct = TestContext.Current.CancellationToken;

        await withDecorator.RunAsync(Options(), ct);
        await baseline.RunAsync(Options(), ct);

        disabled.Calls.Should().Be(0);
        llmA.Prompts.Should().Equal(llmB.Prompts);
        llmA.Prompts.Should().ContainSingle().Which.Should().Contain(TradeDecisionPromptBuilder.VolumeNotProvidedLine);
        sinkA.Saved!.Records[0].InputFingerprint.Should().Be(sinkB.Saved!.Records[0].InputFingerprint);
        sinkA.Saved.Records[0].InputFingerprint.Should().Be(Fingerprint(ExpectedPrompt(volume: null)));
        sinkA.Saved.StrategyId.Should().Be(sinkB.Saved.StrategyId);
    }

    [Fact]
    public async Task 既定のNoOpの口もasofの要求をしない()
    {
        (await new NoOpDailyBarsProvider().GetConfirmedBarsAsOfAsync("AAPL", Market.UnitedStates, AsOf, TestContext.Current.CancellationToken))
            .Should().BeNull();
    }

    // ---- T-10-2035: 取れない・足りない・古い・日本株・例外・キャンセル（本番と同じ扱い） ----
    [Theory]
    [InlineData("null")]
    [InlineData("throw")]
    [InlineData("stale")]
    [InlineData("empty")]
    public async Task 取れない日は未提供の別の文で記録を続ける(string answer)
    {
        var source = new FakeSource(_ => answer switch
        {
            "null" => null,
            "throw" => throw new HttpRequestException("down"),
            "stale" => Bars(new DateOnly(2026, 9, 25), Repeat(1_000, 21)), // 最後の足が前営業日（月）より古い
            _ => [],
        });
        using var bars = Cached(source, FarFuture);
        var (recorder, llm, sink) = Build(inner => new DailyVolumeAsOfDecisionInputProvider(
            inner, bars, NullLogger<DailyVolumeAsOfDecisionInputProvider>.Instance));

        var outcome = await recorder.RunAsync(Options(), TestContext.Current.CancellationToken);

        outcome.Status.Should().Be(Stage0RecordingStatus.Completed);
        sink.Saved!.Records.Should().ContainSingle();
        llm.Prompts.Should().ContainSingle().Which.Should().Contain(TradeDecisionPromptBuilder.VolumeUnavailableLine)
            .And.NotContain(TradeDecisionPromptBuilder.VolumeNotProvidedLine);
    }

    [Fact]
    public async Task 口が例外を投げても未提供にして記録を続ける()
    {
        var throwing = new CountingDailyBars(enabled: true, (_, _) => throw new InvalidOperationException("boom"));
        var (recorder, llm, sink) = Build(inner => new DailyVolumeAsOfDecisionInputProvider(
            inner, throwing, NullLogger<DailyVolumeAsOfDecisionInputProvider>.Instance));

        await recorder.RunAsync(Options(), TestContext.Current.CancellationToken);

        throwing.Calls.Should().Be(1);
        sink.Saved!.Records.Should().ContainSingle();
        llm.Prompts.Single().Should().Contain(TradeDecisionPromptBuilder.VolumeUnavailableLine);
    }

    [Fact]
    public async Task 足りなければ出来高は出し比だけ不明()
    {
        var source = new FakeSource(_ => Bars(Monday, Repeat(1_000, 19)));
        using var bars = Cached(source, FarFuture);
        var (recorder, llm, _) = Build(inner => new DailyVolumeAsOfDecisionInputProvider(
            inner, bars, NullLogger<DailyVolumeAsOfDecisionInputProvider>.Instance));

        await recorder.RunAsync(Options(), TestContext.Current.CancellationToken);

        llm.Prompts.Single().Should().Contain("出来高: 前営業日（2026-09-28）の確定値 1000 株 / 20 日平均比: " + TradeDecisionPromptBuilder.VolumeRatioUnknownText);
    }

    [Fact]
    public async Task 日本株は要求せず未提供()
    {
        var source = new FakeSource(_ => throw new InvalidOperationException("呼ばない"));
        using var bars = Cached(source, FarFuture);
        var decorator = new DailyVolumeAsOfDecisionInputProvider(
            new StubInputs(), bars, NullLogger<DailyVolumeAsOfDecisionInputProvider>.Instance);

        var input = await decorator.GetAsync("7203", Market.Japan, AsOf, TestContext.Current.CancellationToken);

        source.Requests.Should().BeEmpty();
        input!.Volume.Should().Be(DailyVolumeContext.Unavailable);
    }

    [Fact]
    public async Task キャンセルは伝える()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancelling = new CountingDailyBars(enabled: true, (_, ct) => throw new OperationCanceledException(ct));
        var decorator = new DailyVolumeAsOfDecisionInputProvider(
            new StubInputs(), cancelling, NullLogger<DailyVolumeAsOfDecisionInputProvider>.Instance);
        var source = new FakeSource(_ => throw new OperationCanceledException(cts.Token));
        using var cached = Cached(source, FarFuture);

        await decorator.Invoking(d => d.GetAsync("AAPL", Market.UnitedStates, AsOf, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        await cached.Invoking(c => c.GetConfirmedBarsAsOfAsync("AAPL", Market.UnitedStates, AsOf, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- T-10-2036: 組み直しで出来高・監視銘柄・理由を落とさない。既に出来高があれば引かない ----
    [Fact]
    public void WithWatchlistは出来高を保ちWithVolumeは監視銘柄と理由と前日終値を保つ()
    {
        var volume = new DailyVolumeContext(Monday, 2_000, 1_050m, 2_000m / 1_050m);
        var watched = new List<WatchedSymbol> { new("META", Market.UnitedStates) };

        Input(volume: volume).WithWatchlist(watched, null).Volume.Should().Be(volume);

        var withReason = Input(watchlistUnavailableReason: "SeededAt より前").WithVolume(volume);
        withReason.Volume.Should().Be(volume);
        withReason.Watchlist.Should().BeNull();
        withReason.AsOfInputs.Should().ContainSingle(s => s.Kind == Stage0AsOfInputKind.Watchlist)
            .Which.Reason.Should().Contain("SeededAt より前");
        withReason.Intraday.PreviousClose.Should().Be(80m);

        Input(watchlist: watched).WithVolume(volume).Watchlist.Should().Equal(watched);
    }

    [Fact]
    public async Task 内側が出来高を渡していれば引かない_監視銘柄のデコレータの外側でも残る()
    {
        var preset = new DailyVolumeContext(Monday, 5, null, null);
        var counting = new CountingDailyBars(enabled: true, (_, _) => throw new InvalidOperationException("呼ばない"));
        var decorator = new DailyVolumeAsOfDecisionInputProvider(
            new StubInputs(preset), counting, NullLogger<DailyVolumeAsOfDecisionInputProvider>.Instance);

        (await decorator.GetAsync("AAPL", Market.UnitedStates, AsOf, TestContext.Current.CancellationToken))!.Volume.Should().Be(preset);
        counting.Calls.Should().Be(0);

        // 本番の組み立ての順（監視銘柄のデコレータが外側）でも、出来高は WithWatchlist の組み直しで落ちない。
        var source = new FakeSource(_ => BarsWithFuture());
        using var bars = Cached(source, FarFuture);
        var chain = new WatchlistAsOfDecisionInputProvider(
            new DailyVolumeAsOfDecisionInputProvider(new StubInputs(), bars, NullLogger<DailyVolumeAsOfDecisionInputProvider>.Instance),
            new FixedWatchlistSource(AsOfWatchlist.Reconstructed([new("META", Market.UnitedStates)])));

        var input = await chain.GetAsync("AAPL", Market.UnitedStates, AsOf, TestContext.Current.CancellationToken);

        input!.Watchlist.Should().ContainSingle();
        input.Volume!.PreviousDayVolume.Should().Be(2_000);
    }

    // ---- T-10-2038: as-of の取得は本番のキャッシュを読まない・書かない ----
    [Fact]
    public async Task asofの取得は本番のキャッシュを読まず書かない()
    {
        var source = new FakeSource(r => Bars(MarketTradingDays.PreviousTradingDay(Market.UnitedStates, r.To.AddDays(1)), Repeat(1_000, 21)));
        using var provider = Cached(source, TuesdayMorning);
        var ct = TestContext.Current.CancellationToken;
        var pastDay = new DateOnly(2026, 9, 15);

        var past = await provider.GetConfirmedBarsAsOfAsync("AAPL", Market.UnitedStates, pastDay, ct);
        var today = await provider.GetConfirmedBarsAsync("AAPL", Market.UnitedStates, ct);
        await provider.GetConfirmedBarsAsOfAsync("AAPL", Market.UnitedStates, pastDay, ct);
        var todayAgain = await provider.GetConfirmedBarsAsync("AAPL", Market.UnitedStates, ct);

        past!.Bars[^1].Date.Should().Be(new DateOnly(2026, 9, 14));
        today!.Bars[^1].Date.Should().Be(Monday, "過去日の取得で今日のキャッシュを埋めない");
        todayAgain.Should().BeSameAs(today, "過去日の取得で今日のキャッシュを置き換えない");
        source.Requests.Select(r => r.To).Should().Equal(
            new DateOnly(2026, 9, 14), Monday, new DateOnly(2026, 9, 14));
    }

    // ------------------------------------------------------------------------------------------------

    private static string Fingerprint(string prompt) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)));

    // 記録器と同じ引数で本番のプロンプト構築を呼ぶ（Stage 0 は保有なし・未約定なし・監視銘柄は不明）。
    private static string ExpectedPrompt(DailyVolumeContext? volume)
    {
        var input = Input();
        return TradeDecisionPromptBuilder.Build(
            DecisionTrigger.Scheduled("AAPL", Market.UnitedStates), input.Policy, input.Sizing, input.References,
            includeProfitability: false, currentPrice: input.ReferencePrice, held: HeldPosition.None,
            working: WorkingEntryOrders.None, watchlist: input.Watchlist, intraday: input.Intraday, volume: volume);
    }

    private static AsOfDecisionInput Input(
        DailyVolumeContext? volume = null,
        IReadOnlyList<WatchedSymbol>? watchlist = null,
        string? watchlistUnavailableReason = null) =>
        new(AsOf,
            new DailyPolicy(AsOf, "当日の方針"),
            new SizingContext(100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits()),
            new DatedPrice(AsOf, 100m),
            watchlist: watchlist,
            watchlistUnavailableReason: watchlistUnavailableReason,
            previousClose: new DatedPrice(Monday, 80m),
            volume: volume);

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
        ApprovedEstimateJpy = 4m, // 銘柄 1 × 平日 1 × 1 ×（一次 1 ＋ 多数決 1）×（1 + 1）円
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

    private sealed class StubInputs(DailyVolumeContext? volume = null) : IAsOfDecisionInputProvider
    {
        public Task<AsOfDecisionInput?> GetAsync(
            string symbol, Market market, DateOnly asOf, CancellationToken cancellationToken = default) =>
            Task.FromResult<AsOfDecisionInput?>(Input(volume: volume));
    }

    private sealed class CountingDailyBars(bool enabled, Func<DateOnly, CancellationToken, ConfirmedDailyBars?> asOfAnswer)
        : IDailyBarsProvider
    {
        public int Calls { get; private set; }

        public bool IsEnabled => enabled;

        public Task<ConfirmedDailyBars?> GetConfirmedBarsAsync(
            string symbol, Market market, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Stage 0 は本番の今日の口を呼ばない");

        public Task<ConfirmedDailyBars?> GetConfirmedBarsAsOfAsync(
            string symbol, Market market, DateOnly tradingDay, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(asOfAnswer(tradingDay, cancellationToken));
        }
    }

    private sealed class FixedWatchlistSource(AsOfWatchlist result) : IAsOfWatchlistSource
    {
        public Task<AsOfWatchlist> GetWatchlistAtAsync(DateTimeOffset at, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
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
            return """{"action":"Hold","rationale":"根拠","referencePrice":100,"stopLossDistancePerShare":2}""";
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
