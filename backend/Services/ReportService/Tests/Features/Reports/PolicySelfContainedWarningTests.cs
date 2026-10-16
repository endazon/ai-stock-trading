using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Common.Abstractions;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Infrastructure.ExternalServices;
using ReportService.Infrastructure.Persistence;
using Xunit;

namespace ReportService.Tests;

// FR-04, FR-07, ADR-0048 決定 4, ADR-0051 決定 2, #1365, IADR-0544 決定 1〜3: 日報の方針は売買条件のしきい値を自己完結で書く。
// 方針の改訂 LLM へ「他の報告書・期間キーを参照で済ませず、数値を書き写す」規律を示し、参照で済ませた日報の方針は
// **確定の前に警告する**（確定は止めない。方針の本文へ入れない）。受け入れ基準 A1〜A5（作業仕様書 20261016_1365）。
public class PolicySelfContainedWarningTests
{
    // PoC の実測（2026-10-15）の形。
    private const string ReferencingPolicy = "週次方針 weekly-2026-W41 のしきい値に従い、条件が揃えば押し目で買う。\n利確: 全銘柄 +8%";
    private const string SelfContainedPolicy = "AAPL: 前日比 -3% 以下で買い。MSFT: 当日始値比 -2% 以下で買い。\n利確: 全銘柄 +8%";

    // ---- A1: 改訂のプロンプト ----

    [Fact]
    public void 日報の改訂のプロンプトは参照で済ませずしきい値を書き写す規律をデータより前に載せる()
    {
        var prompt = PolicyRevisionPromptBuilder.Build(new PolicyRevisionContext(
            ReportKind.Daily, "daily-2026-10-15", ReferencingPolicy,
            new ParentPolicyReference("weekly-2026-W41", "数値目標: -200 〜 +500 USD\nAAPL は前日比 -3% で買う"), "指示"));

        prompt.Should().Contain(PolicyRevisionPromptBuilder.SelfContainedHeading)
            .And.Contain(PolicyRevisionPromptBuilder.SelfContainedRule)
            .And.Contain(PolicyRevisionPromptBuilder.CopyThresholdsRule)
            .And.Contain(PolicyRevisionPromptBuilder.DailyParentPolicyInstruction);
        PolicyRevisionPromptBuilder.SelfContainedRule.Should().Contain("weekly-").And.Contain("参照で済ませない");
        PolicyRevisionPromptBuilder.CopyThresholdsRule.Should().Contain("書き写し").And.Contain("警告");
        prompt.IndexOf(PolicyRevisionPromptBuilder.SelfContainedHeading, StringComparison.Ordinal)
            .Should().BeLessThan(prompt.IndexOf("ownerInstruction:", StringComparison.Ordinal));
        // 案内の例（書き写した形）は警告に掛からない。利確の行の規律（説明の文で「利確」を使わない）にも触れない。
        PolicySelfContainedCheck.WarningFor(ReportKind.Daily, "AAPL: 前日比 -3% 以下で買い").Should().BeNull();
        PolicyRevisionPromptBuilder.CopyThresholdsRule.Should().NotContain("利確");
        PolicyRevisionPromptBuilder.SelfContainedRule.Should().NotContain("利確");
    }

    [Theory]
    [InlineData(ReportKind.Weekly)]
    [InlineData(ReportKind.Monthly)]
    public void 週報と月報の改訂のプロンプトには自己完結の案内を出さない(ReportKind kind)
    {
        var prompt = PolicyRevisionPromptBuilder.Build(new PolicyRevisionContext(
            kind, "weekly-2026-W41", "方針", new ParentPolicyReference("monthly-2026-10", "月の方針"), "指示"));

        prompt.Should().NotContain(PolicyRevisionPromptBuilder.SelfContainedHeading)
            .And.NotContain(PolicyRevisionPromptBuilder.DailyParentPolicyInstruction);
    }

    // ---- A2・A3: 判定 ----

    [Theory]
    [InlineData(ReferencingPolicy)]
    [InlineData("weekly-2026-W41 の条件で買う。")]
    [InlineData("ｗｅｅｋｌｙ－２０２６－Ｗ４１ の条件で買う。")]
    [InlineData("monthly-2026-10 の配分に従う。")]
    [InlineData("daily-2026-10-14 と同じ条件で買う。")]
    [InlineData("週報のしきい値に従う。")]
    [InlineData("月報の資産配分の範囲で買う。")]
    [InlineData("前日の日報の条件を継続する。")]
    [InlineData("上位方針のとおり押し目で買う。")]
    [InlineData("週次の方針に沿って買う。")]
    [InlineData("前日の方針を継続する。")]
    public void 他の報告書か期間キーを参照する日報の方針は警告する(string policy)
    {
        PolicySelfContainedCheck.WarningFor(ReportKind.Daily, policy).Should().Be(PolicySelfContainedCheck.Warning);
    }

    [Theory]
    [InlineData(SelfContainedPolicy)]
    [InlineData("本日報告された決算を材料に、AAPL: 前日比 -3% 以下で買い。")]
    [InlineData("前日報道のニュースだけでは買わない。")]
    [InlineData(null)]
    [InlineData("")]
    // 生成器の注記行（継続の初稿に付く）は方針の実体ではない。
    [InlineData("AAPL: 前日比 -3% 以下で買い。\n\n上位方針（週報）は確定済みのものがないため参照していません。")]
    [InlineData("参照できる確定済みの日報方針がありません。")]
    public void 自己完結した日報の方針と生成器の注記行は警告しない(string? policy)
    {
        PolicySelfContainedCheck.WarningFor(ReportKind.Daily, policy).Should().BeNull();
    }

    [Theory]
    [InlineData(ReportKind.Weekly)]
    [InlineData(ReportKind.Monthly)]
    public void 週報と月報の方針は上位を参照しても警告しない(ReportKind kind)
    {
        PolicySelfContainedCheck.WarningFor(kind, "monthly-2026-10 の月報の方針に沿う。").Should().BeNull();
    }

    [Fact]
    public void 警告の文は印で始まり確定は止めないと書き方針の文字列を含めない()
    {
        PolicySelfContainedCheck.Warning.Should().StartWith(ReportSummaryMarkers.PolicyExternalReferencePrefix)
            .And.Contain("確定はできます")
            .And.Contain("書き写して");
        PolicySelfContainedCheck.Warning.Should().NotContain("\n");
    }

    [Fact]
    public void 警告の並べは空を除いて行で並べ_1つも無ければnull()
    {
        PolicyDraftWarnings.Join(null, "a", "", "b").Should().Be("a\nb");
        PolicyDraftWarnings.Join(null, null).Should().BeNull();
    }

    // ---- A4: /policy の改訂案 ----

    private static readonly DateTimeOffset SundayMorning = new(2026, 9, 27, 1, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class FakeReviser(string policy) : IReportPolicyReviser
    {
        public Task<PolicyRevisionOutcome> ReviseAsync(PolicyRevisionContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(PolicyRevisionOutcome.Proposed(new PolicyRevisionProposal(policy, [], "説明"), null));
    }

    private static (ReportPolicyRevisionService Service, InMemoryReportStore Store) CreateRevision(string proposedPolicy)
    {
        var store = new InMemoryReportStore();
        var service = new ReportPolicyRevisionService(
            store, new FixedClock(SundayMorning), new FakeReviser(proposedPolicy),
            new PolicyRevisionSchedule(new ReportScheduleOptions(), AutoDailyEnabled: true),
            new InMemoryPolicyRevisionLedger(), new PolicyRevisionLimit(10),
            NullLogger<ReportPolicyRevisionService>.Instance);
        store.UpsertDraft(new TradingReport
        {
            PeriodKey = "daily-2026-09-25",
            Kind = ReportKind.Daily,
            PeriodStart = new DateOnly(2026, 9, 25),
            PolicySummary = "自動生成の方針",
            Body = "# 日報",
        }, 0);
        store.ApplyReview("daily-2026-09-25", new ReviewCommand(ReviewAction.Present, "scheduler", 1));
        return (service, store);
    }

    [Fact]
    public async Task 参照で済ませた案は保存し承認待ちにしたうえで案内文と本文の記録で警告する()
    {
        var (service, store) = CreateRevision(ReferencingPolicy);

        var result = await service.ReviseAsync("daily-2026-09-25", "週報に合わせて", "developer");

        (result.Status, result.Presented).Should().Be((PolicyRevisionStatus.Proposed, true), "警告は確定も提示も止めない");
        result.Message.Split('\n').Should().Contain(PolicySelfContainedCheck.Warning);

        var saved = store.Get("daily-2026-09-25")!.Report;
        saved.PolicySummary.Should().Be(ReferencingPolicy, "警告を方針の本文へ入れない");
        var warningAt = saved.Body.IndexOf(PolicySelfContainedCheck.Warning, StringComparison.Ordinal);
        warningAt.Should().BeGreaterThan(0);
        warningAt.Should().BeLessThan(saved.Body.IndexOf("### 改訂後の方針（AI の案）", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 利確の警告と参照の警告は両方出る()
    {
        var (service, _) = CreateRevision("週報のしきい値に従う。");

        var result = await service.ReviseAsync("daily-2026-09-25", "週報に合わせて", "developer");

        var lines = result.Message.Split('\n');
        lines.Should().Contain(PolicyTakeProfitCheck.Warning).And.Contain(PolicySelfContainedCheck.Warning);
    }

    [Fact]
    public async Task 自己完結した案は参照の警告を出さない()
    {
        var (service, store) = CreateRevision(SelfContainedPolicy);

        var result = await service.ReviseAsync("daily-2026-09-25", "数値で", "developer");

        result.Message.Should().NotContain(ReportSummaryMarkers.PolicyExternalReferencePrefix);
        store.Get("daily-2026-09-25")!.Report.Body.Should().NotContain(ReportSummaryMarkers.PolicyExternalReferencePrefix);
    }

    // ---- A5: 自動生成の日報の初稿（継続） ----

    // 2026-07-08（水）16:00 JST。日報だけが生成境界を越えている時刻。
    private static readonly DateTimeOffset WedAfterClose = new(2026, 7, 8, 7, 0, 0, TimeSpan.Zero);

    private sealed class StubDrafter : IReportNarrativeDrafter
    {
        public Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult("自動生成の散文");
    }

    private sealed class RecordingNotifier : IReportDraftPresentedNotifier
    {
        public List<PresentedReportNotice> Notices { get; } = [];

        public Task NotifyAsync(PresentedReportNotice notice, CancellationToken cancellationToken = default)
        {
            Notices.Add(notice);
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(ReferencingPolicy, true)]
    [InlineData(SelfContainedPolicy, false)]
    public async Task 日報の初稿の方針が他の報告書を参照していれば提示の要約で警告する(string previousPolicy, bool warns)
    {
        var store = new InMemoryReportStore();
        var version = store.UpsertDraft(new TradingReport
        {
            PeriodKey = "daily-2026-07-07",
            Kind = ReportKind.Daily,
            PeriodStart = new DateOnly(2026, 7, 7),
            PolicySummary = previousPolicy,
        }, 0);
        store.Confirm("daily-2026-07-07", version, DateTimeOffset.UnixEpoch);
        var notifier = new RecordingNotifier();
        var generator = new ReportAutoGenerator(
            store, new ReportDraftService(new StubDrafter()), new NoOpPeriodFillSource(), new FixedClock(WedAfterClose),
            new ReportAutoGenerationSettings(), notifier);

        await generator.RunOnceAsync();

        var notice = notifier.Notices.Should().ContainSingle().Which;
        notice.Summary.Contains(PolicySelfContainedCheck.Warning, StringComparison.Ordinal).Should().Be(warns);
        // 上位（週報）が無い初稿には生成器の注記行が付くが、それだけでは警告しない（自己完結の方針で warns=false）。
        store.Get("daily-2026-07-08")!.Report.PolicySummary.Should().Contain("上位方針（週報）は確定済みのものがないため参照していません。")
            .And.NotContain(ReportSummaryMarkers.PolicyExternalReferencePrefix);
        store.GetReview("daily-2026-07-08")!.State.Should().Be(ReviewState.PendingApproval);
    }
}
