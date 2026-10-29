using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution.AmendOrder;

namespace OrderExecutionService.Features.OrderExecution.CancelUnfilledDecisionCloses;

// 🔴 FR-10, FR-19, ADR-0067 決定1〜4, #1339, IADR-0539 / #1486, IADR-0594 決定1: 判断由来の注文（決済・新規建て）の指値の未約定時の取消の本体。
// 決済（DecisionCloseUnfilledCanceller）と新規建て（DecisionEntryUnfilledCanceller）が同じ規律で呼ぶ —— 取り消すだけ（置き直し・訂正・成行はしない）、
// 印を先に書く、1 建玉（銘柄・市場・注文の方向）・1 市場の現地取引日あたりの回数の上限、当日に発注した注文だけ、取消は利用者の取消と同じ口。
// 呼び出し側は有効のときだけ呼ぶ（無効ならストアも読まない）。
internal static class UnfilledDecisionOrderCancellation
{
    private static readonly Market[] SupportedMarkets = Enum.GetValues<Market>();

    public static async Task<DecisionCloseUnfilledCancelResult> RunAsync(
        IExecutedOrderStore store,
        OrderAmendmentService amendments,
        IClock clock,
        UnfilledDecisionOrderCancelOptions settings,
        PositionEffect effect,
        string reasonPrefix,
        string kind,
        ILogger logger,
        IReadOnlyCollection<string>? cappedOrderIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = clock.UtcNow;
        // 🔴 対象は今の市場の現地取引日に発注した注文だけ（前日以前の非終端は当日の一覧に無く、取消を送れない記録＝IADR-0536 残余 1。
        // 回数の枠を消費させない）。問い合わせの下限は全市場の日の始まりの早いほう、市場ごとの厳密な下限は下で見る。
        var earliestDayStart = SupportedMarkets.Min(m => DecisionCloseUnfilledCanceller.TradingDayRange(m, now).Start);
        var candidates = store.FindUnfilledDecisionOrders(
            effect, now - settings.Unfilled, settings.EffectiveBatchSize, cappedOrderIds, earliestDayStart);
        if (candidates.Count == 0)
            return DecisionCloseUnfilledCancelResult.Empty;

        var confirmed = new List<OrderCancelled>();
        var requested = new List<ExecutionRecord>();
        var capped = new List<ExecutionRecord>();
        var failed = 0;

        foreach (var record in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 決定2: 回数の上限は市場の現地取引日で数える（米国株は米国東部の暦日。UTC の暦日では数えない）。
            var (dayStart, dayEnd) = DecisionCloseUnfilledCanceller.TradingDayRange(record.Market, now);
            if (record.ExecutedAt < dayStart)
                continue; // 前の取引日に発注した注文（上の注記）。
            var used = store.CountUnfilledCancelRequests(effect, record.Symbol, record.Market, record.Side, dayStart, dayEnd);
            if (used >= settings.MaxCancelsPerPositionPerDay)
            {
                capped.Add(record);
                continue;
            }

            // 印を先に書く。書けない（並行に終端した・既に印がある）なら取り消さない。
            if (!store.MarkUnfilledCancelRequested(record.OrderId, now))
                continue;

            requested.Add(record);
            var reason = $"{reasonPrefix}: 発注から {(int)(now - record.ExecutedAt).TotalMinutes} 分（N={settings.UnfilledMinutes} 分）";
            try
            {
                var outcome = await amendments.CancelAsync(record.DecisionId, reason, cancellationToken).ConfigureAwait(false);
                logger.LogWarning(
                    "判断由来の{Kind}の指値を取り消しました（発注から {N} 分以上未約定。置き直し・成行への切り替えはしません。次の判断の巡回に任せます）: "
                        + "DecisionId={DecisionId} OrderId={OrderId} 銘柄={Symbol} 方向={Side} 約定 {Filled}/{Quantity} 発注={PlacedAt} "
                        + "取消の確認={Confirmed} 照会した状態={Observed}。確認できない取消は約定追跡が終端を確かめてから解放します。",
                    kind, settings.UnfilledMinutes, record.DecisionId, record.OrderId, record.Symbol, record.Side,
                    record.FilledQuantity, record.Quantity, record.ExecutedAt, outcome.Confirmed,
                    outcome.ObservedStatus is { } observed ? OrderStatusDisplay.Describe(observed) : "不明");
                if (outcome.Event is { } cancelled)
                    confirmed.Add(cancelled);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogWarning(ex,
                    "判断由来の{Kind}の指値の取消に失敗しました（再試行はしません。注文は当日限り〔DAY〕で失効します）: "
                        + "DecisionId={DecisionId} OrderId={OrderId} 銘柄={Symbol}。証券会社の画面で注文を確認してください。",
                    kind, record.DecisionId, record.OrderId, record.Symbol);
            }
        }

        return new DecisionCloseUnfilledCancelResult(requested, confirmed, capped, failed);
    }
}
