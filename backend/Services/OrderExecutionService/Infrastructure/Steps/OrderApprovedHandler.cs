using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using Microsoft.Extensions.Logging;
using OrderExecutionService.Features.OrderExecution.RecordTradeExpenses;
using Wolverine;
using AppSvc = OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder.OrderExecutionAppService;

namespace OrderExecutionService.Infrastructure.Steps;

// FR-05, UC-01, UC-02, ADR-0003: リスク管理が承認した注文（OrderApproved・損切りの Close 含む）を購読し、
// ブローカへ発注して結果を OrderExecuted として発行する。発注ロジックは Application 層に委譲する。
//
// ADR-0013, IADR-0129, #354: MassTransit の IConsumer<OrderApproved> から Wolverine のハンドラへ移行した。
// **本ハンドラは IADR-0129 決定 3（DisableConventionalLocalRouting）の直接の受益者である**:
// OrderApproved は発行元の RiskManagementService 自身も購読しており、Wolverine の既定のままだと発行が
// RiskManagement のプロセス内に閉じ、本サービスへ一通も届かない（＝発注が一件も執行されない）。
// IADR-0129 決定 9 によりハンドラ型は public sealed とする。
//
// NFR-07, #287, IADR-0255: 発注の健全性メトリクス（発注結果と、発注に届かなかった見送り）はここで計上する。
// **見送りは注文状態を持たない**ため、ブローカーの拒否（OrderStatus.Rejected）とは別の計器で数える。
//
// FR-11, FR-16, ADR-0016 決定15, #633, IADR-0300: 即時約定（内蔵 paper）の経費を記録する経路もここに置く。
// 段 1 の既定（UnsuppliedOrderExpenseSource）ではイベントが 1 本も出ないため、発行の面での挙動は不変であり、
// 増えるのは「7 区分すべて未計上」を残す警告ログ 1 行だけである。
public sealed class OrderApprovedHandler(
    AppSvc executionService,
    TradeExpenseRecordingService tradeExpenses,
    BusinessMetrics metrics,
    ILogger<OrderApprovedHandler> logger)
{
    public async Task Handle(OrderApproved message, IMessageBus bus, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(bus);

        var result = await executionService.ExecuteAsync(message, cancellationToken).ConfigureAwait(false);

        // FR-05, ADR-0002（SPOF）, #331, IADR-0211: 見送り（OpenD 切断・逆指値を張れない Open）は
        // **例外を投げずに**正常終了する——投げると Wolverine の共通再試行がキューで再送し、
        // 「キューイングせず見送り」の裁定に反する。再発注は次の取引判断からのみ。
        if (result.Forgone is { } forgone)
        {
            metrics.RecordOrderDispatchForgone(forgone.Reason);
            logger.LogWarning(
                "発注見送り: DecisionId={DecisionId} 銘柄={Symbol} 理由={Reason}（再試行しません）",
                forgone.DecisionId, forgone.Intent.Symbol, forgone.Reason);
            await bus.PublishAsync(forgone).ConfigureAwait(false);
            return;
        }

        var executed = result.Executed!;
        metrics.RecordOrderExecuted(executed.Status, executed.Provider);

        // NFR-01, #689, IADR-0307: **ここが「発注完了」＝ NFR-01 の終点である。**
        // 起点（価格変動検知・情報収集の完了）は承認が運んでくる。起点を持たない注文
        // （owner 手仕舞い・維持証拠金の自動縮小）は 0 ms ではなく**未観測**として数える
        // ——0 を入れると「5 分以内」を満たしているように見えてしまう。判断は BusinessMetrics 側に 1 か所。
        metrics.RecordOrderCompletionLatency(
            executed.CycleTrigger, executed.CycleStartedAt, executed.ExecutedAt);

        logger.LogInformation(
            "発注執行: DecisionId={DecisionId} OrderId={OrderId} 状態={Status} 約定数={Filled} 平均価格={AvgPrice}",
            executed.DecisionId, executed.OrderId, executed.Status, executed.FilledQuantity, executed.AveragePrice);

        await bus.PublishAsync(executed).ConfigureAwait(false);

        // FR-11, ADR-0016 決定15, #633, IADR-0300: 約定の経費を記録する（段 1 では常に「取得できない」）。
        await RecordTradeExpensesAsync(executed, bus, cancellationToken).ConfigureAwait(false);

        // FR-10, #331, IADR-0210: 保護逆指値の結果。Placed はリスク管理が台帳の承認行へ結線し、
        // CoverageLost は監査・Critical 通知（および手仕舞いレグの台帳結線）へ流れる。
        if (result.StopPlaced is { } stopPlaced)
        {
            logger.LogInformation(
                "保護逆指値を発注: EntryDecisionId={EntryDecisionId} StopOrderId={StopOrderId} トリガー={Trigger} 試行={Attempt}",
                stopPlaced.EntryDecisionId, stopPlaced.StopOrderId, stopPlaced.TriggerPrice, stopPlaced.Attempt);
            await bus.PublishAsync(stopPlaced).ConfigureAwait(false);
        }

        if (result.CoverageLost is { } coverageLost)
        {
            logger.LogWarning(
                "保護逆指値が成立せず建玉解消: EntryDecisionId={EntryDecisionId} 銘柄={Symbol} 原因={Cause} 対処={Remediation} 数量={Quantity}",
                coverageLost.EntryDecisionId, coverageLost.Symbol, coverageLost.Cause,
                coverageLost.Remediation, coverageLost.Quantity);
            await bus.PublishAsync(coverageLost).ConfigureAwait(false);
        }
    }

    // FR-11, FR-16, ADR-0016 決定15, ADR-0027 決定2/決定4, #633, IADR-0300:
    // 約定 1 件ぶんの経費を記録する。**経費の記録が発注執行を止めてはならない**ため例外は握る
    // （発注は既に成立しており、ここで投げると再配送で同じ OrderApproved が再処理される）。
    //
    // 🔴 **段 2（実費の供給）の前提**: 供給が始まると本経路は同じ約定を 2 度観測し得る
    // （メッセージ再配送・約定追跡の複数巡回）。TradeExpenseRecorded の発行には
    // TradeExpense.SourceId を鍵とした重複排除が要る。**実装するまで供給を有効にしない。**
    private async Task RecordTradeExpensesAsync(
        OrderExecuted executed, IMessageBus bus, CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await tradeExpenses
                .RecordForExecutionAsync(executed, cancellationToken)
                .ConfigureAwait(false);

            TradeExpenseRecordingLog.Write(logger, executed, outcome);

            foreach (var recorded in outcome.Events)
                await bus.PublishAsync(recorded).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "経費の記録に失敗しました（発注執行は継続します）。OrderId={OrderId}", executed.OrderId);
        }
    }
}
