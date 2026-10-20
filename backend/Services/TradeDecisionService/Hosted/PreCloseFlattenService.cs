using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradeDecisionService.Features.TradeDecision.FlattenBeforeClose;
using Wolverine;

namespace TradeDecisionService.Hosted;

// 🔴 FR-10, FR-19, #1396, IADR-0561 決定 3: 閉場前の手仕舞いの常駐（PeriodicTimer。既定 60 秒ごと）。
//
// 定時サイクル（情報収集の完了を起点にした LLM の判断）に載せない理由: サイクルの起点は収集に従属し、LLM の所要で遅れ・古い起点は捨てられる
// （IADR-0490）。閉場の M 分前という時刻に効く統制を、判断の巡回の遅れに従属させない（維持率割れの自動縮小の常駐〔IADR-0298〕と同じ選び方）。
// 方針の行でも有効になるため、構成で無効でも常駐は動き続ける（窓の外では照会もしない＝手元の時刻と暦だけで戻る）。
// 巡回ごとにスコープを作り、手仕舞いの判断を Wolverine で発行する（判断の決済と同じ経路）。1 巡回の失敗で常駐を落とさない。
public sealed class PreCloseFlattenService(
    IServiceScopeFactory scopeFactory,
    PreCloseFlattenOptions options,
    ILogger<PreCloseFlattenService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.IntervalSeconds));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "閉場前の手仕舞いの巡回でエラーが発生しました。次の巡回を継続します。");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>1 巡回（試験の単位）。出した手仕舞いの判断の件数を返す。</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var decisions = await scope.ServiceProvider.GetRequiredService<PreCloseFlattener>()
            .FlattenAsync(cancellationToken).ConfigureAwait(false);
        if (decisions.Count == 0)
            return 0;

        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        foreach (var decision in decisions)
            await bus.PublishAsync(decision).ConfigureAwait(false);

        return decisions.Count;
    }
}
