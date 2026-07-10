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

            var realized = Apply(ref pos, signedQ, fill.Price);
            positions[key] = pos;

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

    // 符号付き在庫へ 1 約定を適用し、在庫が減少した分の実現損益を返す（増加のみなら 0）。
    private static decimal Apply(ref (int Qty, decimal AvgCost) pos, int signedQ, decimal price)
    {
        if (pos.Qty == 0)
        {
            pos = (signedQ, price);
            return 0m;
        }

        // 同方向 = 建て増し。加重平均で取得単価を更新（実現なし）。
        if (Math.Sign(pos.Qty) == Math.Sign(signedQ))
        {
            var newQty = pos.Qty + signedQ;
            var total = pos.AvgCost * Math.Abs(pos.Qty) + price * Math.Abs(signedQ);
            pos = (newQty, total / Math.Abs(newQty));
            return 0m;
        }

        // 反対方向 = 建玉の減少（および反転の可能性）。減少分に実現損益を計上する。
        var reduce = Math.Min(Math.Abs(pos.Qty), Math.Abs(signedQ));
        // ロング（Qty>0）を売り決済: (決済単価 − 取得単価) × 数量。ショートは (取得単価 − 決済単価) × 数量。
        var realized = pos.Qty > 0
            ? (price - pos.AvgCost) * reduce
            : (pos.AvgCost - price) * reduce;

        var resultQty = pos.Qty + signedQ;
        if (resultQty == 0)
            pos = (0, 0m);
        else if (Math.Sign(resultQty) != Math.Sign(pos.Qty))
            // 反転: 元の建玉を全決済し、余りを新規建玉として約定単価で建てる。
            pos = (resultQty, price);
        else
            // 同方向のまま一部決済: 取得単価は不変。
            pos = (resultQty, pos.AvgCost);

        return realized;
    }
}
