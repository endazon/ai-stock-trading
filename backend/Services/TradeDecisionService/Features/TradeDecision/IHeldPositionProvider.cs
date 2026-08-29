using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision;

// FR-04, FR-05, FR-10, #292, IADR-0119: 判断対象の銘柄について現在の保有建玉を照会するポート。
// 実体はリスク管理（#12・#63 台帳）の GET /risk-controls/open-positions（既存・OwnerOrService）。
// 安全既定は NoOpHeldPositionProvider（常に不明）。
public interface IHeldPositionProvider
{
    /// <summary>
    /// (銘柄, 市場) の**符号付き**建玉数量（+ ロング / − ショート / 0 保有なし）。
    ///
    /// 照会できない場合は **null（＝不明）** を返す。0（保有なし）と厳格に区別すること。
    /// 失敗を 0 へ倒すと「保有していない」と誤断定し、裸の新規売りを通してしまう。
    /// </summary>
    Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken cancellationToken = default);
}
