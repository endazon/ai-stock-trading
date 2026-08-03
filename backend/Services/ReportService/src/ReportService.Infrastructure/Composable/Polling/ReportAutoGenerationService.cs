using AiStockTrading.Report.Application.Services;
// IADR-0128: Web SDK（旧 Worker）の暗黙 using に頼っていた型を、ライブラリ SDK では明示する。
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiStockTrading.Report.Infrastructure.Composable.Polling;

// FR-06/07, UC-03〜05, 04_workflows/03_reporting-cycle, IADR-0115, #280: 報告書自動生成の常駐ドライバ。
// 閉場後の日報・週報・月報のドラフトを生成し、提示（PendingApproval）まで進める。**確定はしない**（ADR-0003）。
//
// 実装作法は ObservedDrawdownRefreshService（IADR-0103）・WithdrawalEvaluationService（IADR-0083）に準拠する:
// PeriodicTimer で定時、巡回ごとに DI スコープを作って scoped な ReportAutoGenerator（EF ストア）を解決し、
// 例外は捕捉して次周期へ縮退する（1 巡回の失敗で常駐を落とさない）。多重起動は逐次 await で防ぐ。
//
// 生成対象の判定は「境界時刻を過ぎていて未生成」であり、巡回時刻の一致を要求しない。したがって巡回の遅延・
// プロセス再起動があっても当期ぶんは次の巡回で回収される（IADR-0115 決定2/3）。
internal sealed class ReportAutoGenerationService(
    IServiceScopeFactory scopeFactory,
    IOptions<ReportAutoGenerationOptions> options,
    ILogger<ReportAutoGenerationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);

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
                // フェイルセーフ: 1 巡回の失敗で常駐を落とさない。未生成の期間は次周期で再び対象になる
                // （冪等の根拠は PeriodKey の存在であり、部分的に生成された期間は二度作られない）。
                logger.LogError(ex, "報告書の自動生成でエラーが発生しました。次回巡回を継続します。");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    // 1 巡回。単体テスト可能な単位として公開する。
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var generator = scope.ServiceProvider.GetRequiredService<ReportAutoGenerator>();

        var result = await generator.RunOnceAsync(cancellationToken).ConfigureAwait(false);

        // 提示まで到達した期間だけを「提示しました」と記録する。未提示の期間は直後の警告が正であり、
        // 同一 PeriodKey に矛盾する 2 行を出さない。
        var notPresented = result.NotPresented.ToHashSet(StringComparer.Ordinal);

        foreach (var report in result.Generated.Where(r => !notPresented.Contains(r.PeriodKey)))
        {
            logger.LogInformation(
                "報告書ドラフトを自動生成し提示しました: {PeriodKey}（{Kind}）。確定は利用者の承認が必要です（ADR-0003）。",
                report.PeriodKey, report.Kind);
        }

        foreach (var periodKey in result.NotPresented)
        {
            // 生成はできたが承認待ちに並んでいない（状態機械が提示を拒否した）。次巡回は PeriodKey 一致でスキップされるため
            // 自動では回復しない＝利用者が気付けるよう警告として残す。
            logger.LogWarning(
                "報告書ドラフト {PeriodKey} の提示（承認待ちへの遷移）が受理されませんでした。承認待ち一覧に並びません。",
                periodKey);
        }

        foreach (var periodKey in result.NotificationFailed)
        {
            // FR-09, IADR-0116 決定2: 提示はできたが確定依頼が発行できていない。ドラフトは承認待ちに並んでいるので
            // 生成は巻き戻さないが、利用者が「届かない」ことに気付けるよう警告として残す（黙って捨てない）。
            logger.LogWarning(
                "報告書ドラフト {PeriodKey} の提示通知を発行できませんでした。承認待ちには並んでいます（確定依頼は届きません）。",
                periodKey);
        }

        foreach (var failure in result.Failed)
        {
            // 期間単位の失敗。他の期間の生成は継続しており、この期間は次周期で再試行される。
            logger.LogWarning(failure.Error, "報告書ドラフトの自動生成に失敗しました: {PeriodKey}。次回巡回で再試行します。",
                failure.PeriodKey);
        }
    }
}
