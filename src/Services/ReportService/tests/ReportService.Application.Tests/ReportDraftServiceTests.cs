using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Report.Application.Services;
using AiStockTrading.Report.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.Report.Application.Tests;

// FR-06/07/16, IADR-0032: 日報ドラフト生成のオーケストレーション（数値集計→散文ドラフト→テンプレート組み立て）を検証する。
public class ReportDraftServiceTests
{
    // 散文を固定で返す fake（LLM ドラフトのポート差し替え）。文脈の数値も受け取れることを確認するため context を保持する。
    private sealed class FakeDrafter(string narrative) : IReportNarrativeDrafter
    {
        public DailyNarrativeContext? LastContext { get; private set; }

        public Task<string> DraftDailyNarrativeAsync(DailyNarrativeContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return Task.FromResult(narrative);
        }
    }

    private static PeriodTradeFill Fill(TradeSide side, int qty, decimal price, int minute) =>
        new("AAPL", Market.UnitedStates, side, side == TradeSide.Buy ? PositionEffect.Open : PositionEffect.Close,
            qty, price, new DateTimeOffset(2026, 7, 10, 0, minute, 0, TimeSpan.Zero));

    private static DailyDraftRequest Request(IReadOnlyList<PeriodTradeFill> fills) => new(
        "daily-2026-07-10", new DateOnly(2026, 7, 10), ["US"], 1, null, "翌営業日は押し目買い", fills, null);

    [Fact]
    public async Task 約定列から数値を集計し散文を含む日報Markdownを生成する()
    {
        // 10株@1,000 買い → 10株@1,200 売り。既定前提（手数料/為替0）で実現損益(税引前)=2,000。
        var fills = new[] { Fill(TradeSide.Buy, 10, 1_000m, 0), Fill(TradeSide.Sell, 10, 1_200m, 1) };
        var drafter = new FakeDrafter("市況の散文ドラフト");
        var svc = new ReportDraftService(drafter);

        var draft = await svc.BuildDailyDraftAsync(Request(fills));

        // 数値はコード集計（PnlAggregator と一致）。
        draft.Pnl.RealizedPnlGross.Should().Be(2_000m);
        draft.Pnl.RealizingTradeCount.Should().Be(1);
        // Markdown に集計値と散文が組み込まれる。
        draft.Markdown.Should().Contain("# 日報 2026-07-10");
        draft.Markdown.Should().Contain("市況の散文ドラフト");
        draft.Markdown.Should().Contain("翌営業日は押し目買い");
        draft.Markdown.Should().Contain("取引回数（買/売/決済） | 1 / 1 / 1");
        // 散文ドラフトの文脈にも集計済み数値が渡る（LLM に再計算させない・提示のみ）。
        drafter.LastContext!.Pnl.RealizedPnlGross.Should().Be(2_000m);
    }

    [Fact]
    public async Task 約定が無ければゼロ集計の日報を生成する()
    {
        var svc = new ReportDraftService(new FakeDrafter("散文"));

        var draft = await svc.BuildDailyDraftAsync(Request([]));

        draft.Pnl.TradeCount.Should().Be(0);
        draft.Markdown.Should().Contain("取引回数（買/売/決済） | 0 / 0 / 0");
    }
}
