using ReportService.Features.Reports;
using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-07, FR-16, #615, IADR-0301, 04_report-templates 週報 §2 / §3:
// **ドラフト生成の結線**（約定列 → 約定単位の損益帰属 → 週報の本文）を検証する。
//
// 🔴 **レンダラのテストとゴールデンだけでは、`ReportDraftService` が帰属を渡していなくても緑になる**——
// ゴールデンは `ReportView` を手で組み立てるためである（#563 で実際に起きた形）。ここでは**約定列から**
// 本文まで通し、**内訳が §1 サマリと整合する**ことを出口で固定する。
public class ReportDraftWeeklyBreakdownTests
{
    private sealed class FakeDrafter : IReportNarrativeDrafter
    {
        public Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult("週の振り返り");
    }

    // 2026-08-24（月）JST 09:05 起点。UTC 00:05 = JST 09:05。
    private static readonly DateTimeOffset T0 = new(2026, 8, 24, 0, 5, 0, TimeSpan.Zero);

    private static PeriodTradeFill Fill(TradeSide side, int qty, decimal price, int minutes, Guid decisionId = default) =>
        new("AAPL", Market.UnitedStates, side, side == TradeSide.Buy ? PositionEffect.Open : PositionEffect.Close,
            qty, price, T0.AddMinutes(minutes), decisionId);

    private static DraftRequest Request(
        ReportKind kind, string periodKey, DateOnly date, IReadOnlyList<PeriodTradeFill> fills,
        IReadOnlyDictionary<Guid, string>? rationales = null) =>
        new(kind, periodKey, date, ["US"], 1, null, "翌週は押し目買い", fills, null, TradeRationales: rationales);

    [Fact]
    public async Task 週報ドラフトは約定列から日別推移とハイライト取引を出す()
    {
        var decisionId = Guid.NewGuid();
        var fills = new[]
        {
            Fill(TradeSide.Buy, 10, 1_000m, 0),            // 月曜: 新規建て（決済なし）
            Fill(TradeSide.Sell, 10, 1_200m, 2_880, decisionId), // 水曜: 利確（持ち越した玉の決済）
        };
        var svc = new ReportDraftService(new FakeDrafter());

        var draft = await svc.BuildDraftAsync(Request(
            ReportKind.Weekly, "weekly-2026-W35", new DateOnly(2026, 8, 24), fills,
            new Dictionary<Guid, string> { [decisionId] = "目標価格へ到達したため利確。" }));

        draft.Markdown.Should().Contain("## 2. 日別推移");
        draft.Markdown.Should().Contain("| 2026-08-24 |");
        draft.Markdown.Should().Contain("決済なし（新規建てのみ）");
        draft.Markdown.Should().Contain("| 2026-08-26 |");
        draft.Markdown.Should().Contain("## 3. ハイライト取引");
        // 判断根拠は記録の転記（報告書生成時に文章を作っていない）。
        draft.Markdown.Should().Contain("判断の要点: 目標価格へ到達したため利確。");
        draft.Markdown.Should().Contain("`daily-2026-08-26`");
    }

    // 🔴 **内訳の合計が §1 サマリと一致する**（畳み込みが期間全体で 1 回だけであることの、出口での証跡）。
    [Fact]
    public async Task 日別推移の合計が週間サマリと整合する()
    {
        var fills = new[]
        {
            Fill(TradeSide.Buy, 10, 1_000m, 0),
            Fill(TradeSide.Sell, 10, 1_200m, 2_880), // 実現損益（税引前・費用前）= 2,000
        };
        var svc = new ReportDraftService(new FakeDrafter());

        var draft = await svc.BuildDraftAsync(Request(
            ReportKind.Weekly, "weekly-2026-W35", new DateOnly(2026, 8, 24), fills));

        // 既定前提（手数料・為替 0）では、日別の実現損益の合計 = 税引前の実現損益 = 2,000。
        draft.Pnl.RealizedPnlGross.Should().Be(2_000m);
        draft.Markdown.Should().Contain("| 2026-08-26 | +2,000.00 USD | 1 |");
        // §1 の取引回数（買/売/決済）の 買＋売 と、日別の取引数の合計（1 + 1）が一致する。
        draft.Markdown.Should().Contain("取引回数（買/売/決済） | 1 / 1 / 1");
    }

    [Theory]
    [InlineData(ReportKind.Daily, "daily-2026-08-24")]
    [InlineData(ReportKind.Monthly, "monthly-2026-08")]
    public async Task 日報と月報のドラフトには週報の内訳を出さない(ReportKind kind, string periodKey)
    {
        var svc = new ReportDraftService(new FakeDrafter());

        var draft = await svc.BuildDraftAsync(Request(
            kind, periodKey, new DateOnly(2026, 8, 24), [Fill(TradeSide.Buy, 10, 1_000m, 0)]));

        draft.Markdown.Should().NotContain("## 2. 日別推移");
        draft.Markdown.Should().NotContain("## 3. ハイライト取引");
    }
}
