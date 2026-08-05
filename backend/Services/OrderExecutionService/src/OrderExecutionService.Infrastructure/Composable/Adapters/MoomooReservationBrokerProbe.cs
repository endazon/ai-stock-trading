using AiStockTrading.OrderExecution.Application.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiStockTrading.OrderExecution.Infrastructure.Composable.Adapters;

// #141, FR-05, IADR-0092: 滞留 Reserved の実照会プローブ（moomoo/OpenD SIMULATE）。
//
// 発注時に伝播した DecisionId（remark）で SIMULATE 口座の現在＋履歴注文を照合し（IMoomooTradeClient.FindOrderByClientIdAsync）、
// 3値へ写像する:
//   - 一致注文あり               → Placed（スナップショットから BrokerOrder/OrderIntent を再構成）。
//   - 全列挙が成功して一致ゼロ    → NotPlaced（確実に未発注＝解放可）。※client が null で「確実に不在」を保証したときだけ。
//   - 照会失敗（例外）           → Indeterminate（据え置き）。OperationCanceledException は握りつぶさず伝播。
//
// at-most-once の要: 「不明」を解放（NotPlaced）に倒さない。client の契約により NotPlaced は「全市場・現在＋履歴を
// 成功裏に列挙して一致ゼロ」の既知の窓でのみ返る。照会経路のあらゆる不確実性は Indeterminate へ倒れる。
internal sealed class MoomooReservationBrokerProbe(
    IMoomooTradeClient client,
    ILogger<MoomooReservationBrokerProbe>? logger = null) : IReservationBrokerProbe
{
    private readonly ILogger<MoomooReservationBrokerProbe> _logger =
        logger ?? NullLogger<MoomooReservationBrokerProbe>.Instance;

    public async Task<ReservationProbeResult> ProbeAsync(
        OrderDispatchReservation reservation, CancellationToken cancellationToken = default)
    {
        var clientOrderId = MoomooClientOrderId.From(reservation.DecisionId);
        try
        {
            var snapshot = await client
                .FindOrderByClientIdAsync(clientOrderId, reservation.ReservedAt.ToUniversalTime(), cancellationToken)
                .ConfigureAwait(false);

            // 見つからない（= client が全列挙を成功裏に完了して一致ゼロ）→ 確実に未発注 → 解放可。
            if (snapshot is null)
                return ReservationProbeResult.NotPlaced;

            // 見つかった → 発注済み（終端/非終端を問わず注文が存在する）→ 終端化（再発注しない）。
            return ReservationProbeResult.Placed(ToBrokerOrder(snapshot));
        }
        catch (OperationCanceledException)
        {
            throw; // シャットダウン等のキャンセルは握りつぶさない。
        }
        catch (Exception ex)
        {
            // 照会不達・応答異常・部分列挙は「不明」。解放も終端化もせず据え置く（fail-safe＝二重発注を招かない）。
            _logger.LogWarning(ex,
                "moomoo リコンサイル照会に失敗したため Indeterminate に倒します decisionId={DecisionId}",
                reservation.DecisionId);
            return ReservationProbeResult.Indeterminate;
        }
    }

    // moomoo 注文スナップショット → BrokerOrder（+ OrderIntent 再構成）。
    // 状態・約定はブローカ実体に由来し正確。intent の ProductType/PositionEffect/Mode は moomoo 注文から一意に復元
    // できないため既定（Cash/Open/Paper）で近似する（ローカル永続の報告用途に限られ、下流 OrderExecuted は非依存・IADR-0092）。
    private static BrokerOrder ToBrokerOrder(MoomooOrderSnapshot s)
    {
        var intent = new OrderIntent(
            Symbol: s.Symbol,
            Market: MapMarket(s.Market),
            Side: MapSide(s.Side),
            ProductType: ProductType.Cash,
            Mode: BrokerProvider.InternalPaper,
            Quantity: s.Quantity,
            Price: s.Price,
            PositionEffect: PositionEffect.Open);
        return new BrokerOrder(
            s.OrderId, intent, MoomooBrokerAdapter.MapState(s.State),
            s.FilledQuantity, s.AveragePrice,
            PlacedAt: s.PlacedAt ?? default, CompletedAt: s.CompletedAt);
    }

    private static Market MapMarket(MoomooMarket market) => market switch
    {
        MoomooMarket.Japan => Market.Japan,
        _ => Market.UnitedStates,
    };

    private static TradeSide MapSide(MoomooSide side) => side switch
    {
        MoomooSide.Sell => TradeSide.Sell,
        _ => TradeSide.Buy,
    };
}
