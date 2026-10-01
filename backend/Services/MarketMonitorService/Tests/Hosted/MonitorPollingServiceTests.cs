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
        public InMemoryPriceBaselineStore Baselines { get; } = new();
        public InMemoryCooldownStore Cooldowns { get; } = new();

        // #902, IADR-0365: 生存要約（null なら配線しない＝従来の構成）。
        public StopLossLivenessReporter? Liveness { get; set; }

        // #1132, IADR-0477: 日次要求見積りの記録器（null なら配線しない＝従来の構成）。
        public FinnhubDailyVolumeRecorder? DailyVolume { get; set; }

        private IHost? _host;

        public Harness(MarketMonitorSettings settings) => Settings = new InMemoryMonitoredSymbolStore(settings);

        public async Task<(MonitorPollingService Service, IHost Host)> StartAsync()
        {
            _host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Services.AddSingleton<IMonitoredSymbolStore>(Settings);
                    opts.Services.AddSingleton<IPositionStore>(Positions);
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
                Schedule, Clock, Options.Create(new MonitorOptions()),
                NullLogger<MonitorPollingService>.Instance, Liveness, DailyVolume);

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
        // 発行順（損切り→変動）は RunOnceAsync の構造で保証される（StopLosses を先に Publish）。
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

    // ---- FR-01, #1132, IADR-0477: Finnhub の日次要求見積りを巡回ごとに保有＋監視銘柄の実数から記録する ----

    private static FinnhubDailyVolumeRecorder Recorder(BusinessMetrics metrics) => new(
        new MarketDataOptions { Provider = "finnhub", Finnhub = new FinnhubMarketDataOptions { ApiKey = "k" } },
        new FinnhubDailyVolumeGuardOptions(), metrics, NullLogger.Instance, MarketSessions.RegularSessionMinutes);

    private static HeldPosition HeldUs(string symbol) => new(symbol, Market.UnitedStates, TradeSide.Buy, 10, 100m, 50m);

    // 🔴 T-10-2015: 保有 3 ＋ 監視銘柄 6（米国・60 秒巡回）なら 1 巡回 9 要求 × 390 ＝ 3,510 を記録する（#1132 の実測 ≈ 9 要求/分と一致）。
    // 保有と監視銘柄に同じ銘柄があれば 2 要求として数える（照会の形と同じ）。是正前は申告 1 銘柄で 390 だった。
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

        h.Market.Requested.Should().HaveCount(9, "1 巡回の照会は保有 3 ＋ 監視銘柄 6（AAPL は 2 回）");
        capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeEstimate).Should().ContainSingle().Which.Value.Should().Be(3_510);
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
}
