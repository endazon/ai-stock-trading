namespace AiStockTrading.Shared.Contracts.Trading;

// FR-10, FR-17, #257, #364, IADR-0107/0152: 取り扱う通貨。計画 06_technical/05_trading-assumptions §3 の
// 「基準通貨（判定）= USD」（利用者決定 2026-07-31）に従い、統制・台帳の金額はすべて基準通貨で判定する。
// 表示通貨は JPY（円換算で統一・外貨併記）だが、換算表示は判定と別層の関心事である（#338）。
public enum Currency
{
    Jpy,
    Usd,
}

// FR-10, FR-17, #257, #364, IADR-0107/0152: 市場と通貨の対応（純関数）。通貨は市場から一意に導けるため、
// 注文意図やイベントに列挙値を重複して持たせない（同じ事実の第二の真実源を作らない）。
public static class MarketCurrency
{
    /// <summary>
    /// 基準通貨（計画 05_trading-assumptions §3「基準通貨〔判定〕= USD」）。統制上限・資金・損益集計はこの通貨で評価する。
    /// <para>
    /// #364, IADR-0152 決定1: 本定数が基準通貨の**単一情報源**である。JPY 基準では取引で損失が無くても円高だけで
    /// 最大 DD 上限に到達し統制が誤作動する（計画 §3 の改定理由）。moomoo の信用必要額（USD 建て）とも基準が一致する。
    /// </para>
    /// </summary>
    public const Currency Base = Currency.Usd;

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

// FR-16, FR-10, #364, IADR-0152 決定6: 金額表記の単位（純関数）。単位の表示は複数の経路（LLM プロンプト・報告書・
// 画面）で行われるため、通貨コードと補助単位の桁数を 1 箇所に置く（同じ金額が経路によって違う単位に見えるのを防ぐ）。
public static class CurrencyFormat
{
    /// <summary>通貨の表示コード（ISO 4217）。</summary>
    public static string CodeOf(Currency currency) => currency switch
    {
        Currency.Jpy => "JPY",
        Currency.Usd => "USD",
        // 通貨の追加時にここを更新し忘れると単位が黙って誤るため、既定値へ倒さず落とす。
        _ => throw new ArgumentOutOfRangeException(nameof(currency), currency, "表示コードが未定義の通貨です。"),
    };

    /// <summary>補助単位の桁数（JPY は補助単位を持たない／USD はセント）。金額表記の小数桁に用いる。</summary>
    public static int MinorUnitDigits(Currency currency) => currency switch
    {
        Currency.Jpy => 0,
        Currency.Usd => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(currency), currency, "補助単位が未定義の通貨です。"),
    };
}
