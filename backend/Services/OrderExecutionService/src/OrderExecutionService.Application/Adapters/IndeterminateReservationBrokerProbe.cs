using AiStockTrading.OrderExecution.Application.Ports;

namespace AiStockTrading.OrderExecution.Application.Adapters;

// #141, FR-05, IADR-0074: リコンサイル・プローブの既定 no-op 実装。**常に Indeterminate を返す**。
//
// これにより、実 OpenD 照会が配線されるまで予約は決して自動解放・終端化されない（Placed/NotPlaced 経路が
// 発火しない）。構造上 NotPlaced を返し得ないため、二重発注を招く誤解放が起き得ない（fail-safe の要）。
// 実照会（DecisionId を client order id として伝播 or 時刻窓突合）は opt-in の後続・#82 系 E2E で差し替える。
public sealed class IndeterminateReservationBrokerProbe : IReservationBrokerProbe
{
    public Task<ReservationProbeResult> ProbeAsync(
        OrderDispatchReservation reservation, CancellationToken cancellationToken = default) =>
        Task.FromResult(ReservationProbeResult.Indeterminate);
}
