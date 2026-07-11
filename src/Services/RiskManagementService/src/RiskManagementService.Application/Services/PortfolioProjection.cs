using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Application.Services;

// FR-10, FR-05, IADR-0018: 取引台帳（LedgerFill 列）から判定入力 PortfolioState を組み立てる純関数。
// 符号付き在庫・平均取得単価法で Open/Close を統一処理し、実現損益は在庫が減少する約定で計上する。
// DB・Clock 非依存（today と initialCapital は呼び出し側＝プロバイダが与える）。
public static class PortfolioProjection
{
    // 取引日境界の解釈に用いる固定オフセット（JST=+9・DST なし）。市場別の取引日境界は後続（IADR-0018）。
    public static readonly TimeSpan TradingDayOffset = TimeSpan.FromHours(9);

    public static DateOnly TradeDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(instant.ToOffset(TradingDayOffset).DateTime);

    public static PortfolioState Project(
        IReadOnlyList<LedgerFill> fills,
        DateOnly today,
        decimal initialCapital)
    {
        ArgumentNullException.ThrowIfNull(fills);

        // 銘柄（銘柄コード, 市場）ごとの符号付き在庫（+ ロング / − ショート）と平均取得単価。
        var positions = new Dictionary<(string Symbol, Market Market), (int Qty, decimal AvgCost)>();

        decimal realizedBeforeToday = 0m;
        decimal realizedToday = 0m;
        decimal orderedToday = 0m;
        var symbolsTradedToday = new HashSet<(string Symbol, Market Market)>();
        var consecutiveLosses = 0;

        // 約定は時系列順に畳み込む（実現損益・連敗・当日境界の判定に順序が必要）。
        foreach (var fill in fills.OrderBy(f => f.ExecutedAt))
        {
            var key = (fill.Symbol, fill.Market);
            var signedQ = fill.Side == TradeSide.Buy ? fill.Quantity : -fill.Quantity;
            positions.TryGetValue(key, out var pos);

            // IADR-0033: 平均取得単価法の畳み込みは共有の純関数（SignedInventory）を単一情報源とする。
            var applied = SignedInventory.Apply(new InventoryLot(pos.Qty, pos.AvgCost), signedQ, fill.Price);
            positions[key] = (applied.Lot.Quantity, applied.Lot.AverageCost);
            var realized = applied.RealizedPnl;

            var date = TradeDate(fill.ExecutedAt);
            var isToday = date == today;
            if (isToday)
            {
                orderedToday += fill.Quantity * fill.Price;
                symbolsTradedToday.Add(key);
                realizedToday += realized;
            }
            else if (date < today)
            {
                realizedBeforeToday += realized;
            }

            // 連敗: 在庫を減少させた（実現が発生した）約定のみを 1 取引として数える。
            // 損失で +1、利益で 0 にリセット、損益ゼロ（建て増しや損益なし）は据え置き。
            if (realized < 0m)
                consecutiveLosses++;
            else if (realized > 0m)
                consecutiveLosses = 0;
        }

        var invested = 0m;
        var openCount = 0;
        foreach (var (qty, avgCost) in positions.Values)
        {
            if (qty == 0)
                continue;
            openCount++;
            invested += Math.Abs(qty) * avgCost;
        }

        return new PortfolioState
        {
            // 当日開始運用資金（固定基準）= 初期資金 + 当日より前の実現損益。当日実現・含みは含めない（当日中不変）。
            Capital = initialCapital + realizedBeforeToday,
            OpenPositionCount = openCount,
            InvestedCapital = invested,
            DailyOrderedAmount = orderedToday,
            DailyRealizedPnl = realizedToday,
            // 含み損益・DD は日次終値マーク（市場データ連携）が必要のため本スライスでは 0（IADR-0008/0018）。
            UnrealizedPnl = 0m,
            DrawdownRatio = 0m,
            ConsecutiveLosses = consecutiveLosses,
            SymbolsTradedToday = symbolsTradedToday,
        };
    }

    // FR-03, FR-10, IADR-0030: 約定列から銘柄別ネット建玉（数量>0）を射影する純関数。損切りライン検知（市場監視）へ
    // 供給する保有ポジションの一次射影。実現損益・当日境界は不要のため、符号付き在庫・平均取得単価のみを畳み込む
    // （Project と同一の Apply を再利用）。数量 0（全決済）は除外する。損切り価格は含まない（OpenPositionsService が導出）。
    public static IReadOnlyList<OpenPosition> ProjectOpenPositions(IReadOnlyList<LedgerFill> fills)
    {
        ArgumentNullException.ThrowIfNull(fills);

        var positions = new Dictionary<(string Symbol, Market Market), (int Qty, decimal AvgCost)>();
        foreach (var fill in fills.OrderBy(f => f.ExecutedAt))
        {
            var key = (fill.Symbol, fill.Market);
            var signedQ = fill.Side == TradeSide.Buy ? fill.Quantity : -fill.Quantity;
            positions.TryGetValue(key, out var pos);
            // IADR-0033: 共有の畳み込み純関数を用いる（実現損益はここでは不要）。
            var applied = SignedInventory.Apply(new InventoryLot(pos.Qty, pos.AvgCost), signedQ, fill.Price);
            positions[key] = (applied.Lot.Quantity, applied.Lot.AverageCost);
        }

        var result = new List<OpenPosition>();
        foreach (var (key, pos) in positions)
        {
            if (pos.Qty == 0)
                continue; // 全決済済みは保有なし
            var side = pos.Qty > 0 ? TradeSide.Buy : TradeSide.Sell;
            result.Add(new OpenPosition(key.Symbol, key.Market, side, Math.Abs(pos.Qty), pos.AvgCost));
        }

        return result;
    }
}
