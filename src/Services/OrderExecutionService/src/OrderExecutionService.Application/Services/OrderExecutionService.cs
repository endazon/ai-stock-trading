using AiStockTrading.OrderExecution.Application.Ports;
using AiStockTrading.OrderExecution.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;

namespace AiStockTrading.OrderExecution.Application.Services;

// FR-05, UC-01, UC-02, ADR-0002/0003: 承認済み注文（OrderApproved・損切りの Close 含む）をブローカへ発注し、
// 結果（約定/拒否/取消）を OrderExecuted として確定する。注文実体＋スリッページを永続化する（FR-16 の月報データ源）。
// ブローカ実装は IBrokerAdapter で差し替える（既定はペーパー・moomoo は PoC まで未実装。IADR-0016）。
public sealed class OrderExecutionService(
    IBrokerAdapter broker,
    IExecutedOrderStore store,
    IClock clock)
{
    public async Task<OrderExecuted> ExecuteAsync(OrderApproved approved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approved);

        // 冪等性: 同一 DecisionId の発注結果が既にあれば、再発注せず既存結果を再発行する。MassTransit の再配送
        // （UseAiStockTradingRetry）で同一 OrderApproved が再処理されても二重発注・二重計上しない。DecisionId は
        // 取引判断/損切り1件に対応する（stop-loss は EventId 由来で安定・IADR-0015）。
        // 注: ブローカ発注は成功したが Save 前に失敗した窓は本チェックでは捕捉できない（記録が無いため）。
        // 実発注（moomoo）では outbox 等の発注前予約が必要になる（IADR-0016 の後続）。
        var existing = store.FindByDecisionId(approved.DecisionId);
        if (existing is not null)
        {
            return new OrderExecuted(
                existing.DecisionId, existing.OrderId, existing.Status,
                existing.FilledQuantity, existing.AveragePrice, existing.ExecutedAt);
        }

        var intent = approved.Intent;

        // ADR-0003: 承認済み注文のみ発注する。Close（損切り）も同一経路。
        var brokerOrder = await broker.PlaceOrderAsync(intent, cancellationToken).ConfigureAwait(false);

        // FR-16: 実効スリッページを取引毎に算出・記録する。
        var slippage = SlippageCalculator.Compute(intent.Price, brokerOrder.AveragePrice, intent.Side);

        var now = clock.UtcNow;
        store.Save(new ExecutionRecord(
            approved.DecisionId,
            brokerOrder.OrderId,
            intent.Symbol,
            intent.Market,
            intent.Side,
            intent.ProductType,
            intent.PositionEffect,
            intent.Quantity,
            intent.Price,
            brokerOrder.FilledQuantity,
            brokerOrder.AveragePrice,
            brokerOrder.Status,
            slippage,
            now));

        return new OrderExecuted(
            approved.DecisionId,
            brokerOrder.OrderId,
            brokerOrder.Status,
            brokerOrder.FilledQuantity,
            brokerOrder.AveragePrice,
            now);
    }
}
