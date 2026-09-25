using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-10 (1)(6), UC-06, ADR-0016 決定2(a)・決定9, #967, IADR-0346 決定2, IADR-0425 決定6:
// 空売りの上限（1 銘柄 equity の 10%・空売り比率 50%）の入力となるエクスポージャ（基準通貨）を出す純関数。
//
// **入力は統制の射影（PortfolioProjection）と同じ**——約定から畳んだ保有建玉と、当日承認・未終端の新規建ての残数量
// （PortfolioProjection.ProjectWorkingEntries。承認数量から同じ DecisionId の約定累計を引いた残り＝**二重計上しない**）。
//   - 保有建玉は**時価**（現在値 × 数量 × 建玉の加重平均約定時レート）。SC-03 の空売り比率の表示（ShortSellingStatusService）と
//     同じ定義である（計画 ADR-0016 決定2(a)・決定9 の本文は評価の基準を明示していない＝実装の選択。IADR-0425 決定6）。
//     取得原価で代用すると、損失の出ている空売りほどエクスポージャを小さく見積もる（上限が緩む側）。
//   - 未約定の新規建ては**残数量 × 承認価格（基準通貨）**（IADR-0346 決定2 と同じ規則）。売り建て（Sell）は空売り、買い建ては
//     ロングとして建玉総額へ入る。**未約定の空売りを数えないと、指値が溜まっている間に上限を超えて承認し続ける**（#829 と同型の穴）。
//
// 🔴 **保有建玉のうち 1 件でも現在値が無ければ null（＝分からない）を返す。** その建玉を 0 と数えると、空売りなら上限が緩み、
// ロングなら空売り比率の分母が縮む——いずれにせよ「観測した結果この額だった」と読める値を発明することになる（IADR-0159 決定5）。
// 建玉も未約定も無いときは 0（＝**無い**ことを台帳で確かめた値）であり、分からないとは別の状態である。
public static class ShortExposureProjection
{
    public static ShortExposure? Project(
        string symbol,
        Market market,
        IReadOnlyList<OpenPosition> heldPositions,
        IReadOnlyDictionary<(string Symbol, Market Market), decimal> currentPrices,
        IReadOnlyList<(WorkingEntryOrder Order, int Remaining)> workingEntries)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(heldPositions);
        ArgumentNullException.ThrowIfNull(currentPrices);
        ArgumentNullException.ThrowIfNull(workingEntries);

        decimal symbolShort = 0m, totalShort = 0m, total = 0m;

        foreach (var position in heldPositions)
        {
            if (!currentPrices.TryGetValue((position.Symbol, position.Market), out var price))
                return null;

            var value = position.Quantity * price * position.FxRateToBase;
            total += value;
            if (position.Side == TradeSide.Sell)
            {
                totalShort += value;
                if (IsSame(position.Symbol, position.Market, symbol, market))
                    symbolShort += value;
            }
        }

        foreach (var (order, remaining) in workingEntries)
        {
            var value = remaining * order.PriceInBase;
            total += value;
            if (order.Side == TradeSide.Sell)
            {
                totalShort += value;
                if (IsSame(order.Symbol, order.Market, symbol, market))
                    symbolShort += value;
            }
        }

        return new ShortExposure(symbolShort, totalShort, total);
    }

    private static bool IsSame(string symbolA, Market marketA, string symbolB, Market marketB) =>
        marketA == marketB && string.Equals(symbolA, symbolB, StringComparison.Ordinal);
}

/// <summary>
/// #967, IADR-0425 決定6: 空売りの上限の入力（基準通貨）。<see cref="Domain.ShortSellOrderContext"/> の同名の 3 項目へそのまま入る。
/// </summary>
public sealed record ShortExposure(decimal SymbolShortExposure, decimal TotalShortExposure, decimal TotalExposure);
