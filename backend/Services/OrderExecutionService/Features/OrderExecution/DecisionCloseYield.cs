using Microsoft.Extensions.Logging;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution;

/// <summary>
/// #1121, IADR-0466 / #1448, IADR-0577: 判断の手仕舞いを取り消す段の結果。
/// </summary>
public enum DecisionCloseYieldResult
{
    /// <summary>取り消すものが無い・確かめられない（従来どおり送る）。</summary>
    None,

    /// <summary>取り消した・約定が進んでいた（建玉を照会し直してから送る）。</summary>
    Changed,

    /// <summary>取消がまだ確定していない（送らない）。</summary>
    AwaitingCancel,
}

// 🔴 FR-10, ADR-0050 決定1, #1121, IADR-0466 / #1448, IADR-0577: 同じ建玉を売る**判断の手仕舞い**（発注執行の非終端の Close の記録のうち、
// 保護の機構が出したものと、利用者の手仕舞い・維持率割れの自動縮小を除いた残り）を取り消し、照会で終端を確かめる段。
// 損切りの成行を送る 2 つの経路——S1（SoftwareStopExecutor）と保護逆指値ガードの成行手仕舞い（ProtectiveStopGuard）——が、
// **同じ決済の直列化（IADR-0575）の内側で、成行の予約の前に**呼ぶ（IADR-0577 で S1 から移設した。挙動は IADR-0466 のまま）。
//   - 保護の機構が出したもの（取り消さない）: 同じ銘柄・市場・方向の保護記録（状態を問わない）の StopDecisionId・StopOrderId と、
//     試行 1..Attempt+1 の逆指値レグ・成行手仕舞い・S1 の決済の DecisionId。🔴 **保護レグを取り消すと保護そのものを外す。**
//     加えて、同じ銘柄・市場・方向のエントリーの記録から導いた、保護逆指値を張れなかった建玉の成行手仕舞い（試行 1）の DecisionId。
//   - 🔴 #1222, IADR-0515 決定3: 利用者の手仕舞い・維持率割れの自動縮小（記録の出どころが OwnerClose / MaintenanceMarginReduction で、
//     記録の時刻から NettedCloseGrace 以内）は取り消さない（S1 とガードは OwnerCloseNetting が差し引く。#1452, IADR-0578）。**出どころが分からない（null）記録は
//     判断の手仕舞いと同じく取り消す**（損切りを止めない側）。猶予を過ぎて処理中のものも取り消す（約定しない指値で止めない）。
//   - 確かめられない（照会 null・例外）ものは取り消さない（拒否され得るが撃ち直しは続く）。
//     環境の違う記録（照会が恒久的に null）で損切りを永遠に待たせないため、待つのは「生きている」と答えたものだけである。
//   - 取消の後に終端を確かめられなければ AwaitingCancel（送らない）。確定前に送ると、売れる数量を押さえる証券会社では拒否され、
//     押さえない証券会社では判断の手仕舞いも約定して二重に売る（仕様書 20260930_1121 の取消の確認の表）。
//   - 記録は書かない（終端の記録・台帳の押さえの解放は約定追跡の OrderExecuted が行う＝書き手を 1 つに保つ）。出し直さない（IADR-0211 決定3）。
public static class DecisionCloseYield
{
    /// <summary>
    /// 🔴 FR-10, UC-06, ADR-0050 決定1, #1222, IADR-0515 決定4: 利用者の手仕舞い・維持率割れの自動縮小の決済を**取り消さない**のは、
    /// 発注の記録の時刻（<c>ExecutionRecord.ExecutedAt</c>。約定追跡は非終端のあいだこの値を進めない＝発注の時刻）からこの時間のあいだだけ。
    /// 常駐ガードの巡回（30 秒）4 回分。成行（利用者の既定・#847）は通常この間に約定する。
    /// </summary>
    public static readonly TimeSpan NettedCloseGrace = TimeSpan.FromMinutes(2);

    // 保護記録・エントリーの記録を引く上限。保有建玉数上限（既定 3）に対して十分大きい。
    private const int ScanLimit = 500;

    /// <summary>🔴 #1222, IADR-0515 決定3: 出どころが利用者の手仕舞い・維持率割れの自動縮小か。null（分からない）・TradeDecision は false。</summary>
    public static bool IsOwnerOrigin(ExecutionRecord record) =>
        record.ApprovalOrigin is OrderApprovalOrigin.OwnerClose or OrderApprovalOrigin.MaintenanceMarginReduction;

    // 🔴 #1222, IADR-0515 決定3: 取消の段で取り消さずに残す決済か（利用者・自動縮小で、猶予の内）。
    private static bool IsNettedOwnerClose(ExecutionRecord record, DateTimeOffset now) =>
        IsOwnerOrigin(record) && now - record.ExecutedAt < NettedCloseGrace;

    /// <summary>
    /// 同じ建玉を売る判断の手仕舞いを取り消し、照会で終端を確かめる。<paramref name="actor"/> はログの主語（「ソフトウェア逆指値の決済」等）。
    /// </summary>
    public static async Task<DecisionCloseYieldResult> CancelAndConfirmAsync(
        ProtectiveStopOrder stop,
        IBrokerAdapter broker,
        IProtectiveStopOrderStore stops,
        IExecutedOrderStore store,
        IClock clock,
        ILogger logger,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stop);
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(stops);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        List<ExecutionRecord> decisionCloses;
        try
        {
            var mechanicalDecisionIds = new HashSet<Guid>();
            var mechanicalOrderIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in stops.FindRecentFor(stop.Symbol, stop.Market, stop.EntrySide, ScanLimit)
                .Append(stop))
            {
                mechanicalDecisionIds.Add(row.StopDecisionId);
                if (!string.IsNullOrEmpty(row.StopOrderId))
                    mechanicalOrderIds.Add(row.StopOrderId);
                for (var attempt = 1; attempt <= Math.Max(row.Attempt, 0) + 1; attempt++)
                {
                    mechanicalDecisionIds.Add(ProtectiveStopIds.StopDecisionId(row.EntryDecisionId, attempt));
                    mechanicalDecisionIds.Add(ProtectiveStopIds.CloseDecisionId(row.EntryDecisionId, attempt));
                    mechanicalDecisionIds.Add(ProtectiveStopIds.SoftwareCloseDecisionId(row.EntryDecisionId, attempt));
                }
            }

            // 🔴 保護逆指値を張れなかったエントリーの成行手仕舞い（OrderExecutionAppService.CloseUnprotectedPositionAsync。
            // DecisionId＝CloseDecisionId(エントリー, 1)）は、保護記録が無いことがある（承認時の保護の文脈を書けなかった等）。
            // 取り消すと出し直されず、逆指値の無い建玉が残る。エントリーの記録から同じ導出で見分ける。
            foreach (var entry in store.FindRecentOpens(stop.Symbol, stop.Market, stop.EntrySide, ScanLimit))
                mechanicalDecisionIds.Add(ProtectiveStopIds.CloseDecisionId(entry.DecisionId, attempt: 1));

            var now = clock.UtcNow;
            decisionCloses = store.FindPendingCloses(stop.Symbol, stop.Market, stop.CloseSide)
                .Where(r => !mechanicalDecisionIds.Contains(r.DecisionId) && !mechanicalOrderIds.Contains(r.OrderId)
                    && !IsNettedOwnerClose(r, now))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "処理中の判断の手仕舞いを読めませんでした（取り消さずに{Actor}を送ります。証券会社に拒否され得ます）。"
                    + "EntryDecisionId={EntryDecisionId} 銘柄={Symbol}",
                actor, stop.EntryDecisionId, stop.Symbol);
            return DecisionCloseYieldResult.None;
        }

        var result = DecisionCloseYieldResult.None;
        foreach (var record in decisionCloses)
        {
            var live = await TryGetOrderAsync(broker, logger, record.OrderId, cancellationToken).ConfigureAwait(false);
            if (live is null)
                continue; // 確かめられない。取り消さない（是正前と同じ）。

            if (OrderStatusLifecycle.IsTerminal(live.Status))
            {
                // 既に終わっていた（記録は約定追跡が書くまで古い）。約定が記録より進んでいれば建玉が動いている。
                if (live.FilledQuantity > record.FilledQuantity && result == DecisionCloseYieldResult.None)
                    result = DecisionCloseYieldResult.Changed;
                continue;
            }

            // 🔴 FR-10, FR-09, UC-06, #1395, IADR-0560: **取消を送る前に**「保護の機構が取消を求めた」印を記録へ書く。
            // 約定追跡がこの取消を観測して OrderExecuted(Cancelled) を出すとき、印を CancelledBySoftwareStop へ写し、
            // リスク管理は「手仕舞いが約定せず終了・建玉が残っている」を出さない（建玉は直後の成行が決済する）。
            // #1448, IADR-0577: ガードの成行手仕舞いも同じ印を書く（列の名前は S1 のままだが、意味は「成行に置き換えるために取り消した」）。
            // 送った後に書くと、その間に約定追跡が取消を観測したとき印の無い終端になる。
            // 印の書き込みの失敗は取消を止めない（発注の挙動は変えない。印が無い＝従来どおり通知が出る側）。
            try
            {
                store.MarkSoftwareStopCancelRequested(record.OrderId, clock.UtcNow);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "判断の手仕舞いに「保護の機構が取消を求めた」印を書けませんでした（取消は送ります。"
                        + "この取消には「手仕舞いが約定せず終了」の通知が出ることがあります）。"
                        + "{Actor} EntryDecisionId={EntryDecisionId} 取消対象 DecisionId={DecisionId} OrderId={OrderId}",
                    actor, stop.EntryDecisionId, record.DecisionId, record.OrderId);
            }

            var cancelFailed = false;
            try
            {
                await broker.CancelOrderAsync(record.OrderId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                cancelFailed = true;
                // 取消に失敗しても、既に終端（約定し切った等）なら次の照会で分かる。
                logger.LogWarning(ex,
                    "{Actor}の前に、判断の手仕舞いの取消に失敗しました（状態を照会します）。"
                        + "EntryDecisionId={EntryDecisionId} 取消対象 DecisionId={DecisionId} OrderId={OrderId}",
                    actor, stop.EntryDecisionId, record.DecisionId, record.OrderId);
            }

            var after = await TryGetOrderAsync(broker, logger, record.OrderId, cancellationToken).ConfigureAwait(false);

            // 🔴 #1395, IADR-0560 決定1（2026-10-20 追記）: 取消が失敗し、注文がまだ終わっていない（照会できない・非終端）なら印を戻す。
            // 残すと、後で利用者が取り消したときの「手仕舞いが約定せず終了」まで黙る（主体は保護の機構ではない）。
            // 次の巡回で取り消し直し、そのときに印を書き直す。失敗しても板で取消が効いて終端していれば印は残す。
            if (cancelFailed && (after is null || !OrderStatusLifecycle.IsTerminal(after.Status)))
                ClearCancelMark(store, logger, stop, record);
            if (after is null || !OrderStatusLifecycle.IsTerminal(after.Status))
            {
                logger.LogWarning(
                    "{Actor}を送りません（同じ建玉を売る判断の手仕舞いの取消がまだ確定していません。次の巡回で確かめ直します）。"
                        + "EntryDecisionId={EntryDecisionId} 銘柄={Symbol} 取消対象 DecisionId={DecisionId} OrderId={OrderId} 数量={Quantity}",
                    actor, stop.EntryDecisionId, stop.Symbol, record.DecisionId, record.OrderId, record.Quantity);
                result = DecisionCloseYieldResult.AwaitingCancel;
                continue;
            }

            logger.LogWarning(
                "{Actor}の前に、同じ建玉を売る判断の手仕舞いを取り消しました（損切りを止めないため。出し直しはしません）。"
                    + "EntryDecisionId={EntryDecisionId} 銘柄={Symbol} 取消した DecisionId={DecisionId} OrderId={OrderId} 数量={Quantity} "
                    + "約定済み={Filled} 状態={Status}",
                actor, stop.EntryDecisionId, stop.Symbol, record.DecisionId, record.OrderId, record.Quantity,
                Math.Max(record.FilledQuantity, after.FilledQuantity), after.Status);
            if (result == DecisionCloseYieldResult.None)
                result = DecisionCloseYieldResult.Changed;
        }

        return result;
    }

    // 🔴 #1395, IADR-0560: 「保護の機構が取消を求めた」印を戻す。戻せなくても取消・決済は止めない（印が残る側の残余は IADR-0560）。
    private static void ClearCancelMark(IExecutedOrderStore store, ILogger logger, ProtectiveStopOrder stop, ExecutionRecord record)
    {
        try
        {
            store.ClearSoftwareStopCancelRequested(record.OrderId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "取消に失敗した判断の手仕舞いの「保護の機構が取消を求めた」印を戻せませんでした（この注文が後で取り消されても"
                    + "「手仕舞いが約定せず終了」が出ないことがあります）。EntryDecisionId={EntryDecisionId} DecisionId={DecisionId} OrderId={OrderId}",
                stop.EntryDecisionId, record.DecisionId, record.OrderId);
        }
    }

    internal static async Task<BrokerOrder?> TryGetOrderAsync(
        IBrokerAdapter broker, ILogger logger, string orderId, CancellationToken cancellationToken)
    {
        try
        {
            return await broker.GetOrderAsync(orderId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "処理中の判断の手仕舞いの状態を照会できません。OrderId={OrderId}", orderId);
            return null;
        }
    }
}
