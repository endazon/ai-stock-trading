using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Domain;

// 🔴 FR-19, FR-10, ADR-0062, IADR-0132 決定 6, #1286, IADR-0521 決定 4: 新規建て・決済を問わず**全注文**を状態（設定）だけで拒否する述語。
//
// 審査（RiskEvaluator.Evaluate）と、新規建ての可否の口（EntryBlockersService の AnyOrder）が**同じ関数を呼ぶ**（規則を 2 か所に置かない）。
//   - 市場の無効（MarketDisabled）: 取引ガードの有効市場に無い市場（ADR-0062: 日本株は API が対応するまで無効）。
//   - 禁止銘柄（BannedSymbol）: 照合は BannedSymbol.Matches が単一情報源（IADR-0132 決定 6）。手仕舞いにも適用する（FR-19）。
// 取引判断は、監視銘柄の外の保有銘柄（出口専用）のうちこれらに当たるものを定時サイクルの判断対象から外す（決済も審査で必ず拒否されるため）。
public static class OrderStateBlockers
{
    /// <summary>(銘柄, 市場) の全注文を拒否する理由（審査の到達順）。当たらなければ空。</summary>
    public static IReadOnlyList<RejectionReason> Determine(TradingGuardSettings guard, string symbol, Market market)
    {
        ArgumentNullException.ThrowIfNull(guard);

        var reasons = new List<RejectionReason>(2);
        if (!guard.EnabledMarkets.Contains(market))
            reasons.Add(RejectionReason.MarketDisabled);

        // 禁止銘柄は銘柄コードと市場の両方で照合する（同一コードが別市場に存在し得るため）。
        if (guard.BannedSymbols.Any(b => b.Matches(symbol, market)))
            reasons.Add(RejectionReason.BannedSymbol);

        return reasons;
    }
}
