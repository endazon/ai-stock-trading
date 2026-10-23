using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Common.Abstractions;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Hosted;
using ReportService.Infrastructure.Persistence;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
using AppSvc = ReportService.Features.Reports.ReportAppService;

namespace ReportService.Tests;

// 🔴 FR-07, UC-03, #1423 M-6, ADR-0075 決定1〜4, IADR-0583:
// **無応答時の設定が「停止」なら、承認待ちの日報が期限（翌営業日の米国市場の開場）を過ぎた間は、方針の照会に停止の印を載せる。
// 方針そのものは直近の確定済み日報のまま返す。継続・停止のどちらでも、期限を過ぎたら期間キーごとに 1 回知らせる。**
public class DailyReportNoResponseTests
{
    // 日報 07-10（金）の生成境界は 16:00 JST ＝ 07-10 03:00 ET。次の米国の開場は同じ日の 09:30 ET（夏時間）＝ 13:30Z。
    private static readonly DateTimeOffset FridayOpen = new(2026, 7, 10, 13, 30, 0, TimeSpan.Zero);

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private static TradingReport Daily(DateOnly day, string policy = "方針") => new()
    {
        PeriodKey = $"daily-{day:yyyy-MM-dd}",
        Kind = ReportKind.Daily,
        PeriodStart = day,
        PolicySummary = policy,
    };

    private static void Confirm(InMemoryReportStore store, DateOnly day, DateTimeOffset at)
    {
        var version = store.UpsertDraft(Daily(day, $"{day:MM-dd} の方針"), 0);
        store.Confirm($"daily-{day:yyyy-MM-dd}", version, at);
    }

    private static void Present(InMemoryReportStore store, DateOnly day)
    {
        var version = store.UpsertDraft(Daily(day, $"{day:MM-dd} の方針（承認待ち）"), 0);
        store.ApplyReview($"daily-{day:yyyy-MM-dd}", new ReviewCommand(ReviewAction.Present, "auto", version));
    }

    private static DailyReportNoResponseEvaluator Evaluator(IReportStore store, IClock clock, NoResponseBehavior behavior) =>
        new(store, clock, new ReportNoResponseSettings(behavior, new ReportScheduleOptions()));

    private static (InMemoryReportStore Store, MutableClock Clock) Pending()
    {
        var store = new InMemoryReportStore();
        Confirm(store, new DateOnly(2026, 7, 9), new DateTimeOffset(2026, 7, 9, 8, 0, 0, TimeSpan.Zero));
        Present(store, new DateOnly(2026, 7, 10));
        return (store, new MutableClock(FridayOpen));
    }

    // 🔴 T-10-3054（境界の両側）: 期限の 1 秒前は応答待ち（印なし）、期限ちょうどから「停止」の印が立つ。方針は直近の確定済み日報のまま。
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(3600, true)]
    public void 停止の設定では期限を過ぎた承認待ちで方針は返したまま停止の印を立てる(int secondsFromDeadline, bool halted)
    {
        var (store, clock) = Pending();
        clock.UtcNow = FridayOpen.AddSeconds(secondsFromDeadline);
        var svc = new AppSvc(store, clock, noResponse: Evaluator(store, clock, NoResponseBehavior.Halt));

        var policy = svc.GetConfirmedDailyPolicy();

        policy.Should().NotBeNull("方針は直近の確定済み日報のまま返す（閉場前の手仕舞いを方針なしへ倒さない）");
        policy!.PeriodKey.Should().Be("daily-2026-07-09");
        policy.Summary.Should().Be("07-09 の方針");
        policy.NoResponseHalted.Should().Be(halted);
    }

    // T-10-3055（否定形）: 既定（継続）・確定済みの後・承認待ちが無い・効いている日報の再確定の下書き、では印を立てない。
    [Fact]
    public void 継続の設定では期限を過ぎても印を立てず_確定すれば印は消える()
    {
        var (store, clock) = Pending();
        clock.UtcNow = FridayOpen.AddHours(1);

        new AppSvc(store, clock, noResponse: Evaluator(store, clock, NoResponseBehavior.ContinueLastConfirmed))
            .GetConfirmedDailyPolicy()!.NoResponseHalted.Should().BeFalse("既定は継続");
        Evaluator(store, clock, NoResponseBehavior.ContinueLastConfirmed).Evaluate().Overdue.Should().BeTrue("継続でも通知の条件は満たす");

        var halt = new AppSvc(store, clock, noResponse: Evaluator(store, clock, NoResponseBehavior.Halt));
        halt.GetConfirmedDailyPolicy()!.NoResponseHalted.Should().BeTrue();

        // 利用者が日報を確定する → 自動で再開（ADR-0075 決定3）。
        store.Confirm("daily-2026-07-10", store.Get("daily-2026-07-10")!.Version, clock.UtcNow);
        var resumed = halt.GetConfirmedDailyPolicy()!;
        resumed.NoResponseHalted.Should().BeFalse();
        resumed.PeriodKey.Should().Be("daily-2026-07-10");
    }

    // T-10-3055（続き）: 効いている日報の再確定（/policy の改訂の下書き。開始日は効いている日報と同じ）は「無応答」ではない。
    [Fact]
    public void 効いている日報の再確定の下書きは承認待ちに数えない()
    {
        var store = new InMemoryReportStore();
        Confirm(store, new DateOnly(2026, 7, 10), new DateTimeOffset(2026, 7, 10, 8, 0, 0, TimeSpan.Zero));
        store.StartRevisionDraft(Daily(new DateOnly(2026, 7, 10), "改訂案"), store.Get("daily-2026-07-10")!.Version);
        var clock = new MutableClock(FridayOpen.AddDays(3));

        Evaluator(store, clock, NoResponseBehavior.Halt).Evaluate().Halted.Should().BeFalse();
    }

    // T-10-3056（境界の表・暦）: 期限は日報の生成境界の後の最初の米国の開場。休場日（独立記念日の振替 2026-07-03）は飛ばす。
    [Theory]
    [InlineData("2026-07-10", "2026-07-10T13:30:00Z")] // 金曜 → 同じ日の 09:30 ET（夏時間）
    [InlineData("2026-07-03", "2026-07-06T13:30:00Z")] // 米国休場（振替）→ 週明け月曜
    [InlineData("2026-07-11", "2026-07-13T13:30:00Z")] // 土曜の日報（あれば）→ 月曜
    [InlineData("2026-12-04", "2026-12-04T14:30:00Z")] // 冬時間 09:30 ET ＝ 14:30Z
    public void 期限は生成境界の後の最初の米国の開場(string day, string expected)
    {
        ReportNoResponsePolicy.DeadlineOf(DateOnly.Parse(day), new ReportScheduleOptions())
            .Should().Be(DateTimeOffset.Parse(expected));
    }

    // T-10-3057: 無応答の通知は期間キーごとに 1 回（継続・停止とも）。応答待ちの間・承認待ちが無い間は出さない。
    [Theory]
    [InlineData(NoResponseBehavior.Halt, true)]
    [InlineData(NoResponseBehavior.ContinueLastConfirmed, false)]
    public async Task 無応答の通知は期間キーごとに1回だけ発行する(NoResponseBehavior behavior, bool halted)
    {
        var (store, clock) = Pending();
        clock.UtcNow = FridayOpen.AddSeconds(-1);
        // 本番の Wolverine 配線（型名の共有 exchange へ発行・外部トランスポートは stub）で数える。
        using var factory = new ReportWorkerWebApplicationFactory();
        factory.CreateClient();
        var host = factory.Services;
        var services = new ServiceCollection();
        services.AddSingleton<IReportStore>(store);
        services.AddSingleton<IClock>(clock);
        services.AddSingleton(new ReportNoResponseSettings(behavior, new ReportScheduleOptions()));
        services.AddScoped<DailyReportNoResponseEvaluator>();
        services.AddScoped<IMessageBus>(_ => new Wolverine.Runtime.MessageBus(host.GetRequiredService<Wolverine.Runtime.IWolverineRuntime>()));
        using var provider = services.BuildServiceProvider();
        var monitor = new DailyReportNoResponseMonitorService(
            provider.GetRequiredService<IServiceScopeFactory>(), clock, TimeSpan.FromMinutes(5),
            NullLogger<DailyReportNoResponseMonitorService>.Instance);

        var session = await host.ExecuteAndWaitForTestAsync(async () =>
        {
            (await monitor.RunOnceAsync(CancellationToken.None)).Should().BeFalse("期限の前は応答待ち");
            clock.UtcNow = FridayOpen.AddMinutes(5);
            (await monitor.RunOnceAsync(CancellationToken.None)).Should().BeTrue();
            (await monitor.RunOnceAsync(CancellationToken.None)).Should().BeFalse("同じ期間キーは 1 回だけ");
        });

        var sent = session.Sent.MessagesOf<DailyReportNoResponse>().Should().ContainSingle().Subject;
        sent.Should().BeEquivalentTo(new
        {
            PeriodKey = "daily-2026-07-10",
            ReportDate = new DateOnly(2026, 7, 10),
            Deadline = FridayOpen,
            TradingHalted = halted,
            LastConfirmedPeriodKey = "daily-2026-07-09",
        });
    }
}
