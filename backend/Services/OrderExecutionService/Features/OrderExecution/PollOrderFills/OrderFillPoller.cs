using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;

namespace OrderExecutionService.Features.OrderExecution.PollOrderFills;

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
// 🔴 FR-10, #833 項目1, IADR-0389 決定2: **ここが「決済が未約定で終わった」を確認できる唯一の点である。**
// ソフトウェア逆指値（S1）の決済は受理の時点で保護記録を完了させており（IADR-0344 決定5-5）、その注文が
// 0 約定のまま失効・取消されると建玉が無保護のまま誰の巡回にも載らない。終端化を観測したこの場で保護記録を
// 再武装する（reArmer。未構成なら従来どおり何もしない）。**新しい常駐を足さない。**
//
// 🔴 FR-10, #958, IADR-0406 決定2: **Active な S0（ブローカー側逆指値）の保護記録の現試行の逆指値レグは、追跡上限の対象外**
// である。S0 のレグの記録は武装の時刻で作られ、逆指値は何日も約定を待ち得る——追跡上限（既定 24 時間）で切ると、
// 武装から 24 時間を超えて約定した損切りが OrderExecuted として一度も発行されず台帳へ届かない（IADR-0394 の
// 「S0 は約定で数える」が働かない）。照会件数が増えないよう、足すのは Active な S0 のレグだけ（保有建玉の数で頭打ち）。
// protectiveStops が未構成なら従来どおり（追跡上限内だけ）。
//
// 発行（OrderExecuted の Publish）は Worker 層が担う（Application はメッセージ基盤に非依存の既存レイヤリングを維持）。
public sealed class OrderFillPoller(
    IBrokerAdapter broker,
    IExecutedOrderStore store,
    IClock clock,
    SoftwareStopReArmer? reArmer = null,
    ILogger<OrderFillPoller>? logger = null,
    IProtectiveStopOrderStore? protectiveStops = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<OrderFillPoller>.Instance;

    /// <summary>
    /// 1 巡回。発注から <paramref name="maxTracking"/> 以内の非終端記録を最大 <paramref name="batchSize"/> 件追跡し、
    /// 発行すべき <see cref="OrderExecuted"/> を結果に載せて返す（発行は呼び出し側＝Worker）。
    /// #958, IADR-0406: Active な S0 の逆指値レグは <paramref name="maxTracking"/> を過ぎていても追跡する。
    /// </summary>
    public async Task<OrderFillPollResult> PollOnceAsync(
        TimeSpan maxTracking, int batchSize, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = clock.UtcNow;
        var pending = WithActiveBrokerStopLegs(store.FindPendingSince(now - maxTracking, batchSize), batchSize);

        var executed = new List<OrderExecuted>();
        var softwareStopEvents = new List<SoftwareStopExecuted>();
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
                    // 🔴 #833 項目1, IADR-0389 決定8: ただし S1 の決済レグの不明は**無音にしない**
                    //（受理の時点で保護記録は完了しており、黙って据え置くと建玉が誰の巡回にも載らない）。
                    // 再武装も完了もせず、一定間隔で 1 回 Critical を出すだけである。
                    reArmer?.OnCloseUnresolved(record);
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
                {
                    terminalized++;

                    // 🔴 #833 項目1, IADR-0389 決定2・9: 記録を終端化した**後**に再武装する。
                    // 先に再武装すると UpdateOutcome が失敗した巡回で記録が非終端のまま残り、
                    // 次の巡回が同じレグで**二度目の再武装**をする（主張が二重に増える）。
                    // 再武装の失敗で巡回を止めない（1 件の失敗でバッチ全体を落とさない＝既存の流儀）が、
                    // 無音にもしない——終端化はこの 1 回しか観測できないため、失敗は必ず Critical で残す。
                    try
                    {
                        var reArmed = reArmer?.OnCloseTerminalized(record, snapshot.Status, filledQuantity);
                        if (reArmed is not null)
                            softwareStopEvents.Add(reArmed);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex,
                            "🔴 ソフトウェア逆指値の再武装に失敗しました（決済は {Status} で終端化済み・保護記録は完了のままです）。"
                                + "**建玉が無保護で残っている可能性があります。直ちに確認してください。**"
                                + "CloseDecisionId={DecisionId} OrderId={OrderId}",
                            snapshot.Status, record.DecisionId, record.OrderId);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
            }
        }

        return new OrderFillPollResult(
            pending.Count, updated, terminalized, unchanged, unknown, failed, executed, softwareStopEvents);
    }

    // 🔴 FR-10, #958, IADR-0406 決定2: 追跡上限内の記録へ、Active な S0 の保護記録の現試行の逆指値レグ（非終端）を足す。
    // S1（ソフトウェア逆指値）の行はブローカーに逆指値を持たない（StopOrderId が空）ので足さない。
    // 保護記録が完了したレグは足さない——ガードは S0 のレグの終端（約定・失効）を観測した・取り消したとき、保護記録を
    // 完了させる**前に**そのレグの記録の追跡の起点を観測時刻へ進める（IADR-0406 決定3）。完了したレグは窓の内側へ
    // 戻っているので、ガードと約定追跡のどちらが先に巡回しても、約定はここで拾われる。
    private IReadOnlyList<ExecutionRecord> WithActiveBrokerStopLegs(
        IReadOnlyList<ExecutionRecord> withinWindow, int batchSize)
    {
        if (protectiveStops is null)
            return withinWindow;

        var stopLegIds = protectiveStops.FindActive(batchSize)
            .Where(s => !s.IsSoftwareStop && !string.IsNullOrEmpty(s.StopOrderId))
            .Select(s => s.StopOrderId)
            .ToHashSet(StringComparer.Ordinal);
        if (stopLegIds.Count == 0)
            return withinWindow;

        var known = withinWindow.Select(r => r.OrderId).ToHashSet(StringComparer.Ordinal);
        stopLegIds.ExceptWith(known);
        if (stopLegIds.Count == 0)
            return withinWindow;

        return [.. store.FindPendingByOrderIds(stopLegIds), .. withinWindow];
    }
}

// #270, IADR-0113: 1 巡回の結果。件数サマリ（可観測性）と、発行すべき OrderExecuted の一覧を持つ。
// Updated は記録を更新した件数（うち終端化が Terminalized）。Unknown は照会できず据え置いた件数。
//
// #833 項目1, IADR-0389: SoftwareStopEvents は再武装で発行すべき SoftwareStopExecuted（CloseUnfilled）。
// 既定 null で足すのは、既存の呼び出し（テスト・集計）を壊さないためである。
public sealed record OrderFillPollResult(
    int Scanned,
    int Updated,
    int Terminalized,
    int Unchanged,
    int Unknown,
    int Failed,
    IReadOnlyList<OrderExecuted> Executed,
    IReadOnlyList<SoftwareStopExecuted>? SoftwareStopEvents = null);
