using AiStockTrading.Shared.Kernel.Trading;
using ReportService.Domain;

namespace ReportService.Features.Reports.SummarizePnl;

internal static class SummarizePnlEndpoint
{
    // FR-16, IADR-0025: 損益集計（数値はコードで集計）。約定列＋任意の現在値から実現損益・費用・税・評価損益を返す。
    // 前提条件は暫定で既定値（#19 のバージョン付き取得・#63 台帳連携は #22 後続）。
    public static void MapSummarizePnl(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/pnl-summary", (PnlSummaryRequest req) =>
        {
            var summary = PnlAggregator.Aggregate(
                req.Fills ?? [], TradingAssumptionsDefaults.Create(), req.CurrentPrices);
            return Results.Ok(summary);
        });
}

// 損益集計の要求（約定列＋任意の現在値。銘柄→現在値）。
internal sealed record PnlSummaryRequest(List<PeriodTradeFill> Fills, Dictionary<string, decimal>? CurrentPrices);
