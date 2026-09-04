using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Common.Abstractions;
// IADR-0128: Web SDK（旧 Worker）の暗黙 using に頼っていた型を、ライブラリ SDK では明示する。
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;

namespace RiskManagementService.Hosted;

// FR-10, FR-11, UC-06, ADR-0003, ADR-0009, ADR-0016 決定7, #330, #634, IADR-0133, IADR-0298:
// 維持率割れ自動縮小（MaintenanceMarginReductionService）の定期評価ドライバ。
//
// #634: 本サービスは DI に登録されているだけで、それを解決して呼ぶ本番コードが存在しなかった
// （「供給元待ち」ではなく「未結線」）。本ドライバがその呼び出し元になる。
//
// 駆動方式は定時常駐（`PeriodicTimer`）を選ぶ。建玉観測（BrokerPositionsObserved）ハンドラからの駆動は
// 採らなかった——観測の到達に従属すると、観測の供給元が落ちた瞬間に評価そのものが止まる。マージンコールは
// 供給が落ちたときこそ危険度が上がる統制であり、自ら周期的に「供給なし」を観測し続ける方を選ぶ
// （IADR-0133 決定8 の SnapshotUntrusted 3 状態設計と噛み合う）。新規 IADR に選択の理由を記録する。
//
// 実績パターンは WithdrawalEvaluationService（IADR-0083）/ ObservedDrawdownRefreshService（IADR-0103）に
// 準拠する: PeriodicTimer で定時、巡回ごとに DI スコープを作り scoped な
// MaintenanceMarginReductionService/IMessageBus を解決する。例外は捕捉して次周期へ縮退する
// （fail-safe・1 巡回の失敗で常駐を落とさない）。多重起動は逐次 await（オーバーラップなし）で防ぐ。
//
// 既定は**有効**（他 2 件の既定無効とは意図的に異なる。MaintenanceMarginEvaluationOptions のコメント参照）。
// 本サービス自身は統制ストア（IKillSwitchStore/ILockoutStore/IPauseStore）を依存に持たない——
// MaintenanceMarginReductionService.Evaluate() を無条件に呼ぶだけであり、3 統制が成立していても
// 評価・発行を行う（UC-06・ADR-0009 の構造的保証をドライバが壊さない）。
public sealed class MaintenanceMarginEvaluationService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IBusinessCalendar calendar,
    IOptions<MaintenanceMarginEvaluationOptions> options,
    ILogger<MaintenanceMarginEvaluationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.IntervalSeconds));
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break; // 停止要求
            }
            catch (Exception ex)
            {
                // フェイルセーフ: 1 巡回の失敗で常駐を落とさない。次周期で再評価する
                // （動かす統制であり、常駐が死んで評価が止まること自体が統制の不作動を意味する）。
                logger.LogError(ex, "維持率割れ自動縮小の定期評価でエラーが発生しました。次回巡回を継続します。");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    // 1 巡回。休場日はスキップし、営業日のみ評価する。単体テスト可能な単位として公開する。
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        // 市場休場ガード（#21・WithdrawalEvaluationService/ObservedDrawdownRefreshService と同型）:
        // 維持率照会はブローカー口座照会であり、非営業日は建玉に変動が無く照会しても意味のある値が返らない。
        if (!calendar.IsBusinessDay(clock.Today))
            return;

        cancellationToken.ThrowIfCancellationRequested();

        using var scope = scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<MaintenanceMarginReductionService>();

        var evaluation = service.Evaluate();
        if (evaluation.Status != MaintenanceMarginEvaluationStatus.Reduced)
        {
            // NoActionRequired: 健全ゆえの無動作（巡回のたびログすると「何もしていない」がログを埋める）。
            // SnapshotUntrusted: MaintenanceMarginReductionService.Evaluate() が既に警告ログを出している
            // （IADR-0133 決定8）。二重記録を避け、ドライバ側では追加のログ・イベントを出さない。
            return;
        }

        var outcome = evaluation.Outcome!;

        // ADR-0013, IADR-0129, #354: 発行は Wolverine の IMessageBus（scoped）。巡回ごとのスコープから解決する。
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        // 決済（実発注へ渡す承認）を先に発行する。記録イベント（MaintenanceMarginReductionExecuted）は
        // 4 記録先（監査・Discord・日報・月報）の単一情報源であり（IADR-0133 決定7）、承認には維持率の
        // 情報を持たせない（PositionCloseService/ClosePositionEndpoint と同じ役割分担）。
        foreach (var approval in outcome.Approvals)
            await bus.PublishAsync(approval).ConfigureAwait(false);

        await bus.PublishAsync(outcome.Executed).ConfigureAwait(false);

        logger.LogWarning(
            "維持率割れ自動縮小が発動しました（決済前 {RatioBefore:P1} → 決済後 {RatioAfter}・閾値 {Threshold:P1}・{Count} 件決済）。",
            outcome.Plan.RatioBefore,
            outcome.Plan.RatioAfter is { } after ? $"{after:P1}" : "建玉なし",
            outcome.Plan.Threshold,
            outcome.Approvals.Count);
    }
}
