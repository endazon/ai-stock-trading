using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;

// FR-10, UC-02, #331, IADR-0210 決定4: 保護逆指値の失効検知・再発注・残存取消の巡回。
// 業務フロー 02「逆指値の未受理・失効を検知 → 再発注、不可なら成行で手仕舞い」の実装であり、
// **逆指値なしの建玉を持たない**（失効側）と**建玉なき逆指値を残さない**（反対建玉の防止）の両方向を守る。
//
// fail-safe:
//   - 逆指値の照会不能（null）・建玉の照会不能（null）→ **据え置き**（不明を「無い」と取り違えない。
//     IADR-0118 と同じ規律。誤った再発注・誤った取消はどちらも実弾で実損になる）。
//   - 1 件の失敗でバッチ全体を止めない（件数集計・OrderFillPoller と同じ流儀）。
//   - 🔴 #848, IADR-0117（2026-09-19 追記・改定 7）: **成行手仕舞いの「届いたか不明」を「未発注」と取り違えない。**
//     成行手仕舞いは予約 → 発注 → 確定の 3 相（IADR-0057）で送る。予約を解放してよいのは
//     BrokerUnavailableException（確実に未発注）だけであり、それ以外の失敗は予約を Reserved のまま残して
//     **次の巡回で撃ち直さない**（撃ち直すと巡回ごとに全数量の成行が 1 本ずつ増える＝二重決済でショート化）。
//   - 🔴 #848, IADR-0117（2026-09-19 追記・改定 9）: **据え置きを無音にしない。** 予約だけが残っている成行手仕舞いは、
//     このプロセスが未通知のとき（再起動後の最初の巡回・送信中／発行前にプロセスが止まった後）と、前回の通知から
//     1 時間たったときに CloseDispatchIndeterminate（Critical・CloseIntent つき）を発行し直す。成行も逆指値も送らない。
//
// 発行（イベントの Publish）は Worker 層（ProtectiveStopGuardService）が担う。
public sealed class ProtectiveStopGuard(
    IBrokerAdapter broker,
    IBrokerPositionSource positions,
    IProtectiveStopOrderStore stops,
    IExecutedOrderStore store,
    IOrderReservationStore reservations,
    IClock clock,
    ILogger<ProtectiveStopGuard>? logger = null,
    HeldCloseNotificationTracker? heldCloseNotifications = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<ProtectiveStopGuard>.Instance;

    // #848, IADR-0117（改定 9）: 据え置き中の成行手仕舞いを「このプロセスがいつ通知したか」の記憶。本番は singleton を
    // 渡す（ガード自体は巡回ごとに作られる scoped）。省略時は本インスタンスの寿命で持つ（単体テスト用）。
    private readonly HeldCloseNotificationTracker _heldCloseNotifications =
        heldCloseNotifications ?? new HeldCloseNotificationTracker();

    public async Task<ProtectiveStopGuardResult> RunOnceAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var active = stops.FindActive(batchSize);
        if (active.Count == 0)
            return ProtectiveStopGuardResult.Empty;

        // 建玉は 1 巡回につき 1 回照会する。null（照会不能）なら巡回ごと据え置く——建玉不明のまま
        // 「消滅した」と誤認して逆指値を取り消すと、直後の失効側の保護が消える。
        var snapshot = await positions.GetPositionsAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
            return new ProtectiveStopGuardResult(active.Count, 0, 0, 0, 0, active.Count, 0, []);

        var events = new List<object>();
        var stillActive = 0;
        var completed = 0;
        var replaced = 0;
        var closedOut = 0;
        var unknown = 0;
        var failed = 0;

        foreach (var stop in active)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var outcome = await EvaluateAsync(stop, snapshot, events, cancellationToken).ConfigureAwait(false);
                switch (outcome)
                {
                    case Outcome.StillActive: stillActive++; break;
                    case Outcome.Completed: completed++; break;
                    case Outcome.Replaced: replaced++; break;
                    case Outcome.ClosedOut: closedOut++; break;
                    case Outcome.Unknown: unknown++; break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
            }
        }

        return new ProtectiveStopGuardResult(active.Count, stillActive, completed, replaced, closedOut, unknown, failed, events);
    }

    private async Task<Outcome> EvaluateAsync(
        ProtectiveStopOrder stop,
        IReadOnlyList<BrokerPositionSnapshot> snapshot,
        List<object> events,
        CancellationToken cancellationToken)
    {
        var order = await broker.GetOrderAsync(stop.StopOrderId, cancellationToken).ConfigureAwait(false);
        if (order is null)
        {
            // 照会不能＝不明。据え置いて次回巡回で再試行する（「無い」と取り違えない）。
            return Outcome.Unknown;
        }

        var remaining = RemainingPositionFor(stop, snapshot);

        if (OrderStatusLifecycle.IsPending(order.Status))
        {
            if (remaining > 0)
                return Outcome.StillActive; // 正常: 建玉あり・逆指値滞留中。

            // 建玉消滅（owner 手仕舞い・自動縮小・強制買戻し等）: 残存逆指値を取り消す。
            // 決済済み建玉に残る注文が発火すると**反対方向の建玉を生む**（業務フロー 02 補足の二重決済問題）。
            await broker.CancelOrderAsync(stop.StopOrderId, cancellationToken).ConfigureAwait(false);
            MarkCompleted(stop);
            return Outcome.Completed;
        }

        if (order.Status == OrderStatus.Filled)
        {
            // ブローカー側で損切りが成立した。台帳への反映は既存の約定追跡ポーリング（IADR-0113）が担う
            // （逆指値レグは ExecutionRecord として保存済み）。ここでは保護の完了だけを記録する。
            MarkCompleted(stop);
            return Outcome.Completed;
        }

        // 失効（Cancelled / Rejected / Expired）。
        if (remaining <= 0)
        {
            MarkCompleted(stop); // 建玉も無い: 保護対象が消えている。
            return Outcome.Completed;
        }

        // 建玉が残っているのに逆指値が無い: 再発注する。不可なら成行で手仕舞う（業務フロー 02 の表）。
        return await ReplaceOrCloseAsync(stop, Math.Min(remaining, stop.Quantity), events, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Outcome> ReplaceOrCloseAsync(
        ProtectiveStopOrder stop, int quantity, List<object> events, CancellationToken cancellationToken)
    {
        var protective = broker as IProtectiveOrderBroker;
        var attempt = stop.Attempt + 1;
        var stopDecisionId = ProtectiveStopIds.StopDecisionId(stop.EntryDecisionId, attempt);
        var closeIntent = BuildCloseIntent(stop, quantity, stop.TriggerPrice);
        var now = clock.UtcNow;

        // 🔴 FR-10, FR-11, UC-06, #848, IADR-0117（2026-09-19 追記・改定 7）:
        // **この試行の成行手仕舞いレグの痕跡を、逆指値の再発注より「前」に見る。**
        // closeDecisionId は決定的（手仕舞いが完了しない限り stop.Attempt は進まない＝巡回をまたいで同じ値）。
        var closeDecisionId = ProtectiveStopIds.CloseDecisionId(stop.EntryDecisionId, attempt);

        // (a) 発注結果の記録が既にある: 送信後に行の更新だけが失われた窓、または突合（IADR-0074）が
        //     Placed と解決した後。**新しい注文は出さず**、記録で保護の完了を確定する。
        var recordedClose = store.FindByDecisionId(closeDecisionId);
        if (recordedClose is not null)
        {
            var recordedIntent = BuildCloseIntent(stop, recordedClose.Quantity, stop.TriggerPrice);
            return CompleteAsClosed(stop, recordedClose.Quantity, closeDecisionId, recordedIntent, events);
        }

        // (b) 予約だけがある（記録なし）: 以前の巡回で成行手仕舞いを**送ったかもしれない**。
        //     逆指値の再発注も成行も行わず据え置く（不明を「未発注」と取り違えない）。
        //     逆指値より前に見る理由: 成行が生きているかもしれない建玉へ新しい逆指値を張ると、成行の約定後に
        //     **建玉なき逆指値**が残り、発火すれば反対建玉になる。
        //     🔴 改定 9: ここは**無音にしない**。「Critical は不明になった巡回で発行済み」とは限らない——
        //     送信中にプロセスが止まった（OperationCanceledException）・巡回の結果を発行する前に止まった場合、
        //     予約だけが残ってイベントは 1 通も出ておらず、**取引台帳も一切押さえていない**。
        if (reservations.Find(closeDecisionId) is not null)
        {
            LogHeldClose(stop, closeDecisionId);
            RenotifyHeldCloseIfDue(stop, quantity, closeDecisionId, closeIntent, events);
            return Outcome.Unknown;
        }

        if (protective is not null)
        {
            BrokerOrder? newStop = null;
            try
            {
                newStop = await protective
                    .PlaceStopOrderAsync(closeIntent, stop.TriggerPrice, stopDecisionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 再発注不可→手仕舞いへ。「届いたか不明」もここへ落ちる（IADR-0117 改定 6 の前後で同一の分岐。
                // 逆指値が生きていた場合に孤立する件は #853）。成行手仕舞いの側は下で 3 相に載せている。
                newStop = null;
            }

            if (newStop is not null && newStop.Status is OrderStatus.Accepted or OrderStatus.PartiallyFilled or OrderStatus.Filled)
            {
                store.Save(new ExecutionRecord(
                    stopDecisionId, newStop.OrderId, stop.Symbol, stop.Market, stop.CloseSide,
                    stop.ProductType, PositionEffect.Close, quantity, stop.TriggerPrice,
                    newStop.FilledQuantity, newStop.AveragePrice, newStop.Status, SlippageRatio: 0m, now));

                stops.Save(stop with
                {
                    StopDecisionId = stopDecisionId,
                    StopOrderId = newStop.OrderId,
                    Quantity = quantity,
                    Attempt = attempt,
                    UpdatedAt = now,
                });

                events.Add(new ProtectiveStopPlaced(
                    stop.EntryDecisionId, stopDecisionId, newStop.OrderId, closeIntent, stop.TriggerPrice, attempt, now));
                return Outcome.Replaced;
            }
        }

        // 再発注できない: 成行で手仕舞う（逆指値なしの建玉を持たない）。
        if (protective is not null)
        {
            // 相 2（発注着手の権威・IADR-0057）: 送る「前」に決定的な DecisionId を予約する。取れなければ送らない
            //（(b) の後に並行して予約された＝送信中か成否不明。重ねて送らない）。
            if (!reservations.TryReserve(closeDecisionId, clock.UtcNow))
            {
                LogHeldClose(stop, closeDecisionId);
                return Outcome.Unknown;
            }

            BrokerOrder? closeOrder = null;
            try
            {
                closeOrder = await protective
                    .PlaceMarketOrderAsync(closeIntent, closeDecisionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (BrokerUnavailableException)
            {
                // 接続確立の失敗＝**確実に未発注**（IADR-0211 決定 1 の契約）。予約を解放してよいのはこの型だけである。
                // 記録は Active のまま残し、次回巡回で同じ DecisionId を再予約して撃ち直す。人手対応は下の None で求める。
                reservations.Release(closeDecisionId);
            }
            catch (BrokerDispatchIndeterminateException ex)
            {
                // 🔴 送信済み・**届いたか不明**。未発注と仮定して撃ち直さない（IADR-0117 改定 7）。
                return HoldIndeterminateClose(stop, quantity, closeDecisionId, closeIntent, events, ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 分類できない失敗は**未発注と言い切れない**。「確実に未発注」の側ではなく「届いたか不明」の側へ倒す。
                return HoldIndeterminateClose(stop, quantity, closeDecisionId, closeIntent, events, ex);
            }

            if (closeOrder is not null)
            {
                // 相 4（確定）: 結果を保存してから予約を確定する。保存に失敗して例外で抜けても予約は Reserved のまま
                // 残るため、次の巡回は (b) で据え置く（送信済みの成行を撃ち直さない）。
                var closedAt = clock.UtcNow;
                store.Save(new ExecutionRecord(
                    closeDecisionId, closeOrder.OrderId, stop.Symbol, stop.Market, stop.CloseSide,
                    stop.ProductType, PositionEffect.Close, quantity, closeIntent.Price,
                    closeOrder.FilledQuantity, closeOrder.AveragePrice, closeOrder.Status,
                    SlippageCalculator.Compute(closeIntent.Price, closeOrder.AveragePrice, stop.CloseSide), closedAt));
                reservations.MarkCompleted(closeDecisionId, closeOrder.OrderId, closedAt);

                return CompleteAsClosed(stop, quantity, closeDecisionId, closeIntent, events);
            }

            // 手仕舞いも失敗（確実に未発注）: 記録は Active のまま残し（次回巡回で再試行）、人手対応を Critical で求める。
        }

        events.Add(new ProtectiveStopCoverageLost(
            stop.EntryDecisionId, stop.Symbol, stop.Market,
            ProtectiveStopLossCause.LapsedInFlight, ProtectiveStopRemediation.None,
            quantity, CloseDecisionId: null, CloseIntent: null, clock.UtcNow));
        return Outcome.ClosedOut;
    }

    // 成行手仕舞いレグが（今回の送信・以前の送信の記録・突合の解決のいずれかで）確定した: 保護を完了し、
    // 手仕舞いレグを台帳へ結線する（AppendApproval は DecisionId で冪等。再発行しても二重計上しない）。
    private Outcome CompleteAsClosed(
        ProtectiveStopOrder stop, int quantity, Guid closeDecisionId, OrderIntent closeIntent, List<object> events)
    {
        MarkCompleted(stop);
        _heldCloseNotifications.Forget(closeDecisionId); // 解決した。以後は再通知しない。
        events.Add(new ProtectiveStopCoverageLost(
            stop.EntryDecisionId, stop.Symbol, stop.Market,
            ProtectiveStopLossCause.LapsedInFlight, ProtectiveStopRemediation.PositionClosed,
            quantity, closeDecisionId, closeIntent, clock.UtcNow));
        return Outcome.ClosedOut;
    }

    // 🔴 FR-10, FR-11, UC-06, #848, IADR-0117（2026-09-19 追記・改定 7）: 成行手仕舞いを送ったが結果を確認できない。
    //   - 予約を**解放も確定もしない**（Reserved のまま。次の巡回は入口の (b) で据え置き、同じ成行を重ねない）。
    //   - 結果を**保存しない**（実在しない注文 ID の記録を作らない）。記録は Active のまま（完了を主張しない）。
    //   - **無音にしない**: CloseDispatchIndeterminate を発行する（Critical）。CloseIntent を運ぶので、
    //     取引台帳は生きているかもしれない成行を処理中の決済として押さえる。据え置きが続くあいだは
    //     入口の (b) が 1 時間ごとに発行し直す（改定 9。30 秒の巡回ごとには重ねない）。
    // 滞留した予約は、自動リコンサイル（IADR-0074 / IADR-0092）が解決する。#856, IADR-0362: アプリ既定は
    // 無効のままだが**配備では有効**であり、突合が「発注済み」と確定すれば次の巡回が (b) で「送らずに完了」させる。
    // ただし**解放（NotPlaced）の門は閉じている**ので、未発注と出ても撃ち直しは起きない（実機検証まで据え置き）。
    // 据え置きが続くあいだは人が証券会社の画面で確認して解決する（docs/operations/broker-execution-paths-runbook.md）。
    private Outcome HoldIndeterminateClose(
        ProtectiveStopOrder stop, int quantity, Guid closeDecisionId, OrderIntent closeIntent,
        List<object> events, Exception ex)
    {
        _logger.LogError(ex,
            "保護逆指値ガード: 成行手仕舞いの結果を確認できませんでした（送信済み・届いたか不明）。"
            + "重ねて発注しません。予約は Reserved のまま据え置きます。証券会社の画面で注文と建玉を確認してください: "
            + "EntryDecisionId={EntryDecisionId} CloseDecisionId={CloseDecisionId} 銘柄={Symbol} 数量={Quantity}",
            stop.EntryDecisionId, closeDecisionId, stop.Symbol, quantity);

        AddHeldCloseNotification(stop, quantity, closeDecisionId, closeIntent, events);
        return Outcome.Unknown;
    }

    // 🔴 #848, IADR-0117（2026-09-19 追記・改定 9）: 据え置き（予約あり・記録なし）が続いている。次のどちらかなら発行し直す。
    //   - このプロセスが未通知: 再起動後の最初の巡回。送信中にプロセスが止まった場合はイベントが 1 通も出ておらず、
    //     **台帳の押さえ（CloseIntent）も無い**。発行前に止まった場合も同じ。ここで初めて押さえさせる。
    //   - 前回の通知から 1 時間: 通知を 1 回見逃すと、逆指値なしの建玉が無期限に残る。
    // 発行するのは通知と台帳への結線だけであり、**成行も逆指値も送らない**。
    private void RenotifyHeldCloseIfDue(
        ProtectiveStopOrder stop, int quantity, Guid closeDecisionId, OrderIntent closeIntent, List<object> events)
    {
        if (_heldCloseNotifications.IsDue(closeDecisionId, clock.UtcNow))
            AddHeldCloseNotification(stop, quantity, closeDecisionId, closeIntent, events);
    }

    private void AddHeldCloseNotification(
        ProtectiveStopOrder stop, int quantity, Guid closeDecisionId, OrderIntent closeIntent, List<object> events)
    {
        var now = clock.UtcNow;
        events.Add(new ProtectiveStopCoverageLost(
            stop.EntryDecisionId, stop.Symbol, stop.Market,
            ProtectiveStopLossCause.LapsedInFlight, ProtectiveStopRemediation.CloseDispatchIndeterminate,
            quantity, closeDecisionId, closeIntent, now));
        _heldCloseNotifications.MarkNotified(closeDecisionId, now);
    }

    private void LogHeldClose(ProtectiveStopOrder stop, Guid closeDecisionId) =>
        _logger.LogWarning(
            "保護逆指値ガード: 成行手仕舞いは発注に着手済みで結果が未確定です（予約あり・記録なし）。"
            + "逆指値の再発注も成行も重ねずに据え置きます: EntryDecisionId={EntryDecisionId} "
            + "CloseDecisionId={CloseDecisionId} 銘柄={Symbol}",
            stop.EntryDecisionId, closeDecisionId, stop.Symbol);

    private void MarkCompleted(ProtectiveStopOrder stop) =>
        stops.Save(stop with { State = ProtectiveStopState.Completed, UpdatedAt = clock.UtcNow });

    // 建玉スナップショットから「エントリー方向の残数量」を求める。数量は符号付き（+ロング/−ショート・IADR-0118）。
    // ロング建玉（Buy 建て）は正の数量、ショート建玉（Sell 建て）は負の数量の絶対値が残である。
    public static int RemainingPositionFor(ProtectiveStopOrder stop, IReadOnlyList<BrokerPositionSnapshot> snapshot)
    {
        var net = snapshot
            .Where(p => p.Symbol == stop.Symbol && p.Market == stop.Market)
            .Sum(p => p.Quantity);
        return stop.EntrySide == TradeSide.Buy ? Math.Max(0, net) : Math.Max(0, -net);
    }

    // FR-17, IADR-0107: 決済レグはエントリーの換算レートを引き継ぐ（OrderExecutionService と同じ規律）。
    private static OrderIntent BuildCloseIntent(ProtectiveStopOrder stop, int quantity, decimal referencePrice) =>
        new(stop.Symbol, stop.Market, stop.CloseSide, stop.ProductType, stop.Mode, quantity, referencePrice,
            PositionEffect.Close, StopLossPrice: null, stop.FxRateToBase);

    private enum Outcome
    {
        StillActive,
        Completed,
        Replaced,
        ClosedOut,
        Unknown,
    }
}

// #331, IADR-0210: 1 巡回の結果。件数サマリ（可観測性）と、発行すべきイベント
// （ProtectiveStopPlaced / ProtectiveStopCoverageLost）の一覧を持つ。
public sealed record ProtectiveStopGuardResult(
    int Scanned,
    int StillActive,
    int Completed,
    int Replaced,
    int ClosedOut,
    int Unknown,
    int Failed,
    IReadOnlyList<object> Events)
{
    public static readonly ProtectiveStopGuardResult Empty = new(0, 0, 0, 0, 0, 0, 0, []);
}
