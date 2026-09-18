using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;

// FR-10, UC-02, #331, IADR-0210 決定4: 保護逆指値の失効検知・再発注・残存取消の巡回。
// 業務フロー 02「逆指値の未受理・失効を検知 → 再発注、不可なら成行で手仕舞い」の実装であり、
// **逆指値なしの建玉を持たない**（失効側）と**建玉なき逆指値を残さない**（反対建玉の防止）の両方向を守る。
//
// fail-safe:
//   - 逆指値の照会不能（null）・建玉の照会不能（null）→ **据え置き**（不明を「無い」と取り違えない。
//     IADR-0118 と同じ規律。誤った再発注・誤った取消はどちらも実弾で実損になる）。
//   - 1 件の失敗でバッチ全体を止めない（件数集計・OrderFillPoller と同じ流儀）。
//
// 発行（イベントの Publish）は Worker 層（ProtectiveStopGuardService）が担う。
//
// FR-10, ADR-0040 決定1（S1）, #820, IADR-0344 決定6・追記(4): ソフトウェア逆指値（S1）の行は**ブローカーの注文照会をしない**
// （ブローカーに注文が無い）。到達済みなら SoftwareStopExecutor で決済を再試行し、未到達なら
// **残保護数量が 0 になったときだけ**完了する（純額や他手法の主張では完了させない。BLK-1・B3）。
// S0 行の建玉残は S1 行の残保護数量を差し引いて判定する（S1 行が無い構成では差し引く量が 0 で、S0 の判定は従来と同一）。
//
// 🔴 **評価の順序は S0 → 到達済み S1 → 未到達 S1**（IADR-0344 追記(4) 決定8）。
//   - S0 を先に評価するのは、S1 の予算が「**この巡回の後も生きている S0**」の数量を引くべきだからである。
//     約定・取消で役目を終えた S0 の数量を引くと、建玉が残っているのに S1 の持ち分が 0 になり保護が黙って消える（B3）。
//   - 到達済みを未到達より先にするのは、未到達の行が先に持ち分を取って**到達した行が決済できなくなる**のを防ぐため
//     （#820 の 4 巡目監査 BLK-4）。
public sealed class ProtectiveStopGuard(
    IBrokerAdapter broker,
    IBrokerPositionSource positions,
    IProtectiveStopOrderStore stops,
    IExecutedOrderStore store,
    IClock clock,
    OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops.SoftwareStopExecutor? softwareStops = null)
{
    public async Task<ProtectiveStopGuardResult> RunOnceAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var scanned = stops.FindActive(batchSize);
        if (scanned.Count == 0)
            return ProtectiveStopGuardResult.Empty;

        // 🔴 #820 の 4 巡目監査, IADR-0344 追記(4): **巡回の最初に S1 行の残保護数量を確定する**。
        // S0 行の建玉残は S1 行の残保護数量を差し引いて判定するため、確定前の（主張 0 の）S1 行があると
        // S0 が「その建玉は自分のもの」と誤認し、#826 項目 3 が 1 巡回ぶん効かない。
        var active = ProtectiveStopNetting.ConfirmEntryFills(scanned, stops, store, clock.UtcNow);

        // 建玉は 1 巡回につき 1 回照会する。null（照会不能）なら巡回ごと据え置く——建玉不明のまま
        // 「消滅した」と誤認して逆指値を取り消すと、直後の失効側の保護が消える。
        var snapshot = await positions.GetPositionsAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
            return new ProtectiveStopGuardResult(active.Count, 0, 0, 0, 0, active.Count, 0, []);

        var events = new List<object>();

        // 🔴 #820 の 4 巡目監査, IADR-0344 追記(4) 決定4: 外部要因（人手決済・強制決済・S0 の逆指値の約定）による減少を
        // **この巡回で一度だけ**割り当てて保存する。S0 の取消判定より前に行う——判定は「割り当て後の主張」を見るべきで、
        // 割り当て前の（消えた建玉をまだ主張している）値で判定すると、生きている逆指値を取り消してしまう（BLK-1）。
        //
        // 🔴 #820 の 5 巡目監査・7 巡目監査, IADR-0344 追記(5)・追記(7): **ここだけが「観測」である**（群につき 1 巡回 1 回）。
        // 観測の連続回数を数え、確定（2 巡回連続）したぶんだけ帳簿を減らして通知するのがこの呼び出しである。
        // **未確定のあいだ帳簿は動かない**ため、建玉が戻ったときに書き戻す（復元する）経路は存在しない。
        foreach (var (symbol, market, entrySide) in active
            .Select(s => (s.Symbol, s.Market, s.EntrySide))
            .Distinct())
        {
            ProtectiveStopNetting.ReconcileShares(
                symbol, market, entrySide, snapshot, active, stops, store, clock.UtcNow,
                observing: true, events: events);
        }

        active = stops.FindActive(batchSize);

        var stillActive = 0;
        var completed = 0;
        var replaced = 0;
        var closedOut = 0;
        var unknown = 0;
        var failed = 0;

        // #820 の 4 巡目監査, IADR-0344 追記(4) 決定8: S0 → 到達済み S1 → 未到達 S1 の順に評価する（理由は冒頭の注記）。
        foreach (var stop in active
            .OrderBy(s => s.IsSoftwareStop ? (s.TriggeredAt is null ? 2 : 1) : 0)
            .ThenBy(s => s.CreatedAt)
            .ThenBy(s => s.EntryDecisionId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var outcome = stop.IsSoftwareStop
                    ? await EvaluateSoftwareStopAsync(stop, snapshot, events, cancellationToken).ConfigureAwait(false)
                    : await EvaluateAsync(stop, snapshot, active, events, cancellationToken).ConfigureAwait(false);
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

    // #820, IADR-0344 決定6・追記(4): ソフトウェア逆指値の巡回（ブローカーの注文照会をしない）。
    private async Task<Outcome> EvaluateSoftwareStopAsync(
        ProtectiveStopOrder stop,
        IReadOnlyList<BrokerPositionSnapshot> snapshot,
        List<object> events,
        CancellationToken cancellationToken)
    {
        if (stop.TriggeredAt is not null)
        {
            // 到達済みで決済できていない（接続断・取消待ち・拒否の途中）。決済を再試行する。
            if (softwareStops is null)
                return Outcome.Unknown;

            var outcome = await softwareStops
                .TryCloseAsync(stop, snapshot, cancellationToken)
                .ConfigureAwait(false);
            if (outcome.Event is not null)
                events.Add(outcome.Event);
            return outcome.Kind switch
            {
                OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops.SoftwareStopCloseKind.Completed =>
                    outcome.Event is { Outcome: SoftwareStopOutcome.ClosePlaced } ? Outcome.ClosedOut : Outcome.Completed,
                // 部分的に決済した行は Active のまま残る（残りは次の巡回で決済する。BLK-3）。
                OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops.SoftwareStopCloseKind.PartiallyClosed =>
                    Outcome.ClosedOut,
                OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops.SoftwareStopCloseKind.Rejected => Outcome.StillActive,
                _ => Outcome.Unknown,
            };
        }

        // 🔴 #820 の 4 巡目監査, IADR-0344 追記(4): **残保護数量が 0 になったときだけ**保護を外す。
        // 純額や他手法の主張で外すと、建玉が残っているのに保護がゼロになる（3 巡目監査 B3）か、
        // 自分の建玉を失った行が不死化して生きている S0 の逆指値を毎巡回取り消す（4 巡目監査 BLK-1）。
        // 減った建玉の観測と確定は巡回の先頭の ReconcileShares が済ませており、保存済みである。
        var current = stops.Find(stop.EntryDecisionId) ?? stop;

        // 未確定（エントリーの発注記録が無い・まだ終端でない）＝これから約定し得る。建玉が 0 でも完了しない。
        if (current.RemainingProtected is not { } remaining)
            return Outcome.StillActive;

        // 🔴 #820 の 5 巡目監査, IADR-0344 追記(5)・追記(7): 外部要因の減少が**まだ確定していない**あいだは完了させない。
        // 建玉照会は 1 巡回だけ過少に返り得る——1 回の観測で行を閉じると、次の巡回で建玉が戻っても取り返せない。
        if (current.HasUnconfirmedExternalReduction)
            return Outcome.StillActive;

        if (remaining <= 0)
        {
            // 建玉が生じなかった、または外部要因で自分の建玉が消えた。保護の役目を終える（ブローカーに取り消す注文は無い）。
            MarkCompleted(current);
            return Outcome.Completed;
        }

        return Outcome.StillActive;
    }

    private async Task<Outcome> EvaluateAsync(
        ProtectiveStopOrder stop,
        IReadOnlyList<BrokerPositionSnapshot> snapshot,
        IReadOnlyList<ProtectiveStopOrder> active,
        List<object> events,
        CancellationToken cancellationToken)
    {
        var order = await broker.GetOrderAsync(stop.StopOrderId, cancellationToken).ConfigureAwait(false);
        if (order is null)
        {
            // 照会不能＝不明。据え置いて次回巡回で再試行する（「無い」と取り違えない）。
            return Outcome.Unknown;
        }

        // #820, IADR-0344 決定6・追記(4) 決定10: 手法の異なる Active 行（S1）の**残保護数量**を差し引く（無ければ従来と同一）。
        var remaining = ProtectiveStopNetting.RemainingPositionFor(stop, snapshot, active);

        if (OrderStatusLifecycle.IsPending(order.Status))
        {
            if (remaining > 0)
                return Outcome.StillActive; // 正常: 建玉あり・逆指値滞留中。

            // 🔴 #820 の 5 巡目監査, IADR-0344 追記(5)・追記(7): 建玉残が 0 になった理由が**まだ確定していない外部要因**なら据え置く。
            // 建玉照会は 1 巡回だけ過少に返り得る——その 1 回で**ブローカーに実在する生きた逆指値を取り消す**のは
            // 無音かつ不可逆な破壊である。確定（2 巡回連続の観測）を待ってから取り消す。
            if (stop.HasUnconfirmedExternalReduction)
                return Outcome.Unknown;

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
            // 建玉残が 0 の理由が未確定の外部要因なら据え置く（確定しなければ主張は減らないまま再発注へ回る。追記(7)）。
            if (stop.HasUnconfirmedExternalReduction)
                return Outcome.Unknown;

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
                newStop = null; // 再発注不可→手仕舞いへ。
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
                    // #820 の 4 巡目監査, IADR-0344 追記(4): 再発注で覆う数量が縮んだら、S0 の主張もその数量へ揃える
                    // （S1 側の予算はこの主張を引くため、古い数量のままだと S1 の持ち分が足りなくなる）。
                    RemainingProtected = quantity,
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
            try
            {
                var closeDecisionId = ProtectiveStopIds.CloseDecisionId(stop.EntryDecisionId, attempt);
                var closeOrder = await protective
                    .PlaceMarketOrderAsync(closeIntent, closeDecisionId, cancellationToken)
                    .ConfigureAwait(false);
                var closedAt = clock.UtcNow;

                store.Save(new ExecutionRecord(
                    closeDecisionId, closeOrder.OrderId, stop.Symbol, stop.Market, stop.CloseSide,
                    stop.ProductType, PositionEffect.Close, quantity, closeIntent.Price,
                    closeOrder.FilledQuantity, closeOrder.AveragePrice, closeOrder.Status,
                    SlippageCalculator.Compute(closeIntent.Price, closeOrder.AveragePrice, stop.CloseSide), closedAt));

                MarkCompleted(stop);
                events.Add(new ProtectiveStopCoverageLost(
                    stop.EntryDecisionId, stop.Symbol, stop.Market,
                    ProtectiveStopLossCause.LapsedInFlight, ProtectiveStopRemediation.PositionClosed,
                    quantity, closeDecisionId, closeIntent, closedAt));
                return Outcome.ClosedOut;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 手仕舞いも失敗: 記録は Active のまま残し（次回巡回で再試行）、人手対応を Critical で求める。
            }
        }

        events.Add(new ProtectiveStopCoverageLost(
            stop.EntryDecisionId, stop.Symbol, stop.Market,
            ProtectiveStopLossCause.LapsedInFlight, ProtectiveStopRemediation.None,
            quantity, CloseDecisionId: null, CloseIntent: null, clock.UtcNow));
        return Outcome.ClosedOut;
    }

    private void MarkCompleted(ProtectiveStopOrder stop) =>
        stops.Save(stop with { State = ProtectiveStopState.Completed, UpdatedAt = clock.UtcNow });

    // 建玉スナップショットから「エントリー方向の残数量」を求める。数量は符号付き（+ロング/−ショート・IADR-0118）。
    // ロング建玉（Buy 建て）は正の数量、ショート建玉（Sell 建て）は負の数量の絶対値が残である。
    public static int RemainingPositionFor(ProtectiveStopOrder stop, IReadOnlyList<BrokerPositionSnapshot> snapshot) =>
        ProtectiveStopNetting.DirectionalNet(stop, snapshot);

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
