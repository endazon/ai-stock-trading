namespace TradeDecisionService.Features.TradeDecision;

// FR-02, FR-04, ADR-0044 決定1, #1035, IADR-0451: 判断対象の銘柄の日中文脈（前日終値・当日始値・日中高値・安値）。
// 実測（2026-09-26 稼働 PoC）: 定時の判断へ渡る市況は現在値 1 行だけで、LLM は「値動きの情報が無い」として全件 Hold に倒れた。
// 現在値と同じ応答（Finnhub /quote）の別項目であり、計画 ADR-0044 決定 1 の「収集情報（市況）」の内側である。
// 🔴 **null＝不明**。0 以下は値として持たない（Of が null へ倒す。前日比が -100% になる）。
// 変化率の計算はコードで行う（ADR-0003・FR-16 の趣旨。LLM に計算させない）。
public sealed record IntradayPriceContext(decimal? PreviousClose, decimal? Open, decimal? High, decimal? Low)
{
    /// <summary>すべて不明。</summary>
    public static IntradayPriceContext Unknown { get; } = new(null, null, null, null);

    /// <summary>0 以下（情報源の「無い」の表現）を null（不明）へ倒して組む。</summary>
    public static IntradayPriceContext Of(decimal? previousClose, decimal? open, decimal? high, decimal? low) =>
        new(Known(previousClose), Known(open), Known(high), Known(low));

    /// <summary>
    /// <paramref name="price"/> の <paramref name="basis"/> からの変化率（0.0123＝+1.23%）。基準が不明・0 以下なら null（不明）。
    /// </summary>
    public static decimal? ChangeRatio(decimal price, decimal? basis) =>
        basis is { } b && b > 0m ? (price - b) / b : null;

    private static decimal? Known(decimal? value) => value is { } v && v > 0m ? v : null;
}

// FR-02, FR-10, IADR-0099, #1035, IADR-0451: 価格供給の結果（権威ある現在値と、同じ取得の日中文脈）。
// 取得不可・鮮度切れ・無効化は従来どおり供給側が null（レコードごと無い）を返す。
public sealed record CurrentPriceReading(decimal Price, IntradayPriceContext Intraday);
