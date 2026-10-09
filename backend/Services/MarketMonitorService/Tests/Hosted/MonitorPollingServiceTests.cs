using MarketMonitorService.Common.Abstractions;
using MarketMonitorService.Domain;
using MarketMonitorService.Features.MarketMonitor;
using MarketMonitorService.Hosted;
using MarketMonitorService.Infrastructure.ExternalServices;
using MarketMonitorService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AiStockTrading.Shared.Kernel.Trading;
using AiStockTrading.TestSupport.Metrics;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
using AppSvc = MarketMonitorService.Features.MarketMonitor.MarketMonitorAppService;

namespace MarketMonitorService.Tests;

// FR-03, UC-02, ADR-0003: ポーリング巡回（RunOnceAsync）の検証。市場開場時に評価結果を発行し、閉場時は発行しない。
//
// ADR-0013, IADR-0129, #354: MassTransit のテストハーネス（AddMassTransitTestHarness + harness.Published）から
// Wolverine.Tracking（TrackActivity + session.Sent）へ移行した。表明の意味は同じ（巡回を回し、外へ出た／
// 出なかったメッセージを見る）。本番と同じ配線（キュー名・fan-out・再試行・DLQ）を用い、送信先だけ stub へ倒す。
public class MonitorPollingServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 1, 0, 0, TimeSpan.Zero);
    private static readonly MonitoredSymbol Aapl = new("AAPL", Market.UnitedStates);
    private const string ServiceName = "ai-stock-trading.market-monitor-service";

    private sealed class Harness : IAsyncDisposable
    {
        public FakeClock Clock { get; } = new(Now);
        public FakeSchedule Schedule { get; } = new(open: true);
        public FakeMarketDataSource Market { get; } = new();
        public InMemoryMonitoredSymbolStore Settings { get; }
        public InMemoryPositionStore Positions { get; } = new();

        // #1251: 保有の照会を差し替える（null なら Positions）。巡回が例外で抜ける形を作るため。
        public IPositionStore? PositionStore { get; init; }
        public InMemoryPriceBaselineStore Baselines { get; } = new();
        public InMemoryCooldownStore Cooldowns { get; } = new();

        // #902, IADR-0365: 生存要約（null なら配線しない＝従来の構成）。
        public StopLossLivenessReporter? Liveness { get; set; }

        // #1132, IADR-0477: 日次要求見積りの記録器（null なら配線しない＝従来の構成）。
        public FinnhubDailyVolumeRecorder? DailyVolume { get; set; }

        // 巡回の構成（既定 60 秒）。#1132 監査 🟡: 見積りに渡す巡回間隔が構成の値であることを固定する。
        public MonitorOptions MonitorOptions { get; init; } = new();

        // #1251, IADR-0513: 巡回の所要の計量・Warning（null なら配線しない＝従来の構成）。
        public BusinessMetrics? Metrics { get; init; }

        public SteppedTimeProvider Time { get; } = new();

        public ILogger<MonitorPollingService> Logger { get; init; } = NullLogger<MonitorPollingService>.Instance;

        private IHost? _host;

        public Harness(MarketMonitorSettings settings) => Settings = new InMemoryMonitoredSymbolStore(settings);

        public async Task<(MonitorPollingService Service, IHost Host)> StartAsync()
        {
            _host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Services.AddSingleton<IMonitoredSymbolStore>(Settings);
                    opts.Services.AddSingleton<IPositionStore>(PositionStore ?? Positions);
                    opts.Services.AddSingleton<IPriceBaselineStore>(Baselines);
                    opts.Services.AddSingleton<ICooldownStore>(Cooldowns);
                    opts.Services.AddSingleton<IMarketDataSource>(Market);
                    opts.Services.AddSingleton<IClock>(Clock);
                    // #909, IADR-0380 決定2: 巡回の評価は市場ごとの開場判定を持つ（AppSvc も同じ実体を見る）。
                    opts.Services.AddSingleton<IMarketSchedule>(Schedule);
                    opts.Services.AddScoped<AppSvc>();

                    opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                    opts.StubAllExternalTransports();
                })
                .StartAsync();

            var service = new MonitorPollingService(
                _host.Services.GetRequiredService<IServiceScopeFactory>(),
                Schedule, Clock, Options.Create(MonitorOptions),
                Logger, Liveness, DailyVolume, Metrics, Time);

            return (service, _host);
        }

        public async ValueTask DisposeAsync()
        {
            if (_host is not null)
            {
                await _host.StopAsync();
                _host.Dispose();
            }
        }
    }

    private static MarketMonitorSettings Settings(params MonitoredSymbol[] symbols) => new()
    {
        MovementThresholdRatio = 0.03m,
        Cooldown = TimeSpan.FromMinutes(15),
        MonitoredSymbols = symbols,
    };

    [Fact]
    public async Task 市場開場時に閾値超過なら価格変動イベントを発行する()
    {
        await using var h = new Harness(Settings(Aapl));
        h.Baselines.SetBaseline("AAPL", Market.UnitedStates, 1_000m);
        h.Market.Set("AAPL", Market.UnitedStates, 1_040m); // +4%
        var (service, host) = await h.StartAsync();

        var session = await host.TrackActivityForTest()
            .ExecuteAndWaitAsync(_ => service.RunOnceAsync(CancellationToken.None));

        session.Sent.MessagesOf<PriceMovementDetected>().Should().NotBeEmpty();
    }

    [Fact]
    public async Task 市場閉場中はイベントを発行しない()
    {
        await using var h = new Harness(Settings(Aapl));
        h.Schedule.Open = false;
        h.Baselines.SetBaseline("AAPL", Market.UnitedStates, 1_000m);
        h.Market.Set("AAPL", Market.UnitedStates, 1_040m);
        var (service, host) = await h.StartAsync();

        var session = await host.TrackActivityForTest()
            .ExecuteAndWaitAsync(_ => service.RunOnceAsync(CancellationToken.None));

        session.Sent.MessagesOf<PriceMovementDetected>().Should().BeEmpty();
    }

    [Fact]
    public async Task 損切りライン到達時に損切りイベントを発行する()
    {
        await using var h = new Harness(Settings()); // 監視銘柄なし・保有のみ
        h.Positions.Set([new HeldPosition("AAPL", Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 970m)]);
        h.Market.Set("AAPL", Market.UnitedStates, 960m);
        var (service, host) = await h.StartAsync();

        var session = await host.TrackActivityForTest()
            .ExecuteAndWaitAsync(_ => service.RunOnceAsync(CancellationToken.None));

        session.Sent.MessagesOf<StopLossTriggered>().Should().NotBeEmpty();
    }

    [Fact]
    public async Task 同一巡回で損切りと変動が両方成立したとき両方を発行する()
    {
        // IADR-0014・損切り優先: 保有 MSFT が損切り到達、監視 AAPL が閾値超過を同一巡回で成立させる。
        // 発行順（損切り→変動）は RunOnceAsync の構造で保証される（到達は保有のループの中で検知した時点で発行し、変動は評価の後。#1282）。
        var msft = new MonitoredSymbol("MSFT", Market.UnitedStates);
        await using var h = new Harness(Settings(Aapl, msft));
        h.Positions.Set([new HeldPosition("MSFT", Market.UnitedStates, TradeSide.Buy, 5, 2_000m, 1_900m)]);
        h.Baselines.SetBaseline("AAPL", Market.UnitedStates, 1_000m);
        h.Market.Set("AAPL", Market.UnitedStates, 1_040m); // +4% 変動
        h.Market.Set("MSFT", Market.UnitedStates, 1_850m); // 損切り 1900 割れ
        var (service, host) = await h.StartAsync();

        var session = await host.TrackActivityForTest()
            .ExecuteAndWaitAsync(_ => service.RunOnceAsync(CancellationToken.None));

        session.Sent.MessagesOf<StopLossTriggered>().Should().NotBeEmpty();
        session.Sent.MessagesOf<PriceMovementDetected>().Should().NotBeEmpty();
    }

    [Fact]
    public async Task T_10_628_巡回が生存要約を出し_到達の発行は変わらず_閉場中は出さない()
    {
        // T-10-628, FR-10, #902, IADR-0365 決定4: 要約は発行の後に置く観測であり、到達の発行を変えない。
        await using var h = new Harness(Settings());
        var log = new StopLossLivenessReporterTests.RecordingLogger<StopLossLivenessReporter>();
        h.Liveness = new StopLossLivenessReporter(Options.Create(new MonitorOptions()), log);
        h.Positions.Set(
        [
            new HeldPosition("AAPL", Market.UnitedStates, TradeSide.Buy, 707, 350m, 338.51m),
            new HeldPosition("MSFT", Market.UnitedStates, TradeSide.Buy, 5, 2_000m, 1_900m),
        ]);
        h.Market.Set("AAPL", Market.UnitedStates, 340.12m);
        h.Market.Set("MSFT", Market.UnitedStates, 1_850m); // 到達
        var (service, host) = await h.StartAsync();

        var session = await host.TrackActivityForTest()
            .ExecuteAndWaitAsync(_ => service.RunOnceAsync(CancellationToken.None));

        session.Sent.MessagesOf<StopLossTriggered>().Should().ContainSingle().Which.Symbol.Should().Be("MSFT");
        log.Informations.Should().ContainSingle()
            .Which.Should().Contain("保有 2 件").And.Contain("現在値=340.12").And.Contain("ライン=338.51");

        // 閉場中は評価しない＝要約も出さない（間隔が過ぎていても）。
        h.Schedule.Open = false;
        h.Clock.UtcNow = Now.AddMinutes(10);
        await service.RunOnceAsync(CancellationToken.None);

        // #909 以降、閉場の巡回は「閉場と判定しています」（保有を知らない東証の側）を別に出すので、要約だけを数える。
        log.Informations.Count(m => m.Contains("損切り評価は稼働中", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task T_10_696_全市場が閉場の巡回は何も発行せず_保護の空白を1回だけ出す()
    {
        // T-10-696, FR-03, FR-10, #909, IADR-0380 決定2・決定3
        await using var h = new Harness(Settings(Aapl));
        var log = new StopLossLivenessReporterTests.RecordingLogger<StopLossLivenessReporter>();
        h.Liveness = new StopLossLivenessReporter(Options.Create(new MonitorOptions()), log);
        h.Positions.Set([new HeldPosition("AAPL", Market.UnitedStates, TradeSide.Buy, 707, 350m, 338.51m)]);
        h.Market.Set("AAPL", Market.UnitedStates, 330m); // 到達しているが閉場なので出してはならない
        h.Baselines.SetBaseline("AAPL", Market.UnitedStates, 1_000m);
        h.Market.Set("AAPL", Market.UnitedStates, 330m);
        var (service, host) = await h.StartAsync();

        // まず開場中に 1 巡回して、引け際の最終観測値を持たせる。
        var session = await host.TrackActivityForTest()
            .ExecuteAndWaitAsync(_ => service.RunOnceAsync(CancellationToken.None));
        session.Sent.MessagesOf<StopLossTriggered>().Should().ContainSingle();
        var requestsWhileOpen = h.Market.Requested.Count;

        // 閉場（全市場）。次の開場時刻を添えて 1 回だけ出す。
        h.Schedule.Open = false;
        h.Schedule.NextOpenAt = Now.AddHours(17);
        h.Clock.UtcNow = Now.AddMinutes(1);
        var closedSession = await host.TrackActivityForTest()
            .ExecuteAndWaitAsync(_ => service.RunOnceAsync(CancellationToken.None));

        closedSession.Sent.MessagesOf<StopLossTriggered>().Should().BeEmpty("閉場中は照会も判定もしない");
        h.Market.Requested.Should().HaveCount(requestsWhileOpen, "閉場中の巡回は 1 件も照会を増やさない");

        var closed = log.Entries.Where(e => e.Message.Contains("市場が閉場しました", StringComparison.Ordinal)).ToList();
        closed.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Critical, "最終観測値がラインを越えていた");

        // 60 秒ごとの巡回で重ねない。
        h.Clock.UtcNow = Now.AddMinutes(2);
        await service.RunOnceAsync(CancellationToken.None);
        log.Entries.Count(e => e.Message.Contains("市場が閉場しました", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task T_10_727_閉場中に起動した最初の巡回と_開場を挟んだ次の閉場で閉場の判定を1行ずつ出す()
    {
        // T-10-727, FR-03, FR-10, #909, IADR-0380［2026-09-24 追記 / PR #929 監査］F3: 保有が無くても
        // 「閉場と判定している・次の開場」を閉場期間ごとに 1 回出す。開場の巡回が印を解く配線（OnMarketOpen）を固定する。
        await using var h = new Harness(Settings(Aapl)); // 保有なし（Observe に評価が来ない）
        var log = new StopLossLivenessReporterTests.RecordingLogger<StopLossLivenessReporter>();
        h.Liveness = new StopLossLivenessReporter(Options.Create(new MonitorOptions()), log);
        h.Market.Set("AAPL", Market.UnitedStates, 100m);
        h.Schedule.Open = false; // 閉場中に起動した
        h.Schedule.NextOpenAt = Now.AddHours(17);
        var (service, _) = await h.StartAsync();

        await service.RunOnceAsync(CancellationToken.None);
        h.Clock.UtcNow = Now.AddMinutes(1);
        await service.RunOnceAsync(CancellationToken.None);

        int ClosedLines(Market m) => log.Informations.Count(x =>
            x.Contains("閉場と判定しています", StringComparison.Ordinal) && x.Contains(m.ToString(), StringComparison.Ordinal));
        ClosedLines(Market.UnitedStates).Should().Be(1, "起動直後の最初の巡回で 1 回・次の巡回では重ねない");
        log.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning, "保有を知らないので無保護の報告は出さない");

        // 開場（保有なし）→ 再び閉場。次の閉場期間の最初の巡回でまた 1 回出す。
        h.Schedule.Open = true;
        h.Clock.UtcNow = Now.AddHours(17);
        await service.RunOnceAsync(CancellationToken.None);
        h.Schedule.Open = false;
        h.Clock.UtcNow = Now.AddHours(24);
        await service.RunOnceAsync(CancellationToken.None);

        ClosedLines(Market.UnitedStates).Should().Be(2);
    }

    // ---- FR-01, #1132, IADR-0477: Finnhub の日次要求見積りを巡回ごとに保有と監視銘柄の和集合（#1189, IADR-0494）の実数から記録する ----

    private static FinnhubDailyVolumeRecorder Recorder(BusinessMetrics metrics) => new(
        new MarketDataOptions { Provider = "finnhub", Finnhub = new FinnhubMarketDataOptions { ApiKey = "k" } },
        new FinnhubDailyVolumeGuardOptions(), metrics, NullLogger.Instance, MarketSessions.RegularSessionMinutes);

    private static HeldPosition HeldUs(string symbol) => new(symbol, Market.UnitedStates, TradeSide.Buy, 10, 100m, 50m);

    // 🔴 T-10-2015: 保有 3 ＋ 監視銘柄 6（米国・60 秒巡回。AAPL が重なる）なら 1 巡回 8 要求 × 390 ＝ 3,120 を記録する。
    // #1189, IADR-0494: 同じ銘柄は 1 巡回に 1 回だけ照会するので 1 要求として数える（照会の形と同じ。#1132 の当時は 2 要求＝9 要求・3,510）。
    // 是正前（#1132）は申告 1 銘柄で 390 だった。
    [Fact]
    public async Task 巡回ごとに保有と監視銘柄の実数から日次見積りを記録する()
    {
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var metrics = BusinessMetrics.WithMeterName(meterName);
        await using var h = new Harness(Settings(
            Aapl, new("MSFT", Market.UnitedStates), new("NVDA", Market.UnitedStates),
            new("AMZN", Market.UnitedStates), new("GOOGL", Market.UnitedStates), new("META", Market.UnitedStates)))
        {
            DailyVolume = Recorder(metrics),
        };
        h.Positions.Set([HeldUs("AAPL"), HeldUs("TSLA"), HeldUs("AMD")]);
        var (service, _) = await h.StartAsync();

        await service.RunOnceAsync(CancellationToken.None);

        h.Market.Requested.Should().HaveCount(8, "1 巡回の照会は保有 3 と監視銘柄 6 の和集合（AAPL は 1 回）");
        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeEstimate).Should().ContainSingle().Which.Value.Should().Be(3_120);
    }

    // T-10-2015, #1132（独立監査 🟡）: 見積りは構成の巡回間隔で数える。120 秒なら 1 日 195 巡回 × 8 要求 ＝ 1,560
    //（巡回間隔を定数 60 に取り違えると 3,120 になる。#1189 で 9 要求→8 要求）。
    [Fact]
    public async Task 日次見積りは構成の巡回間隔で数える()
    {
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var metrics = BusinessMetrics.WithMeterName(meterName);
        await using var h = new Harness(Settings(
            Aapl, new("MSFT", Market.UnitedStates), new("NVDA", Market.UnitedStates),
            new("AMZN", Market.UnitedStates), new("GOOGL", Market.UnitedStates), new("META", Market.UnitedStates)))
        {
            DailyVolume = Recorder(metrics),
            MonitorOptions = new MonitorOptions { PollIntervalSeconds = 120 },
        };
        h.Positions.Set([HeldUs("AAPL"), HeldUs("TSLA"), HeldUs("AMD")]);
        var (service, _) = await h.StartAsync();

        await service.RunOnceAsync(CancellationToken.None);

        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeEstimate).Should().ContainSingle().Which.Value.Should().Be(8 * 195);
    }

    // 🔴 T-10-2016: 米国が閉場で東証だけ開いた巡回でも、米国の銘柄を数える（見積りは開場中の量。照会した数で数えると 0 に落ちる）。
    // 東証の銘柄は Finnhub へ送らないので 0。
    [Fact]
    public async Task 米国が閉場の巡回でも米国の銘柄を数え東証の銘柄は数えない()
    {
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var metrics = BusinessMetrics.WithMeterName(meterName);
        await using var h = new Harness(Settings(Aapl, new("7203", Market.Japan))) { DailyVolume = Recorder(metrics) };
        h.Schedule.ClosedMarkets.Add(Market.UnitedStates);
        h.Positions.Set([HeldUs("MSFT")]);
        var (service, _) = await h.StartAsync();

        await service.RunOnceAsync(CancellationToken.None);

        h.Market.Requested.Should().OnlyContain(r => r.Market == Market.Japan, "米国の銘柄は照会しない");
        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeEstimate).Should().ContainSingle().Which.Value.Should().Be(2 * 390);
    }

    // T-10-2019: 全市場が閉じている巡回は評価しない（従来どおり）ので記録もしない（最後の値をゲージが保つ）。
    [Fact]
    public async Task 全市場が閉場の巡回は日次見積りを記録しない()
    {
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var metrics = BusinessMetrics.WithMeterName(meterName);
        await using var h = new Harness(Settings(Aapl)) { DailyVolume = Recorder(metrics) };
        h.Schedule.Open = false;
        var (service, _) = await h.StartAsync();

        await service.RunOnceAsync(CancellationToken.None);

        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeEstimate).Should().BeEmpty();
    }

    // ---- FR-04, NFR-01, ADR-0043 決定 2 (b), #1251, IADR-0513: 1 巡回の所要を計量（秒）と Warning で観測する ----
    // 経過は偽の時計（SteppedTimeProvider）を照会ごとに進めて作る（実時間は待たない）。

    private const string CycleOverrunMessage = "市場監視の 1 巡回の所要";

    private static Harness CycleHarness(
        BusinessMetrics metrics, StopLossLivenessReporterTests.RecordingLogger<MonitorPollingService> log, int pollIntervalSeconds = 60) =>
        new(Settings(
            Aapl, new("MSFT", Market.UnitedStates), new("NVDA", Market.UnitedStates), new("AMZN", Market.UnitedStates)))
        {
            Metrics = metrics,
            Logger = log,
            MonitorOptions = new MonitorOptions { PollIntervalSeconds = pollIntervalSeconds },
        };

    // 🔴 T-10-2461: 巡回の所要が巡回間隔（60 秒）に達する・超えると、所要の秒数が計量に 1 件入り、Warning が 1 行出る。
    // 4 銘柄 × 15 秒 ＝ 60 秒（ちょうど達する）・4 銘柄 × 16 秒 ＝ 64 秒（超える）。
    [Theory]
    [InlineData(15, 60d)]
    [InlineData(16, 64d)]
    public async Task T_10_2461_巡回の所要が巡回間隔に達すると秒数を計量しWarningを出す(int secondsPerQuote, double expectedSeconds)
    {
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var metrics = BusinessMetrics.WithMeterName(meterName);
        var log = new StopLossLivenessReporterTests.RecordingLogger<MonitorPollingService>();
        await using var h = CycleHarness(metrics, log);
        h.Market.OnRequest = () => h.Time.Advance(TimeSpan.FromSeconds(secondsPerQuote));
        var (service, _) = await h.StartAsync();

        await service.RunOnceAsync(CancellationToken.None);

        h.Market.Requested.Should().HaveCount(4);
        capture.ValuesOf(BusinessMetricNames.MarketMonitorCycleDurationSeconds)
            .Should().ContainSingle().Which.Value.Should().Be(expectedSeconds);
        log.Warnings.Should().ContainSingle(m => m.Contains(CycleOverrunMessage, StringComparison.Ordinal))
            .Which.Should().Contain("巡回間隔 60 秒");
    }

    // T-10-2462（否定形）: 所要が巡回間隔に満たなければ計量は入るが Warning は出ない。
    // 巡回間隔は構成の値で判定する（120 秒の構成で 4 × 16 ＝ 64 秒の巡回は達していない。定数 60 と取り違えると Warning が出る）。
    [Theory]
    [InlineData(60, 14, 56d)]
    [InlineData(120, 16, 64d)]
    public async Task T_10_2462_巡回の所要が巡回間隔に満たなければWarningを出さない(
        int pollIntervalSeconds, int secondsPerQuote, double expectedSeconds)
    {
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var metrics = BusinessMetrics.WithMeterName(meterName);
        var log = new StopLossLivenessReporterTests.RecordingLogger<MonitorPollingService>();
        await using var h = CycleHarness(metrics, log, pollIntervalSeconds);
        h.Market.OnRequest = () => h.Time.Advance(TimeSpan.FromSeconds(secondsPerQuote));
        var (service, _) = await h.StartAsync();

        await service.RunOnceAsync(CancellationToken.None);

        capture.ValuesOf(BusinessMetricNames.MarketMonitorCycleDurationSeconds)
            .Should().ContainSingle().Which.Value.Should().Be(expectedSeconds);
        log.Warnings.Should().NotContain(m => m.Contains(CycleOverrunMessage, StringComparison.Ordinal));
    }

    // T-10-2463: 全市場が閉場の巡回は評価しないので所要を記録しない（0 秒でヒストグラムを薄めない）。
    [Fact]
    public async Task T_10_2463_全市場が閉場の巡回は所要を記録しない()
    {
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var metrics = BusinessMetrics.WithMeterName(meterName);
        var log = new StopLossLivenessReporterTests.RecordingLogger<MonitorPollingService>();
        await using var h = CycleHarness(metrics, log);
        h.Schedule.Open = false;
        var (service, _) = await h.StartAsync();

        await service.RunOnceAsync(CancellationToken.None);

        capture.ValuesOf(BusinessMetricNames.MarketMonitorCycleDurationSeconds).Should().BeEmpty();
        log.Warnings.Should().NotContain(m => m.Contains(CycleOverrunMessage, StringComparison.Ordinal));
    }

    // T-10-2467, FR-04, NFR-01, ADR-0043, #1251（PR #1266 の AI レビュー 🟡）: 例外で抜けた巡回も所要を記録する
    // （遅い失敗も次の刻みを遅らせる）。保有の照会が 70 秒かかってから失敗する → 70 秒が 1 件入り、Warning も出る。
    [Fact]
    public async Task T_10_2467_例外で抜けた巡回も所要を記録する()
    {
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var metrics = BusinessMetrics.WithMeterName(meterName);
        var log = new StopLossLivenessReporterTests.RecordingLogger<MonitorPollingService>();
        SteppedTimeProvider? time = null;
        await using var h = new Harness(Settings(Aapl))
        {
            Metrics = metrics,
            Logger = log,
            PositionStore = new ThrowingPositionStore(() => time!.Advance(TimeSpan.FromSeconds(70))),
        };
        time = h.Time;
        var (service, _) = await h.StartAsync();

        var act = () => service.RunOnceAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        capture.ValuesOf(BusinessMetricNames.MarketMonitorCycleDurationSeconds)
            .Should().ContainSingle().Which.Value.Should().Be(70d);
        log.Warnings.Should().ContainSingle(m => m.Contains(CycleOverrunMessage, StringComparison.Ordinal));
    }

    // T-10-2467, FR-04, NFR-01, ADR-0043, #1251（PR #1266 の独立監査 🟡-2）: 所要は評価の後（発行・生存の報告）まで数える。
    // 照会に 50 秒・生存の報告（評価の後に置く）に 15 秒かかる → 65 秒が入り Warning が出る（評価の直後で測り止めると 50 秒になる）。
    [Fact]
    public async Task T_10_2467_評価の後の発行と生存の報告にかかった時間も所要に数える()
    {
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var metrics = BusinessMetrics.WithMeterName(meterName);
        var log = new StopLossLivenessReporterTests.RecordingLogger<MonitorPollingService>();
        SteppedTimeProvider? time = null;
        await using var h = new Harness(Settings()) // 監視銘柄なし・保有のみ
        {
            Metrics = metrics,
            Logger = log,
            Liveness = new StopLossLivenessReporter(
                Options.Create(new MonitorOptions()),
                new AdvancingLogger<StopLossLivenessReporter>(() => time!.Advance(TimeSpan.FromSeconds(15)))),
        };
        time = h.Time;
        h.Positions.Set([HeldUs("AAPL")]);
        h.Market.Set("AAPL", Market.UnitedStates, 100m);
        h.Market.OnRequest = () => h.Time.Advance(TimeSpan.FromSeconds(50));
        var (service, _) = await h.StartAsync();

        await service.RunOnceAsync(CancellationToken.None);

        capture.ValuesOf(BusinessMetricNames.MarketMonitorCycleDurationSeconds)
            .Should().ContainSingle().Which.Value.Should().Be(65d, "生存の報告（評価の後）の 15 秒も巡回の所要である");
        log.Warnings.Should().ContainSingle(m => m.Contains(CycleOverrunMessage, StringComparison.Ordinal));
    }

    // T-10-2468, FR-04, NFR-01, ADR-0043, #1251（PR #1266 の AI レビュー 🟡・🟢）: 停止要求で中断した巡回
    // （取り消し済みのトークンで OperationCanceledException）は記録しない。最後まで回ってから停止要求が来た巡回は記録する
    // （トークンの状態だけで判定すると、停止の直前に回り切った巡回まで落とす）。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T_10_2468_停止要求で中断した巡回は記録せず回り切った巡回は停止要求の後でも記録する(bool aborted)
    {
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var metrics = BusinessMetrics.WithMeterName(meterName);
        var log = new StopLossLivenessReporterTests.RecordingLogger<MonitorPollingService>();
        await using var h = new Harness(Settings(Aapl)) { Metrics = metrics, Logger = log }; // 照会 1 件だけの巡回
        using var stopping = new CancellationTokenSource();
        h.Market.OnRequest = () =>
        {
            h.Time.Advance(TimeSpan.FromSeconds(20));
            stopping.Cancel(); // 照会の最中に停止要求が来る
            if (aborted)
                throw new OperationCanceledException(stopping.Token); // 照会が停止要求で中断する
        };
        var (service, _) = await h.StartAsync();

        var act = () => service.RunOnceAsync(stopping.Token);

        if (aborted)
        {
            await act.Should().ThrowAsync<OperationCanceledException>();
            capture.ValuesOf(BusinessMetricNames.MarketMonitorCycleDurationSeconds).Should().BeEmpty();
        }
        else
        {
            await act.Should().NotThrowAsync();
            stopping.IsCancellationRequested.Should().BeTrue();
            capture.ValuesOf(BusinessMetricNames.MarketMonitorCycleDurationSeconds)
                .Should().ContainSingle().Which.Value.Should().Be(20d);
        }
    }

    // ---- FR-10, FR-03, UC-02, #1280, #1282, IADR-0520: 損切りライン到達（S1）の発行は検知の時点で 1 回だけ ----

    private static HeldPosition HeldAapl() => new("AAPL", Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 970m);

    private static async Task<IReadOnlyList<StopLossTriggered>> RunCycleAsync(IHost host, MonitorPollingService service)
    {
        var session = await host.TrackActivityForTest()
            .ExecuteAndWaitAsync(_ => service.RunOnceAsync(CancellationToken.None));
        return [.. session.Sent.MessagesOf<StopLossTriggered>()];
    }

    // 🔴 T-10-2476（#1280）: 決済が台帳へ反映されるまでの巡回は、同じ建玉を保有として読み直して同じ到達を再評価する。
    // 発行は 1 回だけにし（通知・監査に「再到達」を作らない）、抑止したことは INF で残す。
    [Fact]
    public async Task T_10_2476_同じ到達は次の巡回で再発行しない()
    {
        var log = new StopLossLivenessReporterTests.RecordingLogger<MonitorPollingService>();
        await using var h = new Harness(Settings()) { Logger = log };
        h.Positions.Set([HeldAapl()]);
        h.Market.Set("AAPL", Market.UnitedStates, 960m);
        var (service, host) = await h.StartAsync();

        (await RunCycleAsync(host, service)).Should().ContainSingle();

        h.Clock.UtcNow = Now.AddSeconds(60); // 決済はまだ台帳へ反映されていない（保有の照会が同じ建玉を返す）
        h.Market.Set("AAPL", Market.UnitedStates, 961m);
        (await RunCycleAsync(host, service)).Should().BeEmpty("同じ建玉・同じラインの到達はすでに発行した");

        log.Informations.Should().ContainSingle(m => m.Contains("損切りライン到達を発行", StringComparison.Ordinal));
        log.Informations.Should().ContainSingle(m => m.Contains("再発行を抑止", StringComparison.Ordinal));
    }

    // T-10-2476（解除の条件）: 建玉が閉じた・価格がラインの内側へ戻った後の到達は新しい到達として発行する。
    // ラインが変わった（別の到達）ときも発行する。抑止は到達を黙らせ続けない（3 分経っても残っていれば出し直す）。
    [Fact]
    public async Task T_10_2476_建玉が閉じた後_価格が戻った後_ラインが変わったとき_抑止の期限を過ぎたときは発行する()
    {
        await using var h = new Harness(Settings());
        h.Positions.Set([HeldAapl()]);
        h.Market.Set("AAPL", Market.UnitedStates, 960m);
        var (service, host) = await h.StartAsync();
        (await RunCycleAsync(host, service)).Should().ContainSingle();

        // 建玉が閉じた巡回 → 同じ銘柄・同じラインで建て直した建玉の到達は発行する。
        h.Positions.Set([]);
        h.Clock.UtcNow = Now.AddSeconds(60);
        (await RunCycleAsync(host, service)).Should().BeEmpty();
        h.Positions.Set([HeldAapl()]);
        h.Clock.UtcNow = Now.AddSeconds(120);
        (await RunCycleAsync(host, service)).Should().ContainSingle("閉じた建玉の到達の記憶を持ち越さない");

        // 価格がラインの内側へ戻った巡回 → 再び割った到達は発行する。
        h.Market.Set("AAPL", Market.UnitedStates, 980m);
        h.Clock.UtcNow = Now.AddSeconds(180);
        (await RunCycleAsync(host, service)).Should().BeEmpty();
        h.Market.Set("AAPL", Market.UnitedStates, 965m);
        h.Clock.UtcNow = Now.AddSeconds(240);
        (await RunCycleAsync(host, service)).Should().ContainSingle("価格が戻った後の到達は新しい到達");

        // ラインが変わった → 別の到達。
        h.Positions.Set([HeldAapl() with { StopLossPrice = 975m }]);
        h.Clock.UtcNow = Now.AddSeconds(300);
        (await RunCycleAsync(host, service)).Should().ContainSingle("ラインが変われば別の到達");

        // 同じ到達が抑止の期限（3 分）を過ぎても残っていれば出し直す（決済が進まないことを黙らせない）。
        h.Clock.UtcNow = Now.AddSeconds(360);
        (await RunCycleAsync(host, service)).Should().BeEmpty();
        h.Clock.UtcNow = Now.AddSeconds(300) + StopLossArrivalGate.RepublishAfter;
        (await RunCycleAsync(host, service)).Should().ContainSingle("抑止の期限を過ぎた到達は出し直す");
    }

    // T-10-2476（否定形）: 価格が取れなかった巡回・閉場で評価しなかった巡回は「戻った」ではない（記憶を消して次の巡回で再発行しない）。
    [Fact]
    public async Task T_10_2476_価格が取れない巡回と閉場の巡回は抑止を解かない()
    {
        await using var h = new Harness(Settings());
        h.Positions.Set([HeldAapl(), new HeldPosition("7203", Market.Japan, TradeSide.Buy, 100, 3_000m, 2_900m)]);
        h.Market.Set("AAPL", Market.UnitedStates, 960m).Set("7203", Market.Japan, 2_850m);
        var (service, host) = await h.StartAsync();
        (await RunCycleAsync(host, service)).Should().HaveCount(2);

        h.Market.Remove("AAPL", Market.UnitedStates); // 米国は開場・AAPL の価格が取れない
        h.Schedule.ClosedMarkets.Add(Market.Japan);   // 東証は閉場（照会も判定もしない）
        h.Clock.UtcNow = Now.AddSeconds(60);
        (await RunCycleAsync(host, service)).Should().BeEmpty();

        h.Market.Set("AAPL", Market.UnitedStates, 960m);
        h.Schedule.ClosedMarkets.Clear();
        h.Clock.UtcNow = Now.AddSeconds(120);
        (await RunCycleAsync(host, service)).Should().BeEmpty("価格が取れなかった・評価しなかった巡回は、価格が戻った証拠ではない");
    }

    // 🔴 T-10-2479（#1285 監査 F1, IADR-0520 決定2）: 市場監視が見るラインは台帳の最も保護的な 1 本だけで（IADR-0393）、
    // 発注執行は到達の価格が行自身のラインに達した S1 の行だけを武装する。同じ鍵でも価格がさらに不利へ進んだ到達は
    // 次の巡回で出し直す（低いラインの行 B＝330.88 を 3 分待たせない）。前回の発行と同じか有利な価格は抑止する。
    [Theory]
    [InlineData(TradeSide.Buy, 331.67, 331.50, 330.50, 330.90)]
    [InlineData(TradeSide.Sell, 100.00, 101.00, 102.00, 101.50)]
    public async Task T_10_2479_同じ鍵でも前回の発行より不利な価格の到達は次の巡回で出し直す(
        TradeSide side, double line, double first, double deeper, double backInside)
    {
        await using var h = new Harness(Settings());
        h.Positions.Set([new HeldPosition("AAPL", Market.UnitedStates, side, 1_428, null, (decimal)line)]);
        h.Market.Set("AAPL", Market.UnitedStates, (decimal)first);
        var (service, host) = await h.StartAsync();
        (await RunCycleAsync(host, service)).Should().ContainSingle().Which.Price.Should().Be((decimal)first);

        h.Clock.UtcNow = Now.AddSeconds(60);
        (await RunCycleAsync(host, service)).Should().BeEmpty("同じ価格（決済の反映待ちの巡回）は抑止する");

        h.Market.Set("AAPL", Market.UnitedStates, (decimal)deeper); // 台帳のラインは同じまま、別の行のラインを割る
        h.Clock.UtcNow = Now.AddSeconds(120);
        (await RunCycleAsync(host, service)).Should().ContainSingle("より不利な価格はより低いラインの行に届き得る")
            .Which.Price.Should().Be((decimal)deeper);

        h.Market.Set("AAPL", Market.UnitedStates, (decimal)backInside); // 前回の発行より有利（台帳のラインは越えたまま）
        h.Clock.UtcNow = Now.AddSeconds(180);
        (await RunCycleAsync(host, service)).Should().BeEmpty("前回の発行より有利な価格は新しい行に届かない");
    }

    // 🔴 T-10-2480（#1285 監査 F2, IADR-0520 決定2）: 同じ到達の出し直しの間隔は、巡回の周期（既定の巡回間隔 × 2 まで）を
    // 足しても発注執行の到達の窓（SoftwareStopExecutor.TriggerEpisodeGap）を超えない。超えると出し直しのたびに
    // 決済の連続失敗の数えと待ち時間が 0 へ戻る（#833 の拒否連発の再発）。発注執行はこの試験から参照できないので、
    // 値は宣言の行（ソース）から読む（宣言を変えたらこの試験が気付く）。
    [Fact]
    public void T_10_2480_出し直しの間隔と巡回の周期の和は発注執行の到達の窓を超えない()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "backend", "backend.slnx")))
            root = root.Parent;
        root.Should().NotBeNull("リポジトリの最上位が見つかる");
        var source = File.ReadAllText(Path.Combine(
            root!.FullName, "backend", "Services", "OrderExecutionService", "Features", "OrderExecution",
            "ExecuteSoftwareStops", "SoftwareStopExecutor.cs"));
        var match = System.Text.RegularExpressions.Regex.Match(
            source, @"TimeSpan TriggerEpisodeGap = TimeSpan\.FromMinutes\((\d+)\);");
        match.Success.Should().BeTrue("発注執行の到達の窓の宣言を読める");
        var triggerEpisodeGap = TimeSpan.FromMinutes(int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));

        var patrolPeriod = TimeSpan.FromSeconds(new MonitorOptions().PollIntervalSeconds);
        (StopLossArrivalGate.RepublishAfter + (2 * patrolPeriod)).Should().BeLessThanOrEqualTo(triggerEpisodeGap);
    }

    // 🔴 T-10-2477（#1282）: 到達の検知時刻（DetectedAt）は巡回の開始ではなく、その建玉の価格を照会し終えた時刻である。
    // 照会は 1 件 5 秒（容量 1 の限流器）。保有の 2 番目の到達は巡回の開始から 10 秒後に検知される。
    [Fact]
    public async Task T_10_2477_到達の検知時刻は照会し終えた時刻であり巡回の開始ではない()
    {
        await using var h = new Harness(Settings());
        h.Positions.Set([HeldAapl(), new HeldPosition("MSFT", Market.UnitedStates, TradeSide.Buy, 5, 2_000m, 1_900m)]);
        h.Market.Set("AAPL", Market.UnitedStates, 960m).Set("MSFT", Market.UnitedStates, 1_850m);
        h.Market.OnRequest = () => h.Clock.UtcNow += TimeSpan.FromSeconds(5);
        var (service, host) = await h.StartAsync();

        var sent = await RunCycleAsync(host, service);

        // 送出の記録の並びは追跡の都合で入れ替わり得るので、順序は見ない（検知した時点で渡すことは MarketMonitorServiceTests が固定する）。
        sent.Select(s => (s.Symbol, s.DetectedAt)).Should().BeEquivalentTo(
            [("AAPL", Now.AddSeconds(5)), ("MSFT", Now.AddSeconds(10))]);
    }

    // T-10-2467: 保有の照会に時間がかかってから失敗する（巡回を例外で抜けさせる）。
    private sealed class ThrowingPositionStore(Action beforeThrow) : IPositionStore
    {
        public Task<IReadOnlyCollection<HeldPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken = default)
        {
            beforeThrow();
            throw new InvalidOperationException("保有の照会に失敗した（試験）");
        }
    }

    // T-10-2467: ログを書くたびに偽の時計を進める（評価の後の生存の報告に時間がかかる形を作る）。
    private sealed class AdvancingLogger<T>(Action onLog) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => onLog();
    }
}
