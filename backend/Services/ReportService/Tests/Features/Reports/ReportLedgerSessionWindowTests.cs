using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using ReportService.Common.Abstractions;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Infrastructure.Persistence;
using Xunit;

namespace ReportService.Tests;

// FR-06, UC-03〜05, 計画 ADR-0053 決定 2・フォローアップ 3, #1224, IADR-0516: 監査台帳を引く報告書の入力（借株料・自動縮小・損切りの手法と
// その解決・強制買戻しの推定・為替の状態）は約定と同じセッションの窓の日報に載り、LLM 利用実績は JST の暦日のまま引いて行で明記する。
//
// 🔴 供給元のスタブは**監査台帳の契約どおりに絞る**（記録の時刻が JST の半開区間 [from 00:00, to+1 日 00:00) に入る記録だけを返す。
// AuditPeriodRange.JstHalfOpen と同じ）。全行を返すスタブでは、照会の範囲の誤り（窓の始まりの前日の夜を引かない）が見えない。
public class ReportLedgerSessionWindowTests
{
    private static readonly TimeZoneInfo Eastern =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    private static readonly TimeSpan JstOffset = TimeSpan.FromHours(9);

    private static DateTimeOffset Et(int month, int day, int hour, int minute = 0)
    {
        var local = new DateTime(2026, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Eastern.GetUtcOffset(local));
    }

    private static DateTimeOffset Jst(int month, int day, int hour, int minute = 0) =>
        new(new DateTime(2026, month, day, hour, minute, 0, DateTimeKind.Unspecified), JstOffset);

    private static bool InJstDays(DateTimeOffset at, DateOnly from, DateOnly to)
    {
        var day = DateOnly.FromDateTime(at.ToOffset(JstOffset).DateTime);
        return day >= from && day <= to;
    }

    private sealed class Requests
    {
        public List<(string Input, DateOnly From, DateOnly To)> Seen { get; } = [];
    }

    private sealed class BorrowFees(Requests requests, BorrowFeeRecord all) : IBorrowFeeRecordSource
    {
        public Task<BorrowFeeRecord?> GetBorrowFeesAsync(DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default)
        {
            requests.Seen.Add(("borrow", fromInclusive, toInclusive));
            return Task.FromResult<BorrowFeeRecord?>(new BorrowFeeRecord(
                [.. all.Accruals.Where(a => InJstDays(a.AccruedAt, fromInclusive, toInclusive))],
                [.. all.Unavailable.Where(u => InJstDays(u.ObservedAt, fromInclusive, toInclusive))]));
        }
    }

    private sealed class Reductions(Requests requests, params MaintenanceMarginReductionExecuted[] all) : IMarginReductionRecordSource
    {
        public Task<IReadOnlyList<MaintenanceMarginReductionExecuted>?> GetReductionsAsync(
            DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default)
        {
            requests.Seen.Add(("reduction", fromInclusive, toInclusive));
            return Task.FromResult<IReadOnlyList<MaintenanceMarginReductionExecuted>?>(
                [.. all.Where(r => InJstDays(r.ExecutedAt, fromInclusive, toInclusive))]);
        }
    }

    private sealed class BuyIns(Requests requests, params BuyInInferred[] all) : IBuyInInferenceRecordSource
    {
        public Task<IReadOnlyList<BuyInInferred>?> GetInferencesAsync(
            DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default)
        {
            requests.Seen.Add(("buyin", fromInclusive, toInclusive));
            return Task.FromResult<IReadOnlyList<BuyInInferred>?>(
                [.. all.Where(b => InJstDays(b.InferredAt, fromInclusive, toInclusive))]);
        }
    }

    private sealed class Approvals(Requests requests, params OrderApproved[] all) : IStopLossMethodUsageSource
    {
        public Task<StopLossMethodUsage?> GetUsageAsync(DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default)
        {
            requests.Seen.Add(("approval", fromInclusive, toInclusive));
            return Task.FromResult<StopLossMethodUsage?>(
                StopLossMethodUsage.From(all.Where(a => InJstDays(a.ApprovedAt, fromInclusive, toInclusive))));
        }
    }

    private sealed class Resolutions(Requests requests, params StopLossMethodResolved[] all) : IStopLossMethodResolutionSource
    {
        public Task<StopLossMethodResolutionFeed?> GetResolutionsAsync(
            DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default)
        {
            requests.Seen.Add(("resolution", fromInclusive, toInclusive));
            // 実装（HttpStopLossMethodResolutionSource）は前後 1 日を足して引く。
            return Task.FromResult<StopLossMethodResolutionFeed?>(new StopLossMethodResolutionFeed(
                [.. all.Where(r => InJstDays(r.OccurredAt, fromInclusive.AddDays(-1), toInclusive.AddDays(1)))]));
        }
    }

    private sealed class Fx(Requests requests, FxSourceStatus all) : IFxSourceStatusSource
    {
        public Task<FxSourceStatus?> GetStatusAsync(DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default)
        {
            requests.Seen.Add(("fx", fromInclusive, toInclusive));
            return Task.FromResult<FxSourceStatus?>(FxSourceStatus.Compose(
                [.. all.FellBacks.Where(e => InJstDays(e.OccurredAt, fromInclusive, toInclusive))],
                [.. all.Restorations.Where(e => InJstDays(e.OccurredAt, fromInclusive, toInclusive))],
                [.. all.StaleWarnings.Where(e => InJstDays(e.OccurredAt, fromInclusive, toInclusive))],
                [.. all.StaleCloses.Where(e => InJstDays(e.OccurredAt, fromInclusive, toInclusive))],
                [.. all.Usages.Where(e => InJstDays(e.OccurredAt, fromInclusive, toInclusive))]));
        }
    }

    private sealed class Llm(Requests requests) : ILlmUsageRecordSource
    {
        public Task<LlmUsageRecord?> GetUsageAsync(DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default)
        {
            requests.Seen.Add(("llm", fromInclusive, toInclusive));
            return Task.FromResult<LlmUsageRecord?>(new LlmUsageRecord([], [], []));
        }
    }

    private sealed class NoFills : IPeriodFillSource
    {
        public Task<IReadOnlyList<PeriodTradeFill>> GetFillsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PeriodTradeFill>>([]);
    }

    private sealed class NoPositions : IOpenPositionSource
    {
        public Task<IReadOnlyList<ReportPosition>?> GetOpenPositionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ReportPosition>?>([]);
    }

    private sealed class StubDrafter : IReportNarrativeDrafter
    {
        public Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult("散文");
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private static OrderApproved Approval(Guid id, Market market, DateTimeOffset at) =>
        new(id, new OrderIntent(market == Market.UnitedStates ? "NVDA" : "7203", market, TradeSide.Buy, ProductType.Cash,
            BrokerProvider.InternalPaper, 1, 100m), 1, at, StopLossMethod: StopLossExecutionMethod.SoftwareStop);

    // 米国の ET 10-05 のセッション（JST 10-05 22:30〜10-06 05:00）とその前後の記録。
    private static readonly Guid UsDecision = Guid.NewGuid();
    private static readonly Guid JpDecision = Guid.NewGuid();

    // TradingDay は契約どおり JST の取引日（記録 ET 10-05 17:00 ＝ JST 10-06 06:00）。配置は市場・記録の時刻で決まる。
    private static readonly BorrowFeeAccrued UsAccrual =
        new("TSLA", Market.UnitedStates, new DateOnly(2026, 10, 6), 0.05m, 1_000m, 0.137m, Et(10, 5, 17));

    private static readonly BorrowFeeAccrued JpAccrual =
        new("7203", Market.Japan, new DateOnly(2026, 10, 6), 0.011m, 2_000m, 0.06m, Jst(10, 6, 11));

    private static readonly MaintenanceMarginReductionExecuted UsReduction = new(
        Guid.NewGuid(), 1.2m, 1.3m, 1.5m, 1.6m,
        [new MaintenanceMarginReductionItem("TSLA", Market.UnitedStates, TradeSide.Buy, ProductType.MarginLong, 3, 200m, 300m)],
        Et(10, 5, 11));

    private static readonly BuyInInferred UsBuyIn =
        new(Guid.NewGuid(), "GME", Market.UnitedStates, -10, 0, 0, 10, 10, [], new DateOnly(2026, 11, 4), Et(10, 5, 18, 55), Et(10, 5, 19));

    private static readonly StopLossMethodResolved UsResolution = new(
        UsDecision, "NVDA", Market.UnitedStates, ProductType.Cash, StopLossExecutionMethod.SoftwareStop,
        StopLossExecutionMethod.BrokerStopOrder, StopLossMethodResolutionReason.BrokerNotMoomooSimulate, BrokerProvider.MoomooReal,
        Et(10, 5, 9, 46));

    private static readonly FxRateSourceFellBack UsSessionFallback = new("USDJPY", "fred", 2, 2, Jst(10, 5, 23, 30));
    private static readonly FxRateSourceUsed JpSessionUsage = new("USDJPY", FxSourceCredits.BojSourceName, 1, 2, Jst(10, 6, 10));

    private static (ReportAutoGenerator Generator, Requests Requests) Build(IReportStore? store = null, IClock? clock = null)
    {
        var requests = new Requests();
        var generator = new ReportAutoGenerator(
            store ?? new InMemoryReportStore(), new ReportDraftService(new StubDrafter()), new NoFills(),
            clock ?? new FixedClock(Jst(10, 8, 9)), new ReportAutoGenerationSettings(),
            reductionSource: new Reductions(requests, UsReduction),
            buyInSource: new BuyIns(requests, UsBuyIn),
            fxSourceStatusSource: new Fx(requests, FxSourceStatus.Compose([UsSessionFallback], [], [], [], [JpSessionUsage])),
            llmUsageSource: new Llm(requests),
            borrowFeeSource: new BorrowFees(requests, new BorrowFeeRecord([UsAccrual, JpAccrual], [])),
            openPositionSource: new NoPositions(),
            stopLossMethodUsageSource: new Approvals(requests,
                Approval(UsDecision, Market.UnitedStates, Et(10, 5, 9, 45)),
                Approval(JpDecision, Market.Japan, Jst(10, 6, 9, 30))),
            stopLossMethodResolutionSource: new Resolutions(requests, UsResolution));
        return (generator, requests);
    }

    private static Task<ReportInputSnapshot> CollectDaily(ReportAutoGenerator generator, int day) =>
        generator.CollectInputsAsync(
            ReportSchedule.PeriodOf(ReportKind.Daily, new DateOnly(2026, 10, day), new ReportScheduleOptions()),
            new HashSet<ReportInput>());

    // T-06-067, FR-06, #1224, IADR-0516 決定 1・2（再現）: ET 10-05 の米国のセッションの借株料・自動縮小・損切りの手法とその解決・
    // 強制買戻しの推定・為替の状態は、約定と同じ日報 10-06 に載り、日報 10-05・10-07 には載らない。
    // 修正前は日報 10-05 が JST 10-05 を引いて（生成の 16:00 にはまだ無い）、日報 10-06 は JST 10-06 だけを引いた（JST 10-05 の夜の記録はどの日報にも無い）。
    [Fact]
    public async Task T06_067_米国のセッションの監査台帳の記録は約定と同じ日報に載る()
    {
        var (generator, _) = Build();

        var mon = await CollectDaily(generator, 5);
        var tue = await CollectDaily(generator, 6);
        var wed = await CollectDaily(generator, 7);

        tue.BorrowFees!.Accruals.Should().Contain(UsAccrual);
        tue.MarginReductions.Should().Equal(UsReduction);
        tue.BuyInInferences.Should().Equal(UsBuyIn);
        tue.StopLossMethods!.Approvals.Select(a => a.DecisionId).Should().Contain(UsDecision);
        tue.StopLossMethodResolutions!.Resolutions.Should().Contain(UsResolution);
        StopLossMethodComparison.From(tue.StopLossMethods, tue.StopLossMethodResolutions).DisagreementCount.Should().Be(1);
        tue.FxSourceStatus!.FellBacks.Should().Equal(UsSessionFallback);

        foreach (var other in new[] { mon, wed })
        {
            other.BorrowFees!.Accruals.Should().NotContain(UsAccrual);
            other.MarginReductions.Should().BeEmpty();
            other.BuyInInferences.Should().BeEmpty();
            other.StopLossMethods!.Approvals.Select(a => a.DecisionId).Should().NotContain(UsDecision);
            other.FxSourceStatus!.FellBacks.Should().BeEmpty();
        }
    }

    // T-06-068, FR-06, #1224, IADR-0516（東証は従来どおり）: 東証のセッション（JST 10-06）の記録は、従来の JST の暦日の照会と同じく
    // 日報 10-06 に載る（東証の窓の取引日は JST の暦日と一致する）。
    [Fact]
    public async Task T06_068_東証のセッションの記録は従来どおり同じ日付の日報に載る()
    {
        var (generator, _) = Build();

        var tue = await CollectDaily(generator, 6);
        var wed = await CollectDaily(generator, 7);

        tue.BorrowFees!.Accruals.Should().Contain(JpAccrual);
        tue.StopLossMethods!.Approvals.Select(a => a.DecisionId).Should().Contain(JpDecision);
        tue.FxSourceStatus!.Usages.Should().Equal(JpSessionUsage);
        wed.BorrowFees!.Accruals.Should().NotContain(JpAccrual);
        wed.StopLossMethods!.Approvals.Select(a => a.DecisionId).Should().NotContain(JpDecision);
    }

    // T-06-069, FR-06, #1224, IADR-0516 決定 2・5: LLM 利用実績は JST の暦日のまま引き（窓に揃えない）、日報の「集計したセッション」の行に
    // 暦日の範囲を書く。
    // T-06-070, FR-06, #1224, IADR-0516 決定 3: 窓に揃える入力の照会の範囲は窓を覆う JST の暦日の外包（日報 10-06 は 10-05〜10-06）。
    [Fact]
    public async Task T06_069_LLM利用実績は暦日のまま引き_集計したセッションの行に暦日を書く()
    {
        var store = new InMemoryReportStore();
        var (generator, requests) = Build(store, new FixedClock(new DateTimeOffset(2026, 10, 6, 7, 0, 0, TimeSpan.Zero)));

        await generator.RunOnceAsync();

        store.Get("daily-2026-10-06")!.Report.Body.Should().Contain(
            "# 日報 2026-10-06\n\n集計したセッション: 米国 2026-10-05（ET）／東証 2026-10-06（JST）・LLM 利用実績は JST の暦日 2026-10-06（生成時点まで）\n\n");
        requests.Seen.Should().Contain(("llm", new DateOnly(2026, 10, 6), new DateOnly(2026, 10, 6)));
        foreach (var input in new[] { "borrow", "reduction", "buyin", "approval", "resolution", "fx" })
            requests.Seen.Should().Contain((input, new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 6)), input);
    }
}
