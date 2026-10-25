namespace AiStockTrading.Shared.Contracts.Trading;

/// <summary>
/// SC-01, SC-02, FR-13, FR-19, IADR-0586 決定 3, #1472: 取引の語（市場・口座種別・商品種別）の<b>利用者向けの表示名</b>。
/// <para>
/// 400 の文言（画面・Discord）へ enum を補間すると列挙名（<c>UnitedStates</c>・<c>Cash</c>）がそのまま出る。利用者へ見せる文言は
/// 本表の呼び名で書く。判定には使わない（表示専用。<see cref="OrderStatusDisplay"/> と同じ作法）。未定義の値は列挙値をそのまま返す。
/// </para>
/// </summary>
public static class TradingTermDisplay
{
    /// <summary>市場の呼び名（日本 / 米国）。</summary>
    public static string Label(Market market) => market switch
    {
        Market.Japan => "日本",
        Market.UnitedStates => "米国",
        _ => market.ToString(),
    };

    /// <summary>口座種別の呼び名（信用口座 / 現金口座）。</summary>
    public static string Label(AccountType accountType) => accountType switch
    {
        AccountType.Margin => "信用口座",
        AccountType.Cash => "現金口座",
        _ => accountType.ToString(),
    };

    /// <summary>商品種別の呼び名（現物 / 信用買い / 空売り。画面の呼び名と同じ）。</summary>
    public static string Label(ProductType productType) => productType switch
    {
        ProductType.Cash => "現物",
        ProductType.MarginLong => "信用買い",
        ProductType.ShortSell => "空売り",
        _ => productType.ToString(),
    };
}
