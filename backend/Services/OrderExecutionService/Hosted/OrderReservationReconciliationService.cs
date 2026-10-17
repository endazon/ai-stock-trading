using AiStockTrading.Shared.Contracts.Observability;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using OrderExecutionService.Features.OrderExecution.ReconcileOrderReservations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Runtime;

namespace OrderExecutionService.Hosted;

// #141, FR-05, IADR-0074: 滞留 Reserved（IADR-0057 の「発注済みか不明」の窓）の自動リコンサイルを定期実行する。
//
// アプリの既定は無効（IADR-0074 決定4）。有効化は appsettings / Helm values の `Reconciliation:Enabled=true`。
// 有効化しても既定の no-op プローブ（IndeterminateReservationBrokerProbe）下では Placed/NotPlaced 経路は
// 発火せず、phase-4 自己修復（ブローカ非依存）のみが作動する。
//
// 🔴 #856, IADR-0362: **配備（deploy/helm/ai-stock-trading/values.yaml）では Enabled / UseBrokerProbe を有効にし、
// 解放の門（ReleaseOnNotPlaced）だけを閉じたままにしている。** 本常駐は 1 巡回ごとに、人が見なければならない
// 2 つを明示的にログする——突合で確定した注文（確定の時点では、エントリーなら保護レグが無い）と、門が閉じて据え置いた未発注判定。
// 🔴 #853, IADR-0428 決定4: 突合で確定したエントリーには、続けて承認時の手法で保護レグを張る（EmitProtectionAsync が結果を出す）。
//
// 終端化した予約の OrderExecuted 発行は本 Worker 層が担う（Application はメッセージ基盤に非依存の既存レイヤリングを維持）。
//
// 🔴 #890, IADR-0371: **記録と発行は「確定した 1 件」ごとに、その場で行う**（本クラスが
// IReservationReconciliationSink を実装し、リコンサイラのループから 1 件ずつ呼ばれる）。
// 巡回の末尾に置くと、巡回が途中で中断されただけで確定済みの所見と OrderExecuted が永久に失われる ——
// 確定した予約は次回巡回の FindStalledReserved に載らないため、「次の巡回で拾い直す」が成立しない。
public sealed class OrderReservationReconciliationService(
    IServiceScopeFactory scopeFactory,
    IWolverineRuntime runtime,
    IClock clock,
    IOptions<ReconciliationOptions> options,
    BusinessMetrics metrics,
    ILogger<OrderReservationReconciliationService> logger)
    : BackgroundService, IReservationReconciliationSink
{
    // FR-05, #856, IADR-0538: 据え置き警告の畳み込み（同じ予約で状態が変わらない限り、Warning は初回と再通知の間隔ごと）。
    // 主コンストラクタの引数を初期化子と本体の両方で捕まえない（CS9124）ため、最初の巡回で作る（巡回は 1 本のループからだけ呼ばれる）。
    private HeldNotPlacedWarningThrottle? _heldWarnings;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            // fail-safe: 明示的に有効化されるまで走査しない。
            logger.LogInformation(
                "発注予約の自動リコンサイルは無効です（Reconciliation:Enabled=false）。滞留 Reserved は人手/`_error` のままです。"
                    + " 配備（Helm values）では有効化されています。この行が出るのは構成が届いていないということです。");
            return;
        }

        // 🔴 NFR-09, ADR-0045 決定2, #1051, IADR-0444 決定2: 解放の門は取引環境ごとに出す（1 つの「許可／禁止」にまとめない）。
        logger.LogInformation(
            "発注予約の自動リコンサイルを開始します（滞留閾値 {Hours} 時間・間隔 {Interval}・未発注時の解放 SIMULATE {ReleaseSimulate} /"
                + " 実弾 {ReleaseReal}）。照会不達・不確定、取引環境が不明な予約は解放しません（fail-safe）。",
            ReconciliationPolicy.EffectiveStallThresholdHours(options.Value.StallThresholdHours),
            options.Value.Interval,
            options.Value.ReleaseOnNotPlaced.Simulate ? "許可" : "禁止",
            options.Value.ReleaseOnNotPlaced.Real ? "許可" : "禁止");

        // #1051, IADR-0444 決定4: 旧キーは SIMULATE の門にだけ写した（実弾の門には写らない）。黙って読み替えない。
        if (options.Value.LegacyReleaseOnNotPlacedMapped is { } legacy)
            logger.LogWarning(
                "旧キー Reconciliation:ReleaseOnNotPlaced={Legacy} を SIMULATE の門にだけ写しました（実弾の門には写しません）。"
                    + " 旧キーは廃止です。Reconciliation__ReleaseOnNotPlaced__Simulate / __Real へ移してください（#1051）。",
                legacy);
        if (options.Value.LegacyReleaseOnNotPlacedIgnored)
            logger.LogWarning(
                "旧キー Reconciliation:ReleaseOnNotPlaced は、新キー Reconciliation:ReleaseOnNotPlaced:Simulate が在るため無視しました。"
                    + " 旧キーを構成から消してください（#1051）。");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // フェイルセーフ: リコンサイルの失敗で発注執行サービスを止めない（次回巡回で再試行する）。
                logger.LogError(ex, "発注予約の自動リコンサイルに失敗しました。次回巡回で再試行します。");
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

    // 1 巡回。終端化した予約の OrderExecuted を発行し、結果を返す（単体テスト可能な単位として公開する）。
    public async Task<ReservationReconciliationResult> ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cutoff = ReconciliationPolicy.StallCutoffFor(clock.UtcNow, options.Value.StallThresholdHours);

        using var scope = scopeFactory.CreateScope();
        var reconciler = scope.ServiceProvider.GetRequiredService<OrderReservationReconciler>();

        // 🔴 #890, IADR-0371: **出口（記録＋発行）は自分自身を sink として渡し、1 件ごとに受け取る。**
        // 巡回の末尾で回すと、中断（ローリングデプロイ・Pod 再起動）が巡回に重なるだけで、
        // 既に確定（MarkCompleted を commit）した予約の所見と OrderExecuted がまとめて失われる（#890）。
        var result = await reconciler
            .ReconcileAsync(cutoff, options.Value.EffectiveBatchSize, this, cancellationToken).ConfigureAwait(false);

        // 巡回全体の要約（件数）。🔴 **1 件ごとの明細はここでは出さない**——出すと EmitAsync と二重になる。
        // #856, IADR-0538: バッチが満杯でなければ滞留の全件を走査した（載らなかった予約はもう滞留していない）。
        ReportRoundSummary(result, scanComplete: result.Scanned < options.Value.EffectiveBatchSize);

        return result;
    }

    // #890, IADR-0371: 確定した 1 件の出口。リコンサイラのループから、その予約の MarkCompleted を
    // commit した**直後**に呼ばれる（巡回の末尾ではない）。
    //
    // 🔴 #856, IADR-0362（PR #882 監査 N1）: **1 件の中でも記録は発行より先に出す。**
    // 発行（PublishAsync）は外部のメッセージ基盤に触れるため落ち得る。落ちた後ろに記録を置くと、
    // 例外で抜けて記録が出ない —— しかも予約は既に MarkCompleted を commit 済みで
    // 次回巡回の FindStalledReserved に載らないため、**「保護レグの無い建玉が載った」Critical は二度と出ない**。
    // 「黙って通り過ぎさせない」は発行の成否に依ってはならない。
    //
    // 🔴 発行の失敗は握り潰さない（伝播させる）。巡回はそこで止まるが、残りの滞留は Reserved のままであり
    // 次回巡回が拾い直せる（据え置きは二重発注を生まない）。
    public async Task EmitAsync(ReservationTerminalizationEmission emission)
    {
        if (emission.ProbeFinding is { } finding)
            ReportProbeTerminalized(finding);

        // 🔴 FR-05, #856, IADR-0441: 確定した 1 件の計数も**発行より先**に残す（記録と同じ理由。発行が落ちても数え漏らさない）。
        // 確定した予約は次の巡回に載らないので、ここで数えなければ永久に数えられない（IADR-0371 の幾何）。
        // 🔴 #1051, IADR-0444 決定6: 予約の取引環境で数える（ADR-0045 決定1 (a) は取引環境ごと）。
        metrics.RecordOrderReservationReconciliation(
            emission.ProbeFinding is null
                ? BusinessMetrics.ReservationReconciliationSelfHealed
                : BusinessMetrics.ReservationReconciliationProbePlaced,
            emission.ReservationProvider);

        // ADR-0013, IADR-0129, #354: BackgroundService（singleton）からの発行。Wolverine の IMessageBus は scoped で
        // singleton へ注入できないため、singleton の IWolverineRuntime から MessageBus を作って発行する。
        //
        // 🔴 CancellationToken を渡さないのは意図的である（EmitAsync が受け取らない理由も同じ）。
        // 確定（commit）済みの 1 件の発行は、停止要求で取り下げてよいものではない。
        await new MessageBus(runtime).PublishAsync(emission.Executed).ConfigureAwait(false);
    }

    // 🔴 FR-05, FR-10, #856, IADR-0362: 突合で「発注済み」と確定した注文。確定した**この時点では**、エントリーなら保護レグが無い。
    // 通知（OrderExecuted）は「約定した」としか言わないので、その事実はここでしか出ない。**無音にしない**（記録は発行より先。#882 監査 N1）。
    // 🔴 #853, IADR-0210（2026-09-25 追記）, IADR-0428 決定4: 旧文面「この経路は保護逆指値を張りません」は裁定で偽になった
    // （突合の後に承認時の手法で張る）。Critical は残す——保護の処理はこの行の**後**に走り、発行の失敗・中断で届かないことがある。
    // その結果は直後の行（EmitProtectionAsync）が種類ごとに出す。直後の行が無ければ、それ自体が「確かめよ」の合図である。
    private void ReportProbeTerminalized(ReservationReconciliationFinding finding) =>
        logger.LogCritical(
            "発注予約リコンサイル: 滞留していた予約を突合で「発注済み」と確定しました"
                + "（DecisionId={DecisionId} 注文ID={OrderId} 銘柄={Symbol} 数量={Quantity} 状態={Status}）。"
                + "🔴 エントリーであれば、この時点では保護レグがありません。承認時の手法で続けて張ります"
                + "（結果は直後の行と通知に出ます）。直後に結果の行が無ければ、証券会社の画面で保護レグの有無を確認してください。",
            finding.DecisionId, finding.OrderId, finding.Symbol, finding.Quantity, finding.Status);

    // 🔴 FR-10, #853, IADR-0428 決定4: 確定した 1 件に保護レグを張った結果の出口。**記録が先・発行が後**（EmitAsync と同じ理由）。
    // 発行は常駐ガードと同じ補償つきの発行（ProtectiveStopGuardService.PublishAllAsync）を使う——据え置きの通知
    // （StopDispatchIndeterminate）を発行できなかったのに「通知済み」と覚えると、ガードの再通知が 1 時間黙る。
    // 発行の失敗は握り潰さない（EmitAsync と同じ）。
    public async Task EmitProtectionAsync(ReconciledEntryProtectionEmission emission)
    {
        ReportProtection(emission);

        if (emission.Outcome is not { Events.Count: > 0 } outcome)
            return;

        using var scope = scopeFactory.CreateScope();
        var bus = new MessageBus(runtime);
        await ProtectiveStopGuardService.PublishAllAsync(
                outcome.Events, evt => bus.PublishAsync(evt),
                scope.ServiceProvider.GetService<HeldCloseNotificationTracker>())
            .ConfigureAwait(false);
    }

    // #853, IADR-0428 決定4: 保護の結果を種類ごとに記録する。「張った」「据え置いた」「張れない」「要らない」を混ぜない。
    private void ReportProtection(ReconciledEntryProtectionEmission emission)
    {
        var confirmed = emission.Confirmed;
        if (emission.Failure is { } failure)
        {
            logger.LogCritical(failure,
                "発注予約リコンサイル: 突合で確定した注文に保護レグを張る処理が失敗しました。**エントリーであれば無保護の建玉が"
                    + "残っている可能性があります。**証券会社の画面で建玉と逆指値を確認してください"
                    + "（DecisionId={DecisionId} 注文ID={OrderId} 銘柄={Symbol} 数量={Quantity}）。",
                confirmed.DecisionId, confirmed.OrderId, confirmed.Symbol, confirmed.Quantity);
            return;
        }

        if (emission.Outcome is not { } outcome)
        {
            if (emission.ProbeConfirmed)
            {
                logger.LogCritical(
                    "発注予約リコンサイル: この構成には、突合で確定したエントリーへ保護レグを張る口がありません。"
                        + "**エントリーであれば保護レグは張られていません。**証券会社の画面で確認してください"
                        + "（DecisionId={DecisionId} 注文ID={OrderId} 銘柄={Symbol}）。",
                    confirmed.DecisionId, confirmed.OrderId, confirmed.Symbol);
            }

            return;
        }

        switch (outcome.Kind)
        {
            case ReconciledEntryProtectionKind.NoProtectionRecord:
                // 自己修復（通常フローが記録を作った）では、保護レグの有無も通常フローが決めている（IADR-0362 決定 3）。
                if (!emission.ProbeConfirmed)
                    return;

                logger.LogCritical(
                    "発注予約リコンサイル: 突合で確定した注文には保護の記録がありません。**エントリーであれば保護逆指値は"
                        + "張られていません**（S2 の免除、または承認時の保護の文脈を残す前に止まった注文）。手仕舞い・保護レグの"
                        + "突合であれば該当しません。証券会社の画面で建玉と逆指値を確認してください"
                        + "（DecisionId={DecisionId} 注文ID={OrderId} 銘柄={Symbol} 数量={Quantity}）。",
                    confirmed.DecisionId, confirmed.OrderId, confirmed.Symbol, confirmed.Quantity);
                break;

            case ReconciledEntryProtectionKind.BrokerStopPlaced:
                logger.LogWarning(
                    "発注予約リコンサイル: 突合で確定したエントリーに、承認時の手法で保護逆指値を張りました"
                        + "（DecisionId={DecisionId} 銘柄={Symbol}）。",
                    confirmed.DecisionId, confirmed.Symbol);
                break;

            case ReconciledEntryProtectionKind.SoftwareStopArmed:
                logger.LogWarning(
                    "発注予約リコンサイル: 突合で確定したエントリーはソフトウェア逆指値（S1）で守られています"
                        + "（記録は発注前に武装済み。DecisionId={DecisionId} 銘柄={Symbol}）。",
                    confirmed.DecisionId, confirmed.Symbol);
                break;

            case ReconciledEntryProtectionKind.StopDispatchHeld:
            case ReconciledEntryProtectionKind.CoverageLost:
                // 通知（ProtectiveStopCoverageLost・Critical）が人へ届く。ここでは突合との相関だけを残す。
                logger.LogError(
                    "発注予約リコンサイル: 突合で確定したエントリーに保護逆指値を張れませんでした（{Kind}）。"
                        + "続く保護喪失の通知に従ってください（DecisionId={DecisionId} 銘柄={Symbol}）。",
                    outcome.Kind, confirmed.DecisionId, confirmed.Symbol);
                break;

            case ReconciledEntryProtectionKind.ProtectiveLeg:
                logger.LogInformation(
                    "発注予約リコンサイル: 突合で確定したのは保護レグ（据え置いた逆指値・成行手仕舞い）でした。保護記録の巡回が"
                        + "結果を引き取ります（DecisionId={DecisionId} 注文ID={OrderId}）。",
                    confirmed.DecisionId, confirmed.OrderId);
                break;

            default:
                // AlreadyHandled（通常フローが既に扱った）・NotRequired（建玉が生じていない）。
                logger.LogInformation(
                    "発注予約リコンサイル: 突合で確定した注文の保護は不要か既に扱われています（{Kind}・DecisionId={DecisionId}）。",
                    outcome.Kind, confirmed.DecisionId);
                break;
        }
    }

    // #856, IADR-0362: 1 巡回の要約を記録に落とす。
    // 🔴 #890, IADR-0371: ここには**巡回が最後まで回りきって初めて言えること**しか置かない
    // （件数・失敗数・据え置き）。確定済み 1 件の事実は EmitAsync が既に出している。
    private void ReportRoundSummary(ReservationReconciliationResult result, bool scanComplete)
    {
        // FR-05, NFR-09, #856, IADR-0441: 確定しなかった判定を件数で計上する（確定した分は EmitAsync が 1 件ずつ数え済み）。
        // これらの予約は Reserved のまま次の巡回に載るため、巡回が中断されて計上されなくても次の巡回で数え直される。
        // 🔴 held-not-placed は門を開けてよいかの観測に使う（ブローカーに存在する注文に対して出たら門を開けない。#856）。
        // 🔴 #1051, IADR-0444 決定6: 予約の取引環境ごとに数える（ADR-0045 決定1 (b) の held-not-placed は取引環境ごと）。
        foreach (var group in result.Verdicts.GroupBy(v => (v.Kind, v.ReservationProvider)))
        {
            metrics.RecordOrderReservationReconciliation(
                OutcomeOf(group.Key.Kind), group.Key.ReservationProvider, group.Count());
        }

        // #856 監査 N3: 据え置き（Held）も件数に出す。出さないと、全件が門で据え置かれた巡回が
        // 「滞留 5 件を走査（終端化 0 / 解放 0 / 不確定 0 / 失敗 0）」になり、運用者には内訳の合わない行に見える。
        //
        // ⚠️ #882 監査 N-5: **内訳名は門を開けても変わらない**（そのときは値が 0 になるだけである）。
        // ⚠️ #882 監査 N-1: **合計が Scanned に一致するのは門が閉じているあいだだけ**である。門を開けると、
        //    解放しようとした行が並行に消えていた場合（`Release()` が false）がどの内訳にも計上されない。
        //    #1343, IADR-0551: 承認の経路の予約は削除ではなく見送り（Forgone）として閉じ、件数は同じ「解放」に数える
        //    （並行に確定していて見送りにできなかった行は、同じくどの内訳にも計上されない）。
        //    門を開ける PR は、この 2 つ（内訳名と合計の不変条件）を運用手順ごと見直すこと。
        if (result.Scanned > 0)
            logger.LogInformation(
                "発注予約リコンサイル: 滞留 {Scanned} 件を走査"
                    + "（終端化 {Terminalized} / 解放 {Released} / 据え置き〔未発注だが門が閉〕 {Held} / 不確定 {Indeterminate} / 失敗 {Failed}）。",
                result.Scanned, result.Terminalized, result.Released, result.HeldNotPlaced.Count,
                result.Indeterminate, result.Failed);

        if (result.Failed > 0)
            logger.LogWarning(
                "発注予約リコンサイルで {Failed} 件が例外により未処理でした（据え置き＝次回巡回で再試行）。", result.Failed);

        // 🔴 FR-05, FR-10, #1343, IADR-0551 決定2: 解放の門が開いていて見送り（Forgone）として閉じた新規建ての、保護の記録の扱いを 1 件ずつ残す。
        // 閉じられなかった行は「発注の記録が無いまま残った保護の記録」であり、放置すると同じ銘柄・同じ方向の別の手法の新規建てを見送り続ける。
        // 予約は既に Forgone で次の巡回に載らないため、ここで出し損ねても S1 の行は幽霊の疑いの警告（IADR-0538）が 1 日 1 回拾う。
        foreach (var closure in result.UnplacedClosures)
        {
            switch (closure.Closure)
            {
                case UnplacedEntryProtectionClosure.SoftwareStopClosed:
                case UnplacedEntryProtectionClosure.AwaitingEntryClosed:
                    logger.LogInformation(
                        "発注予約リコンサイル: 未発注と確定した新規建てを見送りとして閉じ、送る前に残した保護の記録を完了にしました"
                            + "（DecisionId={DecisionId} 記録={Closure}）。同じ承認は再投入されても発注しません。",
                        closure.DecisionId, closure.Closure);
                    break;

                case UnplacedEntryProtectionClosure.LeftOpen:
                    logger.LogWarning(
                        closure.Failure,
                        "発注予約リコンサイル: 未発注と確定した新規建てを見送りとして閉じましたが、送る前に残した保護の記録を閉じられませんでした"
                            + "（DecisionId={DecisionId}。発注の記録がある・到達や決済の経路に入っている・並行に更新された・処理が失敗した のいずれか）。"
                            + "残った記録は同じ銘柄・同じ方向の別の手法の新規建てを見送らせます。発注経路の Runbook"
                            + "「発注の記録が無いまま残った S1 の記録を閉じる」に従って確かめてください。",
                        closure.DecisionId);
                    break;
            }
        }

        // #856, IADR-0362: 解放の門が閉じているため据え置いた「未発注」判定。据え置き自体は安全側だが、
        // 滞留は解消していない。門を開ける（＝実機検証を行う）判断の入力として毎巡回で残す。
        //
        // 🔴 #890, IADR-0371: これは巡回の末尾のままでよい。据え置いた予約は **Reserved のまま**であり
        // 次回巡回の FindStalledReserved に載るため、巡回が中断されても警告は失われない
        // （永久に失われるのは「確定済みで再走査されない」ものだけである）。
        // 🔴 #1051, IADR-0444: 予約の取引環境も出す（どちらの門の判断材料かを取り違えないため。不明は不明と出す）。
        // 🔴 #856, IADR-0538: **Warning は同じ予約について初回と再通知の間隔（既定 24 時間）ごとだけ**にする。
        // 門が閉じている限り同じ予約は人が片付けるまで永久に載り直し、毎時の Warning が続いていた（2026-10-05 の故障注入の AAPL）。
        // 間の巡回も **DecisionId つきの Information で必ず残す**（門を開ける判断の記録 (b) と後始末の確認が grep する）。計器は上で毎巡回数えている。
        _heldWarnings ??= new HeldNotPlacedWarningThrottle(options.Value.HeldNotPlacedReminder);
        foreach (var notice in _heldWarnings.Classify(result.Verdicts, scanComplete, clock.UtcNow))
        {
            var provider = notice.ReservationProvider?.ToString() ?? BusinessMetrics.ReservationReconciliationProviderUnknown;
            if (notice.Warn)
            {
                logger.LogWarning(
                    "発注予約リコンサイル: 照会は「未発注」と答えましたが、この予約の取引環境の解放の門が閉じているため据え置きます"
                        + "（DecisionId={DecisionId} 取引環境={ReservationProvider}）。門を開けてよいのは、その取引環境の実機の記録で"
                        + "誤判定が無いことを示した後だけです（#856 / #1051。取引環境が不明な予約はどちらの門でも解放しません）。"
                        + "それまでは人が証券会社の画面で確認してください。片付けは発注経路の Runbook「滞留した予約を人が解決する」"
                        + "（S1 の記録が残っていれば「発注の記録が無いまま残った S1 の記録を閉じる」も）に従います。"
                        + "同じ予約が据え置かれ続ける間、この警告は {NextWarnAt:O} まで繰り返しません（毎巡回の継続は Information に残します。初回 {FirstWarnedAt:O}）。",
                    notice.DecisionId, provider, notice.NextWarnAt, notice.FirstWarnedAt);
            }
            else
            {
                logger.LogInformation(
                    "発注予約リコンサイル: 据え置きが続いています（照会は「未発注」・解放の門は閉。DecisionId={DecisionId} 取引環境={ReservationProvider}）。"
                        + "警告は初回 {FirstWarnedAt:O} に出しました。次の警告は {NextWarnAt:O} 以降です。",
                    notice.DecisionId, provider, notice.FirstWarnedAt, notice.NextWarnAt);
            }
        }
    }

    // #1051, IADR-0444 決定6: 確定しなかった判定の種類 → 計器の outcome の値。
    private static string OutcomeOf(ReservationReconciliationVerdictKind kind) => kind switch
    {
        ReservationReconciliationVerdictKind.HeldNotPlaced => BusinessMetrics.ReservationReconciliationHeldNotPlaced,
        ReservationReconciliationVerdictKind.Released => BusinessMetrics.ReservationReconciliationReleased,
        ReservationReconciliationVerdictKind.Indeterminate => BusinessMetrics.ReservationReconciliationIndeterminate,
        ReservationReconciliationVerdictKind.Failed => BusinessMetrics.ReservationReconciliationFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知の判定の種類"),
    };
}
