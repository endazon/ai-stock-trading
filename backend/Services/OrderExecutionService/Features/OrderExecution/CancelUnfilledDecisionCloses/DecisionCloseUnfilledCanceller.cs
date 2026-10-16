using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution.AmendOrder;

namespace OrderExecutionService.Features.OrderExecution.CancelUnfilledDecisionCloses;

// 🔴 FR-10, FR-19, FR-05, ADR-0067 決定1〜4, 05_trading-assumptions §5, #1339, IADR-0539:
// 取引判断（LLM）由来の決済の指値が、発注から N 分（既定 15 分）たっても約定しなければ、**系が取り消すだけ**にする。
//
//   - **置き直し・訂正・成行への切り替えはしない**（決定1。古い売りの判断を LLM なしで執行し直すことになり、計画 ADR-0051 決定3 に抵触する）。
//     取り消した後は何も出さない。売るかどうかは次の判断の巡回が現在値で決める。
//   - 取り消した決済が処理中から外れるのは、**ブローカーが終端を確かめた時点**である（既存の約定追跡が照会で観測して OrderExecuted を出す。
//     IADR-0536 決定3「迷ったら解放しない」を変えない）。ここでは記録の状態を書き換えない。
//   - 対象は**判断由来の決済だけ**（決定4。ApprovalOrigin=TradeDecision）。利用者の手仕舞い・維持率割れの自動縮小・出どころ不明（Unknown）・
//     出どころなし（保護の機構）は取り消さない。Unknown は「分からない」であり、能動的な取消の側へ倒さない。
//   - **1 建玉（銘柄・市場・決済の方向）・1 市場の現地取引日あたり MaxCancelsPerPositionPerDay 回まで**（決定2）。上限に達したら取り消さず DAY の失効に任せる。
//   - 一部約定の決済も取り消す（残りだけが取り消され、約定した分は残る。IADR-0539 決定3）。
//   - 取消は OrderAmendmentService.CancelAsync（利用者の取消と同じ口）を通す。ライフサイクル台帳に残り、確認できた取消は OrderCancelled として
//     リスク管理の注文アクティビティ射影＝**相場操縦の検知の母集団**へ入る（決定3。FR-19 を全注文に適用する）。
//
// 🔴 **無効（既定）なら何もしない**——ストアも読まず、ブローカーへ何も送らない（決定2・6）。
// 🔴 **印を先に書く**（MarkUnfilledCancelRequested → 取消）。取消を送った後に落ちても、再起動後に同じ注文をもう一度取り消さず、
//    回数にも数えられる。取消が例外で失敗しても印は残す（再試行しない＝取消の反復を作らない。注文は DAY で失効する）。
public sealed class DecisionCloseUnfilledCanceller(
    IExecutedOrderStore store,
    OrderAmendmentService amendments,
    IClock clock,
    IOptions<DecisionCloseUnfilledCancelOptions> options,
    ILogger<DecisionCloseUnfilledCanceller>? logger = null)
{
    /// <summary>ライフサイクル台帳・OrderCancelled に残す取消の理由（系による取消であることを識別できる固定の接頭辞）。</summary>
    public const string ReasonPrefix = "判断由来の決済の指値が未約定のため系が取消（ADR-0067）";

    private readonly ILogger _logger = logger ?? NullLogger<DecisionCloseUnfilledCanceller>.Instance;

    /// <summary>1 巡回。無効なら何もせず空を返す。</summary>
    public async Task<DecisionCloseUnfilledCancelResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.Enabled)
            return DecisionCloseUnfilledCancelResult.Empty;

        cancellationToken.ThrowIfCancellationRequested();
        var now = clock.UtcNow;
        var candidates = store.FindUnfilledDecisionCloses(now - settings.Unfilled, settings.EffectiveBatchSize);
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
            var (dayStart, dayEnd) = TradingDayRange(record.Market, now);
            var used = store.CountUnfilledCancelRequests(record.Symbol, record.Market, record.Side, dayStart, dayEnd);
            if (used >= settings.MaxCancelsPerPositionPerDay)
            {
                capped.Add(record);
                continue;
            }

            // 印を先に書く。書けない（並行に終端した・既に印がある）なら取り消さない。
            if (!store.MarkUnfilledCancelRequested(record.OrderId, now))
                continue;

            requested.Add(record);
            var reason = $"{ReasonPrefix}: 発注から {(int)(now - record.ExecutedAt).TotalMinutes} 分（N={settings.UnfilledMinutes} 分）";
            try
            {
                var outcome = await amendments.CancelAsync(record.DecisionId, reason, cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "判断由来の決済の指値を取り消しました（発注から {N} 分以上未約定。置き直し・成行への切り替えはしません。次の判断の巡回に任せます）: "
                        + "DecisionId={DecisionId} OrderId={OrderId} 銘柄={Symbol} 方向={Side} 約定 {Filled}/{Quantity} 発注={PlacedAt} "
                        + "取消の確認={Confirmed} 照会した状態={Observed}。確認できない取消は約定追跡が終端を確かめてから解放します。",
                    settings.UnfilledMinutes, record.DecisionId, record.OrderId, record.Symbol, record.Side,
                    record.FilledQuantity, record.Quantity, record.ExecutedAt, outcome.Confirmed,
                    outcome.ObservedStatus is { } observed ? OrderStatusDisplay.Describe(observed) : "不明");
                if (outcome.Event is { } cancelled)
                    confirmed.Add(cancelled);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _logger.LogWarning(ex,
                    "判断由来の決済の指値の取消に失敗しました（再試行はしません。注文は当日限り〔DAY〕で失効します）: "
                        + "DecisionId={DecisionId} OrderId={OrderId} 銘柄={Symbol}。証券会社の画面で注文を確認してください。",
                    record.DecisionId, record.OrderId, record.Symbol);
            }
        }

        return new DecisionCloseUnfilledCancelResult(requested, confirmed, capped, failed);
    }

    /// <summary>
    /// その瞬間が属する<b>市場の現地取引日</b>の UTC の範囲 [始まり, 次の日の始まり)（米国株は米国東部時間・日本株は JST の暦日。
    /// サマータイムは TimeZoneInfo が吸収する）。
    /// </summary>
    public static (DateTimeOffset Start, DateTimeOffset End) TradingDayRange(Market market, DateTimeOffset instant)
    {
        var zone = MarketHours.ZoneOf(market);
        var localDate = TimeZoneInfo.ConvertTime(instant, zone).Date;
        var nextDate = localDate.AddDays(1);
        return (new DateTimeOffset(localDate, zone.GetUtcOffset(localDate)),
            new DateTimeOffset(nextDate, zone.GetUtcOffset(nextDate)));
    }
}

/// <summary>
/// 🔴 #1339, IADR-0539: 1 巡回の結果。<see cref="Confirmed"/> は呼び出し側（常駐）が発行する。
/// <see cref="Capped"/> は回数の上限で取り消さなかった決済（常駐が注文ごとに 1 回だけ INF を残す）。
/// </summary>
public sealed record DecisionCloseUnfilledCancelResult(
    IReadOnlyList<ExecutionRecord> Requested,
    IReadOnlyList<OrderCancelled> Confirmed,
    IReadOnlyList<ExecutionRecord> Capped,
    int Failed)
{
    public static readonly DecisionCloseUnfilledCancelResult Empty = new([], [], [], 0);
}
