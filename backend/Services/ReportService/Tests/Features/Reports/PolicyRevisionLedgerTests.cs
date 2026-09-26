using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Common.Abstractions;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Infrastructure.Persistence;
using Xunit;

namespace ReportService.Tests;

// FR-14, ADR-0042 決定 3, #1024, IADR-0432（PR #1026 の監査で追加。T-10-1409〜T-10-1414）: `/policy` の試行の台帳の
// 列の長さ・同時要求での上限・JST の暦日の境界・失敗の数え方・並行更新での SaveFailed・保存後の台帳の失敗。
public class PolicyRevisionLedgerTests
{
    private static ReportDbContext InMemoryContext(string dbName) =>
        new(new DbContextOptionsBuilder<ReportDbContext>().UseInMemoryDatabase(dbName).Options);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private static readonly PolicyRevisionProposal Proposal = new("押し目買いを優先する", [], null);

    private sealed class CountingReviser(Action? duringCall = null) : IReportPolicyReviser
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public async Task<PolicyRevisionOutcome> ReviseAsync(PolicyRevisionContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            duringCall?.Invoke();
            await Task.Delay(20, cancellationToken);
            return PolicyRevisionOutcome.Proposed(Proposal, null);
        }
    }

    private static PolicyRevisionAttempt Attempt(DateOnly day) => new(Guid.NewGuid(), DateTimeOffset.UnixEpoch, day, "developer", "daily-2026-09-25");

    private static TradingReport Draft(string key = "daily-2026-09-25") => new()
    {
        PeriodKey = key,
        Kind = ReportKind.Daily,
        PeriodStart = new DateOnly(2026, 9, 25),
        PolicySummary = "元の方針",
    };

    // T-10-1409（監査 1）: 最大の案（理由 200 字の日本語 × 10 件）の JSON は日本語を逃がさずに書かれ、列（EF のモデル＝Npgsql）は
    // 長さの上限を持たない text である（InMemory は長さを強制しないため、モデルのメタデータで固定する）。
    [Fact]
    public void 最大の案のJSONが列に収まる()
    {
        var changes = Enumerable.Range(0, 10)
            .Select(i => new WatchlistChangeSuggestion(
                i < 5 ? WatchlistChangeAction.Add : WatchlistChangeAction.Remove, $"SY{(char)('A' + i)}", new string((char)('あ' + i), 200)))
            .ToList();

        var json = ReportPolicyRevisionService.SerializeChanges(changes);

        json.Should().NotContain("\\u", "日本語を \\uXXXX（6 文字）へ逃がさない");
        json.Should().Contain(new string('あ', 200));

        using var npgsql = new ReportDbContext(
            new DbContextOptionsBuilder<ReportDbContext>().UseNpgsql("Host=localhost;Database=report_svc").Options);
        var property = npgsql.Model.FindEntityType(typeof(PolicyRevisionAttemptRow))!.FindProperty(nameof(PolicyRevisionAttemptRow.WatchlistChangesJson))!;
        property.GetColumnType().Should().Be("text");
        (property.GetMaxLength() ?? int.MaxValue).Should().BeGreaterThanOrEqualTo(json.Length);
    }

    // T-10-1410（監査 2）: 同じ暦日の TryBegin を同時に 16 本走らせても、上限 1 で書けるのは 1 本だけ（台帳 2 種）。
    [Fact]
    public async Task 同時の要求でも上限を超えて書かない()
    {
        var day = new DateOnly(2026, 9, 28);

        var memory = new InMemoryPolicyRevisionLedger();
        (await RaceAsync(_ => memory.TryBegin(Attempt(day), 1))).Should().Be(1);
        memory.CountOn(day).Should().Be(1);

        var dbName = Guid.NewGuid().ToString();
        (await RaceAsync(_ =>
        {
            using var db = InMemoryContext(dbName);
            return new EfPolicyRevisionLedger(db).TryBegin(Attempt(day), 1);
        })).Should().Be(1);
        using var check = InMemoryContext(dbName);
        new EfPolicyRevisionLedger(check).CountOn(day).Should().Be(1);
    }

    private static async Task<int> RaceAsync(Func<int, PolicyRevisionBeginResult> begin)
    {
        const int N = 16;
        using var barrier = new Barrier(N);
        var tasks = Enumerable.Range(0, N).Select(i => Task.Run(() =>
        {
            barrier.SignalAndWait();
            return begin(i);
        }));
        var results = await Task.WhenAll(tasks);
        return results.Count(r => r.Begun);
    }

    // T-10-1410（サービス）: 上限 1 で `/policy` を同時に 8 本送っても、LLM は 1 回しか呼ばれない。
    [Fact]
    public async Task 同時の改訂でもLLMは上限の回数しか呼ばれない()
    {
        var store = new InMemoryReportStore();
        store.UpsertDraft(Draft(), 0);
        var reviser = new CountingReviser();
        var service = new ReportPolicyRevisionService(
            store, new FixedClock(new DateTimeOffset(2026, 9, 27, 1, 0, 0, TimeSpan.Zero)), reviser,
            new PolicyRevisionSchedule(new ReportScheduleOptions(), AutoDailyEnabled: true),
            new InMemoryPolicyRevisionLedger(), new PolicyRevisionLimit(1), NullLogger<ReportPolicyRevisionService>.Instance);

        using var barrier = new Barrier(8);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            try
            {
                return (await service.ReviseAsync("daily-2026-09-25", "積極的に", "developer")).Status;
            }
            catch (ReportService.Common.Exceptions.ReportConcurrencyException)
            {
                return PolicyRevisionStatus.Proposed; // 上限内の 1 本が保存の競合になる形は数に含めない（LLM は呼ばれている）
            }
        })));

        reviser.Calls.Should().Be(1);
        results.Count(s => s == PolicyRevisionStatus.DailyLimitReached).Should().Be(7);
    }

    // T-10-1411（監査 3）: 回数は JST の暦日で数える。14:59 UTC（JST 23:59）の試行と 15:00 UTC（JST 翌日 00:00）の試行は別の日。
    [Fact]
    public async Task 回数はJSTの暦日で数える()
    {
        var store = new InMemoryReportStore();
        store.UpsertDraft(Draft(), 0);
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 27, 14, 59, 0, TimeSpan.Zero));
        var ledger = new InMemoryPolicyRevisionLedger();
        var service = new ReportPolicyRevisionService(
            store, clock, new CountingReviser(), new PolicyRevisionSchedule(new ReportScheduleOptions(), AutoDailyEnabled: true),
            ledger, new PolicyRevisionLimit(1), NullLogger<ReportPolicyRevisionService>.Instance);

        (await service.ReviseAsync("daily-2026-09-25", "a", "developer")).Status.Should().Be(PolicyRevisionStatus.Proposed);
        (await service.ReviseAsync("daily-2026-09-25", "b", "developer")).Status.Should().Be(PolicyRevisionStatus.DailyLimitReached);

        clock.UtcNow = new DateTimeOffset(2026, 9, 27, 15, 0, 0, TimeSpan.Zero);
        (await service.ReviseAsync("daily-2026-09-25", "c", "developer")).Status.Should().Be(PolicyRevisionStatus.Proposed);

        ledger.CountOn(new DateOnly(2026, 9, 27)).Should().Be(1);
        ledger.CountOn(new DateOnly(2026, 9, 28)).Should().Be(1);
    }

    // T-10-1412（監査 4）: EF の台帳は失敗した試行（AiFailed・SaveFailed・Pending）も上限に数える。
    [Fact]
    public void EFの台帳は失敗した試行も数える()
    {
        var day = new DateOnly(2026, 9, 28);
        using var db = InMemoryContext(Guid.NewGuid().ToString());
        var ledger = new EfPolicyRevisionLedger(db);

        var failed = Attempt(day);
        ledger.TryBegin(failed, 3).Begun.Should().BeTrue();
        ledger.Complete(failed.Id, PolicyRevisionAttemptOutcome.AiFailed, null, null);
        var saveFailed = Attempt(day);
        ledger.TryBegin(saveFailed, 3).Begun.Should().BeTrue();
        ledger.Complete(saveFailed.Id, PolicyRevisionAttemptOutcome.SaveFailed, null, null);
        ledger.TryBegin(Attempt(day), 3).Begun.Should().BeTrue(); // Pending のまま

        ledger.CountOn(day).Should().Be(3);
        var refused = ledger.TryBegin(Attempt(day), 3);
        (refused.Begun, refused.UsedBefore).Should().Be((false, 3));
    }

    // T-10-1413（監査 5）: LLM を待つ間の並行更新で EF の保存が DbUpdateConcurrencyException になっても、同じ DbContext の
    // 台帳へ SaveFailed を書ける（失敗した行の変更を追跡したまま残さない）。例外は上へ（エンドポイントが 409）。
    [Fact]
    public async Task 並行更新の失敗でもSaveFailedを記録できる()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seed = InMemoryContext(dbName))
            new EfReportStore(seed).UpsertDraft(Draft(), 0);

        using var db = InMemoryContext(dbName);
        var ledger = new EfPolicyRevisionLedger(db);
        var reviser = new CountingReviser(() =>
        {
            using var other = InMemoryContext(dbName);
            new EfReportStore(other).UpsertDraft(Draft() with { PolicySummary = "並行して書かれた方針" }, 1);
        });
        var service = new ReportPolicyRevisionService(
            new EfReportStore(db), new FixedClock(new DateTimeOffset(2026, 9, 27, 1, 0, 0, TimeSpan.Zero)), reviser,
            new PolicyRevisionSchedule(new ReportScheduleOptions(), AutoDailyEnabled: true),
            ledger, new PolicyRevisionLimit(10), NullLogger<ReportPolicyRevisionService>.Instance);

        var act = () => service.ReviseAsync("daily-2026-09-25", "積極的に", "developer");

        (await act.Should().ThrowAsync<Exception>()).Which.Should().Match(e =>
            e is DbUpdateConcurrencyException || e is ReportService.Common.Exceptions.ReportConcurrencyException);
        using var check = InMemoryContext(dbName);
        check.PolicyRevisionAttempts.Single().Outcome.Should().Be(PolicyRevisionAttemptOutcome.SaveFailed);
        new EfReportStore(check).Get("daily-2026-09-25")!.Report.PolicySummary.Should().Be("並行して書かれた方針");
    }

    // T-10-1414（監査 1）: 保存の後に台帳の書き込みが失敗しても、保存済みのドラフトを 500 にしない（案として返し、提示する）。
    [Fact]
    public async Task 保存後の台帳の失敗で保存済みのドラフトを失敗にしない()
    {
        var store = new InMemoryReportStore();
        store.UpsertDraft(Draft(), 0);
        var service = new ReportPolicyRevisionService(
            store, new FixedClock(new DateTimeOffset(2026, 9, 27, 1, 0, 0, TimeSpan.Zero)), new CountingReviser(),
            new PolicyRevisionSchedule(new ReportScheduleOptions(), AutoDailyEnabled: true),
            new CompleteFailingLedger(), new PolicyRevisionLimit(10), NullLogger<ReportPolicyRevisionService>.Instance);

        var result = await service.ReviseAsync("daily-2026-09-25", "積極的に", "developer");

        (result.Status, result.Version, result.Presented).Should().Be((PolicyRevisionStatus.Proposed, 2, true));
        store.Get("daily-2026-09-25")!.Report.PolicySummary.Should().Be("押し目買いを優先する");
    }

    // T-10-1424（PR #1026 の再監査 F1）: EF のストアと台帳が DbContext を共有し、保存の後の台帳の完了（Modified の試行の行）の保存が
    // 失敗しても、続く提示の保存がその行を保存し直して落ちない——保存済みの案は**提示される**（承認待ち）。
    [Fact]
    public async Task EFで保存後の台帳の失敗があっても案は提示される()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seed = InMemoryContext(dbName))
            new EfReportStore(seed).UpsertDraft(Draft(), 0);

        var interceptor = new FailingSave(e => e.Entity is PolicyRevisionAttemptRow && e.State == EntityState.Modified, times: 1);
        using var db = new ReportDbContext(new DbContextOptionsBuilder<ReportDbContext>()
            .UseInMemoryDatabase(dbName).AddInterceptors(interceptor).Options);
        var service = new ReportPolicyRevisionService(
            new EfReportStore(db), new FixedClock(new DateTimeOffset(2026, 9, 27, 1, 0, 0, TimeSpan.Zero)), new CountingReviser(),
            new PolicyRevisionSchedule(new ReportScheduleOptions(), AutoDailyEnabled: true),
            new EfPolicyRevisionLedger(db), new PolicyRevisionLimit(10), NullLogger<ReportPolicyRevisionService>.Instance);

        var result = await service.ReviseAsync("daily-2026-09-25", "積極的に", "developer");

        interceptor.Failures.Should().Be(1, "前提: 台帳の完了の保存が 1 回失敗した");
        (result.Status, result.Version, result.Presented).Should().Be((PolicyRevisionStatus.Proposed, 2, true));
        using var check = InMemoryContext(dbName);
        new EfReportStore(check).GetReview("daily-2026-09-25")!.State.Should().Be(ReviewState.PendingApproval);
        check.PolicyRevisionAttempts.Single().Outcome.Should().Be(PolicyRevisionAttemptOutcome.Pending, "台帳の完了は書けていない（上限には数える）");
    }

    // T-10-1425（PR #1026 の再監査 F2）: 報告書の保存が並行更新以外の DbUpdateException で失敗したら、下書きは**保存されず**、
    // 台帳は SaveFailed（SaveFailed の書き込みが失敗した下書きを一緒に保存しない）。例外は上へ。
    [Fact]
    public async Task 並行更新以外の保存の失敗でも下書きを保存せずSaveFailedを記録する()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seed = InMemoryContext(dbName))
            new EfReportStore(seed).UpsertDraft(Draft(), 0);

        var interceptor = new FailingSave(e => e.Entity is ReportRow && e.State == EntityState.Modified, times: 1);
        using var db = new ReportDbContext(new DbContextOptionsBuilder<ReportDbContext>()
            .UseInMemoryDatabase(dbName).AddInterceptors(interceptor).Options);
        var service = new ReportPolicyRevisionService(
            new EfReportStore(db), new FixedClock(new DateTimeOffset(2026, 9, 27, 1, 0, 0, TimeSpan.Zero)), new CountingReviser(),
            new PolicyRevisionSchedule(new ReportScheduleOptions(), AutoDailyEnabled: true),
            new EfPolicyRevisionLedger(db), new PolicyRevisionLimit(10), NullLogger<ReportPolicyRevisionService>.Instance);

        var act = () => service.ReviseAsync("daily-2026-09-25", "積極的に", "developer");

        await act.Should().ThrowAsync<DbUpdateException>();
        using var check = InMemoryContext(dbName);
        var saved = new EfReportStore(check).Get("daily-2026-09-25")!;
        (saved.Version, saved.Report.PolicySummary).Should().Be((1, "元の方針"), "失敗した下書きを保存しない");
        check.PolicyRevisionAttempts.Single().Outcome.Should().Be(PolicyRevisionAttemptOutcome.SaveFailed);
    }

    // T-10-1426（PR #1026 の再監査 F4）: 台帳を閉じられなかったことを黙って捨てず、試行と結果を添えて Error で残す。
    [Fact]
    public async Task 台帳を閉じられなければ試行と結果をErrorで残す()
    {
        var store = new InMemoryReportStore();
        store.UpsertDraft(Draft(), 0);
        var logger = new RecordingLogger();
        var service = new ReportPolicyRevisionService(
            store, new FixedClock(new DateTimeOffset(2026, 9, 27, 1, 0, 0, TimeSpan.Zero)), new CountingReviser(),
            new PolicyRevisionSchedule(new ReportScheduleOptions(), AutoDailyEnabled: true),
            new CompleteFailingLedger(), new PolicyRevisionLimit(10), logger);

        await service.ReviseAsync("daily-2026-09-25", "積極的に", "developer");

        var entry = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error).Subject;
        entry.Exception.Should().BeOfType<InvalidOperationException>();
        entry.State.Should().Contain(kv => kv.Key == "AttemptId" && kv.Value is Guid && (Guid)kv.Value != Guid.Empty);
        entry.State.Should().Contain(kv => kv.Key == "Outcome" && Equals(kv.Value, PolicyRevisionAttemptOutcome.Proposed));
    }

    // 条件に合う変更を含む保存を指定の回数だけ DbUpdateException で失敗させる。
    private sealed class FailingSave(Func<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry, bool> when, int times) : SaveChangesInterceptor
    {
        public int Failures { get; private set; }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            if (Failures < times && eventData.Context!.ChangeTracker.Entries().Any(when))
            {
                Failures++;
                throw new DbUpdateException("保存に失敗した（模擬）");
            }

            return result;
        }
    }

    private sealed class RecordingLogger : ILogger<ReportPolicyRevisionService>
    {
        public List<(LogLevel Level, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>> State)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception, state as IReadOnlyList<KeyValuePair<string, object?>> ?? []));
    }

    private sealed class CompleteFailingLedger : IPolicyRevisionLedger
    {
        private readonly InMemoryPolicyRevisionLedger _inner = new();

        public int CountOn(DateOnly jstDate) => _inner.CountOn(jstDate);

        public Guid Begin(PolicyRevisionAttempt attempt) => _inner.Begin(attempt);

        public PolicyRevisionBeginResult TryBegin(PolicyRevisionAttempt attempt, int dailyLimit) => _inner.TryBegin(attempt, dailyLimit);

        public void Complete(Guid id, PolicyRevisionAttemptOutcome outcome, int? reportVersion, string? watchlistChangesJson) =>
            throw new InvalidOperationException("台帳の DB が落ちた（模擬）");

        public PolicyRevisionAttempt? Find(Guid id) => _inner.Find(id);

        public PolicyRevisionAttempt? FindProposed(string periodKey, int reportVersion) => _inner.FindProposed(periodKey, reportVersion);

        public bool RecordWatchlistApply(Guid id, string resultJson, DateTimeOffset recordedAt) =>
            _inner.RecordWatchlistApply(id, resultJson, recordedAt);

        public void MarkProposalConfirmed(Guid id, DateTimeOffset confirmedAt) => _inner.MarkProposalConfirmed(id, confirmedAt);
    }
}
