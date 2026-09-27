using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution.ReconcileOrderReservations;

// 🔴 NFR-09, FR-05, FR-20, ADR-0045 決定1・決定2, #1051, IADR-0444 決定3: 照会が「未発注（NotPlaced）」と答えた予約を
// 解放してよいか（＝再発注を許可してよいか）を、**予約ごとに、その予約の取引環境で**決める（純関数）。
//
// 門の設定はプロセスに 1 つだが、予約は送った時点の取引環境を持つ（IOrderReservationStore.TryReserve が記録する）。
// 発注先は構成の入れ替え（broker.tier）でも、将来は画面の発注先（FR-20）でも変わり得るため、
// 「いま門が開いているか」ではなく「**この予約の取引環境の門が開いているか**」で判定する。
//
// 解放してよいのは次の 2 つだけであり、それ以外はすべて据え置く（held-not-placed）:
//   - 予約が moomoo SIMULATE で、照会先も SIMULATE、SIMULATE の門が開。
//   - 予約が moomoo REAL で、照会先も REAL、実弾の門が開。
// 🔴 不明（null＝取引環境の列を足す前の予約）は SIMULATE とは読まない（原則 A）。実弾と同じ厳しい側に倒しても、照会先と
//    一致することを示せないため、**どちらの門を開けても解放しない**。内蔵 paper の予約も外部の門の対象ではないため据え置く。
// 🔴 予約の取引環境と照会先が食い違う（SIMULATE で送った予約を実弾の口座で照会した等）ときも据え置く
//    ——別の口座で「一致ゼロ」は「未発注」の根拠にならない。
public static class ReleaseGatePolicy
{
    /// <summary>
    /// 予約（取引環境 <paramref name="reservationProvider"/>）の <c>NotPlaced</c> を解放してよいか。
    /// </summary>
    /// <param name="reservationProvider">予約を取った時点で送る先だった発注先。null は不明。</param>
    /// <param name="probeProvider">照会した口座の発注先（リコンサイラが持つ発注アダプタの <c>Provider</c>。照会は同じ接続を共有する）。</param>
    /// <param name="gates">取引環境ごとの門。</param>
    public static bool MayRelease(
        BrokerProvider? reservationProvider, BrokerProvider probeProvider, ReleaseOnNotPlacedGates gates)
    {
        ArgumentNullException.ThrowIfNull(gates);

        if (reservationProvider is not { } provider || provider != probeProvider)
            return false;

        return provider switch
        {
            BrokerProvider.MoomooSimulate => gates.Simulate,
            BrokerProvider.MoomooReal => gates.Real,
            _ => false,
        };
    }
}
