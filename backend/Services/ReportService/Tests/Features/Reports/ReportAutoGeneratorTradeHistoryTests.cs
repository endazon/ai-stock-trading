using ReportService.Infrastructure.Persistence;
using ReportService.Infrastructure.ExternalServices;
using ReportService.Common.Abstractions;
using ReportService.Features.Reports;
using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-16, FR-11, #563, IADR-0269: 自動生成の**出口（永続化された本文）**に日報 §2 / §3 が出ることを固定する。
//
// 🔴 **供給 → 組み立て → 描画 → 永続化まで通しで見る。** ポートやレンダラの単体テストは、
// 途中の結線が 1 本切れていても緑になる（#563 はまさにその状態が長期間続いた事故である）。
public class ReportAutoGeneratorTradeHistoryTests
{
    // 2026-07-08（水）16:00 JST ＝ 07:00 UTC。日報だけが生成境界を越えている時刻。
    private static readonly DateTimeOffset WedAfterClose = new(2026, 7, 8, 7, 0, 0, TimeSpan.Zero);
    private static readonly Guid DecisionId = new("11111111-1111-1111-1111-111111111111");

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class StubDrafter : IReportNarrativeDrafter
    {
        public Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult("自動生成の散文");
    }

    private sealed class StubFillSource(params PeriodTradeFill[] fills) : IPeriodFillSource
    {
        public Task<IReadOnlyList<PeriodTradeFill>> GetFillsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PeriodTradeFill>>(fills);
    }

    private sealed class StubRationaleSource(IReadOnlyDictionary<Guid, string>? rationales) : ITradeRationaleSource
    {
        public List<(DateOnly From, DateOnly To)> Requested { get; } = [];

        public Task<IReadOnlyDictionary<Guid, string>?> GetRationalesAsync(
            DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
        {
            Requested.Add((from, to));
            return Task.FromResult(rationales);
        }
    }

    private sealed class ThrowingRationaleSource : ITradeRationaleSource
    {
        public Task<IReadOnlyDictionary<Guid, string>?> GetRationalesAsync(
            DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("監査台帳へ到達できません");
    }

    private sealed class StubOpenPositionSource(IReadOnlyList<ReportPosition>? positions) : IOpenPositionSource
    {
        public Task<IReadOnlyList<ReportPosition>?> GetOpenPositionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(positions);
    }

    private sealed class ThrowingOpenPositionSource : IOpenPositionSource
    {
        public Task<IReadOnlyList<ReportPosition>?> GetOpenPositionsAsync(CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("台帳へ到達できません");
    }

    private static ReportAutoGenerator NewGenerator(
        IReportStore store,
        IPeriodFillSource? fills = null,
        ITradeRationaleSource? rationaleSource = null,
        IOpenPositionSource? openPositionSource = null) =>
        new(store,
            new ReportDraftService(new StubDrafter()),
            fills ?? new NoOpPeriodFillSource(),
            new FixedClock(WedAfterClose),
            new ReportAutoGenerationSettings(),
            notifier: null,
            reductionSource: null,
            buyInSource: null,
            fxSourceStatusSource: null,
            llmUsageSource: null,
            borrowFeeSource: null,
            rationaleSource: rationaleSource,
            openPositionSource: openPositionSource);

    private static PeriodTradeFill Fill(TradeSide side, decimal price, int hour, Guid decisionId = default) =>
        new("7203", Market.Japan, side, side == TradeSide.Buy ? PositionEffect.Open : PositionEffect.Close,
            100, price, new DateTimeOffset(2026, 7, 8, hour, 0, 0, TimeSpan.Zero), decisionId);

    private static string BodyOf(IReportStore store) => store.Get("daily-2026-07-08")!.Report.Body;

    // 🔴 **受け入れ基準 1・4**: 生成された日報の**本文**に §2 / 取引詳細 / 見送り判断 / §3 が出る。
    [Fact]
    public async Task 生成された日報の本文に取引履歴とポジション一覧の節が出る()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, new StubFillSource(Fill(TradeSide.Buy, 2_000m, 1))).RunOnceAsync();

        var body = BodyOf(store);
        body.Should().Contain("## 2. 取引履歴（全明細）");
        body.Should().Contain("### 取引詳細（選定・売買の判断理由）");
        body.Should().Contain("### 見送り判断（主要なもの）");
        body.Should().Contain("## 3. ポジション一覧（当日終了時点）");
    }

    // 🔴 **肯定形**: 台帳の約定が明細の行として本文へ届く（節の見出しだけでは結線とは言えない）。
    [Fact]
    public async Task 台帳の約定が明細の行として本文へ届く()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, new StubFillSource(
            Fill(TradeSide.Buy, 2_000m, 1), Fill(TradeSide.Sell, 2_100m, 3))).RunOnceAsync();

        var body = BodyOf(store);
        // 1 約定 = 1 行。時刻は JST（01:00Z = 10:00 JST / 03:00Z = 12:00 JST）。
        body.Should().Contain("| 1 | 10:00 | JP | 7203 **未供給** | 買 | 100 | 2,000 |");
        body.Should().Contain("| 2 | 12:00 | JP | 7203 **未供給** | 売 | 100 | 2,100 |");
        // 実現損益は在庫が減る約定にのみ計上する（(2,100 − 2,000) × 100）。
        body.Should().Contain("| +10,000 |");
    }

    // 🔴 **否定形（上の肯定形と対）**: 約定があるのに「当日の約定なし」と書かない。
    [Fact]
    public async Task 約定があるのに約定なしと書かない()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, new StubFillSource(Fill(TradeSide.Buy, 2_000m, 1))).RunOnceAsync();

        BodyOf(store).Should().NotContain("（当日の約定なし）");
    }

    // 受け入れ基準 3: 約定が 0 件の日でも節ごと消えない。
    [Fact]
    public async Task 約定が0件でも節は消えない()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, new StubFillSource()).RunOnceAsync();

        var body = BodyOf(store);
        body.Should().Contain("## 2. 取引履歴（全明細）");
        body.Should().Contain("（当日の約定なし）");
    }

    // 🔴 **受け入れ基準 2（肯定形）**: 記録済みの判断根拠が**そのまま**明細へ載る（LLM を介さない）。
    [Fact]
    public async Task 記録済みの判断根拠が明細へそのまま載る()
    {
        var store = new InMemoryReportStore();
        var rationales = new StubRationaleSource(
            new Dictionary<Guid, string> { [DecisionId] = "始値が支持線で反発。出来高増。" });

        await NewGenerator(store, new StubFillSource(Fill(TradeSide.Buy, 2_000m, 1, DecisionId)), rationales)
            .RunOnceAsync();

        BodyOf(store).Should().Contain("| 始値が支持線で反発。出来高増。 |");
        // 判断根拠は**当該報告期間**で引く（別の日の根拠を混ぜない）。
        rationales.Requested.Should().Contain((new DateOnly(2026, 7, 8), new DateOnly(2026, 7, 8)));
    }

    // 🔴 **否定形（上の肯定形と対）**: 供給元が未注入・照会失敗なら未供給と書く（「根拠なし」ではない）。
    [Fact]
    public async Task 判断根拠の供給が無ければ明細は未供給と書く()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, new StubFillSource(Fill(TradeSide.Buy, 2_000m, 1, DecisionId)))
            .RunOnceAsync();

        BodyOf(store).Should().Contain("| **未供給** |");
    }

    [Fact]
    public async Task 判断根拠の照会が失敗しても生成は続き未供給になる()
    {
        var store = new InMemoryReportStore();

        var result = await NewGenerator(
                store, new StubFillSource(Fill(TradeSide.Buy, 2_000m, 1, DecisionId)), new ThrowingRationaleSource())
            .RunOnceAsync();

        result.Failed.Should().BeEmpty();
        BodyOf(store).Should().Contain("| **未供給** |");
    }

    // 🔴 **肯定形**: 建玉が本文の §3 へ届き、現在値から評価損益が算出される。
    [Fact]
    public async Task 建玉が本文のポジション一覧へ届く()
    {
        var store = new InMemoryReportStore();
        var positions = new StubOpenPositionSource(
        [
            new ReportPosition(Market.Japan, "7203", TradeSide.Buy, 100, 2_000m, 1_900m,
                CurrentPrice: null, UnrealizedPnl: null, BorrowFeeTotal: null, HoldingDays: null),
        ]);

        await NewGenerator(store, new StubFillSource(Fill(TradeSide.Buy, 2_000m, 1)), openPositionSource: positions)
            .RunOnceAsync();

        BodyOf(store).Should().Contain("| JP | 7203 | ロング | 100 | 2,000 |");
    }

    // 🔴 **否定形（上の肯定形と対）**: 供給が無い・照会失敗を「建玉なし」と書かない。
    [Fact]
    public async Task 建玉の供給が無ければ照会できなかったと書き建玉なしとは書かない()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, new StubFillSource()).RunOnceAsync();

        var body = BodyOf(store);
        body.Should().Contain("**建玉を照会できませんでした（供給元がありません）**");
        body.Should().NotContain("（当日終了時点の建玉なし）");
    }

    [Fact]
    public async Task 建玉の照会が失敗しても生成は続き未供給になる()
    {
        var store = new InMemoryReportStore();

        var result = await NewGenerator(store, new StubFillSource(), openPositionSource: new ThrowingOpenPositionSource())
            .RunOnceAsync();

        result.Failed.Should().BeEmpty();
        BodyOf(store).Should().Contain("**建玉を照会できませんでした（供給元がありません）**");
    }

    // 空列は「建玉なし」（未供給ではない）。上の 2 件と合わせて 3 状態を区別できることを固定する。
    [Fact]
    public async Task 建玉が空列なら建玉なしと書き未供給とは書かない()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, new StubFillSource(), openPositionSource: new StubOpenPositionSource([]))
            .RunOnceAsync();

        var body = BodyOf(store);
        body.Should().Contain("（当日終了時点の建玉なし）");
        body.Should().NotContain("**建玉を照会できませんでした（供給元がありません）**");
    }
}
