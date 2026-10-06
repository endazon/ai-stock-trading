using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Common.Abstractions;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Infrastructure.Persistence;
using Xunit;

namespace ReportService.Tests;

// FR-06, UC-03〜05, 計画 ADR-0052 決定 2, #1172, IADR-0492: 日報が米国市場の約定を集計すること（自動生成と作り直しの両経路）。
//
// 🔴 供給元のスタブは**取引台帳の契約どおりに絞る**（約定・取り込みの市場の現地取引日が [from, to] に入る行だけを返す。
// RiskManagementService の PeriodFillQuery / PeriodDriftAdoptionQuery と同じ）。全行を返すスタブでは、
// 照会の範囲の誤り（#1172 の原因）が見えない。
public class ReportUsSessionCoverageTests
{
    private static readonly TimeZoneInfo Eastern =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    private static readonly TimeZoneInfo Tokyo =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Tokyo Standard Time" : "Asia/Tokyo");

    // 2026-10-05（月）16:00 JST・2026-10-06（火）16:00 JST・2026-10-07（水）10:00 JST。
    private static readonly DateTimeOffset MonAfterBoundary = new(2026, 10, 5, 7, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset TueAfterBoundary = new(2026, 10, 6, 7, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WedMorning = new(2026, 10, 7, 1, 0, 0, TimeSpan.Zero);

    private const string AdoptionReason = "証券会社のアプリで手動決済（#1172 の検証）";

    private static DateTimeOffset Et(int month, int day, int hour, int minute)
    {
        var local = new DateTime(2026, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Eastern.GetUtcOffset(local));
    }

    private static DateOnly LocalTradingDay(DateTimeOffset instant, Market market) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, market == Market.UnitedStates ? Eastern : Tokyo).DateTime);

    // 2026-10-05（ET）の米国のセッション: AAPL を買って同じセッションで決済する（買 1・売 1・決済 1）。
    private static readonly PeriodTradeFill[] UsSessionFills =
    [
        new("AAPL", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 10, 100m, Et(10, 5, 10, 0)),
        new("AAPL", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, 10, 110m, Et(10, 5, 15, 30)),
    ];

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class StubDrafter : IReportNarrativeDrafter
    {
        public Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult("散文");
    }

    // 取引台帳の契約（市場の現地取引日で [from, to] を絞る）を写したスタブ。
    private sealed class LedgerFillSource(params PeriodTradeFill[] fills) : IPeriodFillSource
    {
        public List<(DateOnly From, DateOnly To)> Requested { get; } = [];

        public Task<IReadOnlyList<PeriodTradeFill>> GetFillsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
        {
            Requested.Add((from, to));
            return Task.FromResult<IReadOnlyList<PeriodTradeFill>>(
                [.. fills.Where(f => LocalTradingDay(f.ExecutedAt, f.Market) is var d && d >= from && d <= to)]);
        }
    }

    private sealed class LedgerDriftSource(params PeriodDriftAdoption[] adoptions) : IPeriodDriftAdoptionSource
    {
        public Task<IReadOnlyList<PeriodDriftAdoption>?> GetDriftAdoptionsAsync(
            DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PeriodDriftAdoption>?>(
                [.. adoptions.Where(a => LocalTradingDay(a.AdoptedAt, a.Market) is var d && d >= fromInclusive && d <= toInclusive)]);
    }

    private sealed class NoPositions : IOpenPositionSource
    {
        public Task<IReadOnlyList<ReportPosition>?> GetOpenPositionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ReportPosition>?>([]);
    }

    private sealed class NoAudit : IReportRegenerationAuditPublisher
    {
        public Task PublishAsync(ReportRegenerated evt) => Task.CompletedTask;
    }

    private static ReportAutoGenerator Generator(
        IReportStore store, IClock clock, IPeriodFillSource fills, IPeriodDriftAdoptionSource? drift = null,
        IReportRegenerationLedger? ledger = null) =>
        new(store, new ReportDraftService(new StubDrafter()), fills, clock, new ReportAutoGenerationSettings(),
            openPositionSource: new NoPositions(), driftAdoptionSource: drift ?? new LedgerDriftSource(), regenerationLedger: ledger);

    private static string DailyBody(IReportStore store, string key) => store.Get(key)!.Report.Body;

    // T-06-023, FR-06, #1172（再現）: 2026-10-05（ET）の米国の約定は日報 2026-10-06（10-06 16:00 JST 生成）に載る。
    // 日報 2026-10-05（10-05 16:00 JST 生成＝ET 03:00・セッション前）には載らない。修正前はどちらも「0 / 0 / 0」だった。
    [Fact]
    public async Task T06_023_ET_10月5日の米国の約定は日報_10月6日に載る()
    {
        var store = new InMemoryReportStore();
        var fills = new LedgerFillSource(UsSessionFills);
        var clock = new FixedClock(MonAfterBoundary);

        await Generator(store, clock, fills).RunOnceAsync();
        clock.UtcNow = TueAfterBoundary;
        await Generator(store, clock, fills).RunOnceAsync();

        DailyBody(store, "daily-2026-10-05").Should().Contain("| 取引回数（買/売/決済） | 0 / 0 / 0 |");
        DailyBody(store, "daily-2026-10-06").Should().Contain("| 取引回数（買/売/決済） | 1 / 1 / 1 |");
        fills.Requested.Should().Contain((new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 6)));
    }

    // T-06-024, FR-06, 計画 ADR-0052 決定 2, #1172: `/report regenerate` は自動生成と同じ窓で引く。
    // 縮退した日報 2026-10-06 を翌朝作り直すと、ET 10-05 のセッションの約定が載り、ET 10-06 の約定（次の日報の分）は載らない。
    [Fact]
    public async Task T06_024_作り直しは自動生成と同じ窓で約定を引く()
    {
        var store = new InMemoryReportStore();
        var nextSession = new PeriodTradeFill("MSFT", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 5, 500m, Et(10, 6, 10, 0));
        var fills = new LedgerFillSource([.. UsSessionFills, nextSession]);
        var clock = new FixedClock(WedMorning);
        var ledger = new InMemoryReportRegenerationLedger();
        var draft = new TradingReport
        {
            PeriodKey = "daily-2026-10-06",
            Kind = ReportKind.Daily,
            PeriodStart = new DateOnly(2026, 10, 6),
            Body = "# 日報 daily-2026-10-06\n\n縮退した下書き\n",
            UnsuppliedInputs = [ReportInput.Fills],
        };
        var version = store.UpsertDraft(draft, 0);
        store.ApplyReview(draft.PeriodKey, new ReviewCommand(ReviewAction.Present, "owner", version));
        var settings = new ReportAutoGenerationSettings();
        var service = new ReportRegenerationService(
            store, clock, Generator(store, clock, fills, ledger: ledger), settings, ledger,
            new ReportRegenerationLimit(ReportRegenerationLimit.DefaultDailyLimit), new NoAudit(),
            NullLogger<ReportRegenerationService>.Instance);

        var result = await service.RegenerateAsync("daily-2026-10-06", "owner");

        result.Status.Should().Be(ReportRegenerationStatus.Regenerated);
        var body = DailyBody(store, "daily-2026-10-06");
        body.Should().Contain("| 取引回数（買/売/決済） | 1 / 1 / 1 |");
        body.Should().NotContain("MSFT");
    }

    // T-06-025, FR-06, FR-11, #1172: 手動売買の取り込みも約定と同じ窓で絞る（§2 と §2-b・在庫の畳み込みが同じセッションを見る）。
    // ET 10-05 の夜（JST 10-06 09:00）に取り込んだ行は日報 2026-10-06 の §2-b に載る。
    [Fact]
    public async Task T06_025_手動売買の取り込みも約定と同じ窓で載る()
    {
        var store = new InMemoryReportStore();
        var adoptedAt = Et(10, 5, 20, 0);
        var adoption = new PeriodDriftAdoption(
            Guid.NewGuid(), "NVDA", Market.UnitedStates, TradeSide.Sell, 5, 10, 5, adoptedAt.AddMinutes(-5), "owner", AdoptionReason, adoptedAt);

        await Generator(store, new FixedClock(TueAfterBoundary), new LedgerFillSource(), new LedgerDriftSource(adoption)).RunOnceAsync();

        DailyBody(store, "daily-2026-10-06").Should().Contain(AdoptionReason);
    }
}
