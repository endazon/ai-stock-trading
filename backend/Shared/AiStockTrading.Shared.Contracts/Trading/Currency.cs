namespace AiStockTrading.Shared.Contracts.Trading;

// FR-10, FR-17, #257, IADR-0106: 取り扱う通貨。計画 06_technical/05_trading-assumptions §3 の
// 「基準通貨 = JPY（円換算で統一、外貨併記）」に従い、統制・台帳の金額はすべて基準通貨で判定する。
public enum Currency
{
    Jpy,
    Usd,
}

// FR-10, FR-17, #257, IADR-0106: 市場と通貨の対応（純関数）。通貨は市場から一意に導けるため、
// 注文意図やイベントに列挙値を重複して持たせない（同じ事実の第二の真実源を作らない）。
public static class MarketCurrency
{
    /// <summary>基準通貨（計画 05_trading-assumptions §3）。統制上限・資金・損益集計はこの通貨で評価する。</summary>
    public const Currency Base = Currency.Jpy;

    /// <summary>市場の取引通貨。</summary>
    public static Currency Of(Market market) => market switch
    {
        Market.Japan => Currency.Jpy,
        Market.UnitedStates => Currency.Usd,
        // 市場の追加時にここを更新し忘れると通貨が黙って誤るため、既定値へ倒さず落とす。
        _ => throw new ArgumentOutOfRangeException(nameof(market), market, "通貨が未定義の市場です。"),
    };

    /// <summary>市場の取引通貨が基準通貨か（＝為替換算が不要か）。</summary>
    public static bool IsBaseCurrency(Market market) => Of(market) == Base;
}
