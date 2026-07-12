namespace AiStockTrading.Configuration.Domain;

// FR-17, 05_trading-assumptions §1/§4/§6: 全体前提条件の既定値。確定値（税率・費用上限・最小期待利益倍率）を既定にし、
// moomoo の手数料・為替スプレッド（§2/§3 の「要確認」）は未登録＝0 とする（口座開設後に利用者が登録）。
public static class TradingAssumptionsDefaults
{
    /// <summary>譲渡益税率 20.315%（利用者・2026 年時点の日本個人）。</summary>
    public const decimal CapitalGainsTaxRate = 0.20315m;

    /// <summary>最小期待利益倍率（往復費用の 1.5 倍。運用で調整）。</summary>
    public const decimal MinimumExpectedProfitMultiple = 1.5m;

    public static TradingAssumptions Create() => new()
    {
        CapitalGainsTaxRate = CapitalGainsTaxRate,
        // §2/§3: moomoo の手数料・為替スプレッドは要確認のため既定 0（未登録）。利用者が口座開設後に登録する。
        JapanCommission = new CommissionSchedule(0m, 0m, 0m),
        UnitedStatesCommission = new CommissionSchedule(0m, 0m, 0m),
        FxSpreadRatio = 0m,
        MinimumExpectedProfitMultiple = MinimumExpectedProfitMultiple,
        // §6: 月次総費用上限 20,000（LLM 15,000／インフラ 5,000／データ 0）。利用者決定 2026-07-07。
        CostLimits = new MonthlyCostLimits(Total: 20_000m, Llm: 15_000m, Infrastructure: 5_000m, Data: 0m),
    };
}
