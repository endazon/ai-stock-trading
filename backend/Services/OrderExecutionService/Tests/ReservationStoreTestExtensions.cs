using OrderExecutionService.Features.OrderExecution;

namespace OrderExecutionService.Tests;

// NFR-09, #1051, IADR-0444 決定1: 本番の TryReserve は取引環境（送る先の発注先）を**必須引数**で受ける（渡し忘れを型で止める）。
// 取引環境を関心に持たない既存の試験は、取引環境を記録しない（＝不明）この 2 引数の形で予約を取る。
// 🔴 **試験アセンブリにだけ置く**——本番のコードからは見えない（本番で不明の予約を作る口を作らない）。
public static class ReservationStoreTestExtensions
{
    public static bool TryReserve(this IOrderReservationStore store, Guid decisionId, DateTimeOffset reservedAt) =>
        store.TryReserve(decisionId, reservedAt, brokerProvider: null);
}
