using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Runtime;

namespace OrderExecutionService.Hosted;

// FR-10, UC-02, #331, IADR-0210 決定4: 保護逆指値の失効検知・再発注・残存取消を定期巡回で回す。
// **逆指値なしの建玉を持たない**（FR-10）の滞留側の守り手であり、エントリー時の同時発注
// （OrderExecutionService）と対で 1 つの不変条件を成す。
//
// 配線は moomoo 選択時のみ（Program.cs）——判定の前提（ブローカー注文照会・建玉照会
// 〔IBrokerPositionSource〕）を paper が持たないため。paper の逆指値は滞留 Accepted であり、
// 失効・建玉消滅の分岐は単体テスト（フェイク注入）で固定する。
public sealed class ProtectiveStopGuardService(
    IServiceScopeFactory scopeFactory,
    IWolverineRuntime runtime,
    IOptions<ProtectiveStopGuardOptions> options,
    ILogger<ProtectiveStopGuardService> logger,
    SoftwareStopLivenessReporter? softwareStopLiveness = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            // 明示的に無効化された場合のみ止まる（既定は有効）。失効した逆指値が検知されなくなることを明示する。
            logger.LogWarning(
                "保護逆指値ガードは無効です（{Section}:Enabled=false）。"
                    + " 逆指値の失効・建玉消滅後の残存注文は検知されず、逆指値なしの建玉が残り得ます（FR-10）。",
                ProtectiveStopGuardOptions.SectionName);
            return;
        }

        logger.LogInformation(
            "保護逆指値ガードを開始します（間隔 {Interval}・バッチ {BatchSize}）。照会不能は据え置きます（fail-safe）。",
            options.Value.Interval, options.Value.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // フェイルセーフ: ガードの失敗で発注執行サービスを止めない（次回巡回で再試行する）。
                logger.LogError(ex, "保護逆指値ガードの巡回に失敗しました。次回巡回で再試行します。");
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

    // 1 巡回。再発注・保護喪失で得たイベントを発行する（単体テスト可能な単位として公開する）。
    public async Task<ProtectiveStopGuardResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = scopeFactory.CreateScope();
        var guard = scope.ServiceProvider.GetRequiredService<ProtectiveStopGuard>();

        var result = await guard.RunOnceAsync(options.Value.BatchSize, cancellationToken).ConfigureAwait(false);

        // ADR-0013, IADR-0129, #354: BackgroundService（singleton）からの発行。Wolverine の IMessageBus は scoped で
        // singleton へ注入できないため、singleton の IWolverineRuntime から MessageBus を作って発行する。
        var bus = new MessageBus(runtime);
        await PublishAllAsync(
                result.Events, evt => bus.PublishAsync(evt),
                scope.ServiceProvider.GetService<HeldCloseNotificationTracker>(),
                scope.ServiceProvider.GetService<CloseRejectionTracker>())
            .ConfigureAwait(false);

        if (result.Replaced > 0 || result.ClosedOut > 0 || result.Unknown > 0 || result.Failed > 0
            || result.CloseRejected > 0 || result.CloseFailed > 0)
        {
            logger.LogWarning(
                "保護逆指値ガード: Active {Scanned} 件を評価（維持 {StillActive} / 完了 {Completed} / 再発注 {Replaced}"
                    + " / 手仕舞い {ClosedOut} / 据え置き（照会不能・送信結果不明） {Unknown}"
                    // #857, IADR-0369: 「拒否（建玉が残っている）」は据え置き（不明）と別枠で数える。
                    + " / 手仕舞い拒否（建玉残存） {CloseRejected}"
                    // #938（PR #916 監査 F5）, IADR-0369（2026-09-25 追記）: 確実に未発注の手仕舞い失敗（Remediation=None）を
                    // 「手仕舞い」に混ぜない。条件にも足す——分けた後に、この巡回の警告そのものが出なくならないように。
                    + " / 手仕舞い失敗（未発注・建玉残存） {CloseFailed} / 失敗 {Failed}）。",
                result.Scanned, result.StillActive, result.Completed, result.Replaced,
                result.ClosedOut, result.Unknown, result.CloseRejected, result.CloseFailed, result.Failed);
        }

        // FR-10, #902, IADR-0365 決定5: Active な S1 行の低頻度の要約（観測のみ）。ストアは間隔に 1 回だけ読む。
        // 要約の失敗は巡回を失敗させない（ガードの結果・発行に一切影響させない）。
        if (softwareStopLiveness is not null)
        {
            try
            {
                var stops = scope.ServiceProvider.GetRequiredService<IProtectiveStopOrderStore>();
                softwareStopLiveness.ReportIfDue(() => stops.FindActive(options.Value.BatchSize));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "ソフトウェア逆指値（S1）の要約の記録に失敗しました（ガードの巡回には影響しません）。");
            }
        }

        return result;
    }

    // 🔴 #848, IADR-0117（2026-09-19 追記・改定 9）: **発行できなかった据え置きの通知は、通知済みとして覚えない。**
    // ガードは CloseDispatchIndeterminate を作った時点で「通知した」と記憶する（1 時間は重ねないため）。
    // ここで発行に失敗したのに記憶が残ると、Critical も台帳の押さえ（CloseIntent）も出ないまま 1 時間黙る。
    // 未発行分の記憶を消してから投げ直す＝次の巡回（既定 30 秒）が入口で発行し直す。
    // プロセスごと落ちた場合は記憶そのものが消えるので、同じく再起動後の最初の巡回が発行する。
    // 🔴 PR #916 監査 F2, #857, IADR-0369（2026-09-24 追記）: **確認できた拒否（CloseRejected）も同じ形で補償する。**
    // ガードは RejectedClose / HoldRejectedClose で発行の前に MarkNotified する。上限に達した行の通知が落ちたまま
    // 記憶が残ると、次の再通知まで最大 1 時間、無保護の建玉について黙る。消すのは**通知の記憶だけ**で、
    // 拒否の数えは残す（数えまで消すと、通知の失敗が成行の撃ち直しへ化ける）。
    public static async Task PublishAllAsync(
        IReadOnlyList<object> events,
        Func<object, ValueTask> publish,
        HeldCloseNotificationTracker? tracker,
        CloseRejectionTracker? rejections = null)
    {
        for (var i = 0; i < events.Count; i++)
        {
            try
            {
                await publish(events[i]).ConfigureAwait(false);
            }
            catch
            {
                for (var j = i; j < events.Count; j++)
                {
                    // 🔴 #853, IADR-0428 決定2: 送信結果が不明な逆指値の据え置き（StopDispatchIndeterminate）も同じ記憶を使う
                    //（キーは逆指値レグの DecisionId）。発行できなかった通知を「通知済み」として 1 時間黙らせない。
                    if (events[j] is ProtectiveStopCoverageLost
                        {
                            Remediation: ProtectiveStopRemediation.CloseDispatchIndeterminate
                                or ProtectiveStopRemediation.StopDispatchIndeterminate
                                or ProtectiveStopRemediation.StopReservationFailed,
                            CloseDecisionId: { } closeDecisionId,
                        })
                    {
                        tracker?.Forget(closeDecisionId);
                    }
                    // 🔴 #1013, IADR-0428（2026-09-26 追記）: エントリーの状態が不明な据え置きの通知もキー＝EntryDecisionId で覚えている。
                    else if (events[j] is ProtectiveStopCoverageLost
                    {
                        Remediation: ProtectiveStopRemediation.EntryStateUnknown,
                        EntryDecisionId: var unknownEntryDecisionId,
                    })
                    {
                        tracker?.Forget(unknownEntryDecisionId);
                    }
                    else if (events[j] is ProtectiveStopCoverageLost
                    {
                        Remediation: ProtectiveStopRemediation.CloseRejected,
                        EntryDecisionId: var entryDecisionId,
                    })
                    {
                        rejections?.ForgetNotification(entryDecisionId);
                    }
                }

                throw;
            }
        }
    }
}
