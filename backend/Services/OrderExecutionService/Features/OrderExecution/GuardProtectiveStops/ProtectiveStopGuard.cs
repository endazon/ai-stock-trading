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
//   - 🔴 #853, IADR-0210（2026-09-25 追記）, IADR-0428: **逆指値の再発注も 3 相で送り、「届いたか不明」なら成行へ倒さず据え置く。**
//     行は送信結果待ち（注文 ID が空）へ移り、次の巡回は同じ逆指値を送り直さない（#853 追記の 1→2→3 を 1→1→1 へ）。
//     突合の記録が現れたら注文 ID を採用し、予約が解放されたら未発注として扱う。据え置きの通知は 1 時間ごと（StopDispatchIndeterminate）。
//   - 🔴 #1013, IADR-0428（2026-09-26 追記）: **「建玉残 0」を建玉消滅と読む前にエントリー注文の状態を確かめる**（HoldUnlessPositionGoneAsync）。
//     指値のエントリーが未約定のあいだ建玉は 0 であり、そこで逆指値を取り消す・記録を閉じると約定後の建玉が無保護で残る。
//     未約定なら据え置き、約定 0 で終端なら従来どおり、約定済みなら建玉を照会し直して 0 のときだけ従来どおり、分からなければ据え置く
//     （EntryStateUnknown を 1 時間ごと）。
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
    /// （<c>SoftwareStopExecutor.MaxCloseAttemptsPerTrigger</c>）と同じ 3 だが、S1 の 3 は Critical を出す連続失敗の回数で
    /// 打ち切りではなく、行ごとの待ち時間を置いて撃ち直しを続けるのに対し（#833 項目2, IADR-0344 追記(15)）、
    /// こちらは<b>保護記録ごとの累計で打ち切り、再武装が無い</b>
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
        //（Hosted/BrokerPositionSnapshotService。既定 600 秒・無条件に照会する）へ相乗りする——**#880, IADR-0412 決定1 で相乗り済み**
        //（この経路が走らない巡回でも、常駐が同じ検知を行う）。
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
        // 🔴 FR-10, #853, IADR-0428 決定2: 送信結果待ち（逆指値を送ったが届いたか不明・注文 ID が空）の行は、注文を照会できない
        // （照会する ID が無い）。送り直しも成行もせず、突合の結果で解決する。
        if (stop.IsStopDispatchPending)
            return await EvaluatePendingStopLegAsync(stop, snapshot, active, events, cancellationToken).ConfigureAwait(false);

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

            // 🔴 FR-10, #1013, IADR-0428（2026-09-26 追記）: 建玉 0 は「エントリーがまだ約定していない」でもあり得る（指値の既定）。
            // エントリーが生きている・状態が分からないなら**生きている逆指値を取り消さない**（取り消すと約定後に無保護の建玉が残る）。
            if (await HoldUnlessPositionGoneAsync(stop, active, events, cancellationToken).ConfigureAwait(false) is { } held)
                return held;

            // 建玉消滅（owner 手仕舞い・自動縮小・強制買戻し等）: 残存逆指値を取り消す。
            // 決済済み建玉に残る注文が発火すると**反対方向の建玉を生む**（業務フロー 02 補足の二重決済問題）。
            await broker.CancelOrderAsync(stop.StopOrderId, cancellationToken).ConfigureAwait(false);
            RenewStopLegTracking(stop); // #958, IADR-0406 決定3: 取消の直前までの部分約定を約定追跡に拾わせる。
            MarkCompleted(stop);
            return Outcome.Completed;
        }

        if (order.Status == OrderStatus.Filled)
        {
            // ブローカー側で損切りが成立した。台帳への反映は既存の約定追跡ポーリング（IADR-0113）が担う
            // （逆指値レグは ExecutionRecord として保存済み）。ここでは保護の完了だけを記録する。
            //
            // 🔴 #958, IADR-0406 決定3: 完了させる**前に**、レグの記録の追跡の起点をこの観測の時刻へ進める。
            // 約定追跡が追跡上限（既定 24 時間）を越えて照会するのは Active な保護記録のレグだけであり（決定2）、
            // 武装から 24 時間を超えた約定をガードが先に見て完了させると、そのレグは次の巡回で照会対象から外れ、
            // OrderExecuted が一度も出ずに台帳へ届かない（ガードと約定追跡は別々の巡回で、どちらが先かは決まらない）。
            // 進めるのは非終端の記録の時刻だけで、約定追跡が既に反映していれば何もしない。
            RenewStopLegTracking(stop);
            MarkCompleted(stop);
            return Outcome.Completed;
        }

        // 失効（Cancelled / Rejected / Expired）。
        // #958, IADR-0406 決定3: 完了・再発注で保護記録の現試行がこのレグから離れる前に、失効までの部分約定を約定追跡に拾わせる。
        RenewStopLegTracking(stop);
        if (remaining <= 0)
        {
            // 建玉残が 0 の理由が未確定の外部要因なら据え置く（確定しなければ主張は減らないまま再発注へ回る。追記(7)）。
            if (stop.HasUnconfirmedExternalReduction)
                return Outcome.Unknown;

            // 🔴 #1013: エントリーが未約定なら記録を閉じない（閉じると約定後の巡回が何もせず、無保護の建玉が残る）。
            // Active のまま残せば、約定して建玉が現れた巡回で上の再発注へ進む。
            if (await HoldUnlessPositionGoneAsync(stop, active, events, cancellationToken).ConfigureAwait(false) is { } held)
                return held;

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
            // 🔴 FR-10, #853, IADR-0210（2026-09-25 追記）, IADR-0428 決定1: **逆指値レグも送る前に決定的な StopDecisionId を予約する**
            // （成行手仕舞いの 3 相〔IADR-0117 改定 7〕と同じ形）。取れない＝以前の巡回で送信に着手した（送信中に止まった・
            // 行の更新だけ失われた）。送らずに送信結果待ちへ移す——ここで送り直すのが #853 追記の「巡回ごとの送り直し」である。
            bool reserved;
            try
            {
                reserved = reservations.TryReserve(stopDecisionId, now);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 🔴 #853（PR #1005 監査 3）, IADR-0428 決定1: 予約そのものが落ちた（DB 障害）。逆指値は送っていない。
                // 行は変えずに（失効した元の逆指値を指したまま＝次の巡回で改めて評価する）、無音にしない。
                // 成行も送らない——DB が不確かなまま予約なしで注文を重ねない。
                // PR #1005 再監査: 読めるが書けない障害が続くと巡回（30 秒）ごとに同じ Critical が重なるので、通知は据え置きと同じ
                // 間隔（このプロセスで未通知、または前回から 1 時間）に絞る。ログ（Critical）は巡回ごとに残す。
                _logger.LogCritical(ex,
                    "保護逆指値ガード: 逆指値の再発注の予約を記録できませんでした（逆指値は送っていません）。成行も送らず、次の巡回で"
                    + "改めて評価します。**逆指値なしの建玉が残っています。**証券会社の画面で確認してください: "
                    + "EntryDecisionId={EntryDecisionId} StopDecisionId={StopDecisionId} 銘柄={Symbol} 数量={Quantity}",
                    stop.EntryDecisionId, stopDecisionId, stop.Symbol, quantity);
                var failedAt = clock.UtcNow;
                if (_heldCloseNotifications.IsDue(stopDecisionId, failedAt))
                {
                    events.Add(new ProtectiveStopCoverageLost(
                        stop.EntryDecisionId, stop.Symbol, stop.Market,
                        ProtectiveStopLossCause.LapsedInFlight, ProtectiveStopRemediation.StopReservationFailed,
                        quantity, stopDecisionId, CloseIntent: null, failedAt));
                    _heldCloseNotifications.MarkNotified(stopDecisionId, stop.EntryDecisionId, failedAt);
                }

                return Outcome.CloseFailed;
            }

            if (!reserved)
                return HoldIndeterminateStop(stop, quantity, attempt, stopDecisionId, closeIntent, events, cause: null);

            BrokerOrder? newStop = null;
            try
            {
                newStop = await protective
                    .PlaceStopOrderAsync(closeIntent, stop.TriggerPrice, stopDecisionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (BrokerUnavailableException)
            {
                // 接続確立の失敗＝**確実に未発注**。予約を解放し、従来どおり成行の手仕舞いへ進む（次の巡回は同じレグを送り直せる）。
                reservations.Release(stopDecisionId);
                newStop = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 🔴 #853, IADR-0428 決定1: **届いたか不明**・分類できない例外は未発注と言い切れない。成行手仕舞いへ倒さず据え置く
                // （逆指値が生きていれば、成行で建玉を落とすと逆指値が孤立して反対建玉を生む）。予約は Reserved のまま。
                return HoldIndeterminateStop(stop, quantity, attempt, stopDecisionId, closeIntent, events, ex);
            }

            if (newStop is not null && newStop.Status is OrderStatus.Accepted or OrderStatus.PartiallyFilled or OrderStatus.Filled)
            {
                store.Save(new ExecutionRecord(
                    stopDecisionId, newStop.OrderId, stop.Symbol, stop.Market, stop.CloseSide,
                    stop.ProductType, PositionEffect.Close, quantity, stop.TriggerPrice,
                    newStop.FilledQuantity, newStop.AveragePrice, newStop.Status, SlippageRatio: 0m, now));
                // #853, IADR-0428 決定1: 相 4（確定）。結果を保存してから予約を確定する。
                reservations.MarkCompleted(stopDecisionId, newStop.OrderId, now);

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

            // #853, IADR-0428 決定1: 確認できた拒否（終端が返った）＝受理されていない。予約を解放し、従来どおり成行へ進む。
            if (newStop is not null)
                reservations.Release(stopDecisionId);
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

    // 🔴 FR-10, #853, IADR-0210（2026-09-25 追記）, IADR-0428 決定2: 送信結果待ちの行（逆指値レグの予約だけがあり、注文 ID が分からない）。
    //   1. 発注結果の記録がある → 突合（client order id）が発注済みと確定した（または行の更新だけ失われた）。注文 ID を採用する。
    //      生きていれば保護を張り直せた（ProtectiveStopPlaced）。生きていなければ採用した行で通常の評価（失効→再発注・約定→完了）へ。
    //   2. 記録なし・予約あり → **据え置く**。送り直さない・成行もしない。**建玉が消えていても完了させない**
    //      （逆指値が生きていれば、記録を閉じた瞬間に誰も取り消さない孤立注文になり、発火で反対建玉を生む）。
    //      このプロセスが未通知、または前回から 1 時間で StopDispatchIndeterminate を発行し直す（改定 9 と同じ作法）。
    //   3. 記録も予約も無い（突合の門を開けて解放された・人が解放した）→ 確実に未発注。通常の失効と同じく、建玉残に応じて
    //      完了または再発注（次の試行＝別の StopDecisionId）。
    private async Task<Outcome> EvaluatePendingStopLegAsync(
        ProtectiveStopOrder stop,
        IReadOnlyList<BrokerPositionSnapshot> snapshot,
        IReadOnlyList<ProtectiveStopOrder> active,
        List<object> events,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var record = store.FindByDecisionId(stop.StopDecisionId);
        if (record is { OrderId.Length: > 0 })
        {
            var adopted = stop with { StopOrderId = record.OrderId, UpdatedAt = now };
            stops.Save(adopted);
            _heldCloseNotifications.Forget(stop.StopDecisionId);

            if (record.Status is OrderStatus.Accepted or OrderStatus.PartiallyFilled or OrderStatus.Filled)
            {
                _logger.LogWarning(
                    "保護逆指値ガード: 送信結果が不明だった逆指値を、発注結果の記録（突合）で確認しました。注文 ID を採用します: "
                    + "EntryDecisionId={EntryDecisionId} StopDecisionId={StopDecisionId} StopOrderId={StopOrderId} 状態={Status} 銘柄={Symbol}",
                    stop.EntryDecisionId, stop.StopDecisionId, record.OrderId, record.Status, stop.Symbol);
                events.Add(new ProtectiveStopPlaced(
                    stop.EntryDecisionId, stop.StopDecisionId, record.OrderId,
                    BuildCloseIntent(stop, stop.Quantity, stop.TriggerPrice), stop.TriggerPrice, stop.Attempt, now));
                return Outcome.Replaced;
            }

            // 突合が見つけた注文は生きていない（拒否・取消・失効）。採用した行で通常の評価へ（失効→再発注・建玉なし→完了）。
            return await EvaluateAsync(stops.Find(stop.EntryDecisionId) ?? adopted, snapshot, active, events, cancellationToken)
                .ConfigureAwait(false);
        }

        if (reservations.Find(stop.StopDecisionId) is { State: OrderDispatchState.Reserved or OrderDispatchState.Completed })
        {
            _logger.LogWarning(
                "保護逆指値ガード: 逆指値の送信結果が未確定です（予約あり・記録なし）。送り直しも成行もせず据え置きます"
                + "（建玉が消えていても記録は閉じません）: EntryDecisionId={EntryDecisionId} StopDecisionId={StopDecisionId} 銘柄={Symbol}",
                stop.EntryDecisionId, stop.StopDecisionId, stop.Symbol);
            if (_heldCloseNotifications.IsDue(stop.StopDecisionId, now))
            {
                AddHeldStopNotification(
                    stop, stop.Quantity, stop.StopDecisionId,
                    BuildCloseIntent(stop, stop.Quantity, stop.TriggerPrice), events);
            }

            return Outcome.Unknown;
        }

        // 予約が無い（解放された）・見送り: この逆指値は証券会社へ届いていないと確定した。通常の失効と同じに扱う。
        _heldCloseNotifications.Forget(stop.StopDecisionId);
        var remaining = ProtectiveStopNetting.RemainingPositionFor(stop, snapshot, active);
        if (remaining <= 0)
        {
            if (stop.HasUnconfirmedExternalReduction)
                return Outcome.Unknown;

            // 🔴 #1013: #853 の経路（逆指値の予約が落ちた・解放された）でも、エントリーが未約定なら記録を閉じない。
            // 約定して建玉が現れた巡回で逆指値を張る（下の ReplaceOrCloseAsync）。
            if (await HoldUnlessPositionGoneAsync(stop, active, events, cancellationToken).ConfigureAwait(false) is { } held)
                return held;

            MarkCompleted(stop);
            return Outcome.Completed;
        }

        return await ReplaceOrCloseAsync(stop, Math.Min(remaining, stop.Quantity), events, cancellationToken)
            .ConfigureAwait(false);
    }

    // 🔴 FR-10, #1013, IADR-0428（2026-09-26 追記）, IADR-0210（2026-09-26 追記）: **「建玉残 0」を建玉消滅と読んでよいか**を、
    // エントリー注文の状態で確かめる（原則 A: まだ建っていない・建って消えた・分からない を分ける）。
    // null を返したら呼び出し側は従来どおり（取消・完了）へ進む。値を返したらその結果で据え置く（取消も完了もしない）。
    //
    //   - エントリーが非終端（受理済み・部分約定）→ StillActive。約定して建玉が現れた巡回で通常の評価に戻る。
    //   - 終端・約定 0（取消・失効・拒否）→ null（建玉は生じなかった。従来どおり）。
    //   - 約定あり → **建玉を照会し直す**。巡回の建玉照会はエントリーの状態を見る「前」に取っており、その間に約定した
    //     エントリーは「約定済み・建玉 0」に見える。建って消えたと言ってよいのは、約定を知った「後」の照会でも 0 のときだけ。
    //   - 分からない（記録が無い・注文 ID が空・照会が null／例外）→ Unknown。巡回ごとに Warning、通知は 1 時間ごと。
    //
    // S1 の行はここを通らない（残保護数量はエントリーの記録が終端のときだけ確定し、未確定では完了しない。IADR-0344 追記(4)）。
    private async Task<Outcome?> HoldUnlessPositionGoneAsync(
        ProtectiveStopOrder stop,
        IReadOnlyList<ProtectiveStopOrder> active,
        List<object> events,
        CancellationToken cancellationToken)
    {
        var (entry, reason) = await ObserveEntryAsync(stop, cancellationToken).ConfigureAwait(false);
        switch (entry)
        {
            case EntryObservation.Working:
                _logger.LogDebug(
                    "保護逆指値ガード: 建玉は 0 ですがエントリー注文はまだ約定していません。逆指値は取り消さず、記録も閉じません: "
                    + "EntryDecisionId={EntryDecisionId} 銘柄={Symbol}",
                    stop.EntryDecisionId, stop.Symbol);
                return Outcome.StillActive;

            case EntryObservation.Unknown:
                HoldUnknownEntry(stop, reason, events);
                return Outcome.Unknown;

            case EntryObservation.NeverOpened:
                return null;
        }

        // 約定あり: 約定を知った後の建玉で判定し直す（照会は取消・完了へ進む直前の 1 回だけ）。
        var fresh = await positions.GetPositionsAsync(cancellationToken).ConfigureAwait(false);
        if (fresh is null)
        {
            _logger.LogWarning(
                "保護逆指値ガード: エントリーは約定していますが、建玉を照会し直せませんでした（不明）。逆指値を取り消さず、"
                + "記録も閉じずに次の巡回で改めて評価します: EntryDecisionId={EntryDecisionId} 銘柄={Symbol}",
                stop.EntryDecisionId, stop.Symbol);
            return Outcome.Unknown;
        }

        if (ProtectiveStopNetting.RemainingPositionFor(stop, fresh, active) > 0)
        {
            _logger.LogInformation(
                "保護逆指値ガード: 巡回の建玉照会より後にエントリーが約定していました（照会し直すと建玉があります）。"
                + "逆指値は取り消さず、記録も閉じません: EntryDecisionId={EntryDecisionId} 銘柄={Symbol}",
                stop.EntryDecisionId, stop.Symbol);
            return Outcome.StillActive;
        }

        return null;
    }

    // #1013: エントリー注文の状態。発注記録（エントリーの DecisionId＝保護記録の EntryDecisionId。発注執行・突合が逆指値より先に保存する）が
    // 終端ならそれを使い（約定追跡が反映済み）、非終端ならブローカーへ照会する（約定追跡は遅れ得るし、追跡上限を過ぎた記録は照会しない）。
    private async Task<(EntryObservation Kind, string Reason)> ObserveEntryAsync(
        ProtectiveStopOrder stop, CancellationToken cancellationToken)
    {
        var record = store.FindByDecisionId(stop.EntryDecisionId);
        if (record is null)
            return (EntryObservation.Unknown, "エントリーの発注記録が見つからない");

        if (OrderStatusLifecycle.IsTerminal(record.Status))
            return (Classify(record.Status, record.FilledQuantity), string.Empty);

        if (string.IsNullOrEmpty(record.OrderId))
            return (EntryObservation.Unknown, "エントリーの注文 ID が空");

        BrokerOrder? order;
        try
        {
            order = await broker.GetOrderAsync(record.OrderId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "保護逆指値ガード: エントリー注文の照会に失敗しました: EntryDecisionId={EntryDecisionId} OrderId={OrderId}",
                stop.EntryDecisionId, record.OrderId);
            return (EntryObservation.Unknown, "エントリー注文の照会に失敗");
        }

        if (order is null)
            return (EntryObservation.Unknown, "エントリー注文を照会できない");

        return OrderStatusLifecycle.IsTerminal(order.Status)
            ? (Classify(order.Status, order.FilledQuantity), string.Empty)
            : (EntryObservation.Working, string.Empty);
    }

    // 終端の注文: 約定（Filled）か、終端までに 1 株でも約定していれば建った。約定 0 の取消・失効・拒否は建っていない。
    private static EntryObservation Classify(OrderStatus terminal, int filledQuantity) =>
        terminal == OrderStatus.Filled || filledQuantity > 0 ? EntryObservation.Opened : EntryObservation.NeverOpened;

    // 🔴 #1013: エントリーの状態が分からないまま「建玉 0」を建玉消滅と読まない。据え置きを無音にしない
    // （このプロセスで未通知、または前回から 1 時間で EntryStateUnknown を発行し直す。改定 9 と同じ作法・キーは EntryDecisionId）。
    // 何も送っていないので CloseDecisionId / CloseIntent は運ばない（台帳は押さえない）。
    private void HoldUnknownEntry(ProtectiveStopOrder stop, string reason, List<object> events)
    {
        _logger.LogWarning(
            "保護逆指値ガード: 建玉は 0 に見えますが、エントリー注文の状態を確認できません（{Reason}）。まだ約定していないのか、"
            + "建って消えたのか分からないため、逆指値を取り消さず、記録も閉じずに据え置きます。"
            + "証券会社の画面でエントリー注文・建玉・逆指値を確認してください: "
            + "EntryDecisionId={EntryDecisionId} StopOrderId={StopOrderId} 銘柄={Symbol} 数量={Quantity}",
            reason, stop.EntryDecisionId, stop.StopOrderId, stop.Symbol, stop.Quantity);

        var now = clock.UtcNow;
        if (!_heldCloseNotifications.IsDue(stop.EntryDecisionId, now))
            return;

        events.Add(new ProtectiveStopCoverageLost(
            stop.EntryDecisionId, stop.Symbol, stop.Market,
            ProtectiveStopLossCause.LapsedInFlight, ProtectiveStopRemediation.EntryStateUnknown,
            stop.Quantity, CloseDecisionId: null, CloseIntent: null, now));
        _heldCloseNotifications.MarkNotified(stop.EntryDecisionId, stop.EntryDecisionId, now);
    }

    // 🔴 FR-10, #853, IADR-0428 決定1・決定2: 逆指値の再発注を送ったが届いたか分からない（または予約が既にある）。
    //   - 予約を**解放も確定もしない**（Reserved のまま）。発注結果は保存しない（実在しない注文 ID を作らない）。
    //   - **成行手仕舞いへ進まない**（逆指値が生きていれば、建玉を落とすと逆指値が孤立して反対建玉を生む）。
    //   - 行を送信結果待ち（注文 ID が空・StopDecisionId＝このレグ・Attempt＝この試行）へ移す。次の巡回は入口の
    //     EvaluatePendingStopLegAsync に入り、**同じ逆指値を送り直さない**（#853 追記: 3 巡回で 1→2→3 → 1→1→1）。
    //   - 無音にしない: StopDispatchIndeterminate（Critical・CloseIntent＝逆指値レグ）を発行する。
    private Outcome HoldIndeterminateStop(
        ProtectiveStopOrder stop, int quantity, int attempt, Guid stopDecisionId, OrderIntent closeIntent,
        List<object> events, Exception? cause)
    {
        _logger.LogError(cause,
            "保護逆指値ガード: 逆指値の再発注の結果を確認できませんでした（送信済み・届いたか不明、または発注に着手済み）。"
            + "成行手仕舞いへは進まず（逆指値が生きていれば孤立して反対方向の建玉を生むため）、同じ逆指値も送り直しません。"
            + "予約は Reserved のまま据え置きます。証券会社の画面で逆指値の注文と建玉を確認してください: "
            + "EntryDecisionId={EntryDecisionId} StopDecisionId={StopDecisionId} 銘柄={Symbol} 数量={Quantity}",
            stop.EntryDecisionId, stopDecisionId, stop.Symbol, quantity);

        var pending = stop with
        {
            StopDecisionId = stopDecisionId,
            StopOrderId = string.Empty,
            Quantity = quantity,
            RemainingProtected = quantity,
            Attempt = attempt,
            UpdatedAt = clock.UtcNow,
        };
        stops.Save(pending);
        AddHeldStopNotification(pending, quantity, stopDecisionId, closeIntent, events);
        return Outcome.Unknown;
    }

    private void AddHeldStopNotification(
        ProtectiveStopOrder stop, int quantity, Guid stopDecisionId, OrderIntent closeIntent, List<object> events)
    {
        var now = clock.UtcNow;
        // 原因: 試行 1＝エントリーと同時に送った逆指値（発注執行が据え置いた）。2 以降＝失効後の再発注（ガードが据え置いた）。
        events.Add(new ProtectiveStopCoverageLost(
            stop.EntryDecisionId, stop.Symbol, stop.Market,
            stop.Attempt <= 1 ? ProtectiveStopLossCause.RejectedAtEntry : ProtectiveStopLossCause.LapsedInFlight,
            ProtectiveStopRemediation.StopDispatchIndeterminate,
            quantity, stopDecisionId, closeIntent, now));
        _heldCloseNotifications.MarkNotified(stopDecisionId, stop.EntryDecisionId, now);
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

    // 🔴 FR-10, #958, IADR-0406 決定3: S0 のレグの終端を観測した（または取り消した）時刻を、そのレグの記録の追跡の起点にする。
    // 記録が無い・既に終端（約定追跡が反映済み）なら何もしない（ストアが判定する）。失敗は例外のまま上げる——
    // 保護記録を完了させずに次の巡回でやり直す側へ倒す（完了してから失敗すると、そのレグは二度と照会されない）。
    private void RenewStopLegTracking(ProtectiveStopOrder stop)
    {
        if (!string.IsNullOrEmpty(stop.StopOrderId))
            store.RenewTracking(stop.StopOrderId, clock.UtcNow);
    }

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
        /// （または発注先に成行の能力が無い。Remediation=None）。記録は Active のまま次の巡回で改めて評価する
        /// （成行を送れる発注先なら撃ち直す。能力が無ければ撃てない——#948, IADR-0369 2026-09-25 追記）。
        /// <b>ClosedOut（解消した）ではない</b>——手仕舞えていない建玉を「手仕舞い」の件数に入れない。
        /// </summary>
        CloseFailed,
    }

    // #1013: 「建玉 0」の解釈に使うエントリー注文の状態（原則 A の 3 値＋終端の 2 値）。
    private enum EntryObservation
    {
        /// <summary>非終端（受理済み・部分約定）。これから約定し得る。</summary>
        Working,

        /// <summary>終端・約定 0（取消・失効・拒否）。建玉は生じなかった。</summary>
        NeverOpened,

        /// <summary>約定した（終端までに 1 株以上）。</summary>
        Opened,

        /// <summary>分からない（記録が無い・注文 ID が空・照会が null／例外）。</summary>
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
