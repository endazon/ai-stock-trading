using System.Globalization;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, FR-11, UC-06, ADR-0003, #849, IADR-0350 決定 2: 利用者が承認した**乖離の取り込み** 1 件（取引台帳の追記専用の行）。
//
// 約定（TradeFillRow）とは別の事実である——約定はブローカーが返した数量と単価を持つが、取り込みは
// 「観測されたブローカーの建玉へ台帳の数量を合わせた」という利用者の承認であり、**約定価格を持たない**。
// 射影（PortfolioProjection）へは LedgerFill（由来 TradeOrigin.ManualAdoption）として合流し、数量だけが効く。
//
// 数量の表現: Side × Quantity（>0）は**台帳へ適用する減少分**（ロングの減少は Sell、ショートの減少は Buy）。
// LedgerQuantityBefore / BrokerQuantity は符号付き（PositionDriftItem と同じ）で、監査と冪等キーのために持つ。
public sealed record LedgerDriftAdoption(
    Guid Id,
    string Symbol,
    Market Market,
    TradeSide Side,
    int Quantity,
    // 取り込み前の台帳の平均取得単価（ローカル通貨）。**約定価格ではない**（射影はこの値を使わない）。
    decimal CostBasisPrice,
    // 取り込み前の建玉の加重平均約定時レート（IADR-0107）。
    decimal FxRateToBase,
    int LedgerQuantityBefore,
    int BrokerQuantity,
    DateTimeOffset ObservedAt,
    string Actor,
    string Reason,
    DateTimeOffset AdoptedAt)
{
    /// <summary>
    /// 冪等キー。**同じ観測に対する同じ取り込みは 1 件だけ**にする（並行の二重投入で台帳が二重に減ると、
    /// ロングが意図しないショートへ反転する）。逐次の 2 回目は「乖離が無い」で先に弾かれるため、本キーが効くのは
    /// 読み取りから追記までの間に他方が先に書いた場合である。
    /// </summary>
    public string IdempotencyKey => string.Create(
        CultureInfo.InvariantCulture,
        $"{Symbol}:{(int)Market}:{LedgerQuantityBefore}:{BrokerQuantity}:{ObservedAt.UtcTicks}");
}
