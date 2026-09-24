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
    IOrderReservationStore reservations,
    IClock clock,
    ILogger<ProtectiveStopGuard>? logger = null,
    HeldCloseNotificationTracker? heldCloseNotifications = null,
    OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops.SoftwareStopExecutor? softwareStops = null,
    CloseRejectionTracker? closeRejections = null)
{
    /// <summary>
    /// 🔴 #857, IADR-0369 決定3: <b>確認できた拒否</b>で終わった成行手仕舞いを撃ち直す上限
    /// （同じ理由で拒否され続ける成行を 30 秒ごとに送り続けない）。回数は S1
    /// （<c>SoftwareStopExecutor.MaxCloseAttemptsPerTrigger</c>）と同じ 3 だが、S1 の上限は到達 1 回あたりで
    /// 次の到達で自ら再武装するのに対し、こちらは<b>保護記録ごとの累計で再武装が無い</b>
    /// （戻るのは再起動・逆指値の再発注の成功・手仕舞いの受理だけ。IADR-0369 の 2026-09-24 追記・#941 で「約定」を「受理」へ是正）。
    /// #938: 記録が完了したときも数えを捨てる（完了した記録へ撃ち直すことは無い。<c>MarkCompleted</c>）。
    /// <b>上限に達しても記録は閉じない</b>（閉じると巡回から外れ、無保護の建玉が無音で残る）。
    /// </summary>
    public const int MaxConfirmedCloseRejections = 3;

    private readonly ILogger _logger = logger ?? NullLogger<ProtectiveStopGuard>.Instance;

    // #848, IADR-0117（改定 9）: 据え置き中の成行手仕舞いを「このプロセスがいつ通知したか」の記憶。本番は singleton を
    // 渡す（ガード自体は巡回ごとに作られる scoped）。省略時は本インスタンスの寿命で持つ（単体テスト用）。
    private readonly HeldCloseNotificationTracker _heldCloseNotifications =
        heldCloseNotifications ?? new HeldCloseNotificationTracker();

    // #857, IADR-0369: 確認できた拒否の数え（撃ち直しの上限）と、上限に達した後の再通知の記憶。上と同じ理由で singleton。
    private readonly CloseRejectionTracker _closeRejections = closeRejections ?? new CloseRejectionTracker();

    public async Task<ProtectiveStopGuardResult> RunOnceAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 🔴 #938（PR #916 監査 F4）, IADR-0369（2026-09-25 追記）: **巡回対象が 0 件で戻るより前に**、ガードの外で
        // 完了した記録（乖離の取り込み・IADR-0370）の記憶を捨てる。最後の Active 行が外で完了した後も漏れないように先に行う。
        ForgetTrackersOfFinishedRecords();

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
        var closeRejected = 0;
        var closeFailed = 0;

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
                    // #857, IADR-0369: 「確認できた拒否」は不明でも完了でもない。件数も混ぜない。
                    case Outcome.CloseRejected: closeRejected++; break;
                    // #938（PR #916 監査 F5）: 確実に未発注の手仕舞い失敗は「手仕舞い」に数えない。
                    case Outcome.CloseFailed: closeFailed++; break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
            }
        }

        // 🔴 #820 の 10 巡目監査, IADR-0344 追記(9) 決定3: **どの保護記録も主張していない建玉**を知らせる。
        // 武装の前提条件は武装の時点しか見ないが、材料（純額と保護記録）はガードが毎巡回持っている。
        // 評価の**後**に行う——この巡回で出した決済のレグが記録済みになっているため、
        // 「送信済みで未反映の決済」を数え落とさない（早まった警告を出さない）。
        // 検知だけであり、建玉を売らず・記録も作らず・主張も動かさない。
        //
        // 🔴 **#820 の 11 巡目監査, IADR-0344 追記(10): ここへ来るのは Active な行が 1 件以上ある巡回だけである。**
        // 上の早期 return（巡回対象ゼロなら建玉を照会しない）は無駄な OpenD 往復を避けるための既存の規律であり、
        // 壊さない。そのため「受理後に 0 約定で取り消された決済の残り」——決済を送った行はその時点で完了し
        // 巡回の対象に残らない——が**その口座で唯一の S1 の痕跡**なら、検知は一度も走らない（監査の PROBE1）。
        // 気づける経路はその銘柄への**次の武装の見送り**である。塞ぐには建玉観測の常駐
        //（Hosted/BrokerPositionSnapshotService。既定 600 秒・無条件に照会する）へ相乗りする——**追随は #880**。
        //
        // 🔴 **#820 の 12 巡目監査（NB-12-5）: ここは保護記録を「照会の後」に引き直している**（下の FindActive）。
        // つまりガードは**照会前の主張を持ち越さない**。発注側（OrderExecutionAppService）は持ち越すため、
        // 「ガードと同じ順序だから安全」という類比は**片手落ちである**——発注側は照会の前と後の両方で主張を読み、
        // **小さい方**を採ることで両方向の窓を閉じている（IADR-0344 追記(11) 決定1）。
        ProtectiveStopNetting.DetectUnattributedPositions(
            snapshot, stops.FindActive(batchSize), stops, store, clock.UtcNow, events);

        return new ProtectiveStopGuardResult(
            active.Count, stillActive, completed, replaced, closedOut, unknown, failed, events, closeRejected, closeFailed);
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

        // 🔴 FR-10, FR-11, UC-06, #848, IADR-0117（2026-09-19 追記・改定 7）:
        // **この試行の成行手仕舞いレグの痕跡を、逆指値の再発注より「前」に見る。**
        // closeDecisionId は決定的（手仕舞いが完了しない限り stop.Attempt は進まない＝巡回をまたいで同じ値）。
        var closeDecisionId = ProtectiveStopIds.CloseDecisionId(stop.EntryDecisionId, attempt);

        // (a) 発注結果の記録が既にある: 送信後に行の更新だけが失われた窓、または突合（IADR-0074）が
        //     Placed と解決した後。**新しい注文は出さず**、記録で保護の完了を確定する。
        var recordedClose = store.FindByDecisionId(closeDecisionId);
        if (recordedClose is not null)
        {
            // 🔴 #857, IADR-0369 決定2: **記録の側でも状態を見る。** 送信直後に行の更新だけが失われた窓では、
            // 同じ CloseDecisionId の**拒否された記録**が残る。ここを塞がないと、次の巡回が
            // 「記録があるから手仕舞い済み」と読んで完了させ、建玉が巡回対象から外れる（本 issue の中心）。
            if (OrderStatusLifecycle.AbandonsUnfilledRemainder(recordedClose.Status))
                return RejectedClose(stop, recordedClose.Quantity, attempt, closeDecisionId, recordedClose.Status, events);

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
                    // #820 の 4 巡目監査, IADR-0344 追記(4): 再発注で覆う数量が縮んだら、S0 の主張もその数量へ揃える
                    // （S1 側の予算はこの主張を引くため、古い数量のままだと S1 の持ち分が足りなくなる）。
                    RemainingProtected = quantity,
                    Attempt = attempt,
                    UpdatedAt = now,
                });

                // #857, IADR-0369 決定3: 保護を張り直せた＝手仕舞いを撃ち直す理由が消えた。数えを 0 へ戻す
                //（次にまた失効して拒否されたら、改めて 3 回試す）。
                _closeRejections.Forget(stop.EntryDecisionId);
                // #938: この試行の手仕舞いレグの据え置き（突合が解放した後にここへ来る）は、試行が進むともう引かれない。
                _heldCloseNotifications.Forget(closeDecisionId);

                events.Add(new ProtectiveStopPlaced(
                    stop.EntryDecisionId, stopDecisionId, newStop.OrderId, closeIntent, stop.TriggerPrice, attempt, now));
                return Outcome.Replaced;
            }
        }

        // 再発注できない: 成行で手仕舞う（逆指値なしの建玉を持たない）。
        if (protective is not null)
        {
            // 🔴 #857, IADR-0369 決定3: **確認できた拒否**が上限に達している行へは、この巡回で成行を送らない
            // （同じ理由で拒否され続ける成行を 30 秒ごとに重ねない）。**記録は閉じない**——閉じると巡回から
            // 外れて無保護の建玉が無音で残る。据え置きと同じ作法で 1 時間ごと（と再起動後）に鳴らし続ける。
            if (_closeRejections.Count(stop.EntryDecisionId) >= MaxConfirmedCloseRejections)
                return HoldRejectedClose(stop, quantity, events);

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

                // 🔴 #857, IADR-0369 決定1: **確認できた拒否**（未約定残を二度と約定させない終端が「返った」）を
                // 「手仕舞い済み」と扱わない。予約は**確定する**——結果は分かっているので据え置く理由が無い
                //（据え置くと、確定した事実を「不明」として扱うことになる。改定 7 の据え置きは不明のためのものである）。
                reservations.MarkCompleted(closeDecisionId, closeOrder.OrderId, closedAt);

                if (OrderStatusLifecycle.AbandonsUnfilledRemainder(closeOrder.Status))
                    return RejectedClose(stop, quantity, attempt, closeDecisionId, closeOrder.Status, events);

                return CompleteAsClosed(stop, quantity, closeDecisionId, closeIntent, events);
            }

            // 手仕舞いも失敗（確実に未発注）: 記録は Active のまま残し（次回巡回で再試行）、人手対応を Critical で求める。
        }

        events.Add(new ProtectiveStopCoverageLost(
            stop.EntryDecisionId, stop.Symbol, stop.Market,
            ProtectiveStopLossCause.LapsedInFlight, ProtectiveStopRemediation.None,
            quantity, CloseDecisionId: null, CloseIntent: null, clock.UtcNow));
        // 🔴 #938（PR #916 監査 F5）, IADR-0369（2026-09-25 追記）: **ClosedOut（解消した）を返さない。** 手仕舞えておらず
        // 記録は Active のまま（次の巡回で撃ち直す）。決定 5 が拒否を別枠にしたのと同じ理由で、件数でも混ぜない。
        return Outcome.CloseFailed;
    }

    // 成行手仕舞いレグが（今回の送信・以前の送信の記録・突合の解決のいずれかで）確定した: 保護を完了し、
    // 手仕舞いレグを台帳へ結線する（AppendApproval は DecisionId で冪等。再発行しても二重計上しない）。
    private Outcome CompleteAsClosed(
        ProtectiveStopOrder stop, int quantity, Guid closeDecisionId, OrderIntent closeIntent, List<object> events)
    {
        // 解決した。以後は再通知しない・撃ち直しの数えも捨てる（#857）。#938: 捨てるのは MarkCompleted が行う
        // （記録を完了させる全経路で同じ出口を通る）。
        MarkCompleted(stop);
        events.Add(new ProtectiveStopCoverageLost(
            stop.EntryDecisionId, stop.Symbol, stop.Market,
            ProtectiveStopLossCause.LapsedInFlight, ProtectiveStopRemediation.PositionClosed,
            quantity, closeDecisionId, closeIntent, clock.UtcNow));
        return Outcome.ClosedOut;
    }

    // 🔴 FR-10, FR-11, UC-02, UC-06, #857, IADR-0369 決定1: 成行手仕舞いが**確認できる形で拒否された**
    // （`Rejected` / `Cancelled` / `Expired` が返った＝未約定残は二度と約定しない）。**建玉は残っている。**
    //   - 記録は **Active のまま**（完了させると、逆指値なしの建玉が巡回対象から外れて無音で残る＝本 issue）。
    //   - **試行番号だけを進める**（次の巡回は新しい CloseDecisionId で改めて評価する。S1 の Settle と同じ形）。
    //     残保護数量は動かさない——減らしたのは建玉ではない。
    //   - 通知は **PositionClosed ではなく CloseRejected**。🔴 **CloseIntent は運ばない**（送った成行は生きて
    //     いないため、取引台帳に処理中の決済として在庫を押さえさせない）。CloseDecisionId だけ相関のために載せる。
    private Outcome RejectedClose(
        ProtectiveStopOrder stop, int quantity, int attempt, Guid closeDecisionId, OrderStatus status,
        List<object> events)
    {
        var now = clock.UtcNow;
        var rejections = _closeRejections.Record(stop.EntryDecisionId);
        // #938: この手仕舞いレグは拒否と確定した（据え置いていたなら突合が解決した）。試行が進むとこのキーはもう引かれない。
        _heldCloseNotifications.Forget(closeDecisionId);

        stops.Save(stop with { Attempt = attempt, UpdatedAt = now });

        _logger.LogError(
            "保護逆指値ガード: 成行手仕舞いが拒否されました（確認できた拒否・状態 {Status}・{Rejections} 回目）。"
            + "**建玉は残っています。**手仕舞い済みとしては扱わず、記録は Active のまま次の巡回で再評価します。"
            + "{Next} EntryDecisionId={EntryDecisionId} CloseDecisionId={CloseDecisionId} 銘柄={Symbol} 数量={Quantity}",
            status, rejections,
            rejections >= MaxConfirmedCloseRejections
                ? $"撃ち直しの上限（{MaxConfirmedCloseRejections} 回）に達したため、以後は成行を送りません（通知は続けます）。"
                : "次の巡回で撃ち直します。",
            stop.EntryDecisionId, closeDecisionId, stop.Symbol, quantity);

        _closeRejections.MarkNotified(stop.EntryDecisionId, now);
        events.Add(new ProtectiveStopCoverageLost(
            stop.EntryDecisionId, stop.Symbol, stop.Market,
            ProtectiveStopLossCause.LapsedInFlight, ProtectiveStopRemediation.CloseRejected,
            quantity, closeDecisionId, CloseIntent: null, now));
        return Outcome.CloseRejected;
    }

    // 🔴 #857, IADR-0369 決定3: 撃ち直しの上限に達した行。**この巡回では成行を 1 本も送らない**が、
    // **無音にしない**（未通知なら即座に、以後は 1 時間ごと）。CloseDecisionId は null ——
    // この巡回では手仕舞いレグを 1 本も送っていないためである（送っていない ID を載せない）。
    private Outcome HoldRejectedClose(ProtectiveStopOrder stop, int quantity, List<object> events)
    {
        var now = clock.UtcNow;
        _logger.LogWarning(
            "保護逆指値ガード: 成行手仕舞いが {Max} 回続けて拒否されたため、この巡回では送りません。"
            + "**逆指値なしの建玉が残っています。**証券会社の画面で建玉を確認してください: "
            + "EntryDecisionId={EntryDecisionId} 銘柄={Symbol} 数量={Quantity}",
            MaxConfirmedCloseRejections, stop.EntryDecisionId, stop.Symbol, quantity);

        if (!_closeRejections.IsRenotifyDue(stop.EntryDecisionId, now))
            return Outcome.CloseRejected;

        _closeRejections.MarkNotified(stop.EntryDecisionId, now);
        events.Add(new ProtectiveStopCoverageLost(
            stop.EntryDecisionId, stop.Symbol, stop.Market,
            ProtectiveStopLossCause.LapsedInFlight, ProtectiveStopRemediation.CloseRejected,
            quantity, CloseDecisionId: null, CloseIntent: null, now));
        return Outcome.CloseRejected;
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
        _heldCloseNotifications.MarkNotified(closeDecisionId, stop.EntryDecisionId, now);
    }

    private void LogHeldClose(ProtectiveStopOrder stop, Guid closeDecisionId) =>
        _logger.LogWarning(
            "保護逆指値ガード: 成行手仕舞いは発注に着手済みで結果が未確定です（予約あり・記録なし）。"
            + "逆指値の再発注も成行も重ねずに据え置きます: EntryDecisionId={EntryDecisionId} "
            + "CloseDecisionId={CloseDecisionId} 銘柄={Symbol}",
            stop.EntryDecisionId, closeDecisionId, stop.Symbol);

    // 🔴 #938（PR #916 監査 F4）, IADR-0369（2026-09-25 追記）: **記録を完了させる出口はここ 1 つ**であり、ここでプロセス内の
    // 記憶（拒否の数えと通知時刻・据え置きの通知時刻）も捨てる。従来は Replaced と CompleteAsClosed でしか捨てず、
    // 建玉消滅→取消・逆指値の Filled・失効かつ建玉 0 の完了では再起動まで残った（誤動作ではないが辞書が単調に増える）。
    // 保存の後に捨てる——保存が例外で落ちたら記録は Active のままなので、記憶も残すのが正しい。
    private void MarkCompleted(ProtectiveStopOrder stop)
    {
        stops.Save(stop with { State = ProtectiveStopState.Completed, UpdatedAt = clock.UtcNow });
        _closeRejections.Forget(stop.EntryDecisionId);
        _heldCloseNotifications.ForgetEntry(stop.EntryDecisionId);
    }

    // 🔴 #938, IADR-0369（2026-09-25 追記）: **ガードの外で完了した記録**（乖離の取り込み・ProtectiveStopDriftAdopter）の記憶を捨てる。
    // 記憶に載っている保護記録だけを引き直す（平常時は記憶が空で、照会は 1 回も起きない）。
    //   - Active の記録がある → 残す（撃ち直しの上限・再通知の間隔はまだ要る）。
    //   - 行が無い（null）・Completed → 捨てる。null は照会が成功して行が無いという答えである（ストアは行を消さない）。
    //   - 🔴 **引き直しが例外で失敗 → 捨てない（原則 A: 分からないは「無い」ではない）。** 捨てると拒否の数えが 0 へ戻り、
    //     まだ Active かもしれない記録へ成行を最大 3 本撃ち直す側へ倒れる。次の巡回で引き直す。
    private void ForgetTrackersOfFinishedRecords()
    {
        var tracked = _closeRejections.TrackedEntryDecisionIds
            .Union(_heldCloseNotifications.TrackedEntryDecisionIds)
            .ToList();
        foreach (var entryDecisionId in tracked)
        {
            ProtectiveStopOrder? record;
            try
            {
                record = stops.Find(entryDecisionId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "保護逆指値ガード: 記憶している保護記録を引き直せませんでした（不明）。記憶は捨てずに次の巡回で引き直します: "
                    + "EntryDecisionId={EntryDecisionId}",
                    entryDecisionId);
                continue;
            }

            if (record is { State: ProtectiveStopState.Active })
                continue;

            _closeRejections.Forget(entryDecisionId);
            _heldCloseNotifications.ForgetEntry(entryDecisionId);
        }
    }

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

        /// <summary>
        /// #857, IADR-0369: 成行手仕舞いが<b>確認できた拒否</b>で終わった（または上限に達して送らなかった）。
        /// <b>Unknown（不明）でも ClosedOut（解消した）でもない</b>——事実は確定していて、建玉が残っている。
        /// </summary>
        CloseRejected,

        /// <summary>
        /// #938（PR #916 監査 F5）, IADR-0369（2026-09-25 追記）: 成行手仕舞いも<b>確実に未発注</b>で失敗した
        /// （または発注先に成行の能力が無い。Remediation=None）。記録は Active のまま次の巡回で撃ち直す。
        /// <b>ClosedOut（解消した）ではない</b>——手仕舞えていない建玉を「手仕舞い」の件数に入れない。
        /// </summary>
        CloseFailed,
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
    IReadOnlyList<object> Events,
    // #857, IADR-0369: 成行手仕舞いが**確認できた拒否**で終わった件数（建玉が残っている）。
    // 🔴 Unknown（不明）にも ClosedOut（解消した）にも混ぜない——混ぜると、可観測性の上でも
    // 「確実に約定していない」と「どうなったか分からない」の区別が消える。**末尾へ足す**（既存の位置を動かさない）。
    int CloseRejected = 0,
    // #938（PR #916 監査 F5）, IADR-0369（2026-09-25 追記）: 成行手仕舞いも**確実に未発注**で失敗した件数（Remediation=None・
    // 建玉が残っている）。ClosedOut（解消した）に混ぜない。決定 5 と同じく**末尾へ足す**（既存の位置を動かさない）。
    int CloseFailed = 0)
{
    public static readonly ProtectiveStopGuardResult Empty = new(0, 0, 0, 0, 0, 0, 0, []);
}
