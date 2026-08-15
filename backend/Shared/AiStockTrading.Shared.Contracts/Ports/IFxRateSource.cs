using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Ports;

// FR-10, FR-17, #257, #364, ADR-0004, IADR-0107/0152: 為替レート源のポート。統制・台帳の金額は基準通貨（USD）で
// 判定するため、非基準通貨建て銘柄の価格を基準通貨へ換算するレートをここから得る（計画 05_trading-assumptions §3）。
//
// 安全既定: 実接続は Fx:Provider で opt-in。既定は no-op（非基準通貨は常に null＝解決しない）。
// fail-safe: 取得不可・鮮度切れ・例外はいずれも null（＝レート無し）に縮退させ、呼び出し側は
// 非基準通貨の新規建てを見送る（古い/無いレートで発注しない・IADR-0107 決定3）。
public interface IFxRateSource
{
    /// <summary>
    /// 対象通貨 1 単位あたりの基準通貨額を返す。解決できない場合は null。
    /// <para>
    /// 契約: <paramref name="quote"/> が基準通貨（<see cref="MarketCurrency.Base"/>＝USD）のときは、外部へ問い合わせず
    /// 必ずレート 1 を返す。これにより FX を有効化していない環境でも基準通貨の市場（米国株）は従来どおり取引できる。
    /// </para>
    /// </summary>
    Task<FxRate?> GetRateToBaseAsync(Currency quote, CancellationToken cancellationToken = default);

    /// <summary>
    /// レートと**鮮度の判定結果**を返す（#506・ADR-0022 決定5・IADR-0197）。
    /// <para>
    /// 🔴 <b>鮮度切れでも値そのものは返す。</b> <see cref="GetRateToBaseAsync"/> は鮮度切れを
    /// <c>null</c> へ潰すため、呼び出し側は「レートが無い」と「古いレートがある」を区別できない。
    /// その結果、<b>入口（新規建て）と出口（手仕舞い）が同じゲートで塞がれていた</b>——
    /// 計画は「30 日超は<b>新規建てを停止する。手仕舞い・損切りは止めない</b>」と両者を分けている。
    /// </para>
    /// <para>
    /// <b>既定実装は鮮度を判定しない</b>（<see cref="FxRateFreshness.Unknown"/> を返す）。
    /// 判定するのは鮮度装飾（<c>CachingFxRateSource</c>）だけであり、
    /// 生のレート源（日銀・FRED）は<b>判定していないことを表明する</b>。
    /// </para>
    /// </summary>
    async Task<FxRateReading?> GetReadingAsync(Currency quote, CancellationToken cancellationToken = default)
    {
        var rate = await GetRateToBaseAsync(quote, cancellationToken).ConfigureAwait(false);
        return rate is null ? null : new FxRateReading(rate, FxRateFreshness.Unknown);
    }
}
