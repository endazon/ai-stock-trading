using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using Microsoft.Extensions.Options;

namespace AiStockTrading.RiskManagement.Worker.Composable.StageGate;

// FR-20, FR-10, UC-06, ADR-0008, IADR-0103, #164: 実DD（観測最大ドローダウン）の供給ドライバ。
// 段階別実績（IStagePerformanceStore）の運用系フィールド ObservedMaxDrawdownRatio を定時サンプリングで供給し、
// ADR-0008 の撤退基準（実DD ≥ バックテスト最大DD × 1.5 で自動停止）の入力を満たす。これが無いと
// WithdrawalEvaluationService（#166/IADR-0083）は有効化しても構造的に不活性のままになる。
//
// なぜサンプリングか: ObservedMaxDrawdownRatio は「観測した」最大 DD であり、取引台帳の約定点だけからは再計算できない。
// 約定と約定の間に生じる含み損の谷は、時価評価つきの PortfolioState.DrawdownRatio（IADR-0066）を定時に読み、
// 最大値を latch することでしか捉えられない。よって stateless な再計算ではなくストアへの単調 latch を採る。
//
// 供給源は Risk が自ら所有するデータ（取引台帳＋時価評価）のみで、他サービスへの s2s 照会もイベント購読も要らない
// （Database per Service・ADR-0001 を跨がない）。更新は read-modify-write で実DD だけに限定し、backtest 由来
// （BacktestEvaluatedProjectionConsumer が所有）と他の運用系フィールドは温存する（IADR-0089 の鏡像）。
//
// 実績パターンは WithdrawalEvaluationService（IADR-0083）に準拠する: PeriodicTimer で定時、巡回ごとに DI スコープを
// 作り scoped な IPortfolioStateProvider / IStagePerformanceStore を解決する。例外は捕捉して次周期へ縮退する
// （fail-safe・1 巡回の失敗で常駐を落とさない）。多重起動は逐次 await（オーバーラップなし）で防ぐ。
// 発注審査の同期ホットパス（OrderScreeningService）には触れず、背景で局所 stores を読むのみ。
//
// 既定は無効（opt-in）。有効化しても時価評価が既定無効（MarketData:EnableMarkToMarket=false）のうちは DD が常に 0 で
// 供給値が変わらず書き込みも起きない＝不活性。取得失敗時は既存値を維持する（供給不達で昇格・撤退の判定が緩まない）。
internal sealed class ObservedDrawdownRefreshService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IBusinessCalendar calendar,
    IOptions<ObservedDrawdownRefreshOptions> options,
    ILogger<ObservedDrawdownRefreshService> logger) : BackgroundService
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
                // フェイルセーフ: 1 巡回の失敗で常駐を落とさない。次周期で再サンプリングする
                // （書き込みを伴わないため、落とした巡回は次周期の latch で回復する）。
                logger.LogError(ex, "実DD の定期サンプリングでエラーが発生しました。次回巡回を継続します。");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    // 1 巡回。休場日はスキップし、営業日のみ実DD をサンプリングする。単体テスト可能な単位として公開する。
    public Task RunOnceAsync(CancellationToken cancellationToken)
    {
        // 市場休場ガード（#21・IADR-0083 と同型）: 非営業日は取引が無く DD も動かないため、台帳・時価に触れない。
        if (!calendar.IsBusinessDay(clock.Today))
            return Task.CompletedTask;

        cancellationToken.ThrowIfCancellationRequested();

        using var scope = scopeFactory.CreateScope();
        var sampled = scope.ServiceProvider.GetRequiredService<IPortfolioStateProvider>().GetCurrent().DrawdownRatio;

        var store = scope.ServiceProvider.GetRequiredService<IStagePerformanceStore>();
        var current = store.GetCurrent();
        var updated = StagePerformanceProjection.WithObservedDrawdown(current, sampled);

        // 単調 latch のため大半の巡回は無変化になる。無変化なら書かない（単一行への無用な更新を出さない）。
        if (updated.ObservedMaxDrawdownRatio != current.ObservedMaxDrawdownRatio)
        {
            store.Save(updated);
            logger.LogInformation(
                "実DD の観測最大値を更新: {Previous} → {Updated}",
                current.ObservedMaxDrawdownRatio, updated.ObservedMaxDrawdownRatio);
        }

        return Task.CompletedTask;
    }
}
