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

    // 🔴 FR-17, #615, IADR-0305: **§5 の費用内訳の合計が §1 の費用合計と一致する**
    //（同じ約定・同じ費用関数から数えていることの、出口での証跡）。
    [Fact]
    public async Task 費用レビューの内訳の合計が週間サマリの費用合計と一致する()
    {
        var fills = new[]
        {
            Fill(TradeSide.Buy, 10, 1_000m, 0),
            Fill(TradeSide.Sell, 10, 1_200m, 2_880),
        };
        var svc = new ReportDraftService(new FakeDrafter());

        var draft = await svc.BuildDraftAsync(Request(
            ReportKind.Weekly, "weekly-2026-W35", new DateOnly(2026, 8, 24), fills));

        draft.Markdown.Should().Contain("## 5. リスク・費用レビュー");
        draft.Markdown.Should().Contain("| 費用の区分 | 金額 |");
        // §1 の費用合計と §5 の「費用合計」が**同じ文字列**で出る（表記も 1 か所に単一化されている）。
        var total = ReportAmountFormat.Base(draft.Pnl.TotalCost);
        draft.Markdown.Should().Contain($"| 費用合計（§1 と同じ値） | {total} |");
        draft.Markdown.Should().Contain($"| 費用合計（手数料・諸費用・為替） | {total} |");
        // 諸費用は記録源が無い（0 と書かない）。
        draft.Markdown.Should().Contain("| 取引諸費用 | **未供給** |");
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
        draft.Markdown.Should().NotContain("| 費用の区分 | 金額 |");
    }

    // 🔴 FR-06, FR-07, FR-16, #615, IADR-0306: **月報も同じ帰属を消費する**（週報だけの供給ゲートだと
    // 月報 §2 が「供給元がありません」のままになる——結線はレンダラのテストでは捕まえられない）。
    [Fact]
    public async Task 月報ドラフトは約定列から週別市場別方向別の内訳を出す()
    {
        var fills = new[]
        {
            Fill(TradeSide.Buy, 10, 1_000m, 0),                       // 2026-W35（月）
            Fill(TradeSide.Sell, 10, 1_200m, 60 * 24 * 14),           // 2026-W37（持ち越した玉の決済）
        };
        var svc = new ReportDraftService(new FakeDrafter());

        var draft = await svc.BuildDraftAsync(Request(
            ReportKind.Monthly, "monthly-2026-08", new DateOnly(2026, 8, 24), fills));

        draft.Markdown.Should().Contain("## 2. 週別・市場別の内訳");
        draft.Markdown.Should().Contain("| 2026-W35 |");
        draft.Markdown.Should().Contain("| 2026-W37 |");
        draft.Markdown.Should().Contain("| 日本株 |");
        draft.Markdown.Should().Contain("| 米国株 |");
        draft.Markdown.Should().Contain("| ロング（現物・信用買い） |");
        draft.Markdown.Should().Contain("| ショート（空売り） |");

        // 🔴 **§2 の中だけを見る**（§3 税金レビューは未実装のまま残るため、全文で見ると必ず拾う）。
        var start = draft.Markdown.IndexOf("## 2. 週別・市場別の内訳", StringComparison.Ordinal);
        var end = draft.Markdown.IndexOf("## 3. 税金レビュー", start, StringComparison.Ordinal);
        draft.Markdown[start..end].Should().NotContain("本節は未実装です");
    }

    // 🔴 **内訳の和が §1 サマリと一致する**（週で切って集計器を呼び直していないことの、出口での証跡）。
    [Fact]
    public async Task 月報の市場別の費用の合計が月間サマリの費用合計と一致する()
    {
        var fills = new[]
        {
            Fill(TradeSide.Buy, 10, 1_000m, 0),
            Fill(TradeSide.Sell, 10, 1_200m, 60 * 24 * 14),
        };
        var svc = new ReportDraftService(new FakeDrafter());

        var draft = await svc.BuildDraftAsync(Request(
            ReportKind.Monthly, "monthly-2026-08", new DateOnly(2026, 8, 24), fills));

        // 既定前提（US・手数料 0）では費用が 0 であり、実現損益（税引前・費用前）は 2,000。
        draft.Pnl.RealizedPnlGross.Should().Be(2_000m);
        draft.Markdown.Should().Contain(
            $"| 米国株 | {ReportAmountFormat.Base(draft.Pnl.RealizedPnlGross)} | {ReportAmountFormat.Base(draft.Pnl.TotalCost)} |");
        // 建てた週と決済した週が分かれても、実現損益は決済した週の行に丸ごと出る（再畳み込みしていない証跡）。
        draft.Markdown.Should().Contain($"| 2026-W37 | {ReportAmountFormat.Base(2_000m)} | 1 |");
    }
}
