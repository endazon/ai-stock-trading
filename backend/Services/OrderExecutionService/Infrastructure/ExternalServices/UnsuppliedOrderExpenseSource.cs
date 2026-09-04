using OrderExecutionService.Features.OrderExecution.RecordTradeExpenses;

namespace OrderExecutionService.Infrastructure.ExternalServices;

// FR-11, ADR-0016 決定15, ADR-0027 決定4, #633, IADR-0300:
// 経費明細の供給が無い構成の**安全既定**。**常に「取得できない」を返す。**
//
// 実費の取得は moomoo の OnReply_GetOrderFee（現状は空実装）に依存し、応答仕様（区分の粒度・通貨・
// 手数料と諸費用の切れ目）は実口座でしか確かめられない。ここで概算（CostCalculator）を実費として
// 積むと、ADR-0027 が塞いだ「表示されている数字が何を意味するか誰も答えられない」状態へ戻る。
//
// 🔴 **空の明細（Supplied([])）を返さない。** 空を返すと「照会できて費用が 1 円も無かった」と読め、
// 供給の結線を忘れた期間がそのまま「費用なし」で通る（UnsuppliedBorrowFeeRecordSource が同じ理由で
// 空の BorrowFeeRecord ではなく null を返している）。
public sealed class UnsuppliedOrderExpenseSource : IOrderExpenseSource
{
    /// <summary>取得できない理由（診断用・固定文言）。テストがこの文言そのものを固定する。</summary>
    public const string Reason = "ブローカーから経費明細を照会する実装が無い（moomoo の注文費用照会は未実装）。";

    public Task<OrderExpenseLookup> GetOrderExpensesAsync(
        OrderExpenseQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult(OrderExpenseLookup.Unavailable(Reason));
}
