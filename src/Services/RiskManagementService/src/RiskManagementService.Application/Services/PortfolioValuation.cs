using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Application.Services;

// FR-10, IADR-0008/0036: 含み損益・ドローダウンの時価評価（純関数）。現在値・エクイティピークは入力として受ける
// （実供給＝市場データ源・ピーク追跡は #22/#82 の後続）。日次損失上限（実現＋含み）・最大DD 判定の入力を供給する。
public static class PortfolioValuation
{
    // 含み損益＝Σ 建玉 (現在値 − 平均取得単価) × 符号付き数量。現在値の無い建玉は 0（フォールバック）。
    public static decimal UnrealizedPnl(
        IReadOnlyList<OpenPosition> positions,
        IReadOnlyDictionary<(string Symbol, Market Market), decimal>? currentPrices)
    {
        ArgumentNullException.ThrowIfNull(positions);
        if (currentPrices is null)
            return 0m;

        var total = 0m;
        foreach (var p in positions)
        {
            if (!currentPrices.TryGetValue((p.Symbol, p.Market), out var price))
                continue; // 現在値欠損はスキップ（含み 0）
            var signedQty = p.Side == TradeSide.Buy ? p.Quantity : -p.Quantity;
            total += (price - p.AverageEntryPrice) * signedQty;
        }

        return total;
    }

    // ドローダウン率＝(ピーク − 現在エクイティ) / ピーク（下限 0）。ピーク未指定/非正、または下落なしは 0。
    public static decimal DrawdownRatio(decimal? equityHighWaterMark, decimal currentEquity)
    {
        if (equityHighWaterMark is not { } peak || peak <= 0m)
            return 0m;

        var drawdown = (peak - currentEquity) / peak;
        return drawdown > 0m ? drawdown : 0m;
    }
}
