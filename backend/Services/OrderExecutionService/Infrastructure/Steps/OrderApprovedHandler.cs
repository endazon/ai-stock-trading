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

        // 🔴 FR-05, FR-10, #876, IADR-0398: 見送り済みの承認の再配送。**発注していないので何も発行しない**
        // （OrderExecuted を作らない・見送りの理由は記録していないので OrderDispatchForgone も再発行しない）。
        // 例外にもしない（投げると共通再試行が同じ承認を再処理するだけで、結論は変わらない）。ログは発注執行が出している。
        if (result.ForgoneReplaySuppressed)
            return;

        // FR-10, FR-06, FR-11, ADR-0040 決定1, #1002, IADR-0429 決定1: 損切りの実行機構の解決結果（承認の手法 → 実際に適用した手法）。
        // 監査台帳へ記録され、日報・月報の「実際に適用された手法」の一次記録になる。**発注・見送りの結果より先に出す**
        // ——解決はそれらの手前の事実であり、見送り（下の return）の経路でも失わない。
        // 🔴 **報告のためだけの事実であり、統制の発行（見送り・発注・保護逆指値）の前に立たせない。** 発行に失敗しても
        // 例外を握ってログに残し、後続の発行へ進む（経費の記録 RecordTradeExpensesAsync と同じ形）。ここで投げると
        // 後続の発行（見送り・発注結果・保護逆指値）が出ないまま再配送になり、再配送は見送りと保護逆指値のイベントを
        // 再発行しない（見送り済みは何も発行せず、完了済みは発注結果と経費の記録だけを出し直す）。
        // 握った回の承認は、日報・月報で「解決結果の記録が見つからない」として件数に出る（黙って消えない）。
        if (result.MethodResolved is { } methodResolved)
            await PublishMethodResolvedAsync(methodResolved, bus).ConfigureAwait(false);

        // 🔴 FR-10, FR-05, FR-09, FR-11, ADR-0016, #864, IADR-0355 決定5: 決済をブローカーの実建玉と突き合わせて
        // 見つけた乖離は、**既存の乖離検知（IADR-0118）と同じイベント**で監査台帳と Critical 通知へ流す
        // （新しい通知経路を作らない）。見送りにも、数量を縮めた発注にも付き得るため**先に**出す
        // ——見送り／発注の結果より、その理由である乖離が時系列で前に並ぶ方が辿りやすい。
        if (result.Drift is { } drift)
        {
            // 🔴 監査（3 巡目）3: **イベントが運ぶ 2 つの数量はどちらも「送った株数」ではない**
            // （台帳の決済数量とブローカーの**ネット**であり、両建てでは 3 つ目の数になる）。
            // 通知を読む人が「結局この決済は出たのか・何株出たのか」を取り違えないよう、
            // **実際に送った株数をログに併記する**（0 なら 1 株も送っていない＝見送り）。
            logger.LogError(
                "決済の発注前にブローカーの建玉との乖離を検知しました（{Count} 件・この決済で実際に送った株数={Dispatched}）: {Drifts}",
                drift.Drifts.Count,
                result.DriftDispatchedQuantity,
                string.Join(
                    "、",
                    drift.Drifts.Select(d => $"{d.Symbol}/{d.Market} 台帳 {d.LedgerQuantity} / ブローカーのネット {d.BrokerQuantity}")));
            await bus.PublishAsync(drift).ConfigureAwait(false);
        }

        // FR-05, ADR-0002（SPOF）, #331, IADR-0211: 見送り（OpenD 切断・逆指値を張れない Open）は
        // **例外を投げずに**正常終了する——投げると Wolverine の共通再試行がキューで再送し、
        // 「キューイングせず見送り」の裁定に反する。再発注は次の取引判断からのみ。
        if (result.Forgone is { } forgone)
        {
            metrics.RecordOrderDispatchForgone(forgone.Reason);
            // FR-10, ADR-0040 決定1, #819, IADR-0342 決定4: 手法による拒否は OrderExecutionAppService が
            // Error で記録済みである（ここでは他の見送りと同じ 1 行を残す）。
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

        // FR-10, FR-11, FR-12, ADR-0040 決定1（S3）, #821, IADR-0347: S3 の試行の記録（注文種別と拒否理由）。
        // **Placed / CoverageLost より先に出す**——「何で試したか」は結果の手前の事実であり、監査台帳を
        // 時系列で読んだときに結果の直前へ並ぶ方が辿りやすい。受理・拒否のどちらでも 1 件出る。
        if (result.StopAttempted is { } attempted)
        {
            logger.LogWarning(
                "S3 代替注文種別で保護レグを試行: EntryDecisionId={EntryDecisionId} 種別={OrderType} 状態={Status}"
                + " retType={RetType} retMsg={RetMsg}",
                attempted.EntryDecisionId, attempted.OrderType, attempted.Status,
                attempted.RejectReasonCode, attempted.RejectReasonMessage);
            await bus.PublishAsync(attempted).ConfigureAwait(false);
        }

        // FR-10, #331, IADR-0210: 保護逆指値の結果。Placed はリスク管理が台帳の承認行へ結線し、
        // CoverageLost は監査・Critical 通知（および手仕舞いレグの台帳結線）へ流れる。
        if (result.StopPlaced is { } stopPlaced)
        {
            logger.LogInformation(
                "保護逆指値を発注: EntryDecisionId={EntryDecisionId} StopOrderId={StopOrderId} トリガー={Trigger} 試行={Attempt}",
                stopPlaced.EntryDecisionId, stopPlaced.StopOrderId, stopPlaced.TriggerPrice, stopPlaced.Attempt);
            await bus.PublishAsync(stopPlaced).ConfigureAwait(false);
        }

        // FR-10, FR-12, ADR-0040 決定1（S2）, #819, IADR-0342 決定6: 保護逆指値の免除（ペーパーで免除）。
        // 監査台帳へ記録され、Discord へ通知される。**逆指値なしの建玉が意図して存在する**ことを埋もれさせない。
        if (result.StopWaived is { } waived)
        {
            logger.LogWarning(
                "保護逆指値を免除（S2・ペーパーで免除）: EntryDecisionId={EntryDecisionId} 銘柄={Symbol} 数量={Quantity} 損切りライン={StopLossPrice} 発注先={Provider}",
                waived.EntryDecisionId, waived.Symbol, waived.Quantity, waived.StopLossPrice, waived.Provider);
            await bus.PublishAsync(waived).ConfigureAwait(false);
        }

        // FR-10, FR-12, ADR-0040 決定1（S1）, #820, IADR-0344 決定3: ソフトウェア逆指値の配置。ブローカー側に保護が無い
        // （システム停止中は決済されない）ことを監査と通知に残す。
        if (result.SoftwareStopArmed is { } softwareStop)
        {
            logger.LogWarning(
                "ソフトウェア逆指値を配置（S1・ブローカーへの逆指値なし）: EntryDecisionId={EntryDecisionId} 銘柄={Symbol} 数量={Quantity} 損切りライン={StopLossPrice} 発注先={Provider}",
                softwareStop.EntryDecisionId, softwareStop.Symbol, softwareStop.Quantity, softwareStop.StopLossPrice, softwareStop.Provider);
            await bus.PublishAsync(softwareStop).ConfigureAwait(false);
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

    // FR-10, FR-06, #1002, IADR-0429 決定1: 解決結果の発行。**失敗しても発注執行の発行を止めない**（例外は握ってログに残す）。
    private async Task PublishMethodResolvedAsync(StopLossMethodResolved methodResolved, IMessageBus bus)
    {
        try
        {
            await bus.PublishAsync(methodResolved).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "損切りの実行機構の解決結果を発行できませんでした（発注執行は継続します。日報・月報では"
                + "「解決結果の記録が見つからない承認」に数えられます）。DecisionId={DecisionId}",
                methodResolved.DecisionId);
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
