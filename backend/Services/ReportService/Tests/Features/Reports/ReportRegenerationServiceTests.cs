using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Common.Abstractions;
using ReportService.Common.Exceptions;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Infrastructure.Persistence;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-14, FR-16, UC-03〜05, 計画 ADR-0052 決定 1〜5, #1156, IADR-0491: 所有者の操作による報告書の作り直し（`/report regenerate`）。
// 受け入れ基準の写像: 対象（確定済み・不在・形式）・別枠の 1 日の上限・期間の時点に復元できない入力・中核の入力の取得失敗で断る・
// 方針の節を保つ・版の記録（本文・台帳）・監査・費用の計上区分・並行更新。
public class ReportRegenerationServiceTests
{
    // 2026-10-06（火）10:00 JST ＝ 01:00 UTC。日報の生成境界（16:00）前なので、自動生成の対象（Due）は前営業日 daily-2026-10-05。
    private static readonly DateTimeOffset TueMorning = new(2026, 10, 6, 1, 0, 0, TimeSpan.Zero);

    private const string PastDaily = "daily-2026-10-02";    // 期間が過ぎた日報（金曜）。
    private const string CurrentDaily = "daily-2026-10-05"; // 自動生成がいま対象にしている日報（月曜）。

    private const string OldNarrativeMarker = "建玉なし（縮退した散文）";
    private const string NewNarrative = "作り直した散文";
    private const string RevisedPolicy = "押し目買いを優先する（/policy の改訂）";

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class RecordingDrafter(string narrative = NewNarrative, Action? duringCall = null) : IReportNarrativeDrafter
    {
        public List<ReportNarrativeContext> Contexts { get; } = [];

        public Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            duringCall?.Invoke();
            return Task.FromResult(narrative);
        }
    }

    private sealed class CountingFillSource(bool fail = false, Action? duringCall = null) : IPeriodFillSource
    {
        public List<(DateOnly From, DateOnly To)> Requested { get; } = [];

        public Task<IReadOnlyList<PeriodTradeFill>> GetFillsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
        {
            Requested.Add((from, to));
            duringCall?.Invoke();
            return fail
                ? throw new HttpRequestException("台帳へ到達できません")
                : Task.FromResult<IReadOnlyList<PeriodTradeFill>>([]);
        }
    }

    private sealed class CountingPositionSource(IReadOnlyList<ReportPosition>? positions) : IOpenPositionSource
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<ReportPosition>?> GetOpenPositionsAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(positions);
        }
    }

    private sealed class StubDriftSource(bool supplied) : IPeriodDriftAdoptionSource
    {
        public Task<IReadOnlyList<PeriodDriftAdoption>?> GetDriftAdoptionsAsync(
            DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PeriodDriftAdoption>?>(supplied ? [] : null);
    }

    private sealed class RecordingAudit : IReportRegenerationAuditPublisher
    {
        public List<ReportRegenerated> Events { get; } = [];

        public Task PublishAsync(ReportRegenerated evt)
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }
    }

    private static readonly IReadOnlyList<ReportPosition> HeldPositions =
    [
        new(Market.UnitedStates, "NVDA", TradeSide.Buy, 10, 100m, 95m, null, null, null, 3),
    ];

    private sealed class Harness
    {
        public InMemoryReportStore Store { get; } = new();
        public FixedClock Clock { get; } = new(TueMorning);
        public RecordingDrafter Drafter { get; init; } = new();
        public CountingFillSource Fills { get; init; } = new();
        public CountingPositionSource Positions { get; init; } = new(HeldPositions);
        public StubDriftSource Drift { get; init; } = new(supplied: true);
        public InMemoryReportRegenerationLedger Ledger { get; } = new();
        public RecordingAudit Audit { get; } = new();
        public int Limit { get; init; } = ReportRegenerationLimit.DefaultDailyLimit;

        public ReportRegenerationService Service()
        {
            var settings = new ReportAutoGenerationSettings();
            var generator = new ReportAutoGenerator(
                Store, new ReportDraftService(Drafter), Fills, Clock, settings,
                openPositionSource: Positions,
                driftAdoptionSource: Drift,
                regenerationLedger: Ledger);
            return new ReportRegenerationService(
                Store, Clock, generator, settings, Ledger, new ReportRegenerationLimit(Limit), Audit,
                NullLogger<ReportRegenerationService>.Instance);
        }

        // 縮退した下書き（版 1）＋利用者の /policy の改訂（版 2）を置く。改訂の記録は本文の末尾にある。
        public int SeedDegradedDraft(string key, DateOnly start, bool confirmed = false)
        {
            var body = $"# 日報 {key}\n\n## 1. サマリ\n\n{OldNarrativeMarker}\n\n"
                + $"{ReportPolicyRevisionService.RevisionRecordHeadingPrefix}2）\n\n- 指示者: owner\n- 指示（原文）:\n\n> 押し目買い\n";
            var report = new TradingReport
            {
                PeriodKey = key,
                Kind = ReportKind.Daily,
                PeriodStart = start,
                BasedOn = "weekly-2026-W40",
                AssumptionsVersion = 3,
                PolicySummary = RevisedPolicy,
                Body = body,
                UnsuppliedInputs = [ReportInput.OpenPositions, ReportInput.Fills, ReportInput.ParentPolicy],
            };
            var v1 = Store.UpsertDraft(report, 0);
            var v2 = Store.UpsertDraft(report, v1);
            Store.ApplyReview(key, new ReviewCommand(ReviewAction.Present, "owner", v2));
            if (confirmed)
                return Store.Confirm(key, v2, TueMorning)!.Version;
            return v2;
        }
    }

    // ---- 対象（ADR-0052 決定 2） ----

    // T-10-2260, FR-06, 計画 ADR-0052 決定 2: 確定済みの版は作り直さない（入力も LLM も呼ばない・台帳も書かない）。
    [Fact]
    public async Task 確定済みの報告書は作り直さない()
    {
        var h = new Harness();
        var version = h.SeedDegradedDraft(PastDaily, new DateOnly(2026, 10, 2), confirmed: true);

        var result = await h.Service().RegenerateAsync(PastDaily, "owner");

        result.Status.Should().Be(ReportRegenerationStatus.AlreadyConfirmed);
        h.Store.Get(PastDaily)!.Version.Should().Be(version);
        h.Fills.Requested.Should().BeEmpty();
        h.Drafter.Contexts.Should().BeEmpty();
        h.Ledger.Attempts.Should().BeEmpty();
        h.Audit.Events.Should().BeEmpty();
    }

    // T-10-2261, FR-06, 計画 ADR-0052 決定 2: 無い報告書・形式の不正な会話キーは作り直さない（新しく作らない）。
    [Theory]
    [InlineData("daily-2026-10-01", ReportRegenerationStatus.NotFound)]
    [InlineData("../daily-2026-10-01", ReportRegenerationStatus.InvalidPeriodKey)]
    [InlineData("", ReportRegenerationStatus.InvalidPeriodKey)]
    public async Task 無い報告書や不正な会話キーは作り直さない(string key, ReportRegenerationStatus expected)
    {
        var h = new Harness();

        var result = await h.Service().RegenerateAsync(key, "owner");

        result.Status.Should().Be(expected);
        h.Store.Get("daily-2026-10-01").Should().BeNull("作り直しは下書きを新しく作らない");
        h.Drafter.Contexts.Should().BeEmpty();
        h.Ledger.Attempts.Should().BeEmpty();
    }

    // ---- 別枠の 1 日の上限（ADR-0052 決定 1） ----

    // T-10-2262, FR-06, FR-14, 計画 ADR-0052 決定 1: 上限に達したら断り、**入力も LLM も呼ばない**。断った行は残すが数えない。
    [Fact]
    public async Task 上限に達したら入力もLLMも呼ばずに断る()
    {
        var h = new Harness { Limit = 1 };
        h.SeedDegradedDraft(PastDaily, new DateOnly(2026, 10, 2));
        var svc = h.Service();

        (await svc.RegenerateAsync(PastDaily, "owner")).Status.Should().Be(ReportRegenerationStatus.Regenerated);
        var fillsAfterFirst = h.Fills.Requested.Count;
        var callsAfterFirst = h.Drafter.Contexts.Count;

        var second = await svc.RegenerateAsync(PastDaily, "owner");

        second.Status.Should().Be(ReportRegenerationStatus.DailyLimitReached);
        second.Message.Should().Contain("上限の 1 回").And.Contain("/policy とは別の枠");
        h.Fills.Requested.Should().HaveCount(fillsAfterFirst, "上限で断る要求に依存先への照会を出さない");
        h.Drafter.Contexts.Should().HaveCount(callsAfterFirst);
        h.Ledger.Attempts.Should().ContainSingle(a => a.Outcome == ReportRegenerationOutcome.LimitReached);
        h.Ledger.CountOn(new DateOnly(2026, 10, 6)).Should().Be(1, "上限で断った行は数えない");
    }

    // T-10-2262, 計画 ADR-0052 決定 1: `/policy` の枠とは別である。同じ DbContext（report_svc）に `/policy` の試行が上限ぶんあっても、
    // 作り直しの台帳は数えない（別の表・別の勧告ロックの鍵）。EF の台帳では断った行（中核の入力・上限）も数えない。結果は列挙名で持つ。
    [Fact]
    public void 方針の改訂の試行は作り直しの枠を食わない()
    {
        using var db = new ReportDbContext(
            new DbContextOptionsBuilder<ReportDbContext>()
                .UseInMemoryDatabase($"regen-{Guid.NewGuid()}").Options);
        var day = new DateOnly(2026, 10, 6);
        var policy = new EfPolicyRevisionLedger(db);
        for (var i = 0; i < 10; i++)
            policy.Begin(new PolicyRevisionAttempt(Guid.NewGuid(), TueMorning, day, "owner", PastDaily));
        var ledger = new EfReportRegenerationLedger(db);
        ledger.RecordRefusal(new ReportRegenerationAttempt(
            Guid.NewGuid(), TueMorning, day, "owner", PastDaily, 1, ReportRegenerationOutcome.RefusedCoreUnsupplied));
        ledger.RecordRefusal(new ReportRegenerationAttempt(
            Guid.NewGuid(), TueMorning, day, "owner", PastDaily, 1, ReportRegenerationOutcome.LimitReached));

        ledger.CountOn(day).Should().Be(0);
        ledger.TryBegin(new ReportRegenerationAttempt(Guid.NewGuid(), TueMorning, day, "owner", PastDaily, 1), 1).Begun.Should().BeTrue();
        ledger.TryBegin(new ReportRegenerationAttempt(Guid.NewGuid(), TueMorning, day, "owner", PastDaily, 1), 1)
            .Should().Be(new ReportRegenerationBeginResult(false, 1));
        db.Model.FindEntityType(typeof(ReportRegenerationAttemptRow))!
            .FindProperty(nameof(ReportRegenerationAttemptRow.Outcome))!.GetProviderClrType().Should().Be<string>();
        ledger.Tally(day, day, 1).Should().Be(new ReportRegenerationTally(Regenerated: 0, Refused: 1, LimitReachedDays: 1));
    }

    // T-10-2262, IADR-0491 決定 3: 入力を引いている間に別の要求が上限の最後の 1 回を使ったら、数えて書く排他区間（TryBegin）で断る。
    // 散文（LLM）を呼ばず、上限で断った行として残す（数えない）。入力を引く前の確認だけでは同時の要求で上限を超える。
    [Fact]
    public async Task 入力を引く間に枠が埋まったら散文を呼ばずに断る()
    {
        InMemoryReportRegenerationLedger? ledger = null;
        var h = new Harness
        {
            Limit = 1,
            Fills = new CountingFillSource(duringCall: () => ledger!.TryBegin(
                new ReportRegenerationAttempt(Guid.NewGuid(), TueMorning, new DateOnly(2026, 10, 6), "other", PastDaily, 1), 1)),
        };
        ledger = h.Ledger;
        var version = h.SeedDegradedDraft(PastDaily, new DateOnly(2026, 10, 2));

        var result = await h.Service().RegenerateAsync(PastDaily, "owner");

        result.Status.Should().Be(ReportRegenerationStatus.DailyLimitReached);
        h.Drafter.Contexts.Should().BeEmpty();
        h.Store.Get(PastDaily)!.Version.Should().Be(version);
        h.Ledger.Attempts.Should().ContainSingle(a => a.Outcome == ReportRegenerationOutcome.LimitReached);
        h.Ledger.CountOn(new DateOnly(2026, 10, 6)).Should().Be(1);
    }

    // T-10-2262, IADR-0491 決定 3: 上限の構成値。空・不正・1 未満は既定（5 回/日）へ倒す（上限を無効にする値を作らない）。
    [Theory]
    [InlineData(null, 5)]
    [InlineData("", 5)]
    [InlineData("abc", 5)]
    [InlineData("0", 5)]
    [InlineData("-3", 5)]
    [InlineData("1", 1)]
    [InlineData("12", 12)]
    public void 上限の構成値は1未満や不正を既定へ倒す(string? configured, int expected) =>
        ReportRegenerationLimit.Read(configured).DailyLimit.Should().Be(expected);

    // ---- 中核の入力の取得失敗（ADR-0052 決定 4） ----

    public static TheoryData<string, ReportInput> CoreInputFailures() => new()
    {
        { "fills", ReportInput.Fills },
        { "drift", ReportInput.DriftAdoptions },
        { "positions", ReportInput.OpenPositions },
    };

    // T-10-2263, FR-06, 計画 ADR-0052 決定 4: 中核の入力（約定・手動売買の取り込み・建玉）の取得に失敗したら、作り直さず理由を返す。
    // 下書きはそのまま・LLM を呼ばない・**回数は消費しない**（断った行は残すが数えない）。建玉は期間が現在の日報で試す（過去の日報は取りに行かない）。
    [Theory]
    [MemberData(nameof(CoreInputFailures))]
    public async Task 中核の入力の取得に失敗したら断り回数を消費しない(string failing, ReportInput expected)
    {
        var h = new Harness
        {
            Limit = 1,
            Fills = new CountingFillSource(fail: failing == "fills"),
            Drift = new StubDriftSource(supplied: failing != "drift"),
            Positions = new CountingPositionSource(failing == "positions" ? null : HeldPositions),
        };
        var version = h.SeedDegradedDraft(CurrentDaily, new DateOnly(2026, 10, 5));
        var before = h.Store.Get(CurrentDaily)!.Report.Body;

        var result = await h.Service().RegenerateAsync(CurrentDaily, "owner");

        result.Status.Should().Be(ReportRegenerationStatus.CoreInputsUnsupplied);
        result.Message.Should().Contain(ReportInputs.Label(expected)).And.Contain("回数は消費していません");
        result.UnsuppliedInputs.Should().Contain(ReportInputs.Label(expected));
        h.Store.Get(CurrentDaily)!.Version.Should().Be(version);
        h.Store.Get(CurrentDaily)!.Report.Body.Should().Be(before);
        h.Drafter.Contexts.Should().BeEmpty("同じ縮退した下書きのために LLM 費用を出さない");
        h.Audit.Events.Should().BeEmpty();
        h.Ledger.Attempts.Should().ContainSingle(a => a.Outcome == ReportRegenerationOutcome.RefusedCoreUnsupplied);
        h.Ledger.CountOn(new DateOnly(2026, 10, 6)).Should().Be(0, "断った作り直しは回数を消費しない");
    }

    // T-10-2263（否定形）: 中核でない入力（LLM 利用実績など）の欠落では断らない（作り直して未供給として記録する）。
    [Fact]
    public async Task 中核でない入力の欠落では断らない()
    {
        var h = new Harness();
        h.SeedDegradedDraft(CurrentDaily, new DateOnly(2026, 10, 5));

        var result = await h.Service().RegenerateAsync(CurrentDaily, "owner");

        result.Status.Should().Be(ReportRegenerationStatus.Regenerated);
        result.UnsuppliedInputs.Should().Contain(ReportInputs.Label(ReportInput.LlmUsage));
    }

    // ---- 期間の時点に復元できない入力（ADR-0052 決定 2） ----

    // T-10-2264, FR-06, 計画 ADR-0052 決定 2・4: 期間が過ぎた日報の建玉は**取りに行かず**「未供給」として扱い、散文にもそう渡す。
    // 🔴 これは「取得の失敗」ではないので断らない（含めると期間が過ぎた日報は常に断られる）。
    [Fact]
    public async Task 期間が過ぎた日報の建玉は今の値を使わず未供給として作り直す()
    {
        var h = new Harness();
        h.SeedDegradedDraft(PastDaily, new DateOnly(2026, 10, 2));

        var result = await h.Service().RegenerateAsync(PastDaily, "owner");

        result.Status.Should().Be(ReportRegenerationStatus.Regenerated);
        h.Positions.Calls.Should().Be(0, "今の建玉を期間の建玉として書かない");
        result.NotRestorableInputs.Should().Equal(ReportInputs.Label(ReportInput.OpenPositions));
        h.Store.Get(PastDaily)!.Report.UnsuppliedInputs.Should().Contain(ReportInput.OpenPositions);
        var context = h.Drafter.Contexts.Single();
        context.Positions.Should().BeNull();
        context.UnsuppliedInputs.Should().Contain(ReportInput.OpenPositions);
        h.Store.Get(PastDaily)!.Report.Body.Should().Contain("期間の時点に復元できないため未供給として扱った入力: 建玉");
    }

    // T-10-2264（逆向き）: 自動生成がいま対象にしている期間（Due）なら、建玉は今の値＝期間の値として取りに行き、未供給にしない。
    [Fact]
    public async Task 期間が現在の日報は建玉を取りに行き未供給にしない()
    {
        var h = new Harness();
        h.SeedDegradedDraft(CurrentDaily, new DateOnly(2026, 10, 5));

        var result = await h.Service().RegenerateAsync(CurrentDaily, "owner");

        result.Status.Should().Be(ReportRegenerationStatus.Regenerated);
        h.Positions.Calls.Should().Be(1);
        result.NotRestorableInputs.Should().BeEmpty();
        h.Store.Get(CurrentDaily)!.Report.UnsuppliedInputs.Should().NotContain(ReportInput.OpenPositions);
        h.Drafter.Contexts.Single().Positions.Should().HaveCount(1);
    }

    // ---- 方針の節を保ち、事実と散文を作り直す（ADR-0052 決定 3）・版の記録と監査（決定 5）・費用（決定 1） ----

    // T-10-2265, FR-06, FR-07, 計画 ADR-0052 決定 3・5: 方針（方針の要約と /policy の改訂の記録）は保ち、事実と散文だけを作り直して
    // 版を上げて再提示する。作り直した旨・日時・なお未供給だった入力を本文と台帳に残し、監査へ発行する。確定はしない。
    [Fact]
    public async Task 方針の節を保って事実と散文を作り直し版を上げて再提示する()
    {
        var h = new Harness();
        var before = h.SeedDegradedDraft(CurrentDaily, new DateOnly(2026, 10, 5));

        var result = await h.Service().RegenerateAsync(CurrentDaily, "owner-1");

        result.Status.Should().Be(ReportRegenerationStatus.Regenerated);
        result.PreviousVersion.Should().Be(before);
        result.Version.Should().Be(before + 1);
        result.Presented.Should().BeTrue();
        result.Message.Should().Contain($"版 {before + 1}").And.Contain("/report approve " + CurrentDaily);

        var saved = h.Store.Get(CurrentDaily)!;
        saved.Version.Should().Be(before + 1);
        saved.Report.State.Should().Be(ReportState.Draft, "作り直しは確定しない（ADR-0003）");
        h.Store.GetReview(CurrentDaily)!.State.Should().Be(ReviewState.PendingApproval);
        saved.Report.PolicySummary.Should().Be(RevisedPolicy, "利用者が改訂した方針を系が消さない");
        saved.Report.BasedOn.Should().Be("weekly-2026-W40");
        saved.Report.AssumptionsVersion.Should().Be(3);

        var body = saved.Report.Body;
        body.Should().Contain(NewNarrative).And.NotContain(OldNarrativeMarker, "事実と散文の節は作り直す");
        body.Should().Contain(ReportPolicyRevisionService.RevisionRecordHeadingPrefix + "2）", "改訂の記録は保つ");
        body.Should().Contain($"{ReportRegenerationService.RegenerationRecordHeadingPrefix}{before + 1}）")
            .And.Contain("- 作り直した利用者: owner-1")
            .And.Contain("- 日時（UTC）: 2026-10-06 01:00:00")
            .And.Contain($"- 作り直す前の版: {before}")
            .And.Contain("- なお未供給だった入力: ");
        body.IndexOf(ReportPolicyRevisionService.RevisionRecordHeadingPrefix, StringComparison.Ordinal)
            .Should().BeLessThan(body.IndexOf(ReportRegenerationService.RegenerationRecordHeadingPrefix, StringComparison.Ordinal));

        // 中核の入力（約定・建玉）はこの版で供給されたので外れる。方針の連鎖（上位方針）の記録は方針の節に属するので引き継ぐ。
        saved.Report.UnsuppliedInputs.Should().NotContain(ReportInput.Fills).And.NotContain(ReportInput.OpenPositions);
        saved.Report.UnsuppliedInputs.Should().Contain(ReportInput.ParentPolicy);

        var row = h.Ledger.Attempts.Single();
        row.Outcome.Should().Be(ReportRegenerationOutcome.Regenerated);
        row.Actor.Should().Be("owner-1");
        row.PreviousVersion.Should().Be(before);
        row.ReportVersion.Should().Be(before + 1);
        ReportInputs.Parse(row.UnsuppliedInputs).Should().Equal(saved.Report.UnsuppliedInputs);

        var evt = h.Audit.Events.Single();
        evt.PeriodKey.Should().Be(CurrentDaily);
        evt.Kind.Should().Be("Daily");
        evt.Actor.Should().Be("owner-1");
        evt.PreviousVersion.Should().Be(before);
        evt.Version.Should().Be(before + 1);
        evt.UnsuppliedInputs.Should().Equal(saved.Report.UnsuppliedInputs.Select(i => i.ToString()));
        evt.RegeneratedAt.Should().Be(TueMorning);
    }

    // T-10-2265, 計画 ADR-0052 決定 1, IADR-0491 決定 2: 散文の LLM 費用は `report-regeneration` へ付け替えて計上する（月次上限の対象外）。
    // 散文の文脈の方針は保った方針である（方針を作り直さない）。
    [Fact]
    public async Task 散文の費用は作り直しの計上区分で方針は保った方針を渡す()
    {
        var h = new Harness();
        h.SeedDegradedDraft(CurrentDaily, new DateOnly(2026, 10, 5));

        await h.Service().RegenerateAsync(CurrentDaily, "owner");

        var context = h.Drafter.Contexts.Single();
        context.UsagePurpose.Should().Be(LlmPurposes.ReportRegeneration);
        context.PolicySummary.Should().Be(RevisedPolicy);
        LlmCostScope.IsGoverned(context.UsagePurpose).Should().BeFalse("月次 LLM 上限に算入しない");
    }

    // T-10-2265: 2 回目の作り直しは、前の作り直しの記録も保つ（記録は版ごとに積む）。
    [Fact]
    public async Task 二回目の作り直しは前の作り直しの記録も保つ()
    {
        var h = new Harness();
        var before = h.SeedDegradedDraft(CurrentDaily, new DateOnly(2026, 10, 5));
        var svc = h.Service();

        await svc.RegenerateAsync(CurrentDaily, "owner");
        await svc.RegenerateAsync(CurrentDaily, "owner");

        var body = h.Store.Get(CurrentDaily)!.Report.Body;
        body.Should().Contain($"{ReportRegenerationService.RegenerationRecordHeadingPrefix}{before + 1}）")
            .And.Contain($"{ReportRegenerationService.RegenerationRecordHeadingPrefix}{before + 2}）");
        CountOf(body, NewNarrative).Should().Be(1, "前の版の事実と散文は残さない（作り直した本文は 1 つ）");
    }

    // T-10-2268, FR-06, 計画 ADR-0052 決定 3: LLM を待つ間に下書きが改訂されたら保存しない（読んだ時点の版で楽観排他）。
    // 試行は SaveFailed で閉じ（数える）、監査は発行しない。
    [Fact]
    public async Task 散文を待つ間に改訂されたら保存しない()
    {
        InMemoryReportStore? store = null;
        var h = new Harness
        {
            Drafter = new RecordingDrafter(duringCall: () =>
            {
                var current = store!.Get(CurrentDaily)!;
                store.UpsertDraft(current.Report with { PolicySummary = "別の改訂" }, current.Version);
            }),
        };
        store = h.Store;
        h.SeedDegradedDraft(CurrentDaily, new DateOnly(2026, 10, 5));

        var act = () => h.Service().RegenerateAsync(CurrentDaily, "owner");

        await act.Should().ThrowAsync<ReportConcurrencyException>();
        h.Store.Get(CurrentDaily)!.Report.PolicySummary.Should().Be("別の改訂");
        h.Store.Get(CurrentDaily)!.Report.Body.Should().NotContain(ReportRegenerationService.RegenerationRecordHeadingPrefix);
        h.Ledger.Attempts.Single().Outcome.Should().Be(ReportRegenerationOutcome.SaveFailed);
        h.Ledger.CountOn(new DateOnly(2026, 10, 6)).Should().Be(1, "LLM を呼んだ試行は数える");
        h.Audit.Events.Should().BeEmpty();
    }

    // ---- 本文の組み立て（ADR-0052 決定 3・5） ----

    // T-10-2269, 計画 ADR-0052 決定 3: 保つのは最初の記録の見出しから後ろ。記録が無ければ何も保たない。
    // 見出しが行頭に無い（本文中の引用）なら切らない。
    [Theory]
    [InlineData("# 日報\n本文だけ", "")]
    [InlineData("# 日報\n本文\n\n## 利用者の指示による方針の改訂（版 2）\n\n- a\n", "## 利用者の指示による方針の改訂（版 2）\n\n- a")]
    [InlineData("## 利用者の指示による方針の改訂（版 2）\n- a", "## 利用者の指示による方針の改訂（版 2）\n- a")]
    [InlineData("# 日報\n引用「## 報告書の作り直しの記録（版 3）」\n", "")]
    [InlineData("# 日報\n\n## 報告書の作り直しの記録（版 3）\n- x\n\n## 利用者の指示による方針の改訂（版 4）\n- y",
        "## 報告書の作り直しの記録（版 3）\n- x\n\n## 利用者の指示による方針の改訂（版 4）\n- y")]
    public void 保つ記録は最初の記録の見出しから後ろ(string previous, string expected) =>
        ReportRegenerationService.PreservedRecords(previous).Should().Be(expected);

    // T-10-2269, 計画 ADR-0052 決定 5: 未供給が無ければ「なし」と書き、復元できない入力の行は出さない。
    [Fact]
    public void 未供給が無い作り直しの記録はなしと書く()
    {
        var body = ReportRegenerationService.ComposeBody("# 日報\n", string.Empty, 3, 2, "owner", TueMorning, [], []);

        body.Should().Contain("- なお未供給だった入力: なし").And.NotContain("復元できない");
    }

    // ---- 台帳（ADR-0052 決定 1・4。月報 §7 の素） ----

    // T-10-2270, 計画 ADR-0052 決定 1・4, IADR-0491 決定 6: 集計。上限到達の日数は「上限で断った日」と「数える試行が上限に届いた日」の和集合。
    // 断った行（中核の入力・上限）は数えない。
    [Fact]
    public void 台帳の集計は断った行を数えず上限到達の日を和集合で数える()
    {
        var d1 = new DateOnly(2026, 10, 1);
        var d2 = new DateOnly(2026, 10, 2);
        var d3 = new DateOnly(2026, 10, 3);
        var tally = ReportRegenerationTallies.Of(
        [
            (d1, ReportRegenerationOutcome.Regenerated),
            (d1, ReportRegenerationOutcome.AiFailed),       // d1: 数える試行 2 件＝上限 2 に届いた
            (d2, ReportRegenerationOutcome.Regenerated),
            (d2, ReportRegenerationOutcome.LimitReached),   // d2: 上限で断った（数える試行は 1 件）
            (d3, ReportRegenerationOutcome.RefusedCoreUnsupplied),
            (d3, ReportRegenerationOutcome.RefusedCoreUnsupplied),
            (d3, ReportRegenerationOutcome.Regenerated),    // d3: 断りは数えない＝数える試行 1 件
        ], dailyLimit: 2);

        tally.Should().Be(new ReportRegenerationTally(Regenerated: 3, Refused: 2, LimitReachedDays: 2));
    }

    // T-10-2270: 断った試行の記録は数える結果を受け付けない（数える行を断った行として書けない）。
    [Fact]
    public void 断った記録へ数える結果は渡せない()
    {
        var ledger = new InMemoryReportRegenerationLedger();
        var act = () => ledger.RecordRefusal(new ReportRegenerationAttempt(
            Guid.NewGuid(), TueMorning, new DateOnly(2026, 10, 6), "owner", PastDaily, 1, ReportRegenerationOutcome.Regenerated));

        act.Should().Throw<ArgumentException>();
        ledger.Attempts.Should().BeEmpty();
    }

    // ---- 月報 §7（ADR-0052 決定 1） ----

    // T-10-2267, FR-06, 計画 ADR-0052 決定 1, IADR-0491 決定 6: 月報の自動生成は台帳から当月の作り直しの回数を引いて §7 に載せる。
    [Fact]
    public async Task 月報の自動生成は当月の作り直しの回数を載せる()
    {
        var store = new InMemoryReportStore();
        var ledger = new InMemoryReportRegenerationLedger();
        var october = new DateOnly(2026, 10, 5);
        ledger.TryBegin(new ReportRegenerationAttempt(Guid.NewGuid(), TueMorning, october, "owner", CurrentDaily, 1), 5);
        ledger.Complete(ledger.Attempts.Single().Id, ReportRegenerationOutcome.Regenerated, 2, null, null);
        ledger.RecordRefusal(new ReportRegenerationAttempt(
            Guid.NewGuid(), TueMorning, october, "owner", CurrentDaily, 2, ReportRegenerationOutcome.RefusedCoreUnsupplied));
        // 2026-10-30（金）17:00 JST: 10 月の最終営業日の月報の生成境界の後。
        var monthEnd = new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
        var generator = new ReportAutoGenerator(
            store, new ReportDraftService(new RecordingDrafter()), new CountingFillSource(), new FixedClock(monthEnd),
            new ReportAutoGenerationSettings(), regenerationLedger: ledger);

        await generator.RunOnceAsync();

        var monthly = store.Get("monthly-2026-10")!.Report.Body;
        monthly.Should().Contain("報告書の作り直し（`/report regenerate`").And.Contain("1 回").And.Contain("断り 1 回");
    }

    // T-10-2267（否定形）: 台帳が無い構成では「0 回」と書かず「照会できませんでした」と書く。
    [Fact]
    public async Task 台帳が無い構成の月報は作り直しの回数をゼロと書かない()
    {
        var store = new InMemoryReportStore();
        var monthEnd = new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
        var generator = new ReportAutoGenerator(
            store, new ReportDraftService(new RecordingDrafter()), new CountingFillSource(), new FixedClock(monthEnd),
            new ReportAutoGenerationSettings());

        await generator.RunOnceAsync();

        var monthly = store.Get("monthly-2026-10")!.Report.Body;
        monthly.Should().Contain("回数は照会できませんでした").And.NotContain("作り直し）: 0 回");
    }

    // T-10-2266, FR-06, 計画 ADR-0052 決定 1, IADR-0491 決定 6: 作り直しの計上は独立区分に集計し、取引判断（上限の対象）・報告書生成・その他へ
    // 混ぜない。当月に計上が無ければ null（0 回・0 円と書かない）。
    [Fact]
    public void 作り直しの費用は独立区分に集計し上限にも報告書生成にも混ぜない()
    {
        var at = TueMorning;
        var with = LlmUsageAggregator.Aggregate(new LlmUsageRecord(
        [
            new LlmCostIncurred(5m, at, LlmPurposes.ReportRegeneration, "claude-opus-5"),
            new LlmCostIncurred(7m, at, "REPORT-REGENERATION", "claude-sonnet-5"),
            new LlmCostIncurred(100m, at, LlmPurposes.ReportDaily, "claude-sonnet-5"),
        ], [], []));
        var without = LlmUsageAggregator.Aggregate(new LlmUsageRecord(
            [new LlmCostIncurred(100m, at, LlmPurposes.ReportDaily, "claude-sonnet-5")], [], []));

        with.ReportRegeneration.Should().Be(new PolicyRevisionUsage(2, 12m));
        with.TradeDecisionCostJpy.Should().Be(0m);
        with.OtherCostJpy.Should().Be(0m);
        with.ReportCostJpyByPurpose.Should().Equal((LlmPurposes.ReportDaily, 100m));
        with.PolicyRevision.Should().BeNull();
        without.ReportRegeneration.Should().BeNull();
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
