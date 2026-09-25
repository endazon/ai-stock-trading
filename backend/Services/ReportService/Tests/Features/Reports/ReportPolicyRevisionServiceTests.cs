using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Common.Abstractions;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Infrastructure.Persistence;
using Xunit;

namespace ReportService.Tests;

// FR-07, FR-14, UC-03〜05, ADR-0003, #1016, IADR-0431 決定 1・4: 方針の改訂の対象決定・保存・提示（T-10-1311〜1319）。
// 🔴 **案が無ければ何も保存しない**・**確定はしない**・**確定済みは変えない**・**確定しても効かない日報は作らない**を固定する。
public class ReportPolicyRevisionServiceTests
{
    // 2026-09-27（日）10:00 JST ＝ 01:00 UTC。当日の日報キーは daily-2026-09-27。
    private static readonly DateTimeOffset SundayMorning = new(2026, 9, 27, 1, 0, 0, TimeSpan.Zero);
    private const string TodayKey = "daily-2026-09-27";

    private static readonly PolicyRevisionProposal Proposal = new(
        "押し目買いを優先する",
        [new WatchlistChangeSuggestion(WatchlistChangeAction.Add, "NVDA", "AI 需要"),
         new WatchlistChangeSuggestion(WatchlistChangeAction.Remove, "META", "決算前")],
        "指示どおり積極化");

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class FakeReviser(PolicyRevisionOutcome outcome) : IReportPolicyReviser
    {
        public List<PolicyRevisionContext> Calls { get; } = [];

        public Task<PolicyRevisionOutcome> ReviseAsync(PolicyRevisionContext context, CancellationToken cancellationToken = default)
        {
            Calls.Add(context);
            return Task.FromResult(outcome);
        }
    }

    private static (ReportPolicyRevisionService Service, InMemoryReportStore Store, FakeReviser Reviser) Create(
        PolicyRevisionOutcome? outcome = null)
    {
        var store = new InMemoryReportStore();
        var reviser = new FakeReviser(outcome ?? PolicyRevisionOutcome.Proposed(Proposal, null));
        var service = new ReportPolicyRevisionService(
            store, new FixedClock(SundayMorning), reviser, NullLogger<ReportPolicyRevisionService>.Instance);
        return (service, store, reviser);
    }

    private static void SeedConfirmedDaily(InMemoryReportStore store, string key, DateOnly start, string policy = "積極運用")
    {
        store.UpsertDraft(new TradingReport
        {
            PeriodKey = "weekly-2026-W39",
            Kind = ReportKind.Weekly,
            PeriodStart = new DateOnly(2026, 9, 21),
            PolicySummary = "週の方針",
            AssumptionsVersion = 3,
        }, 0);
        store.Confirm("weekly-2026-W39", 1, SundayMorning);

        store.UpsertDraft(new TradingReport
        {
            PeriodKey = key,
            Kind = ReportKind.Daily,
            PeriodStart = start,
            BasedOn = "weekly-2026-W39",
            PolicySummary = policy,
            AssumptionsVersion = 3,
        }, 0);
        store.Confirm(key, 1, SundayMorning);
    }

    // T-10-1311: 会話キーを省略すると当日（JST）の日報を、直近の確定済み日報を土台に新しく作り、承認待ちにする（確定はしない）。
    [Fact]
    public async Task 省略時は当日の日報を直近の確定済み日報から作り承認待ちにする()
    {
        var (service, store, reviser) = Create();
        SeedConfirmedDaily(store, "daily-2026-09-26", new DateOnly(2026, 9, 26));

        var result = await service.ReviseAsync(null, "もっと積極的に", "developer");

        result.Status.Should().Be(PolicyRevisionStatus.Proposed);
        (result.PeriodKey, result.Version, result.Created, result.Presented, result.AutoGenerationSkipped)
            .Should().Be((TodayKey, 1, true, true, true));

        var context = reviser.Calls.Should().ContainSingle().Which;
        context.CurrentPolicy.Should().Be("積極運用");
        context.ParentPolicy.Should().Be(new ParentPolicyReference("weekly-2026-W39", "週の方針"));
        context.Instruction.Should().Be("もっと積極的に");

        var saved = store.Get(TodayKey)!;
        saved.Report.State.Should().Be(ReportState.Draft, "確定はしない（ADR-0003）");
        saved.Report.PolicySummary.Should().Be("押し目買いを優先する");
        saved.Report.BasedOn.Should().Be("weekly-2026-W39");
        saved.Report.AssumptionsVersion.Should().Be(3, "前提条件の版は土台から引き継ぐ（発明しない）");
        store.GetReview(TodayKey)!.State.Should().Be(ReviewState.PendingApproval);
        store.GetLatestConfirmed(ReportKind.Daily)!.Report.PeriodKey.Should().Be("daily-2026-09-26", "確定するまで方針は変わらない");
    }

    // T-10-1312: 既存の未確定の報告書はその方針を土台に改訂し、版を 1 つ進める。本文へ改訂の記録を追記し既存の本文は残す。
    [Fact]
    public async Task 既存の未確定の報告書は改訂し版を進め本文に記録を残す()
    {
        var (service, store, reviser) = Create();
        store.UpsertDraft(new TradingReport
        {
            PeriodKey = "daily-2026-09-25",
            Kind = ReportKind.Daily,
            PeriodStart = new DateOnly(2026, 9, 25),
            PolicySummary = "自動生成の方針",
            Body = "# 日報\n本文",
            UnsuppliedInputs = [ReportInput.OpenPositions],
        }, 0);
        store.ApplyReview("daily-2026-09-25", new ReviewCommand(ReviewAction.Present, "scheduler", 1));

        var result = await service.ReviseAsync("daily-2026-09-25", "防御的に\n現金比率を上げて", "developer");

        (result.Status, result.Version, result.Created, result.Presented)
            .Should().Be((PolicyRevisionStatus.Proposed, 2, false, true));
        reviser.Calls.Single().CurrentPolicy.Should().Be("自動生成の方針");

        var saved = store.Get("daily-2026-09-25")!.Report;
        saved.PolicySummary.Should().Be("押し目買いを優先する");
        saved.Body.Should().StartWith("# 日報\n本文");
        saved.Body.Should().Contain("## 利用者の指示による方針の改訂（版 2）")
            .And.Contain("- 指示者: developer")
            .And.Contain("> 防御的に\n> 現金比率を上げて")
            .And.Contain("- 追加 NVDA（米国）: AI 需要")
            .And.Contain("- 除外 META（米国）: 決算前")
            .And.Contain("提示のみ。適用は設定画面から");
        saved.UnsuppliedInputs.Should().Equal([ReportInput.OpenPositions], "本文を差し替えても欠けた入力の記録は残す");
    }

    // T-10-1313: AI の案が作れなければ**何も保存しない**（新規も既存も）。
    [Fact]
    public async Task AIが失敗したら何も保存しない()
    {
        var (service, store, _) = Create(PolicyRevisionOutcome.Failed(PolicyRevisionFailure.TimedOut, "AI の応答が 60 秒以内に返りませんでした"));
        SeedConfirmedDaily(store, "daily-2026-09-26", new DateOnly(2026, 9, 26));

        var result = await service.ReviseAsync(null, "積極的に", "developer");

        result.Status.Should().Be(PolicyRevisionStatus.AiFailed);
        result.Message.Should().Contain("60 秒").And.Contain("方針は変わっていません");
        store.Get(TodayKey).Should().BeNull("案が無ければ行を作らない");
    }

    // T-10-1314: 確定済みの報告書は改訂しない（AI も呼ばない）。
    [Fact]
    public async Task 確定済みは改訂せずAIも呼ばない()
    {
        var (service, store, reviser) = Create();
        SeedConfirmedDaily(store, "daily-2026-09-26", new DateOnly(2026, 9, 26));

        var result = await service.ReviseAsync("daily-2026-09-26", "積極的に", "developer");

        result.Status.Should().Be(PolicyRevisionStatus.AlreadyConfirmed);
        reviser.Calls.Should().BeEmpty("費用を使わない");
        store.Get("daily-2026-09-26")!.Version.Should().Be(2, "確定時の版のまま");
    }

    // T-10-1315: 存在しない会話キーは、当日の日報以外は作らない。
    [Theory]
    [InlineData("daily-2026-09-28")]
    [InlineData("weekly-2026-W40")]
    [InlineData("daily-2026-09-20")]
    public async Task 当日の日報以外は新しく作らない(string key)
    {
        var (service, store, reviser) = Create();
        SeedConfirmedDaily(store, "daily-2026-09-26", new DateOnly(2026, 9, 26));

        var result = await service.ReviseAsync(key, "積極的に", "developer");

        result.Status.Should().Be(PolicyRevisionStatus.NotFound);
        result.Message.Should().Contain(TodayKey);
        reviser.Calls.Should().BeEmpty();
        store.Get(key).Should().BeNull();
    }

    // T-10-1316: 確定済み日報が無い・より新しい確定済み日報がある（確定しても効かない）ときは作らない。
    [Fact]
    public async Task 土台が無いか確定しても効かない日報は作らない()
    {
        var (noBase, _, _) = Create();
        (await noBase.ReviseAsync(null, "積極的に", "developer")).Status.Should().Be(PolicyRevisionStatus.NoBasePolicy);

        var (service, store, reviser) = Create();
        SeedConfirmedDaily(store, "daily-2026-09-28", new DateOnly(2026, 9, 28));

        var result = await service.ReviseAsync(null, "積極的に", "developer");

        result.Status.Should().Be(PolicyRevisionStatus.WouldNotTakeEffect);
        result.Message.Should().Contain("daily-2026-09-28");
        reviser.Calls.Should().BeEmpty();
        store.Get(TodayKey).Should().BeNull();
    }

    // T-10-1317: 指示・会話キーの検証（空・長すぎ・書式外）。AI は呼ばない。
    [Theory]
    [InlineData(null, "", PolicyRevisionStatus.InvalidInstruction)]
    [InlineData(null, "  \u0007 ", PolicyRevisionStatus.InvalidInstruction)]
    [InlineData("daily 2026", "積極的に", PolicyRevisionStatus.InvalidPeriodKey)]
    [InlineData("../reports", "積極的に", PolicyRevisionStatus.InvalidPeriodKey)]
    public async Task 指示と会話キーを検証する(string? key, string instruction, PolicyRevisionStatus expected)
    {
        var (service, _, reviser) = Create();

        (await service.ReviseAsync(key, instruction, "developer")).Status.Should().Be(expected);
        reviser.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task 長すぎる指示は拒否する()
    {
        var (service, _, reviser) = Create();

        var result = await service.ReviseAsync(
            null, new string('あ', ReportPolicyRevisionService.MaxInstructionLength + 1), "developer");

        result.Status.Should().Be(PolicyRevisionStatus.InvalidInstruction);
        reviser.Calls.Should().BeEmpty();
    }

    // T-10-1318: 次の改訂で版が進み、前の案の版では確定できない（古い確認ボタンを無効にする）。
    [Fact]
    public async Task 次の改訂で版が進み前の版では確定できない()
    {
        var (service, store, _) = Create();
        SeedConfirmedDaily(store, "daily-2026-09-26", new DateOnly(2026, 9, 26));

        var first = await service.ReviseAsync(null, "積極的に", "developer");
        var second = await service.ReviseAsync(null, "やはり控えめに", "developer");

        (first.Version, second.Version).Should().Be((1, 2));
        var act = () => store.Confirm(TodayKey, first.Version, SundayMorning);
        act.Should().Throw<ReportService.Common.Exceptions.ReportConcurrencyException>();
        store.Get(TodayKey)!.Report.Body.Should().Contain("（版 1）").And.Contain("（版 2）");
    }
}
