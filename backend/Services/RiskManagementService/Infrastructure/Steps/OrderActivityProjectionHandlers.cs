using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Events;

namespace RiskManagementService.Infrastructure.Steps;

// FR-19, #154, IADR-0067: 注文系イベントを購読して注文アクティビティストア（相場操縦検知の入力源）へ射影するハンドラ群。
// IOrderActivitySource は同期契約かつ発注審査のホットパス上にあるため、供給は他サービスへの同期照会ではなく
// Risk 専有 DB への射影とする（IADR-0018 の取引台帳射影と同型・IADR-0067）。冪等（再送）はストア側で担保する。
//
// 承認（OrderApproved）だけが銘柄・方向を持つため行の生成を担い、約定・訂正・取消は DecisionId で既存行を更新する。
// 相場操縦検知の母集団には「約定ゼロで取り消された注文」（#63 取引台帳が構造的に捨てる型）が要るため、
// 取引台帳とは別のストアに射影する（IADR-0067）。
//
// ADR-0013, IADR-0129 決定 10, #354: MassTransit の IConsumer<T> から Wolverine のハンドラへ移行した。
// **OrderApproved / OrderExecuted は台帳側ハンドラ（OrderApprovedLedgerHandler / OrderExecutedLedgerHandler）と
// 同一のハンドラチェーンで実行される**（MassTransit ではキューが分かれていた）。再試行で両方が再実行されるが、
// 双方の書き込みが冪等であることで意味が保たれる（根拠は IADR-0129 決定 10）。
public sealed class OrderApprovedActivityHandler(IOrderActivityStore store)
{
    public void Handle(OrderApproved message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // 発注時刻は承認時刻で近似する（承認→発注は同期的に連続し、窓長に対して誤差は無視できる・IADR-0067）。
        store.RecordPlacement(
            message.DecisionId, message.Intent.Symbol, message.Intent.Market,
            message.Intent.Side, message.Intent.Quantity, message.ApprovedAt);
    }
}

public sealed class OrderExecutedActivityHandler(IOrderActivityStore store)
{
    public void Handle(OrderExecuted message)
    {
        ArgumentNullException.ThrowIfNull(message);
        store.RecordExecution(message.DecisionId, message.Status, message.FilledQuantity, message.ExecutedAt);
    }
}

public sealed class OrderModifiedActivityHandler(IOrderActivityStore store)
{
    public void Handle(OrderModified message)
    {
        ArgumentNullException.ThrowIfNull(message);
        store.RecordModification(message.DecisionId, message.Quantity, message.ModifiedAt);
    }
}

public sealed class OrderCancelledActivityHandler(IOrderActivityStore store)
{
    public void Handle(OrderCancelled message)
    {
        ArgumentNullException.ThrowIfNull(message);
        store.RecordCancellation(message.DecisionId, message.CancelledAt);
    }
}
