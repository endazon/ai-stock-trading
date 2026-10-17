using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
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

        // LLM を待っている間に起きる出来事（並行更新）を模す。
        public Action? DuringCall { get; set; }

        public Task<PolicyRevisionOutcome> ReviseAsync(PolicyRevisionContext context, CancellationToken cancellationToken = default)
        {
            Calls.Add(context);
            DuringCall?.Invoke();
            return Task.FromResult(outcome);
        }
    }

    private static readonly PolicyRevisionSchedule AutoDailyOn = new(new ReportScheduleOptions(), AutoDailyEnabled: true);

    private static (ReportPolicyRevisionService Service, InMemoryReportStore Store, FakeReviser Reviser) Create(
        PolicyRevisionOutcome? outcome = null, DateTimeOffset? now = null, PolicyRevisionSchedule? schedule = null,
        IReportStore? storeOverride = null, IPolicyRevisionLedger? ledger = null, int dailyLimit = 10,
        IReportDraftKnowledgeCopy? draftCopy = null, IReportDraftPresentedNotifier? notifier = null)
    {
        var store = new InMemoryReportStore();
        var reviser = new FakeReviser(outcome ?? PolicyRevisionOutcome.Proposed(Proposal, null));
        var service = new ReportPolicyRevisionService(
            storeOverride ?? store, new FixedClock(now ?? SundayMorning), reviser, schedule ?? AutoDailyOn,
            ledger ?? new InMemoryPolicyRevisionLedger(), new PolicyRevisionLimit(dailyLimit),
            NullLogger<ReportPolicyRevisionService>.Instance, draftKnowledgeCopy: draftCopy, presentedNotifier: notifier);
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

    // FR-07, ADR-0068 決定1, #1373: 日報だけを確定済みにする（週報は SeedConfirmedDaily が既に置いた前提）。
    private static void SeedAnotherConfirmedDaily(InMemoryReportStore store, string key, DateOnly start, string policy, DateTimeOffset at)
    {
        store.UpsertDraft(new TradingReport
        {
            PeriodKey = key,
            Kind = ReportKind.Daily,
            PeriodStart = start,
            BasedOn = "weekly-2026-W39",
            PolicySummary = policy,
            AssumptionsVersion = 3,
        }, 0);
        store.Confirm(key, 1, at);
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

    // T-10-1311: 会話キーを省略すると当日（JST）の日報を、直近の確定済み日報を土台に新しく作り、承認待ちにする（確定はしない）。
    [Fact]
    public async Task 省略時は当日の日報を直近の確定済み日報から作り承認待ちにする()
    {
        var (service, store, reviser) = Create();
        SeedConfirmedDaily(store, "daily-2026-09-26", new DateOnly(2026, 9, 26));

        var result = await service.ReviseAsync(null, "もっと積極的に", "developer");

        result.Status.Should().Be(PolicyRevisionStatus.Proposed);
        (result.PeriodKey, result.Version, result.Created, result.Presented)
            .Should().Be((TodayKey, 1, true, true));

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
            .And.Contain("/policy の確認ボタンで確定したときに適用する");
        saved.UnsuppliedInputs.Should().Equal([ReportInput.OpenPositions], "本文を差し替えても欠けた入力の記録は残す");
    }

    // T-10-1313: AI の案が作れなければ**何も保存しない**（新規も既存も）。
    // FR-06, FR-08, UC-03, #1300, IADR-0526 決定 2: `/policy` は提示の通知を出さないが、承認待ちの版が変わるので写しの本文を差し替える。
    // AI が失敗して何も保存しないときは写しへ渡さない。
    [Fact]
    public async Task 改訂案を承認待ちにしたら写しへ新しい版を渡し失敗したら渡さない()
    {
        var draftCopy = new RecordingDraftKnowledgeCopy();
        var (service, store, _) = Create(draftCopy: draftCopy);
        SeedConfirmedDaily(store, "daily-2026-09-26", new DateOnly(2026, 9, 26));

        var result = await service.ReviseAsync(null, "もっと積極的に", "developer");

        var published = draftCopy.Published.Should().ContainSingle().Which;
        published.Version.Should().Be(result.Version);
        published.Report.PeriodKey.Should().Be(result.PeriodKey);
        published.Report.Body.Should().Be(store.Get(result.PeriodKey!)!.Report.Body);

        var failedCopy = new RecordingDraftKnowledgeCopy();
        var (failing, failingStore, _) = Create(
            PolicyRevisionOutcome.Failed(PolicyRevisionFailure.TimedOut, "AI の応答が 60 秒以内に返りませんでした"), draftCopy: failedCopy);
        SeedConfirmedDaily(failingStore, "daily-2026-09-26", new DateOnly(2026, 9, 26));
        await failing.ReviseAsync(null, "積極的に", "developer");
        failedCopy.Published.Should().BeEmpty();
    }

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

    // T-10-1314: 効いていない確定済みの報告書（より古い日報・週報）は改訂しない（AI も呼ばない）。
    // ［2026-10-17 改 / #1373・ADR-0068 決定1・IADR-0549］効いている確定済み日報は版を足して再確定できる（T-10-2685）。対象を「効いていない」に絞った。
    [Fact]
    public async Task 効いていない確定済みは改訂せずAIも呼ばない()
    {
        var (service, store, reviser) = Create();
        SeedConfirmedDaily(store, "daily-2026-09-25", new DateOnly(2026, 9, 25));
        SeedAnotherConfirmedDaily(store, "daily-2026-09-26", new DateOnly(2026, 9, 26), "26日", SundayMorning);

        var older = await service.ReviseAsync("daily-2026-09-25", "積極的に", "developer");
        var weekly = await service.ReviseAsync("weekly-2026-W39", "積極的に", "developer");

        older.Status.Should().Be(PolicyRevisionStatus.AlreadyConfirmed);
        older.Message.Should().Contain("版を足して再確定できるのは効いている日報 daily-2026-09-26 だけです");
        weekly.Status.Should().Be(PolicyRevisionStatus.AlreadyConfirmed, "週報・月報は再確定の対象外（日報だけ）");
        reviser.Calls.Should().BeEmpty("費用を使わない");
        store.Get("daily-2026-09-25")!.Version.Should().Be(2, "確定時の版のまま");
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

    // 2026-09-28（月）10:00 JST ＝ 01:00 UTC（日報の生成境界 16:00 より前）／17:00 JST ＝ 08:00 UTC（境界の後）。
    private static readonly DateTimeOffset MondayMorning = new(2026, 9, 28, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MondayEvening = new(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);

    // T-10-1336（利用者裁定 2026-09-26）: 営業日にまだ自動生成されていない当日の日報は、境界の前でも後でも作らない
    // （作ると数値入りの自動生成の日報が失われる）。AI も呼ばない。
    [Theory]
    [MemberData(nameof(BusinessDayInstants))]
    public async Task 営業日にまだ自動生成されていない当日の日報は作らない(DateTimeOffset now)
    {
        var (service, store, reviser) = Create(now: now);
        SeedConfirmedDaily(store, "daily-2026-09-26", new DateOnly(2026, 9, 26));

        var result = await service.ReviseAsync(null, "積極的に", "developer");

        result.Status.Should().Be(PolicyRevisionStatus.AutoDailyPending);
        result.Message.Should().Contain("daily-2026-09-28").And.Contain("自動生成").And.Contain("16:00").And.Contain("/policy");
        reviser.Calls.Should().BeEmpty();
        store.Get("daily-2026-09-28").Should().BeNull();
    }

    public static TheoryData<DateTimeOffset> BusinessDayInstants() => new() { MondayMorning, MondayEvening };

    // T-10-1337: 自動生成の後なら、その日の日報（自動生成のドラフト）を改訂する。
    [Fact]
    public async Task 自動生成の後は当日の日報のドラフトを改訂する()
    {
        var (service, store, _) = Create(now: MondayEvening);
        SeedConfirmedDaily(store, "daily-2026-09-26", new DateOnly(2026, 9, 26));
        store.UpsertDraft(new TradingReport
        {
            PeriodKey = "daily-2026-09-28",
            Kind = ReportKind.Daily,
            PeriodStart = new DateOnly(2026, 9, 28),
            PolicySummary = "自動生成の方針",
            Body = "# 日報（数値入り）",
        }, 0);

        var result = await service.ReviseAsync(null, "積極的に", "developer");

        (result.Status, result.Created, result.Version).Should().Be((PolicyRevisionStatus.Proposed, false, 2));
        store.Get("daily-2026-09-28")!.Report.Body.Should().StartWith("# 日報（数値入り）");
    }

    // T-10-1338: 構成の休場日（自動生成が無い日）と、自動生成が無効な構成では、営業日の曜日でも当日の日報を作ってよい。
    [Fact]
    public async Task 休場日と自動生成が無効な構成では当日の日報を作れる()
    {
        var holiday = new PolicyRevisionSchedule(
            new ReportScheduleOptions { Holidays = new HashSet<DateOnly> { new(2026, 9, 28) } }, AutoDailyEnabled: true);
        var (onHoliday, store1, _) = Create(now: MondayMorning, schedule: holiday);
        SeedConfirmedDaily(store1, "daily-2026-09-26", new DateOnly(2026, 9, 26));
        (await onHoliday.ReviseAsync(null, "積極的に", "developer")).Status.Should().Be(PolicyRevisionStatus.Proposed);

        var disabled = new PolicyRevisionSchedule(new ReportScheduleOptions(), AutoDailyEnabled: false);
        var (noAuto, store2, _) = Create(now: MondayMorning, schedule: disabled);
        SeedConfirmedDaily(store2, "daily-2026-09-26", new DateOnly(2026, 9, 26));
        (await noAuto.ReviseAsync(null, "積極的に", "developer")).Status.Should().Be(PolicyRevisionStatus.Proposed);
    }

    // T-10-1339: LLM を待つ間に報告書が更新されたら（自動生成・別の改訂）、版が合わず保存しない（新しい版を古い土台の案で踏まない）。
    [Fact]
    public async Task LLMを待つ間に更新されたら保存しない()
    {
        var (service, store, reviser) = Create();
        store.UpsertDraft(new TradingReport
        {
            PeriodKey = "daily-2026-09-25",
            Kind = ReportKind.Daily,
            PeriodStart = new DateOnly(2026, 9, 25),
            PolicySummary = "元の方針",
        }, 0);
        reviser.DuringCall = () => store.UpsertDraft(new TradingReport
        {
            PeriodKey = "daily-2026-09-25",
            Kind = ReportKind.Daily,
            PeriodStart = new DateOnly(2026, 9, 25),
            PolicySummary = "並行して書かれた方針",
        }, 1);

        var act = () => service.ReviseAsync("daily-2026-09-25", "積極的に", "developer");

        await act.Should().ThrowAsync<ReportService.Common.Exceptions.ReportConcurrencyException>("エンドポイントが 409 へ写す");
        var saved = store.Get("daily-2026-09-25")!;
        (saved.Version, saved.Report.PolicySummary).Should().Be((2, "並行して書かれた方針"));
    }

    // T-10-1340: 保存の後の提示が失敗しても 409（何も保存していない側の応答）にせず、保存済み・未提示として返す。
    [Fact]
    public async Task 保存の後の提示の失敗は保存済み未提示として返す()
    {
        var inner = new InMemoryReportStore();
        SeedConfirmedDaily(inner, "daily-2026-09-26", new DateOnly(2026, 9, 26));
        var (service, _, _) = Create(storeOverride: new PresentFailingStore(inner));

        var result = await service.ReviseAsync(null, "積極的に", "developer");

        (result.Status, result.Version, result.Presented).Should().Be((PolicyRevisionStatus.Proposed, 1, false));
        result.Message.Should().Contain("承認待ちにできませんでした");
        inner.Get(TodayKey)!.Report.PolicySummary.Should().Be("押し目買いを優先する");
    }

    // T-10-1355（再監査 nit 4）: 日付の境界は JST。日曜 23:30 UTC は月曜 08:30 JST（営業日）→ 月曜の日報は作らない。
    // 日曜 14:30 UTC は日曜 23:30 JST（休場日）→ 日曜の日報を作れる。UTC の日付で判定すると逆になる。
    [Fact]
    public async Task 日付の境界はJSTで判定する()
    {
        var (mondayJst, store1, reviser1) = Create(now: new DateTimeOffset(2026, 9, 27, 23, 30, 0, TimeSpan.Zero));
        SeedConfirmedDaily(store1, "daily-2026-09-26", new DateOnly(2026, 9, 26));
        var refused = await mondayJst.ReviseAsync(null, "積極的に", "developer");
        (refused.Status, refused.PeriodKey).Should().Be((PolicyRevisionStatus.AutoDailyPending, "daily-2026-09-28"));
        reviser1.Calls.Should().BeEmpty();

        var (sundayJst, store2, _) = Create(now: new DateTimeOffset(2026, 9, 27, 14, 30, 0, TimeSpan.Zero));
        SeedConfirmedDaily(store2, "daily-2026-09-26", new DateOnly(2026, 9, 26));
        var created = await sundayJst.ReviseAsync(null, "積極的に", "developer");
        (created.Status, created.PeriodKey, created.Created).Should().Be((PolicyRevisionStatus.Proposed, "daily-2026-09-27", true));
    }

    // T-10-1356（ADR-0042 決定 3）: 本日（JST）の試行が上限に達したら LLM を呼ばず、上限・回数を返す。何も保存しない。
    // 失敗した試行（AI の失敗）も上限に数える（費用が掛かり得るため）。前日の試行は数えない。
    [Fact]
    public async Task 一日の上限に達したらLLMを呼ばない()
    {
        var ledger = new InMemoryPolicyRevisionLedger();
        ledger.Begin(new PolicyRevisionAttempt(Guid.NewGuid(), SundayMorning.AddDays(-1), new DateOnly(2026, 9, 26), "developer", "x"));
        var (failing, store, _) = Create(
            PolicyRevisionOutcome.Failed(PolicyRevisionFailure.CallFailed, "失敗"), ledger: ledger, dailyLimit: 2);
        SeedConfirmedDaily(store, "daily-2026-09-26", new DateOnly(2026, 9, 26));

        (await failing.ReviseAsync(null, "1 回目", "developer")).Status.Should().Be(PolicyRevisionStatus.AiFailed);
        (await failing.ReviseAsync(null, "2 回目", "developer")).Status.Should().Be(PolicyRevisionStatus.AiFailed);

        var (service, store2, reviser) = Create(ledger: ledger, dailyLimit: 2, storeOverride: store);
        var result = await service.ReviseAsync(null, "3 回目", "developer");

        result.Status.Should().Be(PolicyRevisionStatus.DailyLimitReached);
        result.Message.Should().Contain("上限の 2 回").And.Contain("2 回実行済み").And.Contain("方針は変わっていません");
        reviser.Calls.Should().BeEmpty("上限に達したら LLM を呼ばない（費用を使わない）");
        store.Get(TodayKey).Should().BeNull();
        ledger.CountOn(new DateOnly(2026, 9, 27)).Should().Be(2, "断った試行は数えない");
    }

    // T-10-1357: 試行は LLM を呼ぶ前に記録され、結果（案の版・入れ替え案）で閉じる（案の監査記録）。検証で断った要求は記録しない。
    [Fact]
    public async Task 試行は呼ぶ前に記録され結果と入れ替え案で閉じる()
    {
        var ledger = new InMemoryPolicyRevisionLedger();
        var (service, store, reviser) = Create(ledger: ledger);
        SeedConfirmedDaily(store, "daily-2026-09-26", new DateOnly(2026, 9, 26));
        reviser.DuringCall = () => ledger.CountOn(new DateOnly(2026, 9, 27)).Should().Be(1, "呼ぶ前に 1 行書く");

        var result = await service.ReviseAsync(null, "積極的に", "developer");
        (await service.ReviseAsync(null, "", "developer")).Status.Should().Be(PolicyRevisionStatus.InvalidInstruction);

        result.Message.Should().Contain("本日の /policy: 1/10 回目");
        ledger.CountOn(new DateOnly(2026, 9, 27)).Should().Be(1);
        var row = ledger.Attempts.Should().ContainSingle().Subject;
        (row.Outcome, row.ReportVersion, row.Actor, row.PeriodKey)
            .Should().Be((PolicyRevisionAttemptOutcome.Proposed, 1, "developer", TodayKey));
        row.WatchlistChangesJson.Should().Contain("\"action\":\"add\"").And.Contain("NVDA").And.Contain("\"action\":\"remove\"");
    }

    // T-10-1358: 上限の構成値の読み方（空・未設定・不正・0 以下は既定 10 へ倒す。上限を無効にする値を作らない）。
    [Theory]
    [InlineData(null, 10)]
    [InlineData("", 10)]
    [InlineData("abc", 10)]
    [InlineData("0", 10)]
    [InlineData("-3", 10)]
    [InlineData("3", 3)]
    public void 上限の構成値を読む(string? configured, int expected)
    {
        PolicyRevisionLimit.Read(configured).DailyLimit.Should().Be(expected);
    }

    // T-10-1385（ADR-0042 決定 1・#1025）: 案を作った時点の監視銘柄を試行に記録し（楽観排他の基準）、米国の銘柄だけを
    // LLM へ渡す。照会できなかった（null）なら記録も null（「空」と区別する）。形式が崩れた一覧は受け取らず AI も呼ばない。
    [Fact]
    public async Task 案を作った時点の監視銘柄を記録し米国の銘柄だけをLLMへ渡す()
    {
        var ledger = new InMemoryPolicyRevisionLedger();
        var (service, store, reviser) = Create(ledger: ledger);
        SeedConfirmedDaily(store, "daily-2026-09-26", new DateOnly(2026, 9, 26));

        await service.ReviseAsync(null, "積極的に", "developer",
            [new WatchlistSnapshotItem("AAPL", "UnitedStates"), new WatchlistSnapshotItem("7203", "Japan")]);

        reviser.Calls.Single().CurrentUsWatchlist.Should().Equal("AAPL");
        ledger.Attempts.Single().WatchlistSnapshotJson.Should()
            .Be("""[{"symbol":"AAPL","market":"UnitedStates"},{"symbol":"7203","market":"Japan"}]""");

        var (unknown, store2, reviser2) = Create(ledger: new InMemoryPolicyRevisionLedger());
        SeedConfirmedDaily(store2, "daily-2026-09-26", new DateOnly(2026, 9, 26));
        await unknown.ReviseAsync(null, "積極的に", "developer");
        reviser2.Calls.Single().CurrentUsWatchlist.Should().BeNull("照会できなかった");

        var (bad, _, reviser3) = Create();
        (await bad.ReviseAsync(null, "積極的に", "developer", [new WatchlistSnapshotItem("AA PL", "UnitedStates")]))
            .Status.Should().Be(PolicyRevisionStatus.InvalidWatchlist);
        (await bad.ReviseAsync(null, "積極的に", "developer", [new WatchlistSnapshotItem("AAPL", "Moon")]))
            .Status.Should().Be(PolicyRevisionStatus.InvalidWatchlist);
        reviser3.Calls.Should().BeEmpty();
    }

    // T-10-1417（PR #1027 の監査 L2）: 上限（200 件）を超える監視銘柄は `/policy` を失敗させず「分からない」（null）として扱う。
    // 200 件ちょうどは記録する。最大の一覧の JSON は台帳の列（text）に収まる。
    [Fact]
    public async Task 上限を超える監視銘柄は分からないとして扱う()
    {
        var ledger = new InMemoryPolicyRevisionLedger();
        var (service, store, reviser) = Create(ledger: ledger);
        SeedConfirmedDaily(store, "daily-2026-09-26", new DateOnly(2026, 9, 26));
        var tooMany = Enumerable.Range(0, WatchlistSnapshotItem.MaxCount + 1)
            .Select(i => new WatchlistSnapshotItem($"S{i:D4}", "UnitedStates")).ToList();

        var result = await service.ReviseAsync(null, "積極的に", "developer", tooMany);

        result.Status.Should().Be(PolicyRevisionStatus.Proposed);
        reviser.Calls.Single().CurrentUsWatchlist.Should().BeNull();
        ledger.Attempts.Single().WatchlistSnapshotJson.Should().BeNull();

        var max = Enumerable.Range(0, WatchlistSnapshotItem.MaxCount)
            .Select(i => new WatchlistSnapshotItem(new string('A', 12) + $"{i:D4}", "UnitedStates")).ToList();
        var json = ReportPolicyRevisionService.SerializeSnapshot(max);
        using var npgsql = new ReportService.Infrastructure.Persistence.ReportDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<ReportService.Infrastructure.Persistence.ReportDbContext>()
                .UseNpgsql("Host=localhost;Database=report_svc").Options);
        var entity = npgsql.Model.FindEntityType(typeof(ReportService.Infrastructure.Persistence.PolicyRevisionAttemptRow))!;
        foreach (var column in new[] { "WatchlistSnapshotJson", "WatchlistApplyJson" })
        {
            var property = entity.FindProperty(column)!;
            property.GetColumnType().Should().Be("text", column);
            (property.GetMaxLength() ?? int.MaxValue).Should().BeGreaterThanOrEqualTo(json.Length, column);
        }

        json.Length.Should().BeGreaterThan(8192, "前提: 旧 varchar(8192) では溢れた長さ");
    }

    // ---- FR-07, FR-14, ADR-0068 決定1, #1373, IADR-0549: 効いている確定済み日報への /policy は版を足す（T-10-2685〜2690）----
    // ［2026-10-17 / #1373］旧 T-10-1520〜1523（当日の確定済み日報への 409 と「明日」の案内）は、当日の確定済み日報が効いている日報であり
    // 版を足して再確定できるようになったため、新しい挙動の試験に置き換えた（案内は効いていない確定済みだけに出る。T-10-1314・T-10-2688）。

    // T-10-2685: 効いている確定済みの当日の日報に会話キー省略の /policy → 版 n+1 のドラフト（承認待ち）。土台は確定済みの版 n の方針。
    // 確定するまで最新の確定済み日報は版 n のまま（FR-07）。本文の状態の行は未確定へ戻る。通知は「版を足した」で 1 件。
    [Fact]
    public async Task 効いている確定済み日報に版を足してドラフトにし確定まで版nが効き続ける()
    {
        var notifier = new RecordingNotifier();
        var (service, store, reviser) = Create(notifier: notifier);
        SeedConfirmedDaily(store, TodayKey, new DateOnly(2026, 9, 27), "矛盾した方針");

        var result = await service.ReviseAsync(null, "条件 B の見送りを外す", "developer");

        result.Status.Should().Be(PolicyRevisionStatus.Proposed);
        (result.PeriodKey, result.Version, result.Created, result.Presented).Should().Be((TodayKey, 3, false, true));
        result.Message.Should().StartWith($"確定済みの日報 {TodayKey}（版 2）に版を足して、版 3 のドラフトにしました。確定するまでは版 2 の方針が効き続けます。");
        reviser.Calls.Single().CurrentPolicy.Should().Be("矛盾した方針", "土台は確定済みの版 n の方針");

        var row = store.Get(TodayKey)!;
        (row.Report.State, row.Version, row.Report.PolicySummary).Should().Be((ReportState.Draft, 3, Proposal.PolicySummary));
        store.GetReview(TodayKey)!.State.Should().Be(ReviewState.PendingApproval);
        // 確定するまで版 n が効く（取引判断が読む最新の確定済み日報は版 2 のまま）。
        var effective = store.GetLatestConfirmed(ReportKind.Daily)!;
        (effective.Report.PeriodKey, effective.Version, effective.Report.PolicySummary).Should().Be((TodayKey, 2, "矛盾した方針"));

        notifier.Notices.Should().ContainSingle().Which.Should().Match<PresentedReportNotice>(n =>
            n.PeriodKey == TodayKey && n.Version == 3 && n.ReconfirmingFromVersion == 2);
    }

    // T-10-2686: 版 n+1 を確定すると、その時点から版 n+1 が効く（版 n は写しとして残る）。未確定の版 n+1 への /policy はそのドラフトを改訂する。
    [Fact]
    public async Task 版nプラス1は改訂でき確定すると効いている版が変わる()
    {
        var (service, store, _) = Create();
        SeedConfirmedDaily(store, TodayKey, new DateOnly(2026, 9, 27), "矛盾した方針");
        (await service.ReviseAsync(null, "直す", "developer")).Version.Should().Be(3);

        var again = await service.ReviseAsync(TodayKey, "もう少し直す", "developer");
        (again.Status, again.Version).Should().Be((PolicyRevisionStatus.Proposed, 4));
        again.Message.Should().NotContain("版を足して", "既存のドラフトの改訂（版を足すのは確定済みに当たったときだけ）");

        var reconfirmAt = SundayMorning.AddMinutes(30);
        var confirmed = store.Confirm(TodayKey, 4, reconfirmAt)!;
        (confirmed.Transitioned, confirmed.Version, confirmed.SupersededVersion).Should().Be((true, 5, 2));
        store.GetLatestConfirmed(ReportKind.Daily)!.Version.Should().Be(5);
        store.GetLatestConfirmedAsOf(ReportKind.Daily, reconfirmAt.AddTicks(-1))!.Version.Should().Be(2, "再確定の前の判断時点は版 2");
    }

    // T-10-2687: より新しい日付の日報が確定して効いている日報が移ったら、版 n+1 のドラフトは確定・改訂のどちらも断る（ドラフトは残す）。
    [Fact]
    public async Task 効いている日報が移った後の版nプラス1は確定も改訂も断る()
    {
        var mondayMorning = new DateTimeOffset(2026, 9, 28, 1, 0, 0, TimeSpan.Zero);
        var (service, store, reviser) = Create();
        SeedConfirmedDaily(store, TodayKey, new DateOnly(2026, 9, 27), "日曜の方針");
        (await service.ReviseAsync(null, "直す", "developer")).Version.Should().Be(3);
        SeedAnotherConfirmedDaily(store, "daily-2026-09-28", new DateOnly(2026, 9, 28), "月曜の方針", mondayMorning);

        var revise = await service.ReviseAsync(TodayKey, "さらに直す", "developer");
        revise.Status.Should().Be(PolicyRevisionStatus.NoLongerEffective);
        revise.Message.Should().Contain("いま効いているのは daily-2026-09-28");
        reviser.Calls.Should().ContainSingle("移った後の改訂は AI を呼ばない（回数も使わない）");

        var confirm = () => store.Confirm(TodayKey, 3, mondayMorning.AddMinutes(1));
        confirm.Should().Throw<ReportService.Common.Exceptions.ReportNoLongerEffectiveException>()
            .Which.EffectivePeriodKey.Should().Be("daily-2026-09-28");
        store.Get(TodayKey)!.Report.State.Should().Be(ReportState.Draft, "ドラフトは記録として残す");
        store.GetLatestConfirmed(ReportKind.Daily)!.Report.PeriodKey.Should().Be("daily-2026-09-28");
    }

    // T-10-2688: 効いていない確定済みの報告書の 409 は、再確定できる日報（効いている日報）と会話キー省略の対象を示し、32 文字の会話キーでも
    // 通知サービスが切る 300 文字（HttpPolicyRevisionController.MaxErrorLength）に収まる。
    [Fact]
    public async Task 効いていない確定済みの409は再確定できる日報を示し300文字に収まる()
    {
        var disabled = new PolicyRevisionSchedule(new ReportScheduleOptions(), AutoDailyEnabled: false);
        var (service, store, _) = Create(schedule: disabled);
        SeedConfirmedDaily(store, "daily-2026-09-25", new DateOnly(2026, 9, 25));
        SeedAnotherConfirmedDaily(store, "daily-2026-09-26", new DateOnly(2026, 9, 26), "26日", SundayMorning);

        var past = await service.ReviseAsync("daily-2026-09-25", "積極的に", "developer");
        past.Status.Should().Be(PolicyRevisionStatus.AlreadyConfirmed);
        past.Message.Should().Be(
            "報告書 daily-2026-09-25 は確定済みのため改訂できません。版を足して再確定できるのは効いている日報 daily-2026-09-26 だけです。"
            + $"period を省略した /policy は当日（JST）の日報 {TodayKey} を対象にします。監視銘柄はいまでもリスク設定画面から変更できます。");

        var longKey = new string('a', 32);
        foreach (var schedule in new[] { AutoDailyOn, disabled })
        {
            ReportPolicyRevisionService.AlreadyConfirmedMessage(longKey, longKey, schedule, longKey).Length.Should().BeLessThanOrEqualTo(300);
            ReportPolicyRevisionService.AlreadyConfirmedMessage(longKey, TodayKey, schedule).Length.Should().BeLessThanOrEqualTo(300);
        }
    }

    // T-10-2689: 会話キー省略の /policy を AutoDailyPending で断るとき（営業日の 0 時から自動生成まで）、効いている前日付の日報の会話キーと
    // 再確定のやり方を案内する。案内のとおりにその会話キーを指定すると版を足せる。日付は JST の暦日で数える（09-27 15:30 UTC＝09-28 00:30 JST）。
    [Fact]
    public async Task AutoDailyPendingの応答で効いている日報への再確定を案内する()
    {
        var mondayJustAfterMidnightJst = new DateTimeOffset(2026, 9, 27, 15, 30, 0, TimeSpan.Zero);
        var (service, store, _) = Create(now: mondayJustAfterMidnightJst);
        SeedConfirmedDaily(store, TodayKey, new DateOnly(2026, 9, 27));

        var pending = await service.ReviseAsync(null, "直す", "developer");
        pending.Status.Should().Be(PolicyRevisionStatus.AutoDailyPending);
        pending.PeriodKey.Should().Be("daily-2026-09-28", "JST の暦日（UTC の 09-27 ではない）");
        pending.Message.Should().Contain($"いま効いている方針を直すには、効いている日報 {TodayKey} を指定して /policy period:{TodayKey} を実行してください");

        var followed = await service.ReviseAsync(TodayKey, "直す", "developer");
        (followed.Status, followed.Version).Should().Be((PolicyRevisionStatus.Proposed, 3));

        // 承認待ちの版 n+1 があるときは、そのドラフトを改訂できると案内する。
        (await service.ReviseAsync(null, "直す", "developer")).Message
            .Should().Contain($"いま効いている日報 {TodayKey} には承認待ちの版 3 のドラフトがあります");
    }

    // T-10-2690: 1 日の上限に達した日は、再確定のための /policy も断る（別枠は無い。ADR-0068 決定 5・IADR-0432）。
    [Fact]
    public async Task 上限に達した日は再確定のための改訂も断る()
    {
        var (service, store, reviser) = Create(dailyLimit: 1);
        SeedConfirmedDaily(store, "daily-2026-09-26", new DateOnly(2026, 9, 26));
        (await service.ReviseAsync("daily-2026-09-26", "1 回目", "developer")).Status.Should().Be(PolicyRevisionStatus.Proposed);
        store.Confirm("daily-2026-09-26", 3, SundayMorning.AddMinutes(1))!.SupersededVersion.Should().Be(2);

        var refused = await service.ReviseAsync("daily-2026-09-26", "再確定したい", "developer");

        refused.Status.Should().Be(PolicyRevisionStatus.DailyLimitReached);
        reviser.Calls.Should().ContainSingle("上限の後は LLM を呼ばない");
        store.Get("daily-2026-09-26")!.Report.State.Should().Be(ReportState.Confirmed, "確定済みのまま（版は足さない）");
    }

    private sealed class PresentFailingStore(InMemoryReportStore inner) : IReportStore
    {
        public VersionedReport? Get(string periodKey) => inner.Get(periodKey);

        public IReadOnlyList<TradingReport> List() => inner.List();

        public IReadOnlyList<ReportPeriodKeyItem> ListPeriodKeys() => inner.ListPeriodKeys();

        public int UpsertDraft(TradingReport report, int expectedVersion) => inner.UpsertDraft(report, expectedVersion);

        public ConfirmResult? Confirm(string periodKey, int expectedVersion, DateTimeOffset confirmedAt) =>
            inner.Confirm(periodKey, expectedVersion, confirmedAt);

        public VersionedReport? GetLatestConfirmed(ReportKind kind) => inner.GetLatestConfirmed(kind);

        public VersionedReport? GetLatestConfirmedAsOf(ReportKind kind, DateTimeOffset at) => inner.GetLatestConfirmedAsOf(kind, at);
        public int StartRevisionDraft(TradingReport draft, int expectedVersion) => inner.StartRevisionDraft(draft, expectedVersion);
        public VersionedReport? GetLatestConfirmedVersion(string periodKey) => inner.GetLatestConfirmedVersion(periodKey);
        public IReadOnlyList<VersionedReport> ListLatestConfirmedVersions() => inner.ListLatestConfirmedVersions();

        public ReportReview? GetReview(string periodKey) => inner.GetReview(periodKey);

        public ReviewDecision? ApplyReview(string periodKey, ReviewCommand command) =>
            throw new InvalidOperationException("並行更新で提示に失敗した（模擬）");
    }
}
