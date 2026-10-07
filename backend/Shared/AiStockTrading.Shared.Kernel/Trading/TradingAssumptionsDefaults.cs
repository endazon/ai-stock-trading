namespace AiStockTrading.Shared.Kernel.Trading;

// FR-17, 05_trading-assumptions §1/§2/§4/§6: 全体前提条件の既定値。確定値（税率・費用上限・最小期待利益倍率）と
// §2 米国株売却時諸費用の暫定値（計画 ADR-0035 決定 5）を既定にし、
// moomoo の手数料・為替スプレッド（§2/§3 の「要確認」）は未登録＝0 とする（口座開設後に利用者が登録）。
public static class TradingAssumptionsDefaults
{
    /// <summary>譲渡益税率 20.315%（利用者・2026 年時点の日本個人）。</summary>
    public const decimal CapitalGainsTaxRate = 0.20315m;

    /// <summary>
    /// 最小期待利益倍率（**往復費用＋税**の 2 倍。§4・利用者決定 2026-07-23）。
    /// <para>
    /// **基準は「往復費用＋税」であり往復費用のみではない**（#358・IADR-0173）。2026-07-18 の実装は
    /// 計画確定前の暫定値（&lt;1.5 倍&gt;・往復費用のみ）のまま追随していなかった。税は譲渡益（= 利益 − 費用）に
    /// 掛かるため、しきい値は不動点として解く —— 算出は <c>CostCalculator.MinimumViableProfit</c> を正とする。
    /// </para>
    /// </summary>
    public const decimal MinimumExpectedProfitMultiple = 2m;

    /// <summary>
    /// FR-17, 05_trading-assumptions §2「米国株 売却時諸費用」, 計画 ADR-0035 決定 5, #1201, IADR-0501:
    /// SEC Section 31 手数料 <b>$20.60 / 百万ドル</b>の売却代金（FY2026 Fee Rate Advisory。2026-04-04 以降の約定日に適用）。
    /// <b>確認日 2026-09-05・暫定値</b>（FY2027 歳出法の成立で改定され得る。改定時は計画と同時に更新する）。
    /// </summary>
    public const decimal SecFeePerMillion = 20.60m;

    /// <summary>FINRA 取引活動料（TAF）<b>$0.000166 / 株</b>（By-Laws Schedule A Section 1。確認日 2026-09-05・暫定値）。</summary>
    public const decimal TafPerShare = 0.000166m;

    /// <summary>FINRA 取引活動料（TAF）の <b>1 取引あたり上限 $8.30</b>（同上）。</summary>
    public const decimal TafCapPerTrade = 8.30m;

    /// <summary>
    /// 米国株の売却時諸費用の既定料率（計画の暫定値）。口座開設後に moomoo の実請求額で置き換える（計画 ADR-0035 フォローアップ 3）。
    /// </summary>
    public static readonly UsSellRegulatoryFeeSchedule UnitedStatesSellRegulatoryFees =
        new(SecFeePerMillion, TafPerShare, TafCapPerTrade);

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
        // §2: 米国株の売却時諸費用（SEC・TAF）は計画の暫定値（ADR-0035 決定 5）。
        UnitedStatesSellRegulatoryFees = UnitedStatesSellRegulatoryFees,
    };
}
