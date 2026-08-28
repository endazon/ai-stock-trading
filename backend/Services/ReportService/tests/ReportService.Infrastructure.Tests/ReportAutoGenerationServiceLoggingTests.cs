using ReportService.Application.Adapters;
using ReportService.Application.Ports;
using ReportService.Application.Services;
using ReportService.Application.State;
using ReportService.Domain;
using ReportService.Infrastructure.Polling;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ReportService.Infrastructure.Tests;

// FR-06, IADR-0115 決定1, #280: 常駐が 1 巡回の結果をどうログへ落とすかを検証する。
// 提示が受理されなかった期間は「承認待ちに並ばない」ため運用者が気付ける必要があり、同一 PeriodKey について
// 「提示しました」と「提示が受理されませんでした」が両方出ると読み手が誤解する。
public class ReportAutoGenerationServiceLoggingTests
{
    // 2026-07-08（水）16:00 JST ＝ 07:00 UTC。日報だけが生成境界を越えている時刻。
    private static readonly DateTimeOffset WedAfterClose = new(2026, 7, 8, 7, 0, 0, TimeSpan.Zero);
    private const string PeriodKey = "daily-2026-07-08";

    [Fact]
    public async Task 提示まで到達した期間は提示済みとして記録する()
    {
        var logger = new RecordingLogger();

        await NewService(new InMemoryReportStore(), logger).RunOnceAsync(CancellationToken.None);

        logger.Messages.Should().ContainSingle(m => m.Contains("提示しました") && m.Contains(PeriodKey));
        logger.Messages.Should().NotContain(m => m.Contains("受理されませんでした"));
    }

    [Fact]
    public async Task 提示が受理されなかった期間に提示済みのログを出さない()
    {
        var logger = new RecordingLogger();

        await NewService(new RejectingPresentStore(), logger).RunOnceAsync(CancellationToken.None);

        logger.Messages.Should().ContainSingle(m => m.Contains("受理されませんでした") && m.Contains(PeriodKey));
        logger.Messages.Should().NotContain(m => m.Contains("提示しました"));
    }

    [Fact]
    public async Task 提示通知の発行に失敗したら警告として残す()
    {
        // IADR-0116 決定2: 確定依頼が届かないまま気付けない状態を作らない（通知を既定 true にした理由と整合させる）。
        var logger = new RecordingLogger();

        await NewService(new InMemoryReportStore(), logger, new ThrowingNotifier()).RunOnceAsync(CancellationToken.None);

        logger.Messages.Should().ContainSingle(m => m.Contains("提示通知を発行できませんでした") && m.Contains(PeriodKey));
        // ドラフト自体は承認待ちに並んでいるので、生成・提示は成功として記録される。
        logger.Messages.Should().Contain(m => m.Contains("提示しました"));
    }

    private static ReportAutoGenerationService NewService(
        IReportStore store,
        ILogger<ReportAutoGenerationService> logger,
        IReportDraftPresentedNotifier? notifier = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton<IPeriodFillSource, NoOpPeriodFillSource>()
            .AddSingleton<IClock>(new FixedClock(WedAfterClose))
            .AddSingleton<IReportNarrativeDrafter, StubDrafter>()
            .AddSingleton(notifier ?? new NoOpReportDraftPresentedNotifier())
            .AddSingleton(new ReportAutoGenerationSettings())
            .AddScoped<ReportDraftService>()
            .AddScoped<ReportAutoGenerator>()
            .BuildServiceProvider();

        return new ReportAutoGenerationService(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ReportAutoGenerationOptions()),
            logger);
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

    private sealed class ThrowingNotifier : IReportDraftPresentedNotifier
    {
        public Task NotifyAsync(PresentedReportNotice notice, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("バスへ到達できません");
    }

    // 提示（Present）だけが受理されないストア。生成は成功するが承認待ちへ進まない状況を作る。
    private sealed class RejectingPresentStore : IReportStore
    {
        private readonly InMemoryReportStore _inner = new();

        public ReviewDecision? ApplyReview(string periodKey, ReviewCommand command) =>
            new(new ReportReview(periodKey, ReviewState.Drafting, 1), Transitioned: false, ReviewRejectionReason.InvalidTransition);

        public VersionedReport? Get(string periodKey) => _inner.Get(periodKey);
        public IReadOnlyList<TradingReport> List() => _inner.List();
        public int UpsertDraft(TradingReport report, int expectedVersion) => _inner.UpsertDraft(report, expectedVersion);
        public ConfirmResult? Confirm(string periodKey, int expectedVersion, DateTimeOffset confirmedAt) =>
            _inner.Confirm(periodKey, expectedVersion, confirmedAt);
        public VersionedReport? GetLatestConfirmed(ReportKind kind) => _inner.GetLatestConfirmed(kind);
        public ReportReview? GetReview(string periodKey) => _inner.GetReview(periodKey);
    }

    private sealed class RecordingLogger : ILogger<ReportAutoGenerationService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
