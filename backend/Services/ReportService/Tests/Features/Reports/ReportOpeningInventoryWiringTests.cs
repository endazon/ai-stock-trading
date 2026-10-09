using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using ReportService.Common.Abstractions;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Infrastructure.Persistence;
using Xunit;

namespace ReportService.Tests;

// T-06-050〜T-06-052, FR-06, FR-16, UC-03〜05, 計画 ADR-0053 決定 2, #1181, IADR-0493 決定 1・4: 自動生成（日報・週報・月報）が
// 期間開始時点の在庫を**窓の市場ごとの下端**で引き、5 つの畳み込みの初期在庫に置くこと。照会できなければ未供給（fail-closed）。
public class ReportOpeningInventoryWiringTests
{
    // 2026-10-06（火）16:00 JST の後 ＝ daily-2026-10-06（米国 ET 10-05・東証 10-06 のセッション）。
    private static readonly DateTimeOffset TueAfterBoundary = new(2026, 10, 6, 7, 0, 0, TimeSpan.Zero);

    // ET 2026-10-05 のセッション（EDT・UTC−4）。
    private static DateTimeOffset Et(int hour, int minute) => new(2026, 10, 5, hour + 4, minute, 0, TimeSpan.Zero);

    // issue #1181 の観測（MSFT 468 株 511.912 → 527.15・NVDA 1049 株 230.77 → 237.69）。
    private static readonly PeriodTradeFill[] IssueFills =
    [
        new("MSFT", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, 468, 527.15m, Et(10, 30)),
        new("NVDA", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, 1049, 237.69m, Et(11, 0)),
    ];

    private static readonly OpeningLot[] IssueOpening =
    [
        new("MSFT", Market.UnitedStates, 468, 511.912m, 150m, 0),
        new("NVDA", Market.UnitedStates, 1049, 230.77m, 150m, 0),
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

    private sealed class Fills(params PeriodTradeFill[] fills) : IPeriodFillSource
    {
        public Task<IReadOnlyList<PeriodTradeFill>> GetFillsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PeriodTradeFill>>(fills);
    }

    private sealed class NoDrift : IPeriodDriftAdoptionSource
    {
        public Task<IReadOnlyList<PeriodDriftAdoption>?> GetDriftAdoptionsAsync(
            DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PeriodDriftAdoption>?>([]);
    }

    private sealed class NoPositions : IOpenPositionSource
    {
        public Task<IReadOnlyList<ReportPosition>?> GetOpenPositionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ReportPosition>?>([]);
    }

    // 市場ごとの在庫を返すスタブ。引かれた (市場, before) を記録する。null を返すと「照会できていない」、例外も同じ扱いになる。
    private sealed class OpeningSource(IReadOnlyList<OpeningLot>? lots, bool throws = false) : IOpeningInventorySource
    {
        public List<(Market Market, DateOnly Before)> Requested { get; } = [];

        public Task<IReadOnlyList<OpeningLot>?> GetOpeningInventoryAsync(
            Market market, DateOnly beforeTradingDay, CancellationToken cancellationToken = default)
        {
            Requested.Add((market, beforeTradingDay));
            if (throws)
                throw new HttpRequestException("台帳に届きません");
            return Task.FromResult<IReadOnlyList<OpeningLot>?>(lots is null ? null : [.. lots.Where(l => l.Market == market)]);
        }
    }

    private static ReportAutoGenerator Generator(IReportStore store, IClock clock, IPeriodFillSource fills, IOpeningInventorySource? opening) =>
        new(store, new ReportDraftService(new StubDrafter()), fills, clock, new ReportAutoGenerationSettings(),
            openPositionSource: new NoPositions(), driftAdoptionSource: new NoDrift(), openingInventorySource: opening);

    // ---- T-06-050: 日報は窓の市場ごとの下端で引き、警告が消える ----

    [Fact]
    public async Task T06_050_日報は窓の市場ごとの下端で在庫を引き_実現損益が出て警告が消える()
    {
        var store = new InMemoryReportStore();
        var opening = new OpeningSource(IssueOpening);

        await Generator(store, new FixedClock(TueAfterBoundary), new Fills(IssueFills), opening).RunOnceAsync();

        // daily-2026-10-06 の窓: 米国 ET 10-05・東証 JST 10-06。下端より前（排他）を引く。
        opening.Requested.Take(2).Should().BeEquivalentTo([(Market.Japan, new DateOnly(2026, 10, 6)), (Market.UnitedStates, new DateOnly(2026, 10, 5))]);
        // T-06-082, FR-06, 計画 ADR-0059 決定 3, #1218, IADR-0519 決定 3: 続けて日報 §6 の週初来の窓（W41 の週報の窓を 10-06 で打ち切ったもの）の
        // 下端で引く。週報と同じく前週金曜の生成境界（10-02 16:00 JST）の後＝米国 ET 10-02・東証 10-03 から。
        opening.Requested.Skip(2).Should().BeEquivalentTo([(Market.Japan, new DateOnly(2026, 10, 3)), (Market.UnitedStates, new DateOnly(2026, 10, 2))]);

        var report = store.Get("daily-2026-10-06")!.Report;
        report.UnsuppliedInputs.Should().NotContain(ReportInput.OpeningInventory);
        // 税引後の手計算（OpeningInventoryAggregationTests の T06_040 の約定代金差額 14,390.464 から）。生成器は既定の前提条件を使うため、
        // #1201・計画 ADR-0035 決定 5 の取引諸費用（米国株の売り: MSFT 246,706.2 USD・468 株／NVDA 249,336.81 USD・1,049 株
        // → SEC 10.21848… ＋ TAF 0.251822 ＝ 10.470308006）が控除される: 税 = (14,390.464 − 10.470308006) × 0.20315 = 2,921.2957…
        // → 税引後 11,458.6979734654189。
        report.Body.Should().Contain($"| 実現損益（税引後・費用込み） | {ReportAmountFormat.Base(11_458.6979734654189m)} |")
            .And.NotContain("算出不能");
    }

    [Fact]
    public async Task T06_050_週報と月報も窓の市場ごとの下端で在庫を引く()
    {
        // 2026-10-30（金）17:30 JST: daily-2026-10-30・weekly-2026-W44・monthly-2026-10 が生成対象。
        var store = new InMemoryReportStore();
        var opening = new OpeningSource([]);

        await Generator(store, new FixedClock(new DateTimeOffset(2026, 10, 30, 8, 30, 0, TimeSpan.Zero)), new Fills(), opening).RunOnceAsync();

        store.Get("weekly-2026-W44").Should().NotBeNull();
        store.Get("monthly-2026-10").Should().NotBeNull();
        // 月報 10 月の窓は 米国 ET 09-30〜・東証 10-01〜（計画 ADR-0053 決定 2）。週報 W44 は 米国 ET 10-23〜・東証は
        // 前週金曜の生成境界（10-23 16:00 JST）の翌暦日 10-24 から（土日はセッションが無いので 10-26 の前と同じ在庫になる）。
        opening.Requested.Should().Contain((Market.UnitedStates, new DateOnly(2026, 9, 30)))
            .And.Contain((Market.Japan, new DateOnly(2026, 10, 1)))
            .And.Contain((Market.UnitedStates, new DateOnly(2026, 10, 23)))
            .And.Contain((Market.Japan, new DateOnly(2026, 10, 24)));
        store.Get("monthly-2026-10")!.Report.UnsuppliedInputs.Should().NotContain(ReportInput.OpeningInventory);
        store.Get("weekly-2026-W44")!.Report.UnsuppliedInputs.Should().NotContain(ReportInput.OpeningInventory);
    }

    // ---- T-06-051: 照会できなければ未供給（fail-closed） ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T06_051_在庫を照会できなければ未供給として記録し_取得原価を要する値を算出不能にする(bool throws)
    {
        var store = new InMemoryReportStore();
        var opening = new OpeningSource(lots: null, throws);
        // 当期に建てて当期に決済した約定（期間の在庫で賄える）——在庫が無いと持ち越し分と混ぜた平均が分からない。
        PeriodTradeFill[] fills =
        [
            new("AAPL", Market.UnitedStates, TradeSide.Buy, PositionEffect.Open, 10, 100m, Et(10, 0)),
            new("AAPL", Market.UnitedStates, TradeSide.Sell, PositionEffect.Close, 10, 110m, Et(15, 0)),
        ];

        await Generator(store, new FixedClock(TueAfterBoundary), new Fills(fills), opening).RunOnceAsync();

        var report = store.Get("daily-2026-10-06")!.Report;
        report.UnsuppliedInputs.Should().Contain(ReportInput.OpeningInventory);
        report.Body.Should().Contain("| 実現損益（税引後・費用込み） | **算出不能**（期間開始時点の在庫を照会できず")
            .And.Contain("| 評価損益（税引前・参考） | **算出不能**（期間開始時点の在庫を照会できず")
            .And.Contain("| 源泉徴収税額 | **算出不能**（期間開始時点の在庫を照会できず")
            // T-06-055（独立監査 🟡2）: 為替差損益（独立表示）も持ち越した建玉の認識時レートが分からず部分値である。
            .And.Contain("| 為替差損益（独立表示） | **算出不能**（期間開始時点の在庫を照会できず")
            .And.NotContain(ReportAmountFormat.Base(79.685m), "部分値（当期の約定だけの税引後の実現損益）を数字として出さない");
    }

    [Fact]
    public async Task T06_051_供給元が未注入なら従来どおり検出した回だけ未供給()
    {
        var store = new InMemoryReportStore();

        await Generator(store, new FixedClock(TueAfterBoundary), new Fills(IssueFills), opening: null).RunOnceAsync();

        var report = store.Get("daily-2026-10-06")!.Report;
        report.UnsuppliedInputs.Should().Contain(ReportInput.OpeningInventory, "期間より前に建てた建玉の決済を検出した（IADR-0381）");
        report.Body.Should().Contain("**算出不能**（期間より前に建てた建玉の決済が 2 件あり");
    }

    // ---- T-06-043（結線）: 在庫を受け取っても賄えない売りは未供給として記録する ----

    [Fact]
    public async Task T06_043_在庫で賄えない手仕舞いは在庫を受け取っても未供給として記録する()
    {
        var store = new InMemoryReportStore();
        var opening = new OpeningSource([new OpeningLot("MSFT", Market.UnitedStates, 100, 511.912m, 150m, 0)]);

        await Generator(store, new FixedClock(TueAfterBoundary), new Fills(IssueFills), opening).RunOnceAsync();

        var report = store.Get("daily-2026-10-06")!.Report;
        report.UnsuppliedInputs.Should().Contain(ReportInput.OpeningInventory);
        report.Body.Should().Contain("**算出不能**（期間より前に建てた建玉の決済が 2 件あり");
    }
}
