using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution;
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
// 2 つを明示的にログする——突合で確定した注文（**保護レグを持たない**。#853）と、門が閉じて据え置いた未発注判定。
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
    ILogger<OrderReservationReconciliationService> logger)
    : BackgroundService, IReservationReconciliationSink
{
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

        logger.LogInformation(
            "発注予約の自動リコンサイルを開始します（滞留閾値 {Hours} 時間・間隔 {Interval}・未発注時の解放 {Release}）。"
                + " 照会不達・不確定は解放しません（fail-safe）。",
            ReconciliationPolicy.EffectiveStallThresholdHours(options.Value.StallThresholdHours),
            options.Value.Interval,
            options.Value.ReleaseOnNotPlaced ? "許可" : "禁止（#856 の実機検証まで閉じる）");

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
        ReportRoundSummary(result);

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

        // ADR-0013, IADR-0129, #354: BackgroundService（singleton）からの発行。Wolverine の IMessageBus は scoped で
        // singleton へ注入できないため、singleton の IWolverineRuntime から MessageBus を作って発行する。
        //
        // 🔴 CancellationToken を渡さないのは意図的である（EmitAsync が受け取らない理由も同じ）。
        // 確定（commit）済みの 1 件の発行は、停止要求で取り下げてよいものではない。
        await new MessageBus(runtime).PublishAsync(emission.Executed).ConfigureAwait(false);
    }

    // 🔴 FR-05, FR-10, #856, IADR-0362（#853 の 2 番）: 突合で「発注済み」と確定した注文には、
    // **この経路が保護逆指値を張っていない**。エントリーであれば無保護の建玉が台帳へ載ったということである。
    // 通知（OrderExecuted）は「約定した」としか言わないので、保護が無い事実はここでしか出ない。**無音にしない。**
    private void ReportProbeTerminalized(ReservationReconciliationFinding finding) =>
        logger.LogCritical(
            "発注予約リコンサイル: 滞留していた予約を突合で「発注済み」と確定しました"
                + "（DecisionId={DecisionId} 注文ID={OrderId} 銘柄={Symbol} 数量={Quantity} 状態={Status}）。"
                + "🔴 **この経路は保護逆指値を張りません。** エントリーであれば無保護の建玉です。"
                + "証券会社の画面で保護レグの有無を確認してください（張るか否かの裁定は #853）。",
            finding.DecisionId, finding.OrderId, finding.Symbol, finding.Quantity, finding.Status);

    // #856, IADR-0362: 1 巡回の要約を記録に落とす。
    // 🔴 #890, IADR-0371: ここには**巡回が最後まで回りきって初めて言えること**しか置かない
    // （件数・失敗数・据え置き）。確定済み 1 件の事実は EmitAsync が既に出している。
    private void ReportRoundSummary(ReservationReconciliationResult result)
    {
        // #856 監査 N3: 据え置き（Held）も件数に出す。出さないと、全件が門で据え置かれた巡回が
        // 「滞留 5 件を走査（終端化 0 / 解放 0 / 不確定 0 / 失敗 0）」になり、運用者には内訳の合わない行に見える。
        //
        // ⚠️ #882 監査 N-5: **内訳名は門を開けても変わらない**（そのときは値が 0 になるだけである）。
        // ⚠️ #882 監査 N-1: **合計が Scanned に一致するのは門が閉じているあいだだけ**である。門を開けると、
        //    解放しようとした行が並行に消えていた場合（`Release()` が false）がどの内訳にも計上されない。
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

        // #856, IADR-0362: 解放の門が閉じているため据え置いた「未発注」判定。据え置き自体は安全側だが、
        // 滞留は解消していない。門を開ける（＝実機検証を行う）判断の入力として毎巡回で出す。
        //
        // 🔴 #890, IADR-0371: これは巡回の末尾のままでよい。据え置いた予約は **Reserved のまま**であり
        // 次回巡回の FindStalledReserved に載るため、巡回が中断されても警告は失われない
        // （永久に失われるのは「確定済みで再走査されない」ものだけである）。
        foreach (var decisionId in result.HeldNotPlaced)
            logger.LogWarning(
                "発注予約リコンサイル: 照会は「未発注」と答えましたが、解放の門が閉じているため据え置きます"
                    + "（DecisionId={DecisionId}）。Reconciliation:ReleaseOnNotPlaced=true にしてよいのは、"
                    + "実機で誤判定が無いことを確かめた後だけです（#856）。それまでは人が証券会社の画面で確認してください。",
                decisionId);
    }
}
