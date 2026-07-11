using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Report.Application.Services;
using AiStockTrading.Report.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.Report.Application.Tests;

// FR-06/07/16, IADR-0032: 報告書ドラフト生成のオーケストレーション（数値集計→散文ドラフト→テンプレート組み立て・日報/週報/月報）を検証する。
public class ReportDraftServiceTests
{
    // 散文を固定で返す fake（LLM ドラフトのポート差し替え）。文脈も保持して検証する。
    private sealed class FakeDrafter(string narrative) : IReportNarrativeDrafter
    {
        public ReportNarrativeContext? LastContext { get; private set; }

        public Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return Task.FromResult(narrative);
        }
    }

    private static PeriodTradeFill Fill(TradeSide side, int qty, decimal price, int minute) =>
        new("AAPL", Market.UnitedStates, side, side == TradeSide.Buy ? PositionEffect.Open : PositionEffect.Close,
            qty, price, new DateTimeOffset(2026, 7, 10, 0, minute, 0, TimeSpan.Zero));

    private static DraftRequest Request(ReportKind kind, string periodKey, DateOnly date, IReadOnlyList<PeriodTradeFill> fills) =>
        new(kind, periodKey, date, ["US"], 1, null, "翌期間は押し目買い", fills, null);

    [Fact]
    public async Task 日報_約定列から数値を集計し散文を含むMarkdownを生成する()
    {
        // 10株@1,000 買い → 10株@1,200 売り。既定前提（手数料/為替0）で実現損益(税引前)=2,000（勝ち決済1）。
        var fills = new[] { Fill(TradeSide.Buy, 10, 1_000m, 0), Fill(TradeSide.Sell, 10, 1_200m, 1) };
        var drafter = new FakeDrafter("市況の散文ドラフト");
        var svc = new ReportDraftService(drafter);

        var draft = await svc.BuildDraftAsync(Request(ReportKind.Daily, "daily-2026-07-10", new DateOnly(2026, 7, 10), fills));

        draft.Pnl.RealizedPnlGross.Should().Be(2_000m);
        draft.Pnl.WinningTradeCount.Should().Be(1);
        draft.Markdown.Should().Contain("# 日報 2026-07-10");
        draft.Markdown.Should().Contain("市況の散文ドラフト");
        draft.Markdown.Should().Contain("翌期間は押し目買い");
        draft.Markdown.Should().Contain("取引回数（買/売/決済） | 1 / 1 / 1");
        // 散文ドラフトの文脈にも集計済み数値・対象市場・種別が渡る（LLM に再計算させない・提示のみ）。
        drafter.LastContext!.Kind.Should().Be(ReportKind.Daily);
        drafter.LastContext!.Markets.Should().BeEquivalentTo(["US"]);
        drafter.LastContext!.Pnl.RealizedPnlGross.Should().Be(2_000m);
    }

    [Fact]
    public async Task 週報_ISO週のPeriodKeyで週報Markdownを生成する()
    {
        // 2026-07-06 は ISO 週 2026-W28。
        var svc = new ReportDraftService(new FakeDrafter("週の振り返り"));

        var draft = await svc.BuildDraftAsync(Request(ReportKind.Weekly, "weekly-2026-W28", new DateOnly(2026, 7, 6), []));

        draft.Markdown.Should().Contain("report_type: weekly");
        draft.Markdown.Should().Contain("# 週報 2026-W28");
        draft.Markdown.Should().Contain("## 1. 週間サマリ");
        draft.Markdown.Should().Contain("週の振り返り");
    }

    [Fact]
    public async Task 月報_年月のPeriodKeyで月報Markdownを生成する()
    {
        var svc = new ReportDraftService(new FakeDrafter("月の総括"));

        var draft = await svc.BuildDraftAsync(Request(ReportKind.Monthly, "monthly-2026-07", new DateOnly(2026, 7, 15), []));

        draft.Markdown.Should().Contain("report_type: monthly");
        draft.Markdown.Should().Contain("# 月報 2026-07");
        draft.Markdown.Should().Contain("月の総括");
    }

    [Fact]
    public async Task PeriodKeyが種別_対象日と不整合なら例外()
    {
        var svc = new ReportDraftService(new FakeDrafter("散文"));
        // Daily 種別に週報キーを渡す不整合。
        var mismatched = Request(ReportKind.Daily, "weekly-2026-W28", new DateOnly(2026, 7, 10), []);

        var act = () => svc.BuildDraftAsync(mismatched);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
