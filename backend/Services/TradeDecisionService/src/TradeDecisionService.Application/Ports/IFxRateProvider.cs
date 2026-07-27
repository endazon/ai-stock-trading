using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.TradeDecision.Application.Ports;

// FR-10, FR-17, #257, IADR-0107: 銘柄の市場通貨を基準通貨（円）へ換算するレートの供給口。
// 統制・台帳の金額は基準通貨で判定するため（計画 05_trading-assumptions §3）、発注意図を作る前にレートを確定させ、
// サイジング・採算評価を基準通貨で行い、確定したレートを OrderIntent に同伴させる（換算点は判断境界の 1 点だけ）。
//
// 安全既定: 基準通貨の市場（日本株）は常に 1 を返し、外部へ問い合わせない。非基準通貨は実レート源
// （Fx:Provider で opt-in）が無ければ null＝解決不能となり、呼び出し側が新規建てを見送る（IADR-0107 決定3）。
public interface IFxRateProvider
{
    /// <summary>市場の取引通貨 1 単位あたりの基準通貨額。解決できない場合は null（＝新規建てを見送る）。</summary>
    Task<decimal?> GetRateToBaseAsync(Market market, CancellationToken cancellationToken = default);
}
