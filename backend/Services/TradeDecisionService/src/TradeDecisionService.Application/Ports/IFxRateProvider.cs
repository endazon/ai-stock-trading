using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.TradeDecision.Application.Ports;

// FR-10, FR-17, #257, #364, IADR-0107/0152: 銘柄の市場通貨を基準通貨（USD）へ換算するレートの供給口。
// 統制・台帳の金額は基準通貨で判定するため（計画 05_trading-assumptions §3）、発注意図を作る前にレートを確定させ、
// サイジング・採算評価を基準通貨で行い、確定したレートを OrderIntent に同伴させる（換算点は判断境界の 1 点だけ）。
//
// 安全既定: 基準通貨の市場（米国株）は常に 1 を返し、外部へ問い合わせない。非基準通貨（日本株）は実レート源
// （Fx:Provider で opt-in）が無ければ null＝解決不能となり、呼び出し側が新規建てを見送る（IADR-0107 決定3）。
public interface IFxRateProvider
{
    /// <summary>市場の取引通貨 1 単位あたりの基準通貨額。解決できない場合は null（＝新規建てを見送る）。</summary>
    Task<decimal?> GetRateToBaseAsync(Market market, CancellationToken cancellationToken = default);

    /// <summary>
    /// レートと**鮮度の判定結果**を返す（#506・ADR-0022 決定5・IADR-0197）。
    /// <para>
    /// 🔴 <b>鮮度切れでも値そのものは返す。</b> 「レートが無い」と「古いレートがある」を
    /// 判断側が区別できないと、<b>入口（新規建て）と出口（手仕舞い）が同じゲートで塞がれる</b>。
    /// </para>
    /// <para>
    /// 既定実装は <see cref="GetRateToBaseAsync"/> へ委譲し、鮮度は
    /// <see cref="FxRateFreshness.Unknown"/> とする（鮮度を判定しない供給元のため）。
    /// </para>
    /// </summary>
    async Task<FxRateReading?> GetReadingAsync(Market market, CancellationToken cancellationToken = default)
    {
        var rate = await GetRateToBaseAsync(market, cancellationToken).ConfigureAwait(false);
        return rate is not { } r || r <= 0m
            ? null
            : new FxRateReading(
                new FxRate(MarketCurrency.Of(market), MarketCurrency.Base, r, DateTimeOffset.MaxValue),
                FxRateFreshness.Unknown);
    }
}
