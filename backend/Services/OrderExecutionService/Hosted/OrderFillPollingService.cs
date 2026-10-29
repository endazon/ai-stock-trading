using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.CancelUnfilledDecisionCloses;
using OrderExecutionService.Features.OrderExecution.CancelUnfilledDecisionEntries;
using OrderExecutionService.Features.OrderExecution.PollOrderFills;
using OrderExecutionService.Features.OrderExecution.RecordTradeExpenses;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Runtime;

namespace OrderExecutionService.Hosted;

// #270, FR-05, FR-10, IADR-0113: 約定状態の追跡ポーリングを短周期（既定 30 秒）で回す。
//
// moomoo は発注時に Accepted（未約定）を返すため、約定の成立を観測しないと取引台帳（統制の入力）が
// 空のままになる。本サービスが非終端の発注結果を追跡し、終端化・部分約定の進捗を OrderExecuted として
// 再発行することで、SameDayReentry・日次発注上限・段階資金上限・建玉が moomoo 経路でも実効する。
//
// 配線は moomoo 選択時のみ（Program.cs）。paper は即時終端のため非終端記録が生まれず、仮に登録されても
// 照会は 1 度も起きない（二重の非干渉）。
//
// 滞留リコンサイル（IADR-0074/0092）とは対象集合が交わらない: あちらは Reserved（発注したか不明）な予約、
// こちらは確定済みの発注結果。両者が同一 OrderId の OrderExecuted を発行しても、台帳側の OrderId 単位の
// 単調 upsert により結果は冪等である。
public sealed class OrderFillPollingService(
    IServiceScopeFactory scopeFactory,
    IWolverineRuntime runtime,
    IOptions<FillPollingOptions> options,
    ILogger<OrderFillPollingService> logger) : BackgroundService
{
    // 🔴 #1339, IADR-0536 決定2・4: 照会し直しと定期ログの次の実行時刻（プロセス内だけ。再起動の直後の巡回で 1 回ずつ走る）。
    private DateTimeOffset _nextAbandonedCloseRecheckAt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextPendingCloseReportAt = DateTimeOffset.MinValue;

    // 🔴 #1339, IADR-0539: 回数の上限で取り消さなかった決済（注文 → その注文の市場の現地取引日の終わり）。プロセス内だけ。
    // 巡回ごとに INF を繰り返さず、次の巡回から候補に戻さない。**取引日が替わったら消す**（上限は日ごとに数え直す）。
    private readonly Dictionary<string, DateTimeOffset> _cappedUntil = new(StringComparer.Ordinal);

    /// <summary>試験用: いま覚えている「上限で取り消さなかった注文」（取引日が替わると消える）。</summary>
    internal IReadOnlyCollection<string> CappedOrderIds => _cappedUntil.Keys;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            // 明示的に無効化された場合のみ止まる（既定は有効・IADR-0113）。統制が実効しなくなることを明示する。
            logger.LogWarning(
                "約定状態の追跡は無効です（FillPolling:Enabled=false）。"
                    + " 未約定のまま成立した約定は取引台帳へ届かず、統制上限は実効しません。");
            return;
        }

        logger.LogInformation(
            "約定状態の追跡を開始します（間隔 {Interval}・追跡上限 {MaxTracking}）。照会不達・不明は据え置きます（fail-safe）。",
            options.Value.Interval, options.Value.MaxTracking);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // フェイルセーフ: 追跡の失敗で発注執行サービスを止めない（次回巡回で再試行する）。
                logger.LogError(ex, "約定状態の追跡に失敗しました。次回巡回で再試行します。");
            }

            try
            {
                await Task.Delay(options.Value.Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // 1 巡回。終端化・進捗で得た OrderExecuted を発行する（単体テスト可能な単位として公開する）。
    public async Task<OrderFillPollResult> PollOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = scopeFactory.CreateScope();
        var poller = scope.ServiceProvider.GetRequiredService<OrderFillPoller>();
        var tradeExpenses = scope.ServiceProvider.GetRequiredService<TradeExpenseRecordingService>();

        var result = await poller
            .PollOnceAsync(options.Value.MaxTracking, options.Value.EffectiveBatchSize, cancellationToken)
            .ConfigureAwait(false);

        // ADR-0013, IADR-0129, #354: BackgroundService（singleton）からの発行。Wolverine の IMessageBus は scoped で
        // singleton へ注入できないため、singleton の IWolverineRuntime から MessageBus を作って発行する。
        var bus = new MessageBus(runtime);
        var executedOrders = scope.ServiceProvider.GetRequiredService<IExecutedOrderStore>();
        for (var i = 0; i < result.Executed.Count; i++)
        {
            var executed = result.Executed[i];

            // 🔴 NFR, FR-10, #1442, IADR-0573 決定2: **再発行（先頭の Republished 件）の失敗は 1 件ごとに隔離する。**
            // 恒常的に投げる 1 件で巡回の残り（S1 の再武装の通知・取消の確認待ちの後始末・打ち切り・照会し直し）が毎巡回止まると、
            // 資金に関わる事象が届かなくなる。その記録は印を残したまま（次の巡回で再び試す）WRN を出して先へ進む。
            // 再発行には、この巡回で後に続く事象（取消の確認待ちの後始末）が無い——後始末は終端化を観測したこの巡回の新しい約定にだけ付く。
            // この巡回の新しい約定の失敗は従来どおり巡回を終える（後始末を OrderExecuted より先に出さない。IADR-0569 決定4）。
            if (i < result.Republished)
            {
                try
                {
                    await bus.PublishAsync(executed).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex,
                        "発行し終えていなかった約定の再発行に失敗しました（印は残し、次の巡回で再び試します。巡回の残りの発行は続けます）: "
                            + "DecisionId={DecisionId} OrderId={OrderId} 状態={Status} 約定={Filled}",
                        executed.DecisionId, executed.OrderId, executed.Status, executed.FilledQuantity);
                    continue;
                }
            }
            else
            {
                await bus.PublishAsync(executed).ConfigureAwait(false);
            }

            // 🔴 NFR, FR-05, FR-10, #1442, IADR-0573 決定2: **発行の後に**発行待ちの印を倒す（先に倒すと、発行に失敗した約定が
            // 二度と発行されない）。発行が投げたら以降は倒さずに巡回を終える——印は残り、次の巡回で再発行される。
            // 耐久アウトボックス（決定1）では、発行が返った時点で封筒は DB にある（ブローカーへ届いたかではない）。
            MarkOrderExecutedPublished(executedOrders, executed);

            // FR-11, FR-16, ADR-0016 決定15, #633, IADR-0300: **moomoo の約定はここでしか観測されない**
            // （発注時は Accepted であり、経費が発生し得るのは約定してからである）。段 1 の既定では
            // イベントが 1 本も出ないため、発行の面での挙動は不変で、増えるのは警告ログ 1 行だけである。
            //
            // 🔴 **段 2（実費の供給）の前提**: 部分約定は同じ注文を複数の巡回で観測する。
            // 🔴 #1442, IADR-0573: **再発行（発行し終えていなかった約定の送り直し）も同じ約定をもう一度ここへ通す。**
            // TradeExpenseRecorded の発行には TradeExpense.SourceId を鍵とした重複排除が要る（再発行の分も対象）。
            // **実装するまで供給を有効にしない。**
            await RecordTradeExpensesAsync(tradeExpenses, bus, executed, cancellationToken).ConfigureAwait(false);
        }

        // 🔴 FR-10, #833 項目1, IADR-0389 決定7: 受理だけで完了させた決済が未約定のまま終端し、保護記録を
        // 再武装した事実は**必ず知らせる**（Critical）。これが今日まで無音だった唯一の失敗様式である。
        foreach (var reArmed in result.SoftwareStopEvents ?? [])
            await bus.PublishAsync(reArmed).ConfigureAwait(false);

        // 🔴 FR-10, UC-02, #1422, IADR-0569 決定4: 取消の確認待ちだった新規建ての後始末（取消の確定・約定数量の成行手仕舞い）を知らせる。
        // OrderExecuted（エントリーの約定）の**後**に出す——台帳は約定で建玉を知ってから、手仕舞いレグの承認行を足す。
        foreach (var resolved in result.EntryCancelResolutions ?? [])
            await bus.PublishAsync(resolved).ConfigureAwait(false);

        // 🔴 FR-10, FR-11, #1048, IADR-0481 決定3: 追跡の打ち切りを監査へ残す。**発行の後に印を書く**——先に書くと、
        // 発行に失敗した打ち切りが二度と記録されない。印を書けずに次の巡回で再び発行しても、監査台帳は OrderId と
        // 追跡の起点から決定的に導いた Id で 1 件に畳む。
        if (result.Abandoned is { Count: > 0 } abandoned)
        {
            foreach (var giveUp in abandoned)
            {
                await bus.PublishAsync(giveUp).ConfigureAwait(false);
                executedOrders.MarkTrackingAbandoned(giveUp.OrderId, giveUp.TrackedFrom);
                logger.LogWarning(
                    "約定追跡を打ち切りました（追跡上限 {MaxTracking} を過ぎても非終端）: DecisionId={DecisionId} OrderId={OrderId} "
                        + "銘柄={Symbol} 最後の状態={Status} 約定 {Filled}/{Quantity} 追跡の起点={TrackedFrom}。"
                        + "以後この注文の約定・終端は台帳へ届きません。証券会社の画面で注文の結果を確認してください。",
                    giveUp.MaxTracking, giveUp.DecisionId, giveUp.OrderId, giveUp.Symbol, giveUp.LastStatus,
                    giveUp.FilledQuantity, giveUp.Quantity, giveUp.TrackedFrom);
            }
        }

        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        await RecheckAbandonedClosesAsync(poller, tradeExpenses, bus, executedOrders, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        ReportPendingCloses(executedOrders, clock.UtcNow);
        await CancelUnfilledDecisionClosesAsync(scope.ServiceProvider, bus, cancellationToken).ConfigureAwait(false);
        RecordPublishPending(scope.ServiceProvider, executedOrders);

        if (result.Updated > 0 || result.Unknown > 0 || result.Failed > 0 || result.Republished > 0)
            logger.LogInformation(
                "約定追跡: 非終端 {Scanned} 件を照会（更新 {Updated} / 終端化 {Terminalized} / 変化なし {Unchanged}"
                    + " / 照会不能 {Unknown} / 失敗 {Failed}）。再発行 {Republished} 件。",
                result.Scanned, result.Updated, result.Terminalized, result.Unchanged, result.Unknown, result.Failed,
                result.Republished);

        return result;
    }

    // 🔴 FR-10, FR-05, #1339, IADR-0536 決定2・3: 追跡を打ち切った承認経路の決済を間隔ごとに照会し直し、ブローカーが終端と
    // 答えたものだけを解放する（OrderExecuted の発行は通常の追跡と同じ。リスク管理の台帳の終端・手仕舞いの未約定残の通知が届く）。
    // 失敗で巡回を止めない（次の間隔で再試行する）。
    private async Task RecheckAbandonedClosesAsync(
        OrderFillPoller poller,
        TradeExpenseRecordingService tradeExpenses,
        MessageBus bus,
        IExecutedOrderStore executedOrders,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (now < _nextAbandonedCloseRecheckAt)
            return;
        _nextAbandonedCloseRecheckAt = now + options.Value.AbandonedCloseRecheckInterval;

        AbandonedCloseRecheckResult recheck;
        try
        {
            recheck = await poller.RecheckAbandonedClosesAsync(options.Value.EffectiveBatchSize, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "追跡を打ち切った決済の照会し直しに失敗しました。次の間隔で再試行します。");
            return;
        }

        foreach (var executed in recheck.Executed)
        {
            await bus.PublishAsync(executed).ConfigureAwait(false);
            MarkOrderExecutedPublished(executedOrders, executed); // #1442, IADR-0573 決定2: 発行の後に倒す（理由は巡回の発行と同じ）。
            await RecordTradeExpensesAsync(tradeExpenses, bus, executed, cancellationToken).ConfigureAwait(false);
        }

        foreach (var reArmed in recheck.SoftwareStopEvents)
            await bus.PublishAsync(reArmed).ConfigureAwait(false);

        foreach (var released in recheck.Released)
        {
            var r = released.Record;
            logger.LogInformation(
                "処理中の決済を解放しました（追跡を打ち切った後、証券会社が終端 {Status} と答えました。約定 {Filled}/{Quantity}）: "
                    + "DecisionId={DecisionId} OrderId={OrderId} 銘柄={Symbol} 方向={Side} 発注={PlacedAt}",
                OrderStatusDisplay.Describe(released.TerminalStatus), released.FilledQuantity, r.Quantity, r.DecisionId, r.OrderId, r.Symbol, r.Side,
                r.ExecutedAt);
        }

        if (recheck.Scanned > 0)
        {
            logger.LogInformation(
                "追跡を打ち切った決済の照会し直し: {Scanned} 件（解放 {Released} / 非終端 {StillPending} / 照会不能 {Unknown}。"
                    + "非終端・照会不能は解放しません）。",
                recheck.Scanned, recheck.Released.Count, recheck.StillPending, recheck.Unknown);
        }
    }

    // FR-10, FR-11, #1339, IADR-0536 決定4: 発注から一定時間たっても非終端の承認経路の決済を、間隔ごとに 1 件ずつ WRN で出す。
    // 窓の両端（発注からの経過＝前の端・まだ非終端＝後の端）で選ぶ。0 件なら何も出さない。通知は送らない（ログだけ）。
    private void ReportPendingCloses(IExecutedOrderStore store, DateTimeOffset now)
    {
        if (now < _nextPendingCloseReportAt)
            return;
        var interval = options.Value.PendingCloseReportInterval;
        _nextPendingCloseReportAt = now + interval;

        IReadOnlyList<ExecutionRecord> stale;
        try
        {
            stale = store.FindStaleApprovedCloses(now - interval, options.Value.EffectiveBatchSize);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "未約定の決済の洗い出しに失敗しました。次の間隔で再試行します。");
            return;
        }

        var maxTracking = options.Value.MaxTracking;
        foreach (var r in stale)
        {
            var age = now - r.ExecutedAt;
            logger.LogWarning(
                "未約定の決済が残っています: DecisionId={DecisionId} OrderId={OrderId} 銘柄={Symbol} 方向={Side} "
                    + "約定 {Filled}/{Quantity} 状態={Status} 発注から {AgeMinutes} 分 追跡={Tracking}。"
                    + "約定しないまま残ると、この建玉の次の決済は見送られ得ます。証券会社の画面で注文を確認してください。",
                r.DecisionId, r.OrderId, r.Symbol, r.Side, r.FilledQuantity, r.Quantity, OrderStatusDisplay.Describe(r.Status),
                (int)age.TotalMinutes, age >= maxTracking ? "打ち切り済み" : "追跡中");
        }
    }

    // 🔴 FR-10, FR-19, ADR-0067 決定1〜4, #1339, IADR-0539: 判断由来の決済の指値が N 分未約定なら系が取り消すだけにする（既定は無効）。
    // 確認できた取消（OrderCancelled）を発行する（リスク管理の台帳・注文アクティビティ射影＝相場操縦の検知の母集団へ届く）。
    // 置き直し・成行は送らない。部品が登録されていない構成（試験の最小ホスト）では何もしない。失敗で巡回を止めない。
    private async Task CancelUnfilledDecisionClosesAsync(
        IServiceProvider services, MessageBus bus, CancellationToken cancellationToken)
    {
        if (services.GetService<DecisionCloseUnfilledCanceller>() is { } closes)
            await CancelUnfilledDecisionOrdersAsync(services, bus, closes.RunOnceAsync, "決済", cancellationToken).ConfigureAwait(false);

        // 🔴 FR-10, FR-19, #1486, IADR-0594 決定1: 判断由来の新規建ての指値も同じ形で取り消す（既定は無効。計画の裁定待ち＝planning#852）。
        if (services.GetService<DecisionEntryUnfilledCanceller>() is { } entries)
            await CancelUnfilledDecisionOrdersAsync(services, bus, entries.RunOnceAsync, "新規建て", cancellationToken).ConfigureAwait(false);
    }

    private async Task CancelUnfilledDecisionOrdersAsync(
        IServiceProvider services,
        MessageBus bus,
        Func<IReadOnlyCollection<string>?, CancellationToken, Task<DecisionCloseUnfilledCancelResult>> runOnce,
        string kind,
        CancellationToken cancellationToken)
    {
        DecisionCloseUnfilledCancelResult result;
        try
        {
            // 回数の上限で取り消さないと決めた注文は問い合わせの段で除く（件数の上限を占めない。DAY で失効するので再起動で忘れてよい）。
            var now = services.GetRequiredService<IClock>().UtcNow;
            foreach (var expired in _cappedUntil.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
                _cappedUntil.Remove(expired);
            result = await runOnce(_cappedUntil.Keys, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "判断由来の{Kind}の指値の未約定時の取消に失敗しました。次の巡回で再試行します。", kind);
            return;
        }

        foreach (var cancelled in result.Confirmed)
        {
            try
            {
                await bus.PublishAsync(cancelled).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 失っても解放は担保される（約定追跡が終端を確かめて OrderExecuted を出す）。残りの発行と上限の記録を止めない。
                logger.LogError(ex, "取消の事実（OrderCancelled）の発行に失敗しました。解放は約定追跡が終端を確かめてから行います。OrderId={OrderId}",
                    cancelled.OrderId);
            }
        }

        foreach (var r in result.Capped)
        {
            if (!_cappedUntil.TryAdd(r.OrderId, DecisionCloseUnfilledCanceller.TradingDayRange(r.Market, r.ExecutedAt).End))
                continue;
            logger.LogInformation(
                "判断由来の{Kind}の指値は未約定ですが、取消の上限（1 建玉 1 日あたり）に達したため取り消しません（当日限り〔DAY〕の失効に任せます）: "
                    + "DecisionId={DecisionId} OrderId={OrderId} 銘柄={Symbol} 方向={Side} 発注={PlacedAt}",
                kind, r.DecisionId, r.OrderId, r.Symbol, r.Side, r.ExecutedAt);
        }
    }

    // 🔴 NFR, FR-10, #1442, IADR-0573 決定3: 巡回の末尾で、発行し終えていない約定の件数を計器へ出す（平常は 0。続けば警報）。
    // 計器が登録されていない構成（試験の最小ホスト）では何もしない。数えられなくても巡回は止めない（計器は記録しない）。
    private void RecordPublishPending(IServiceProvider services, IExecutedOrderStore store)
    {
        if (services.GetService<BusinessMetrics>() is not { } metrics)
            return;
        try
        {
            metrics.RecordOrderExecutedPublishPending(store.CountOrderExecutedPublishPending());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "発行し終えていない約定の件数を数えられませんでした（計器はこの巡回では記録しません）。");
        }
    }

    // 🔴 NFR, FR-05, FR-10, #1442, IADR-0573 決定2: 発行待ちの印を倒す。倒せなかった（書き込みの失敗）ときは巡回を止めず、
    // 次の巡回で同じ状態を再発行する——受け手（台帳の累積数量の単調 upsert・終端は初回だけ・GFV / Stage 1 は先着優先・
    // 注文アクティビティは絶対値の代入）は冪等であり、二重に数えない。監査と通知は 1 件ずつ増え得る（IADR-0573 残余）。
    private void MarkOrderExecutedPublished(IExecutedOrderStore store, OrderExecuted executed)
    {
        try
        {
            store.MarkOrderExecutedPublished(executed.OrderId, executed.Status, executed.FilledQuantity);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "約定の発行済みの印を書けませんでした（次の巡回で同じ約定を再発行します。受け手は冪等です）。OrderId={OrderId}",
                executed.OrderId);
        }
    }

    // FR-11, #633, IADR-0300: 経費の記録は約定の追跡を止めない（1 件の失敗で巡回全体を落とさない＝既存の流儀）。
    private async Task RecordTradeExpensesAsync(
        TradeExpenseRecordingService tradeExpenses,
        MessageBus bus,
        OrderExecuted executed,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await tradeExpenses
                .RecordForExecutionAsync(executed, cancellationToken)
                .ConfigureAwait(false);

            TradeExpenseRecordingLog.Write(logger, executed, outcome);

            foreach (var recorded in outcome.Events)
                await bus.PublishAsync(recorded).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "経費の記録に失敗しました（約定の追跡は継続します）。OrderId={OrderId}", executed.OrderId);
        }
    }
}
