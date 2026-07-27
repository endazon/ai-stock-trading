using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Ports;

// FR-10, FR-17, #257, ADR-0004, IADR-0107: 為替レート源のポート。統制・台帳の金額は基準通貨（円）で判定するため、
// 外貨建て銘柄の価格を基準通貨へ換算するレートをここから得る（計画 05_trading-assumptions §3）。
//
// 安全既定: 実接続は Fx:Provider で opt-in。既定は no-op（外貨は常に null＝解決しない）。
// fail-safe: 取得不可・鮮度切れ・例外はいずれも null（＝レート無し）に縮退させ、呼び出し側は
// 非基準通貨の新規建てを見送る（古い/無いレートで発注しない・IADR-0107 決定3）。
public interface IFxRateSource
{
    /// <summary>
    /// 対象通貨 1 単位あたりの基準通貨額を返す。解決できない場合は null。
    /// <para>
    /// 契約: <paramref name="quote"/> が基準通貨（<see cref="MarketCurrency.Base"/>）のときは、外部へ問い合わせず
    /// 必ずレート 1 を返す。これにより FX を有効化していない環境でも基準通貨の市場は従来どおり取引できる。
    /// </para>
    /// </summary>
    Task<FxRate?> GetRateToBaseAsync(Currency quote, CancellationToken cancellationToken = default);
}
