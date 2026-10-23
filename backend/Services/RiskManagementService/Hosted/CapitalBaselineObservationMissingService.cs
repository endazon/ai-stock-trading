using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RiskManagementService.Common.Abstractions;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Persistence;
using Wolverine;

namespace RiskManagementService.Hosted;

// FR-10, FR-09, SC-03, #1423 M-3, ADR-0073 決定3, IADR-0581 決定3: **基準資金の口座照会の欠けを検知して知らせる巡回。**
//
// 計画は「直前の米国東部暦日に成功した観測が無ければ新規建てを拒否し、そのことを Discord と SC-03 に知らせる」と定める。
// 拒否そのものは審査（EfCapitalBaselineStore.GetCurrent）が行う。🔴 **通知を審査に載せない**のは、
// (1) 新規建ての要求が来ない限り鳴らない（気づくのが遅れる）、(2) 拒否のたびに鳴らすと本物の警告が埋もれる、の 2 点による。
// よって審査と同じ判定（ICapitalBaselineStore.Read。計器を動かさない口）を定時に読み、**取引日（米国東部の暦日）ごとに 1 回**だけ
// CapitalBaselineObservationMissing を発行する。
//
// 重複の抑止は in-memory（PublishingDailyPolicyUnconfirmedNotifier・IADR-0096 と同じ判断）。再起動を挟むと同じ取引日に
// 最大 1 回重なり得るが、通知が 1 通増えるだけで害は無い。発行に失敗したら記録を戻し、次の巡回で再試行する。
// 回復（直前の暦日の観測が戻った）は通知しない —— 解除は自動であり、計画も回復の通知は求めていない。
//
// 例外は捕捉して次周期へ縮退する（1 巡回の失敗で常駐を落とさない。WithdrawalEvaluationService と同じ形）。
public sealed class CapitalBaselineObservationMissingService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    CapitalBaselineOptions options,
    ILogger<CapitalBaselineObservationMissingService> logger) : BackgroundService
{
    private DateOnly? _notifiedTradingDay;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.ObservationMissingCheckIntervalSeconds));
        using var timer = new PeriodicTimer(interval);

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
                logger.LogError(ex, "基準資金の観測の欠けの検知でエラーが発生しました。次回巡回を継続します。");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>1 巡回。欠けていれば、その取引日に未通知のときだけ発行する。発行したら true。単体試験の単位。</summary>
    public async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = scopeFactory.CreateScope();
        var reading = scope.ServiceProvider.GetRequiredService<ICapitalBaselineStore>().Read();

        if (!reading.ObservationMissing || reading.LatestTradingDay is not { } latest)
            return false;
        if (_notifiedTradingDay == reading.Today)
            return false;

        _notifiedTradingDay = reading.Today;
        try
        {
            // ADR-0013, IADR-0129, #354: 発行は Wolverine の IMessageBus（scoped）。巡回ごとのスコープから解決する。
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            await bus.PublishAsync(new CapitalBaselineObservationMissing(
                reading.Today,
                latest,
                reading.Outcome == AiStockTrading.Shared.Contracts.Observability.CapitalBaselineReadOutcome.UnavailableStale,
                clock.UtcNow)).ConfigureAwait(false);
        }
        catch
        {
            _notifiedTradingDay = null; // 次の巡回で再試行する（恒久的に握り潰さない）
            throw;
        }

        logger.LogWarning(
            "基準資金の口座照会の欠けを通知しました（判定日 {TradingDay}・最後の観測 {LatestTradingDay}）。新規建てを止めています。",
            reading.Today,
            latest);
        return true;
    }
}
