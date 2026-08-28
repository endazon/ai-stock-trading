using OrderExecutionService.Application.Ports;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;

namespace OrderExecutionService.Application.Polling;

// #270, FR-05, FR-10, IADR-0113: 発注済み・非終端の注文をブローカへ照会し、約定の進捗・終端化を記録へ反映して
// OrderExecuted を再発行する（＝統制の入力である取引台帳へ約定を届ける）。
//
// 背景: moomoo は発注時に Accepted（未約定）を返し、約定は後から非同期に成立する。この遷移を観測しないと
// trade_fills は 0 行のままで、SameDayReentry も金額系上限も次サイクルで素通しになる（#270）。
// paper（即時 Filled）では非終端の記録が生まれないため、本ポーラーは構造的に何もしない。
//
// fail-safe:
//   - 照会が null（当日一覧に無い・アダプタが例外を握って null に倒した）→ **何も書かない**・発行しない。
//     ブローカ状態を推測せず、次回巡回で再試行する。
//   - 例外 → 件数のみ集計して据え置き（1 件の失敗でバッチ全体を止めない＝OrderReservationReconciler と同じ流儀）。
//   - 約定数は巻き戻さない（部分列挙・順序前後で数量が減る応答を採らない）。
//
// 発行（OrderExecuted の Publish）は Worker 層が担う（Application はメッセージ基盤に非依存の既存レイヤリングを維持）。
public sealed class OrderFillPoller(IBrokerAdapter broker, IExecutedOrderStore store, IClock clock)
{
    /// <summary>
    /// 1 巡回。発注から <paramref name="maxTracking"/> 以内の非終端記録を最大 <paramref name="batchSize"/> 件追跡し、
    /// 発行すべき <see cref="OrderExecuted"/> を結果に載せて返す（発行は呼び出し側＝Worker）。
    /// </summary>
    public async Task<OrderFillPollResult> PollOnceAsync(
        TimeSpan maxTracking, int batchSize, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = clock.UtcNow;
        var pending = store.FindPendingSince(now - maxTracking, batchSize);

        var executed = new List<OrderExecuted>();
        var updated = 0;
        var terminalized = 0;
        var unchanged = 0;
        var unknown = 0;
        var failed = 0;

        foreach (var record in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var snapshot = await broker.GetOrderAsync(record.OrderId, cancellationToken).ConfigureAwait(false);
                if (snapshot is null)
                {
                    // 「不明」を「未約定」と取り違えない。記録は非終端のまま残り、次回巡回で再試行される。
                    unknown++;
                    continue;
                }

                // 約定数は単調（moomoo の FillQty は累積値）。減る方向の応答は数量だけ無視し、状態変化は採る。
                var filledQuantity = Math.Max(record.FilledQuantity, snapshot.FilledQuantity);
                var progressed = filledQuantity > record.FilledQuantity;
                var statusChanged = snapshot.Status != record.Status;
                if (!progressed && !statusChanged)
                {
                    // 変化なし＝書かない・発行しない（同一内容のイベントで下流を溢れさせない）。
                    unchanged++;
                    continue;
                }

                var averagePrice = progressed ? snapshot.AveragePrice : record.AveragePrice;
                var terminal = OrderStatusLifecycle.IsTerminal(snapshot.Status);

                // 約定時刻は終端化したときだけ更新する（ブローカの完了時刻を優先し、無ければ巡回時刻）。
                // 非終端の進捗では発注時刻を保持する（当日集計の帰属を発注日から動かさない）。
                var executedAt = terminal ? snapshot.CompletedAt ?? now : record.ExecutedAt;

                // FR-16: 実効スリッページは約定価格が確定してから算出し直す（発注時点の 0 のままにしない）。
                var slippage = SlippageCalculator.Compute(record.PlannedPrice, averagePrice, record.Side);

                if (!store.UpdateOutcome(
                        record.OrderId, snapshot.Status, filledQuantity, averagePrice, slippage, executedAt))
                {
                    // 走査後に記録が消えた（保持期間パージ等）。新規に作らず据え置く。
                    unknown++;
                    continue;
                }

                // FR-20, #386, IADR-0149 決定1: 照会したアダプタ＝発注したアダプタであるため、その発注先を載せる
                // （照会できるのは自分が出した注文だけであり、構成が変われば照会は null に倒れて発行されない）。
                executed.Add(new OrderExecuted(
                    record.DecisionId, record.OrderId, snapshot.Status, filledQuantity, averagePrice, executedAt,
                    broker.Provider));
                updated++;
                if (terminal)
                    terminalized++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
            }
        }

        return new OrderFillPollResult(pending.Count, updated, terminalized, unchanged, unknown, failed, executed);
    }
}

// #270, IADR-0113: 1 巡回の結果。件数サマリ（可観測性）と、発行すべき OrderExecuted の一覧を持つ。
// Updated は記録を更新した件数（うち終端化が Terminalized）。Unknown は照会できず据え置いた件数。
public sealed record OrderFillPollResult(
    int Scanned,
    int Updated,
    int Terminalized,
    int Unchanged,
    int Unknown,
    int Failed,
    IReadOnlyList<OrderExecuted> Executed);
