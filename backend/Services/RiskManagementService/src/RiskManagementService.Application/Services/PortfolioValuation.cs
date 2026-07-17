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

    // IADR-0065: エクイティピーク（DrawdownRatio の入力）を取引台帳から再計算する純関数。永続化しない
    // （台帳から常に同値を再現でき、再起動・復旧時に台帳と乖離した第二の真実を作らないため）。
    // 「初期資金 + 累積実現損益」の走査最大と現在エクイティの最大を採る。実現ベースのため含みだけで生じた山は
    // 捉えず、ピークを過小＝DD を過小に見積もる（暫定。実観測ピークの永続化要否は live 検証後に判断・IADR-0065）。
    public static decimal EquityHighWaterMark(
        IReadOnlyList<LedgerFill> fills,
        decimal initialCapital,
        decimal currentEquity)
    {
        ArgumentNullException.ThrowIfNull(fills);

        // 開始時点（初期資金）も山の候補に含める（約定が無い、または全期間が損失でも下限は初期資金）。
        var peak = initialCapital;
        var equity = initialCapital;
        var positions = new Dictionary<(string Symbol, Market Market), (int Qty, decimal AvgCost)>();

        // 実現損益は在庫を減少させる約定で確定するため、射影（Project）と同じく時系列順に畳み込む。
        foreach (var fill in fills.OrderBy(f => f.ExecutedAt))
        {
            var key = (fill.Symbol, fill.Market);
            var signedQ = fill.Side == TradeSide.Buy ? fill.Quantity : -fill.Quantity;
            positions.TryGetValue(key, out var pos);

            // IADR-0033: 平均取得単価法の畳み込みは共有の純関数（SignedInventory）を単一情報源とする。
            var applied = SignedInventory.Apply(new InventoryLot(pos.Qty, pos.AvgCost), signedQ, fill.Price);
            positions[key] = (applied.Lot.Quantity, applied.Lot.AverageCost);

            equity += applied.RealizedPnl;
            if (equity > peak)
                peak = equity;
        }

        return currentEquity > peak ? currentEquity : peak;
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
