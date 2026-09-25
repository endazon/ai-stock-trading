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
        IReportStore? storeOverride = null, IPolicyRevisionLedger? ledger = null, int dailyLimit = 10)
    {
        var store = new InMemoryReportStore();
        var reviser = new FakeReviser(outcome ?? PolicyRevisionOutcome.Proposed(Proposal, null));
        var service = new ReportPolicyRevisionService(
            storeOverride ?? store, new FixedClock(now ?? SundayMorning), reviser, schedule ?? AutoDailyOn,
            ledger ?? new InMemoryPolicyRevisionLedger(), new PolicyRevisionLimit(dailyLimit),
            NullLogger<ReportPolicyRevisionService>.Instance);
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

    private sealed class PresentFailingStore(InMemoryReportStore inner) : IReportStore
    {
        public VersionedReport? Get(string periodKey) => inner.Get(periodKey);

        public IReadOnlyList<TradingReport> List() => inner.List();

        public IReadOnlyList<ReportPeriodKeyItem> ListPeriodKeys() => inner.ListPeriodKeys();

        public int UpsertDraft(TradingReport report, int expectedVersion) => inner.UpsertDraft(report, expectedVersion);

        public ConfirmResult? Confirm(string periodKey, int expectedVersion, DateTimeOffset confirmedAt) =>
            inner.Confirm(periodKey, expectedVersion, confirmedAt);

        public VersionedReport? GetLatestConfirmed(ReportKind kind) => inner.GetLatestConfirmed(kind);

        public ReportReview? GetReview(string periodKey) => inner.GetReview(periodKey);

        public ReviewDecision? ApplyReview(string periodKey, ReviewCommand command) =>
            throw new InvalidOperationException("並行更新で提示に失敗した（模擬）");
    }
}
