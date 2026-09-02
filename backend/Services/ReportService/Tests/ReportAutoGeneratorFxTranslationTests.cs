using ReportService.Infrastructure.Persistence;
using ReportService.Infrastructure.ExternalServices;
using ReportService.Common.Abstractions;
using ReportService.Features.Reports;
using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-16, #611, 04_report-templates 日報 §1 / 月報 §1（為替差損益の独立行）, IADR-0250, IADR-0285:
// 為替差損益の供給（認識時レート＝約定・期末レート＝供給ポート）が**自動生成へ結線されている**ことと、
// **その失敗の向き**（未供給へ戻る・0 円と書かない）を固定する。
//
// 🔴 「呼ばれたこと」と「結果が出口へ出たこと」は別の事実である。本テストは**生成された本文**を見る。
public class ReportAutoGeneratorFxTranslationTests
{
    // 2026-07-08（水）16:00 JST ＝ 07:00 UTC。日報だけが生成境界を越えている時刻。
    private static readonly DateTimeOffset WedAfterClose = new(2026, 7, 8, 7, 0, 0, TimeSpan.Zero);

    // 2026-07-31（金）17:00 JST ＝ 08:00 UTC。月報の生成境界を越えている時刻。
    private static readonly DateTimeOffset MonthEndAfterClose = new(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);

    private const string Unsupplied = "為替差損益（独立表示） | **供給されていません**（0 円ではありません）";

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class StubDrafter : IReportNarrativeDrafter
    {
        public Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult("自動生成の散文");
    }

    private sealed class StubFillSource(IReadOnlyList<PeriodTradeFill> fills) : IPeriodFillSource
    {
        public Task<IReadOnlyList<PeriodTradeFill>> GetFillsAsync(
            DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            Task.FromResult(fills);
    }

    private sealed class StubPeriodEndFxRateSource(PeriodEndFxRate? rate) : IPeriodEndFxRateSource
    {
        public List<DateOnly> Requested { get; } = [];

        public Task<PeriodEndFxRate?> GetRateAsync(DateOnly periodEnd, CancellationToken cancellationToken = default)
        {
            Requested.Add(periodEnd);
            return Task.FromResult(rate);
        }
    }

    private sealed class ThrowingPeriodEndFxRateSource : IPeriodEndFxRateSource
    {
        public Task<PeriodEndFxRate?> GetRateAsync(DateOnly periodEnd, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("為替レート源へ到達できません");
    }

    // 2026-07-08 に AAPL を 10 株 $100 で買い（認識時 150 円/ドル）、期末まで持ち越す（＝期末レートが要る）。
    private static PeriodTradeFill UsBuyHeld(decimal? recognitionRate = 150m) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 10, 100m,
            new DateTimeOffset(2026, 7, 8, 14, 30, 0, TimeSpan.Zero), FxRateBaseToDisplay: recognitionRate);

    private static ReportAutoGenerator NewGenerator(
        IReportStore store,
        DateTimeOffset now,
        IReadOnlyList<PeriodTradeFill>? fills = null,
        IPeriodEndFxRateSource? periodEndFxRateSource = null) =>
        new(store,
            new ReportDraftService(new StubDrafter()),
            fills is null ? new NoOpPeriodFillSource() : new StubFillSource(fills),
            new FixedClock(now),
            new ReportAutoGenerationSettings(),
            periodEndFxRateSource: periodEndFxRateSource);

    private static string BodyOf(IReportStore store, ReportKind kind) =>
        store.List().Single(r => r.Kind == kind).Body;

    // 🔴 **否定形**: 期末レートの供給元が未注入で期末に建玉が残るなら未供給である（0 円ではない）。
    [Fact]
    public async Task 期末レートの供給元が未注入で建玉が残るなら未供給として描く()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, WedAfterClose, fills: [UsBuyHeld()]).RunOnceAsync();

        BodyOf(store, ReportKind.Daily).Should().Contain(Unsupplied);
    }

    // 🔴 **否定形**: 照会が失敗しても未供給へ倒す（0 円へ倒さない）。
    [Fact]
    public async Task 期末レートの照会が失敗しても未供給へ倒す()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, WedAfterClose, fills: [UsBuyHeld()], periodEndFxRateSource: new ThrowingPeriodEndFxRateSource())
            .RunOnceAsync();

        BodyOf(store, ReportKind.Daily).Should().Contain(Unsupplied);
    }

    // 🔴 **否定形**: 認識時レートが未記録の USD 建て約定があれば未供給とし、**件数を明記**する（黙って落とさない）。
    [Fact]
    public async Task 認識時レートが未記録の約定があれば未供給とし件数を明記する()
    {
        var store = new InMemoryReportStore();
        var rate = new PeriodEndFxRate(160m, new DateOnly(2026, 7, 8));

        await NewGenerator(store, WedAfterClose, fills: [UsBuyHeld(recognitionRate: null)],
                periodEndFxRateSource: new StubPeriodEndFxRateSource(rate))
            .RunOnceAsync();

        BodyOf(store, ReportKind.Daily)
            .Should().Contain("為替差損益（独立表示） | **供給されていません**（0 円ではありません。認識時レートが未記録の USD 建て約定 1 件）");
    }

    // **対の肯定形**: 認識時レート（約定）と期末レート（供給）が揃えば実値が日報の本文へ出る。
    // $1,000 を 150 円で認識し、期末 160 円で再測定 → +10,000 円（明細 1 件・期末レート 160.00〔2026-07-08 観測〕）。
    [Fact]
    public async Task 供給された期末レートで為替差損益を日報へ載せる()
    {
        var store = new InMemoryReportStore();
        var rate = new PeriodEndFxRate(160m, new DateOnly(2026, 7, 8));

        await NewGenerator(store, WedAfterClose, fills: [UsBuyHeld()], periodEndFxRateSource: new StubPeriodEndFxRateSource(rate))
            .RunOnceAsync();

        BodyOf(store, ReportKind.Daily)
            .Should().Contain("為替差損益（独立表示） | +10,000 JPY（明細 1 件・期末レート 160.00 JPY/USD〔2026-07-08 観測〕）");
    }

    // **対の肯定形（月報）**: 月報 §1 にも同じ独立行で出る。
    [Fact]
    public async Task 供給された期末レートで為替差損益を月報へ載せる()
    {
        var store = new InMemoryReportStore();
        var fill = new PeriodTradeFill("AAPL", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 10, 100m,
            new DateTimeOffset(2026, 7, 15, 14, 30, 0, TimeSpan.Zero), FxRateBaseToDisplay: 150m);
        var rate = new PeriodEndFxRate(155m, new DateOnly(2026, 7, 29));

        await NewGenerator(store, MonthEndAfterClose, fills: [fill], periodEndFxRateSource: new StubPeriodEndFxRateSource(rate))
            .RunOnceAsync();

        BodyOf(store, ReportKind.Monthly)
            .Should().Contain("為替差損益（独立表示） | +5,000 JPY（明細 1 件・期末レート 155.00 JPY/USD〔2026-07-29 観測〕）");
    }

    // 期末に建玉が残らなければ期末レートが無くても集計できる（決済は決済時レートへの再測定で確定する）。
    [Fact]
    public async Task 期末に建玉が残らなければ期末レート無しでも集計する()
    {
        var store = new InMemoryReportStore();
        var fills = new[]
        {
            UsBuyHeld(),
            new PeriodTradeFill("AAPL", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, 10, 110m,
                new DateTimeOffset(2026, 7, 8, 15, 0, 0, TimeSpan.Zero), FxRateBaseToDisplay: 155m),
        };

        await NewGenerator(store, WedAfterClose, fills: fills).RunOnceAsync();

        BodyOf(store, ReportKind.Daily).Should().Contain("為替差損益（独立表示） | +5,000 JPY（明細 1 件）");
    }

    // USD 建て約定が無い期間は「0 円（明細 0 件）」＝事実であり未供給ではない（供給元が未注入でも同じ）。
    [Fact]
    public async Task USD建て約定が無ければ0円0件として描く()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, WedAfterClose).RunOnceAsync();

        BodyOf(store, ReportKind.Daily).Should().Contain("為替差損益（独立表示） | 0 JPY（明細 0 件）");
    }

    // 期末レートの照会は**当該報告書の期末日**で行う（別の日の観測を期末レートにしない）。
    [Fact]
    public async Task 期末レートの照会は当該報告書の期末日で行う()
    {
        var source = new StubPeriodEndFxRateSource(null);

        await NewGenerator(new InMemoryReportStore(), MonthEndAfterClose, periodEndFxRateSource: source).RunOnceAsync();

        source.Requested.Should().Contain(new DateOnly(2026, 7, 31));
    }
}
