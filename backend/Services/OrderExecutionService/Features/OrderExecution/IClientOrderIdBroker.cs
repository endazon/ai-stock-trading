using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution;

// #141, FR-05, IADR-0092: 発注時に DecisionId を「ブローカ側の client order id」として伝播できるブローカの capability。
//
// 滞留 Reserved は DecisionId のみを持ち注文 ID を持たないため、実照会リコンサイル（IReservationBrokerProbe）が
// 滞留を照合できるのは、発注時に DecisionId をブローカ注文へ紐づけておいた場合に限る（moomoo の remark 等）。
// この capability を持たないブローカ（PaperBrokerAdapter・テスト fake 等）は本ポートを実装せず、OrderExecutionService
// は従来の IBrokerAdapter.PlaceOrderAsync 経路に倒れる（remark 非対応でも発注は成立し冪等化は保たれる）。
//
// shared 契約 IBrokerAdapter を変更せず、この capability を OrderExecution 内に閉じることで、paper・全テスト fake を
// 無改修に保つ（IADR-0092 決定1）。
public interface IClientOrderIdBroker
{
    /// <summary>
    /// 注文を送信し、<paramref name="decisionId"/> をブローカ側の client order id（例: moomoo の remark）として
    /// 当該注文に紐づける。滞留 Reserved のリコンサイルが後からこの ID で発注済み注文を照合できるようにする。
    /// それ以外の意味論は <see cref="Shared.Contracts.Ports.IBrokerAdapter.PlaceOrderAsync"/> と同一。
    /// </summary>
    Task<BrokerOrder> PlaceOrderAsync(
        OrderIntent intent, Guid decisionId, CancellationToken cancellationToken = default);
}
