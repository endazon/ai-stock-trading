using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.Shared.Contracts.Events;
using MassTransit;

namespace AiStockTrading.RiskManagement.Infrastructure.Composable.Steps;

// FR-19, #154, IADR-0067: 注文系イベントを購読して注文アクティビティストア（相場操縦検知の入力源）へ射影する Consumer 群。
// IOrderActivitySource は同期契約かつ発注審査のホットパス上にあるため、供給は他サービスへの同期照会ではなく
// Risk 専有 DB への射影とする（IADR-0018 の取引台帳射影と同型・IADR-0067）。冪等（再送）はストア側で担保する。
//
// 承認（OrderApproved）だけが銘柄・方向を持つため行の生成を担い、約定・訂正・取消は DecisionId で既存行を更新する。
// 相場操縦検知の母集団には「約定ゼロで取り消された注文」（#63 取引台帳が構造的に捨てる型）が要るため、
// 取引台帳とは別のストアに射影する（IADR-0067）。

internal sealed class OrderApprovedActivityConsumer(IOrderActivityStore store)
    : IConsumer<OrderApproved>
{
    public Task Consume(ConsumeContext<OrderApproved> context)
    {
        var m = context.Message;
        // 発注時刻は承認時刻で近似する（承認→発注は同期的に連続し、窓長に対して誤差は無視できる・IADR-0067）。
        store.RecordPlacement(
            m.DecisionId, m.Intent.Symbol, m.Intent.Market, m.Intent.Side, m.Intent.Quantity, m.ApprovedAt);
        return Task.CompletedTask;
    }
}

internal sealed class OrderExecutedActivityConsumer(IOrderActivityStore store)
    : IConsumer<OrderExecuted>
{
    public Task Consume(ConsumeContext<OrderExecuted> context)
    {
        var m = context.Message;
        store.RecordExecution(m.DecisionId, m.Status, m.FilledQuantity, m.ExecutedAt);
        return Task.CompletedTask;
    }
}

internal sealed class OrderModifiedActivityConsumer(IOrderActivityStore store)
    : IConsumer<OrderModified>
{
    public Task Consume(ConsumeContext<OrderModified> context)
    {
        var m = context.Message;
        store.RecordModification(m.DecisionId, m.Quantity, m.ModifiedAt);
        return Task.CompletedTask;
    }
}

internal sealed class OrderCancelledActivityConsumer(IOrderActivityStore store)
    : IConsumer<OrderCancelled>
{
    public Task Consume(ConsumeContext<OrderCancelled> context)
    {
        var m = context.Message;
        store.RecordCancellation(m.DecisionId, m.CancelledAt);
        return Task.CompletedTask;
    }
}
