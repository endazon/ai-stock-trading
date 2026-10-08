using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Common.Abstractions;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Infrastructure.ExternalServices;
using ReportService.Infrastructure.Persistence;
using Xunit;

namespace ReportService.Tests;

// FR-04, FR-07, ADR-0051 決定 4・フォローアップ 1, #1223, IADR-0470（2026-10-08 追記）:
// 方針全体に読める「利確:」行があっても、保有中の銘柄に掛かる行（その銘柄の行も「全銘柄」の行も）が無ければ、
// その銘柄を名指しして確定の前に警告する（確定は止めない）。建玉が得られなければ方針全体の判定へ戻る（黙って省かない）。
// 新規建ての対象は銘柄ごとに見ない。T-10-2448〜T-10-2450。
public class PolicyTakeProfitHeldSymbolWarningTests
{
    private const string AaplOnly = "押し目買いを優先する。\n利確: AAPL +5%";
    private const string AllSymbols = "押し目買いを優先する。\n利確: 全銘柄 +8%";
    private const string NoLine = "含み益が十分に出た段階で売る。";

    private static ReportPosition Held(string symbol, int quantity = 10, Market market = Market.UnitedStates) =>
        new(market, symbol, TradeSide.Buy, quantity, 100m, 95m, null, null, null, null);

    // ---- T-10-2448: 判定（純関数） ----

    [Fact]
    public void 保有中の銘柄に掛かる行が無ければ名指しして警告し_掛かれば警告しない()
    {
        IReadOnlyList<ReportPosition> held = [Held("MSFT"), Held("AAPL"), Held("7203", market: Market.Japan), Held("msft")];

        var warning = PolicyTakeProfitCheck.WarningFor(ReportKind.Daily, AaplOnly, held);

        warning.Should().NotBeNull();
        warning!.Should().StartWith(ReportSummaryMarkers.PolicyTakeProfitMissingPrefix, "通知サービスが印で拾う")
            .And.Contain("保有中の銘柄 7203・MSFT ／ 確定はできます")
            .And.Contain("利確: 全銘柄 +8%")
            .And.NotContain("AAPL ／");
        PolicyTakeProfitCheck.HeldSymbolsWithoutLine(AaplOnly, held).Should().Equal("7203", "MSFT");

        // 「全銘柄」の行はすべての保有に掛かる。名指しの行がすべての保有を覆えば警告しない。
        PolicyTakeProfitCheck.WarningFor(ReportKind.Daily, AllSymbols, held).Should().BeNull();
        PolicyTakeProfitCheck.WarningFor(ReportKind.Daily, AaplOnly, [Held("AAPL")]).Should().BeNull();
        PolicyTakeProfitCheck.WarningFor(ReportKind.Daily, AaplOnly, []).Should().BeNull("保有なし");
        PolicyTakeProfitCheck.WarningFor(ReportKind.Daily, AaplOnly, [Held("MSFT", quantity: 0)]).Should().BeNull("数量 0 は保有ではない");
    }

    [Fact]
    public void 方針全体に行が無ければ従来の警告で_週報と月報は警告しない()
    {
        IReadOnlyList<ReportPosition> held = [Held("MSFT")];

        PolicyTakeProfitCheck.WarningFor(ReportKind.Daily, NoLine, held).Should().Be(PolicyTakeProfitCheck.Warning);
        PolicyTakeProfitCheck.WarningFor(ReportKind.Weekly, AaplOnly, held).Should().BeNull();
        PolicyTakeProfitCheck.WarningFor(ReportKind.Monthly, AaplOnly, held).Should().BeNull();
    }

    // 🔴 受け入れ基準 3: 建玉が得られない（null）ときは方針全体の判定へ戻る（行が無ければ従来の警告は出る）。
    [Fact]
    public void 建玉が得られなければ方針全体の行の有無だけで判定する()
    {
        PolicyTakeProfitCheck.WarningFor(ReportKind.Daily, AaplOnly, null).Should().BeNull();
        PolicyTakeProfitCheck.WarningFor(ReportKind.Daily, NoLine, null).Should().Be(PolicyTakeProfitCheck.Warning);
    }

    [Fact]
    public void 名指しは10銘柄までで_表示できない銘柄は件数にだけ数える()
    {
        var symbols = Enumerable.Range(0, 12).Select(i => $"S{i:00}").ToList();
        var warning = PolicyTakeProfitCheck.HeldSymbolsWarning(symbols);
        warning.Should().Contain("S00・S01").And.Contain("S09 ほか 2 件").And.NotContain("S10");

        PolicyTakeProfitCheck.HeldSymbolsWarning(["MSFT", "BAD\nLINE"]).Should().Contain("保有中の銘柄 MSFT ほか 1 件")
            .And.NotContain("BAD");
        PolicyTakeProfitCheck.HeldSymbolsWarning(["<x>"]).Should().Contain("保有中の銘柄 1 銘柄");
    }

    // ---- T-10-2449: /policy の改訂案 ----

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

    private sealed class StubPositions(Func<IReadOnlyList<ReportPosition>?> answer) : IOpenPositionSource
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<ReportPosition>?> GetOpenPositionsAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(answer());
        }
    }

    private static (ReportPolicyRevisionService Service, InMemoryReportStore Store) CreateRevision(
        string proposedPolicy, IOpenPositionSource? positions)
    {
        var store = new InMemoryReportStore();
        var service = new ReportPolicyRevisionService(
            store, new FixedClock(SundayMorning), new FakeReviser(proposedPolicy),
            new PolicyRevisionSchedule(new ReportScheduleOptions(), AutoDailyEnabled: true),
            new InMemoryPolicyRevisionLedger(), new PolicyRevisionLimit(10),
            NullLogger<ReportPolicyRevisionService>.Instance, positions);
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
    public async Task 改訂案の行が掛からない保有中の銘柄を案内文と本文の記録で名指しし_確定は止めない()
    {
        var (service, store) = CreateRevision(AaplOnly, new StubPositions(() => [Held("AAPL"), Held("MSFT")]));

        var result = await service.ReviseAsync("daily-2026-09-25", "AAPL の利確を数値で", "developer");

        (result.Status, result.Presented).Should().Be((PolicyRevisionStatus.Proposed, true));
        var expected = PolicyTakeProfitCheck.HeldSymbolsWarning(["MSFT"]);
        result.Message.Split('\n').Should().Contain(expected);
        var saved = store.Get("daily-2026-09-25")!.Report;
        saved.PolicySummary.Should().Be(AaplOnly, "警告を方針の本文へ入れない");
        saved.Body.Should().Contain(expected);
    }

    [Fact]
    public async Task 建玉の照会がnullか例外なら方針全体の判定へ戻り_改訂は保存する()
    {
        foreach (var source in new IOpenPositionSource[]
                 {
                     new StubPositions(() => null),
                     new StubPositions(() => throw new HttpRequestException("down")),
                 })
        {
            var (service, store) = CreateRevision(AaplOnly, source);

            var result = await service.ReviseAsync("daily-2026-09-25", "指示", "developer");

            result.Status.Should().Be(PolicyRevisionStatus.Proposed);
            result.Message.Should().NotContain(ReportSummaryMarkers.PolicyTakeProfitMissingPrefix);
            store.Get("daily-2026-09-25")!.Report.PolicySummary.Should().Be(AaplOnly);
        }

        // 案に読める行が無ければ方針全体の警告で足り、建玉を照会しない。
        var noLine = new StubPositions(() => [Held("MSFT")]);
        var (s2, _) = CreateRevision(NoLine, noLine);
        var r2 = await s2.ReviseAsync("daily-2026-09-25", "指示", "developer");
        r2.Message.Split('\n').Should().Contain(PolicyTakeProfitCheck.Warning);
        noLine.Calls.Should().Be(0);
    }

    // ---- T-10-2450: 自動生成の日報の初稿（提示の要約） ----

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
    [InlineData(true)]
    [InlineData(false)]
    public async Task 日報の初稿の方針で行が掛からない保有中の銘柄を提示の要約で名指しし_建玉が未供給なら方針全体の判定へ戻る(bool positionsSupplied)
    {
        var store = new InMemoryReportStore();
        var version = store.UpsertDraft(new TradingReport
        {
            PeriodKey = "daily-2026-07-07",
            Kind = ReportKind.Daily,
            PeriodStart = new DateOnly(2026, 7, 7),
            PolicySummary = AaplOnly,
        }, 0);
        store.Confirm("daily-2026-07-07", version, DateTimeOffset.UnixEpoch);
        var notifier = new RecordingNotifier();
        var positions = new StubPositions(() => positionsSupplied ? [Held("AAPL"), Held("NVDA")] : null);
        var generator = new ReportAutoGenerator(
            store, new ReportDraftService(new StubDrafter()), new NoOpPeriodFillSource(), new FixedClock(WedAfterClose),
            new ReportAutoGenerationSettings(), notifier, openPositionSource: positions);

        await generator.RunOnceAsync();

        var notice = notifier.Notices.Should().ContainSingle().Which;
        var heldWarning = PolicyTakeProfitCheck.HeldSymbolsWarning(["NVDA"]);
        notice.Summary.Contains(heldWarning, StringComparison.Ordinal).Should().Be(positionsSupplied);
        if (!positionsSupplied)
        {
            notice.Summary.Should().NotContain(ReportSummaryMarkers.PolicyTakeProfitMissingPrefix, "方針全体には行がある");
            notice.Summary.Should().Contain("建玉", "建玉の未供給は要約の警告で見える（黙って省かない）");
        }

        store.Get("daily-2026-07-08")!.Report.PolicySummary.Should().NotContain(ReportSummaryMarkers.PolicyTakeProfitMissingPrefix);
        store.GetReview("daily-2026-07-08")!.State.Should().Be(ReviewState.PendingApproval);
    }
}
