using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain;

// FR-10, FR-19, FR-20, ADR-0003, ADR-0007: 発注前の決定的判定コア。
// 生成AIの判断がどうであれ、ここで違反と判定された注文は発注執行へ到達しない。
// 違反は最初の1件で打ち切らず全件列挙する（FR-11 監査のため）。
public static class RiskEvaluator
{
    public static OrderScreeningResult Evaluate(
        OrderIntent intent,
        RiskManagementSettings settings,
        PortfolioSnapshot snapshot)
    {
        var reasons = new List<RejectionReason>();
        var isEntry = intent.Side == TradeSide.Buy;

        // 全停止スイッチ（kill switch）: 新規発注（エントリー）のみ停止する。
        // NFR フェイルセーフ（02_requirements: 新規発注停止。保有ポジションの損切り監視は最後まで維持）
        // および ADR-0003（損切りは機械的に執行）により、手仕舞い（売り）は止めない。
        if (isEntry && snapshot.KillSwitchEngaged)
        {
            reasons.Add(RejectionReason.KillSwitchActive);
        }

        // FR-20: 段階ゲート（動作モードと資金上限）
        if (intent.Mode == TradeMode.Live && settings.Stage.Mode != TradeMode.Live)
        {
            reasons.Add(RejectionReason.StageProhibitsLiveTrading);
        }

        if (isEntry && intent.Notional > settings.Stage.CapitalCap)
        {
            reasons.Add(RejectionReason.StageCapitalCapExceeded);
        }

        // FR-19: 取引ガード
        if (!settings.Guard.EnabledProductTypes.Contains(intent.ProductType))
        {
            reasons.Add(RejectionReason.ProductTypeDisabled);
        }

        if (!settings.Guard.EnabledMarkets.Contains(intent.Market))
        {
            reasons.Add(RejectionReason.MarketDisabled);
        }

        if (settings.Guard.BannedSymbols.Any(b => b.Symbol == intent.Symbol))
        {
            reasons.Add(RejectionReason.BannedSymbol);
        }

        if (isEntry
            && settings.Guard.PreventSameDayReentry
            && snapshot.SymbolsTradedToday.Contains(intent.Symbol))
        {
            reasons.Add(RejectionReason.SameDayReentry);
        }

        // FR-10: リスク上限
        if (intent.Notional > settings.Limits.MaxOrderAmount)
        {
            reasons.Add(RejectionReason.PerOrderAmountExceeded);
        }

        if (snapshot.DailyOrderedAmount + intent.Notional > settings.Limits.MaxDailyOrderAmount)
        {
            reasons.Add(RejectionReason.DailyOrderAmountExceeded);
        }

        if (isEntry && snapshot.OpenPositionCount >= settings.Limits.MaxOpenPositions)
        {
            reasons.Add(RejectionReason.MaxPositionsExceeded);
        }

        // 日次損失上限・最大DD 到達時も「新規発注停止・損切り監視は維持」（フェイルセーフ）。
        // 損失拡大局面での手仕舞い（売り）を止めないよう、エントリーにのみ適用する。
        if (isEntry && snapshot.DailyRealizedPnl <= -(snapshot.Capital * settings.Limits.DailyLossLimitRatio))
        {
            reasons.Add(RejectionReason.DailyLossLimitReached);
        }

        if (isEntry && snapshot.DrawdownRatio >= settings.Limits.MaxDrawdownRatio)
        {
            reasons.Add(RejectionReason.MaxDrawdownReached);
        }

        return reasons.Count > 0
            ? OrderScreeningResult.Reject(reasons)
            : OrderScreeningResult.Approve(intent.Quantity);
    }
}
