using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReportService.Common.Abstractions;
using ReportService.Domain;
using ReportService.Features.Reports;
using Wolverine;

namespace ReportService.Hosted;

// FR-07, FR-09, UC-03, #1423 M-6, ADR-0075 決定4, IADR-0583 決定4: **日報の無応答を知らせる常駐。**
//
// 計画は「期限を過ぎて無応答になったときは、設定が継続でも停止でも、その旨を Discord へ通知する」と定める。方針の照会（取引判断が判断のたびに
// 呼ぶ）で鳴らすと、照会のたびに鳴るか、照会が来なければ鳴らない。よって方針の照会と同じ判定（DailyReportNoResponseEvaluator）を定時に読み、
// **期間キーごとに 1 回**だけ DailyReportNoResponse を発行する。
//
// 重複の抑止は in-memory（IADR-0096・IADR-0581 と同じ判断。再起動を挟むと同じ期間キーで最大 1 回重なり得るが、通知が 1 通増えるだけ）。
// 発行に失敗したら記録を戻し、次の巡回で再試行する。確定（解除）は通知しない —— 確定の通知は既存の ReportConfirmed が出る。
public sealed class DailyReportNoResponseMonitorService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    TimeSpan interval,
    ILogger<DailyReportNoResponseMonitorService> logger) : BackgroundService
{
    private string? _notifiedPeriodKey;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval > TimeSpan.Zero ? interval : TimeSpan.FromMinutes(5));
        do
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
                logger.LogError(ex, "日報の無応答の検知でエラーが発生しました。次回巡回を継続します。");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>1 巡回。承認待ちの日報が期限を過ぎていて、その期間キーを未通知のときだけ発行する。発行したら true。単体試験の単位。</summary>
    public async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = scopeFactory.CreateScope();
        var status = scope.ServiceProvider.GetRequiredService<DailyReportNoResponseEvaluator>().Evaluate();
        if (!status.Overdue || status.Pending is not { } pending || status.Deadline is not { } deadline)
            return false;
        if (_notifiedPeriodKey == pending.PeriodKey)
            return false;

        var lastConfirmed = scope.ServiceProvider.GetRequiredService<IReportStore>().GetLatestConfirmed(ReportKind.Daily);
        _notifiedPeriodKey = pending.PeriodKey;
        try
        {
            // ADR-0013, IADR-0129, #354: 発行は Wolverine の IMessageBus（scoped）。巡回ごとのスコープから解決する。
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().PublishAsync(new DailyReportNoResponse(
                pending.PeriodKey, pending.PeriodStart, deadline, status.Halted, lastConfirmed?.Report.PeriodKey, clock.UtcNow))
                .ConfigureAwait(false);
        }
        catch
        {
            _notifiedPeriodKey = null; // 次の巡回で再試行する
            throw;
        }

        logger.LogWarning(
            "日報 {PeriodKey} が承認待ちのまま期限（{Deadline}）を過ぎました。無応答時の動作 {Behavior}（停止={Halted}）を通知しました。",
            pending.PeriodKey, deadline, status.Behavior, status.Halted);
        return true;
    }
}
