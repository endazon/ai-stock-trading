namespace AiStockTrading.Shared.Kernel.Trading;

// FR-17, 05_trading-assumptions §2: 市場別の売買手数料体系。手数料 = clamp(約定代金 × Rate, Minimum, Cap)。
// Cap <= 0 は上限なしを表す。moomoo の実額は口座開設後に登録するため既定は 0（未登録）。
public sealed record CommissionSchedule(decimal Rate, decimal Minimum, decimal Cap)
{
    public decimal For(decimal notional)
    {
        var fee = notional * Rate;
        if (fee < Minimum)
            fee = Minimum;
        if (Cap > 0m && fee > Cap)
            fee = Cap;
        return fee;
    }
}

// FR-17, 05_trading-assumptions §2「米国株 売却時諸費用」, 計画 ADR-0035 決定 5, #1201, IADR-0501:
// 米国株の売却時に掛かる規制料（SEC Section 31 手数料・FINRA 取引活動料〔TAF〕）。**売り約定（空売りを含む）だけ**に掛かる。
// 値は規制当局の公表料率の暫定値であり、口座開設後に証券会社の実請求額で置き換える（計画の確認日つき登録）。
public sealed record UsSellRegulatoryFeeSchedule(decimal SecFeePerMillion, decimal TafPerShare, decimal TafCapPerTrade)
{
    /// <summary>SEC Section 31 手数料＝売却代金 × 料率 ÷ 1,000,000。</summary>
    public decimal SecFee(decimal notional) => notional * SecFeePerMillion / 1_000_000m;

    /// <summary>FINRA 取引活動料＝数量 × 1 株あたり料率（1 取引あたり上限で頭打ち。上限 0 以下は上限なし）。</summary>
    public decimal Taf(int quantity)
    {
        var fee = quantity * TafPerShare;
        return TafCapPerTrade > 0m && fee > TafCapPerTrade ? TafCapPerTrade : fee;
    }

    /// <summary>売り約定 1 件の規制料の合計。</summary>
    public decimal For(decimal notional, int quantity) => SecFee(notional) + Taf(quantity);
}

// FR-17, 05_trading-assumptions §6: 月次費用上限（円）。総額と内訳（LLM・インフラ・データ）。
public sealed record MonthlyCostLimits(decimal Total, decimal Llm, decimal Infrastructure, decimal Data);

// FR-17, 05_trading-assumptions: システム全体に一律適用する前提条件（税・手数料・為替・計算方針・費用上限）。
// 損益集計（報告書）・AI 判断の採算評価（取引判断）・費用込み上限判定（リスク管理）が共通参照する単一の真実源。
public sealed record TradingAssumptions
{
    /// <summary>譲渡益税率（0.20315 = 20.315%。所得税15.315%＋住民税5%）。</summary>
    public required decimal CapitalGainsTaxRate { get; init; }

    /// <summary>日本株の売買手数料体系。</summary>
    public required CommissionSchedule JapanCommission { get; init; }

    /// <summary>米国株の売買手数料体系。</summary>
    public required CommissionSchedule UnitedStatesCommission { get; init; }

    /// <summary>非 JPY 市場に適用する為替スプレッド率（片道・約定代金比）。実 FX レート連携までの近似（IADR-0021）。</summary>
    public required decimal FxSpreadRatio { get; init; }

    /// <summary>最小期待利益倍率（往復費用の何倍を下回る期待利益の取引を見送るか。既定 1.5）。</summary>
    public required decimal MinimumExpectedProfitMultiple { get; init; }

    /// <summary>月次費用上限。</summary>
    public required MonthlyCostLimits CostLimits { get; init; }

    /// <summary>
    /// FR-17, 05_trading-assumptions §2, 計画 ADR-0035 決定 5, #1201, IADR-0501: 米国株の売却時諸費用（取引諸費用）の料率。
    /// <para>
    /// 🔴 <b><c>required</c> にしない。</b> 既存の永続化行（JSON）と、gRPC／HTTP で前提条件を受け取る側（取引判断・費用統制）は
    /// この欄を運ばない。欠けたときは<b>計画の暫定値</b>（<see cref="TradingAssumptionsDefaults.UnitedStatesSellRegulatoryFees"/>）で埋まる
    /// ——受け取る側は事前見積り（<see cref="CostCalculator.EstimateOneWayCost"/>）しか使わず、本欄を読まない。
    /// </para>
    /// </summary>
    public UsSellRegulatoryFeeSchedule UnitedStatesSellRegulatoryFees { get; init; } =
        TradingAssumptionsDefaults.UnitedStatesSellRegulatoryFees;
}
