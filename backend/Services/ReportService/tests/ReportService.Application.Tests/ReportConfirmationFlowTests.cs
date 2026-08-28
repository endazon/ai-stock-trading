using ReportService.Application.Adapters;
using ReportService.Application.Ports;
using ReportService.Application.Services;
using ReportService.Domain;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Application.Tests;

// FR-06, FR-07, #338, #310, UC-03〜05, ADR-0003, INDEX 決定29, 04_workflows/03_reporting-cycle:
// **確定フローの状態遷移**を、自動生成 → 提示 → 確定 の一連で固定する。
//
// 計画の明文（03_reporting-cycle 補足）: 「**確定前の方針は無効**: 取引サイクルは『確定済みの日報』のみを
// 参照する。ドラフト段階の方針では取引しない（FR-07）。」
//
// 🔴 **「確定後に『未確定』の文言が残らない」ことを、本文（YAML を含む）まで見て確かめる**（#310）。
// 決定29 により取引判断サービスは YAML ブロックを読むため、状態の食い違いは方針の採否そのものを誤らせる。
public class ReportConfirmationFlowTests
{
    // 2026-07-08（水）16:00 JST ＝ 07:00 UTC。日報だけが生成境界を越えている時刻。
    private static readonly DateTimeOffset WedAfterClose = new(2026, 7, 8, 7, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class StubDrafter : IReportNarrativeDrafter
    {
        public Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult("自動生成の散文");
    }

    private static ReportAutoGenerator NewGenerator(IReportStore store) =>
        new(store,
            new ReportDraftService(new StubDrafter()),
            new NoOpPeriodFillSource(),
            new FixedClock(WedAfterClose),
            new ReportAutoGenerationSettings());

    // --- 🔴 未確定の方針は取引へ適用されない ---

    // **否定形**: 自動生成しただけの（＝未確定の）方針は、取引が読む経路に現れない。
    [Fact]
    public async Task 自動生成しただけの方針は取引へ適用されない()
    {
        var store = new InMemoryReportStore();
        var svc = new ReportAppService(store, new FixedClock(WedAfterClose));

        await NewGenerator(store).RunOnceAsync();

        // ドラフトは存在する（生成は成功している）。
        store.List().Should().NotBeEmpty();
        // しかし取引が読む「確定済み日報の方針」は無い。
        svc.GetConfirmedDailyPolicy().Should().BeNull();
    }

    // **対の肯定形**: 確定すると、同じ方針が取引の読む経路へ現れる。
    // 否定形だけでは、経路そのものが壊れていても緑になる。
    [Fact]
    public async Task 確定した方針は取引へ適用される()
    {
        var store = new InMemoryReportStore();
        var svc = new ReportAppService(store, new FixedClock(WedAfterClose));

        await NewGenerator(store).RunOnceAsync();
        var daily = store.List().Single(r => r.Kind == ReportKind.Daily);
        var version = store.GetReview(daily.PeriodKey)!.Version;

        svc.Confirm(daily.PeriodKey, version, "owner")!.Transitioned.Should().BeTrue();

        var policy = svc.GetConfirmedDailyPolicy();
        policy.Should().NotBeNull();
        policy!.Summary.Should().Be(daily.PolicySummary);
    }

    // --- 🔴 #310: 確定後に「未確定」の文言が残らない ---

    [Fact]
    public async Task 確定した報告書の本文は未確定を名乗らない()
    {
        var store = new InMemoryReportStore();
        var svc = new ReportAppService(store, new FixedClock(WedAfterClose));

        await NewGenerator(store).RunOnceAsync();
        var daily = store.List().Single(r => r.Kind == ReportKind.Daily);

        // 前提: ドラフトの本文は draft を名乗っている。
        daily.Body.Should().Contain("status: draft");

        svc.Confirm(daily.PeriodKey, store.GetReview(daily.PeriodKey)!.Version, "owner");

        var confirmed = store.Get(daily.PeriodKey)!.Report;
        confirmed.Body.Split('\n').Should().NotContain("status: draft");
        confirmed.Body.Split('\n').Count(l => l == "status: fixed").Should().Be(2); // frontmatter と YAML の両方
        confirmed.Body.Should().NotContain("confirmed_at: null");
    }

    // 方針文そのものに、生成器の前置き・状態文言が残らない（IADR-0125 決定1 の回帰）。
    [Fact]
    public async Task 確定した方針文に生成器の状態文言が残らない()
    {
        var store = new InMemoryReportStore();
        var svc = new ReportAppService(store, new FixedClock(WedAfterClose));

        await NewGenerator(store).RunOnceAsync();
        var daily = store.List().Single(r => r.Kind == ReportKind.Daily);
        svc.Confirm(daily.PeriodKey, store.GetReview(daily.PeriodKey)!.Version, "owner");

        var policy = svc.GetConfirmedDailyPolicy()!.Summary;

        policy.Should().NotContain("（自動生成ドラフト・未確定）");
        policy.Should().NotContain("確定前に内容を見直してください");
    }

    // --- 無応答時の既定（03_reporting-cycle「無応答時の既定動作」） ---

    // 🔴 計画の明文: 「翌営業日開場までに応答がない場合は**直近の確定済み日報の方針を継続する**（既定）。」
    [Fact]
    public async Task 無応答のまま期限を過ぎたら直近の確定済み方針を継続する()
    {
        var store = new InMemoryReportStore();
        var svc = new ReportAppService(store, new FixedClock(WedAfterClose));

        await NewGenerator(store).RunOnceAsync();
        var daily = store.List().Single(r => r.Kind == ReportKind.Daily);
        var review = store.GetReview(daily.PeriodKey)!;

        // 提示済み・承認待ちのまま期限（翌営業日開場）を過ぎた。
        review.State.Should().Be(ReviewState.PendingApproval);

        var outcome = ReportNoResponsePolicy.Decide(
            now: WedAfterClose.AddDays(1),
            deadline: WedAfterClose.AddHours(17),
            hasPendingReview: true,
            behavior: NoResponseBehavior.ContinueLastConfirmed);

        outcome.Should().Be(NoResponseOutcome.ContinueLastConfirmed);

        // 継続とは「未確定の新方針を適用しない」ことである。
        svc.GetConfirmedDailyPolicy().Should().BeNull();
    }

    // 自動生成は確定しない（ADR-0003「完全無人での方針変更は行わない」）。
    [Fact]
    public async Task 自動生成は確定へ進まず承認待ちで止まる()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store).RunOnceAsync();

        foreach (var report in store.List())
        {
            report.State.Should().Be(ReportState.Draft);
            store.GetReview(report.PeriodKey)!.State.Should().Be(ReviewState.PendingApproval);
        }
    }
}
