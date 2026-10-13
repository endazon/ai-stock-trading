using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution;

// 🔴 FR-10, FR-05, UC-06, #1105, IADR-0461 決定1・2・4 / FR-04, #1338, IADR-0535 決定2: **同じ建玉を売る処理中の決済**の数え方と、
// それが決済方向の建玉を**覆うか**の判定。**決済のゲート（OrderExecutionAppService）と処理中の決済の照会の口
// （QueryInFlightCloses。取引判断が LLM の前に読む）の 2 か所がこの 1 つを呼ぶ**——判定を 2 か所に書かない。
// ゲートが見送る（InFlightCloseCoversPosition）のと同じ条件でしか、判断は LLM の前に省かない（IADR-0535 決定1）。
//
//   - 対象: 発注執行の非終端の Close の記録で、銘柄・市場・決済の方向が同じもの（IExecutedOrderStore.FindPendingCloses）。
//   - 除外: 今の承認（excludeDecisionId）の記録。🔴 **Active な S0 / S3 の保護記録の逆指値レグ**——これを引くと S0 の建玉への
//     判断の決済が常に 0 株になり、FR-10「手仕舞いは止めない」に反する（SIMULATE では逆指値は売れる数量を押さえない）。
//     OrderId（StopOrderId）と DecisionId（StopDecisionId）のどちらかが一致すれば保護レグとみなす。
//   - 🔴 **窓の両端（IADR-0461 決定4 / IADR-0535 の規則 11 の表）**: 記録が非終端（前の端＝発注の後）**かつ** ブローカーが
//     「まだ生きている」と答えた（後の端＝終端の前）注文だけを数える。記録の状態は約定追跡（30 秒周期）が書くまで古く、
//     追跡を打ち切った記録は非終端のまま残る——記録だけで数えると、約定・失効した決済をいつまでも処理中と読む。
//     確かめられない（null・例外）・終端なら数えない（覆わない側＝ゲートは送る・判断は LLM を呼ぶ側）へ倒す。
public static class InFlightCloses
{
    /// <summary>
    /// 処理中の決済の候補（記録が非終端のもの。保護レグと <paramref name="excludeDecisionId"/> を除く）。ブローカーへは照会しない。
    /// 例外は呼び出し側へ伝える（倒し方は呼び出し側の規律に従う）。
    /// </summary>
    public static IReadOnlyList<ExecutionRecord> FindCandidates(
        IExecutedOrderStore store, IProtectiveStopOrderStore protectiveStops,
        string symbol, Market market, TradeSide closeSide, Guid? excludeDecisionId = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(protectiveStops);

        var entrySide = closeSide == TradeSide.Sell ? TradeSide.Buy : TradeSide.Sell;
        var protectiveLegs = protectiveStops.FindActiveFor(symbol, market, entrySide)
            .Where(s => s.State == ProtectiveStopState.Active && !s.IsSoftwareStop)
            .ToList();
        var legOrderIds = protectiveLegs
            .Select(s => s.StopOrderId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet(StringComparer.Ordinal);
        var legDecisionIds = protectiveLegs.Select(s => s.StopDecisionId).ToHashSet();

        return store.FindPendingCloses(symbol, market, closeSide)
            .Where(r => r.DecisionId != excludeDecisionId
                && !legOrderIds.Contains(r.OrderId)
                && !legDecisionIds.Contains(r.DecisionId))
            .ToList();
    }

    /// <summary>
    /// 候補のうち、ブローカーが「まだ生きている」と答えた注文の残り株数を数える（記録の数量 − max(記録の約定数, 照会の約定数)）。
    /// 照会が null・例外・終端なら数えない。キャンセルは伝える。
    /// </summary>
    public static async Task<InFlightCloseCount> CountLiveAsync(
        IEnumerable<ExecutionRecord> candidates, IBrokerAdapter broker, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(broker);

        var quantity = 0;
        var decisionIds = new List<Guid>();
        var orderIds = new List<string>();
        foreach (var record in candidates)
        {
            BrokerOrder? live;
            try
            {
                live = await broker.GetOrderAsync(record.OrderId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                live = null;
            }

            if (live is null || OrderStatusLifecycle.IsTerminal(live.Status))
                continue;

            var remaining = record.Quantity - Math.Max(record.FilledQuantity, live.FilledQuantity);
            if (remaining <= 0)
                continue;

            quantity += remaining;
            decisionIds.Add(record.DecisionId);
            orderIds.Add(record.OrderId);
        }

        return new InFlightCloseCount(quantity, decisionIds, orderIds);
    }

    /// <summary>
    /// 🔴 IADR-0461 決定2 / IADR-0535 決定2: 処理中の決済が決済方向の建玉（<paramref name="closableQuantity"/>）を<b>すべて覆う</b>か。
    /// 覆うなら決済を送っても拒否されるだけなので、ゲートは見送り（InFlightCloseCoversPosition）、判断は決済の結論を出しても発注にならない。
    /// 処理中が 0 なら覆わない。
    /// </summary>
    public static bool Covers(int closableQuantity, int inFlightQuantity) =>
        inFlightQuantity > 0 && closableQuantity - inFlightQuantity <= 0;
}

/// <summary>#1105, IADR-0461 / #1338, IADR-0535: 処理中の決済の株数と、その決済（判断 ID・注文 ID。同じ並び）。</summary>
public sealed record InFlightCloseCount(int Quantity, IReadOnlyList<Guid> DecisionIds, IReadOnlyList<string> OrderIds)
{
    public static InFlightCloseCount None { get; } = new(0, [], []);
}
