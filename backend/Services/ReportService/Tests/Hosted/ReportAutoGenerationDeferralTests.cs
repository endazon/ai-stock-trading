using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReportService.Common.Abstractions;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Hosted;
using ReportService.Infrastructure.ExternalServices;
using ReportService.Infrastructure.Persistence;
using Xunit;

namespace ReportService.Tests;

// FR-06, UC-03〜05, #840, IADR-0352 決定 3・4: 常駐が「見送り」と「縮退した生成」をどう扱うか
// （警告を残す＝沈黙しない／次の巡回を早める）と、構成値の解釈（fail-safe）を検証する。
public class ReportAutoGenerationDeferralTests
{
    // 2026-07-08（水）16:00 JST ＝ 07:00 UTC。日報だけが生成境界を越えている時刻。
    private static readonly DateTimeOffset WedAfterClose = new(2026, 7, 8, 7, 0, 0, TimeSpan.Zero);
    private const string PeriodKey = "daily-2026-07-08";

    // #866: 2026-07-31（金・月末最終営業日）23:59:45 JST ＝ 14:59:45 UTC。
    // 次に再試行する時刻（+30 秒）には月報 monthly-2026-07 が生成対象から外れる。
    private static readonly DateTimeOffset MonthEnd235945 = new(2026, 7, 31, 14, 59, 45, TimeSpan.Zero);
    private const string MonthlyKey = "monthly-2026-07";

    // ---- 常駐のログと次回巡回 -------------------------------------------------------------------

    [Fact]
    public async Task 見送った巡回は警告を残し_次の巡回を早める()
    {
        var logger = new RecordingLogger();
        var positions = new FailingPositionSource { Transient = true };
        var (service, store) = NewService(logger, positions, new ReportAutoGenerationOptions());

        var retryAfter = await service.RunOnceAsync(CancellationToken.None);

        retryAfter.Should().Be(TimeSpan.FromSeconds(30));
        logger.Warnings.Should().ContainSingle(m =>
            m.Contains("生成を見送りました") && m.Contains(PeriodKey) && m.Contains("建玉")
            && m.Contains("risk-ledger") && m.Contains("1/5"));
        // 🔴 否定形: 見送った期間を「提示しました」と記録しない・報告書も出来ていない。
        logger.Messages.Should().NotContain(m => m.Contains("提示しました"));
        store.Get(PeriodKey).Should().BeNull();
    }

    [Fact]
    public async Task 見送りが無ければ_巡回間隔は通常のまま()
    {
        var (service, _) = NewService(new RecordingLogger(), new FailingPositionSource { Fail = false }, new ReportAutoGenerationOptions());

        (await service.RunOnceAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task 上限に達して縮退した報告書を出したら_その旨と欠けた入力を警告に残す()
    {
        var logger = new RecordingLogger();
        var positions = new FailingPositionSource { Transient = true };
        var (service, store) = NewService(
            logger, positions, new ReportAutoGenerationOptions { DependencyRetryMaxAttempts = 2 });

        (await service.RunOnceAsync(CancellationToken.None)).Should().Be(TimeSpan.FromSeconds(30));
        (await service.RunOnceAsync(CancellationToken.None)).Should().Be(TimeSpan.FromSeconds(60));
        // 🔴 否定形: 上限までは縮退の警告を出さない（まだ報告書を出していない）。
        logger.Warnings.Should().NotContain(m => m.Contains("未供給のまま生成しました"));

        var retryAfter = await service.RunOnceAsync(CancellationToken.None);

        retryAfter.Should().BeNull();
        logger.Warnings.Should().ContainSingle(m =>
            m.Contains("見送りの上限に達したため") && m.Contains(PeriodKey) && m.Contains("建玉"));
        store.Get(PeriodKey)!.Report.UnsuppliedInputs.Should().Contain(ReportInput.OpenPositions);
    }

    [Fact]
    public async Task 恒常的な失敗は見送らず_縮退した生成を警告に残す()
    {
        var logger = new RecordingLogger();
        var positions = new FailingPositionSource { Transient = false };
        var (service, store) = NewService(logger, positions, new ReportAutoGenerationOptions());

        var retryAfter = await service.RunOnceAsync(CancellationToken.None);

        retryAfter.Should().BeNull();
        logger.Warnings.Should().ContainSingle(m =>
            m.Contains("未供給のまま生成しました") && m.Contains(PeriodKey) && m.Contains("建玉"));
        logger.Warnings.Should().NotContain(m => m.Contains("見送り"));
        store.Get(PeriodKey).Should().NotBeNull();
    }

    [Fact]
    public async Task 窓が閉じる直前の縮退は_上限到達とは別の文言で警告に残す()
    {
        // #866: 月報が使う入力（運用段階）が一過性に落ちている。見送ると次の試行時刻には窓が閉じており、
        // その期間は二度と生成対象にならない＝縮退版すら出ない。待たずに出し、理由を言う警告を残す。
        var logger = new RecordingLogger();
        var (service, store) = NewService(
            logger, new FailingPositionSource { Fail = false }, new ReportAutoGenerationOptions(),
            MonthEnd235945, new FailingStageSource());

        var retryAfter = await service.RunOnceAsync(CancellationToken.None);

        logger.Warnings.Should().ContainSingle(m =>
            m.Contains("生成窓") && m.Contains(MonthlyKey) && m.Contains("運用段階"));
        // 🔴 否定形: 上限到達の文言は出さない（原因が違う）。見送ってもいない。
        logger.Warnings.Should().NotContain(m => m.Contains("見送りの上限に達したため"));
        logger.Warnings.Should().NotContain(m => m.Contains("生成を見送りました"));
        retryAfter.Should().BeNull();
        store.Get(MonthlyKey)!.Report.UnsuppliedInputs.Should().Contain(ReportInput.CurrentStage);
    }

    // ---- 構成値の解釈 ---------------------------------------------------------------------------

    [Fact]
    public void 既定は_5_回まで_30_秒からの倍々で_巡回間隔を超えない()
    {
        var settings = new ReportAutoGenerationOptions().ToDeferralSettings();

        settings.MaxDeferrals.Should().Be(5);
        Enumerable.Range(1, 6).Select(settings.DelayFor).Should().Equal(
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120),
            TimeSpan.FromSeconds(240), TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(300));
    }

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(0, 0)] // 0 は「見送らない」を明示する値であり、既定へ倒さない。
    [InlineData(3, 3)]
    public void 見送りの上限は_負値だけ既定へ倒す(int configured, int expected)
    {
        new ReportAutoGenerationOptions { DependencyRetryMaxAttempts = configured }
            .ToDeferralSettings().MaxDeferrals.Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void 待ち時間の基準は_非正値を既定へ倒す(int configured)
    {
        // 0 秒で回し続ける暴走ループにしない。
        new ReportAutoGenerationOptions { DependencyRetryBaseSeconds = configured }
            .ToDeferralSettings().DelayFor(1).Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void 待ち時間は_巡回間隔より短い構成でも間隔を超えない()
    {
        var settings = new ReportAutoGenerationOptions { IntervalSeconds = 20 }.ToDeferralSettings();

        settings.DelayFor(1).Should().Be(TimeSpan.FromSeconds(20));
        settings.DelayFor(30).Should().Be(TimeSpan.FromSeconds(20));
        // 桁あふれしない。
        settings.DelayFor(int.MaxValue).Should().Be(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void 見送り回数は期間ごとに数え_生成できたら捨てる()
    {
        var tracker = new ReportGenerationDeferralTracker(new ReportDeferralSettings { MaxDeferrals = 2 });

        tracker.TryDefer("daily-a")!.Attempt.Should().Be(1);
        tracker.TryDefer("daily-a")!.Attempt.Should().Be(2);
        tracker.TryDefer("daily-a").Should().BeNull();
        // 別の期間は別に数える。
        tracker.TryDefer("weekly-b")!.Attempt.Should().Be(1);

        tracker.Clear("daily-a");
        tracker.DeferralsOf("daily-a").Should().Be(0);
        tracker.TryDefer("daily-a")!.Attempt.Should().Be(1);
    }

    [Fact]
    public void 次の待ち時間は回数を消費せずに先読みでき_上限に達していれば無い()
    {
        // #866: 「その待ち時間の後もまだ生成対象か」を確かめてから見送るため、回数と待ち時間を分ける。
        var tracker = new ReportGenerationDeferralTracker(new ReportDeferralSettings { MaxDeferrals = 2 });

        tracker.NextDelay("daily-a").Should().Be(TimeSpan.FromSeconds(30));
        // 🔴 否定形: 先読みしただけでは 1 回も数えない。
        tracker.DeferralsOf("daily-a").Should().Be(0);

        tracker.TryDefer("daily-a")!.Attempt.Should().Be(1);
        tracker.NextDelay("daily-a").Should().Be(TimeSpan.FromSeconds(60));
        tracker.TryDefer("daily-a")!.Attempt.Should().Be(2);
        tracker.NextDelay("daily-a").Should().BeNull();
    }

    [Fact]
    public void 生成対象から外れた期間の見送り回数だけを捨てる()
    {
        // #866: 窓が閉じた期間は二度と Due に現れず、生成による解放（Clear）が起きない。
        var tracker = new ReportGenerationDeferralTracker(new ReportDeferralSettings { MaxDeferrals = 5 });
        tracker.TryDefer("monthly-2026-07");
        tracker.TryDefer("daily-2026-07-31");

        tracker.RetainOnly(["daily-2026-07-31"]);

        tracker.DeferralsOf("monthly-2026-07").Should().Be(0);
        // 🔴 否定形: まだ生成対象である期間の回数は捨てない（捨てると上限が効かなくなる）。
        tracker.DeferralsOf("daily-2026-07-31").Should().Be(1);
    }

    // ---- 部品 -----------------------------------------------------------------------------------

    private static (ReportAutoGenerationService Service, InMemoryReportStore Store) NewService(
        RecordingLogger logger, FailingPositionSource positions, ReportAutoGenerationOptions options,
        DateTimeOffset? now = null, FailingStageSource? stages = null)
    {
        var store = new InMemoryReportStore();
        var probe = new ReportDependencyProbe();
        positions.Probe = probe;
        if (stages is not null)
            stages.Probe = probe;

        var services = new ServiceCollection()
            .AddSingleton<IReportStore>(store)
            .AddSingleton<IPeriodFillSource, NoOpPeriodFillSource>()
            .AddSingleton<IOpenPositionSource>(positions)
            .AddSingleton<IClock>(new FixedClock(now ?? WedAfterClose))
            .AddSingleton<IReportNarrativeDrafter, StubDrafter>()
            .AddSingleton<IReportDraftPresentedNotifier>(new NoOpReportDraftPresentedNotifier())
            .AddSingleton(new ReportAutoGenerationSettings())
            .AddSingleton(probe)
            .AddSingleton(new ReportGenerationDeferralTracker(options.ToDeferralSettings()))
            .AddScoped<ReportDraftService>()
            .AddScoped<ReportAutoGenerator>();

        if (stages is not null)
            services.AddSingleton<IStageProgressSource>(stages);

        var provider = services.BuildServiceProvider();

        var service = new ReportAutoGenerationService(
            provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(options), logger);
        return (service, store);
    }

    // #866: 月報だけが使う入力（運用段階）の供給元。一過性の失敗を観測へ残し、未供給（null）を返す。
    private sealed class FailingStageSource : IStageProgressSource
    {
        public ReportDependencyProbe? Probe { get; set; }

        public Task<AiStockTrading.Shared.Kernel.Trading.TradingStage?> GetCurrentStageAsync(
            CancellationToken cancellationToken = default)
        {
            Probe!.Record(
                "risk-ledger", ReportDependencyFailureKind.ServiceTokenUnavailable, transient: true,
                "サービストークンを取得できない");
            return Task.FromResult<AiStockTrading.Shared.Kernel.Trading.TradingStage?>(null);
        }
    }

    // 建玉の供給元。鎖（ReportDependencyHandler）が記録するのと同じ形で失敗を観測へ残し、未供給（null）を返す。
    private sealed class FailingPositionSource : IOpenPositionSource
    {
        public ReportDependencyProbe? Probe { get; set; }

        public bool Fail { get; init; } = true;

        public bool Transient { get; init; }

        public Task<IReadOnlyList<ReportPosition>?> GetOpenPositionsAsync(CancellationToken cancellationToken = default)
        {
            if (!Fail)
                return Task.FromResult<IReadOnlyList<ReportPosition>?>([]);

            Probe!.Record(
                "risk-ledger",
                Transient ? ReportDependencyFailureKind.ServiceTokenUnavailable : ReportDependencyFailureKind.HttpStatus,
                Transient,
                Transient ? "サービストークンを取得できない" : "403");
            return Task.FromResult<IReadOnlyList<ReportPosition>?>(null);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class StubDrafter : IReportNarrativeDrafter
    {
        public Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult("自動生成の散文");
    }

    private sealed class RecordingLogger : ILogger<ReportAutoGenerationService>
    {
        public List<string> Messages { get; } = [];

        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Messages.Add(message);
            if (logLevel == LogLevel.Warning)
                Warnings.Add(message);
        }
    }
}
