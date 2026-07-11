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

    // IADR-0036: currentPrices（(Symbol,Market)→現在値）と equityHighWaterMark（資金ピーク）を与えると含み損益・DD を
    // 時価で算出する。既定（null）では従来どおり UnrealizedPnl=0/DrawdownRatio=0（実供給の結線は #22/#82 の後続）。
    public static PortfolioState Project(
        IReadOnlyList<LedgerFill> fills,
        DateOnly today,
        decimal initialCapital,
        IReadOnlyDictionary<(string Symbol, Market Market), decimal>? currentPrices = null,
        decimal? equityHighWaterMark = null)
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
        var openPositions = new List<OpenPosition>();
        foreach (var (key, pos) in positions)
        {
            if (pos.Qty == 0)
                continue;
            invested += Math.Abs(pos.Qty) * pos.AvgCost;
            var side = pos.Qty > 0 ? TradeSide.Buy : TradeSide.Sell;
            openPositions.Add(new OpenPosition(key.Symbol, key.Market, side, Math.Abs(pos.Qty), pos.AvgCost));
        }

        // IADR-0036: 含み損益は現在値入力から時価算出（現在値欠損は 0）。当日開始運用資金（固定基準）= 初期資金 + 当日より前の実現損益。
        var capital = initialCapital + realizedBeforeToday;
        var unrealized = PortfolioValuation.UnrealizedPnl(openPositions, currentPrices);
        // 現在エクイティ = 固定基準 + 当日実現 + 含み。DD はピーク入力から算出（ピーク追跡は #22/#82 の後続）。
        var currentEquity = capital + realizedToday + unrealized;

        return new PortfolioState
        {
            Capital = capital,
            OpenPositionCount = openPositions.Count,
            InvestedCapital = invested,
            DailyOrderedAmount = orderedToday,
            DailyRealizedPnl = realizedToday,
            UnrealizedPnl = unrealized,
            DrawdownRatio = PortfolioValuation.DrawdownRatio(equityHighWaterMark, currentEquity),
            ConsecutiveLosses = consecutiveLosses,
            SymbolsTradedToday = symbolsTradedToday,
        };
    }

    // FR-03, FR-10, IADR-0030: 約定列から銘柄別ネット建玉（数量>0）を射影する純関数。損切りライン検知（市場監視）へ
    // 供給する保有ポジションの一次射影。実現損益・当日境界は不要のため、符号付き在庫・平均取得単価のみを畳み込む
    // （Project と同一の Apply を再利用）。数量 0（全決済）は除外する。
    // IADR-0035: 損切り価格は最新の同方向エントリー（新規/建て増し/反転）を採る（一部決済では保持・全決済で消滅）。
    public static IReadOnlyList<OpenPosition> ProjectOpenPositions(IReadOnlyList<LedgerFill> fills)
    {
        ArgumentNullException.ThrowIfNull(fills);

        var positions = new Dictionary<(string Symbol, Market Market), (int Qty, decimal AvgCost)>();
        var stops = new Dictionary<(string Symbol, Market Market), decimal?>();
        foreach (var fill in fills.OrderBy(f => f.ExecutedAt))
        {
            var key = (fill.Symbol, fill.Market);
            var signedQ = fill.Side == TradeSide.Buy ? fill.Quantity : -fill.Quantity;
            positions.TryGetValue(key, out var pos);
            // IADR-0033: 共有の畳み込み純関数を用いる（実現損益はここでは不要）。
            var applied = SignedInventory.Apply(new InventoryLot(pos.Qty, pos.AvgCost), signedQ, fill.Price);
            positions[key] = (applied.Lot.Quantity, applied.Lot.AverageCost);

            // IADR-0035: 建玉が消滅したら損切りも消滅。約定が建玉と同方向（新規/建て増し/反転）なら最新エントリーの損切りに更新。
            // 一部決済（反対方向で符号は不変）は既存の損切りを保持する。
            if (applied.Lot.Quantity == 0)
                stops.Remove(key);
            else if (Math.Sign(applied.Lot.Quantity) == Math.Sign(signedQ))
                stops[key] = fill.StopLossPrice;
        }

        var result = new List<OpenPosition>();
        foreach (var (key, pos) in positions)
        {
            if (pos.Qty == 0)
                continue; // 全決済済みは保有なし
            var side = pos.Qty > 0 ? TradeSide.Buy : TradeSide.Sell;
            result.Add(new OpenPosition(
                key.Symbol, key.Market, side, Math.Abs(pos.Qty), pos.AvgCost, stops.GetValueOrDefault(key)));
        }

        return result;
    }
}
