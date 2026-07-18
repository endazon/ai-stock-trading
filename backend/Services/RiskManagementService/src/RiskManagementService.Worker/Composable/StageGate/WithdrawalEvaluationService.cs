using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.Shared.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Options;

namespace AiStockTrading.RiskManagement.Worker.Composable.StageGate;

// FR-20, FR-11, FR-09, UC-06, ADR-0008, IADR-0070/0083, #166: 撤退（差し戻し）基準の定期評価ドライバ。
// StageGateService.EvaluateWithdrawal()（#20/IADR-0070 実装済み）を定時に叩き、撤退基準到達かつ HaltNewEntries なら
// kill switch を自動起動する（自動＝停止・承認＝段階変更。段階の実降格は提案に留める）。新規に停止したときだけ
// WithdrawalTriggered を発行して通知（FR-09）と中央監査（FR-11）へ連携する。
//
// 実績パターンは QuoteRefreshService（IADR-0066）に準拠する: PeriodicTimer で定時、巡回ごとに DI スコープを作り
// scoped な StageGateService/KillSwitchService/IPublishEndpoint を解決する（EF ストアは scoped）。例外は捕捉して
// 次周期へ縮退する（fail-safe・1 巡回の失敗で常駐を落とさない）。多重起動は逐次 await（オーバーラップなし）で防ぐ。
// 発注審査の同期ホットパス（OrderScreeningService）には触れず、背景で局所 stores を読むのみ。
//
// 既定は無効（opt-in）。有効化しても既定 StagePerformance（実 DD 未供給・BacktestMaxDrawdownRatio=0・起点 Stage 0）では
// AssessWithdrawal が発火しないため完全に不活性。実 DD 供給（別 issue）が結線されて初めて自動停止が作動する。
internal sealed class WithdrawalEvaluationService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IBusinessCalendar calendar,
    IOptions<WithdrawalEvaluationOptions> options,
    ILogger<WithdrawalEvaluationService> logger) : BackgroundService
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
                // フェイルセーフ: 1 巡回の失敗で常駐を落とさない。次周期で再評価する（kill switch の権威は不変）。
                logger.LogError(ex, "撤退の定期評価でエラーが発生しました。次回巡回を継続します。");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    // 1 巡回。休場日はスキップし、営業日のみ撤退を評価する。新規に自動停止したときだけ WithdrawalTriggered を発行する。
    // 単体テスト可能な単位として公開する。
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        // 市場休場ガード（#21 と同型）: 非営業日は評価しない。評価自体は冪等・無害だが churn を避ける。
        if (!calendar.IsBusinessDay(clock.Today))
            return;

        using var scope = scopeFactory.CreateScope();
        var stageGate = scope.ServiceProvider.GetRequiredService<StageGateService>();

        // EvaluateWithdrawal は撤退基準到達かつ HaltNewEntries なら kill switch を自動起動する（起動済みなら再起動しない）。
        // 「この呼び出しで新規に起動したか」は EvaluateWithdrawal が起動可否と同一箇所で判定して返す（NewlyEngaged）。
        var outcome = stageGate.EvaluateWithdrawal();

        // 新規に自動停止したときだけ通知する。判定はサービス側で確定済みのため、ドライバ側で kill switch 状態を別読みして
        // 比較する必要がなく、手動評価エンドポイントとの同時起動での誤通知も避けられる。kill switch 状態（DB 永続）が
        // 起動済みなら NewlyEngaged=false となり、撤退継続中・再起動後も再発行しない（スパム回避・冪等）。
        if (outcome.NewlyEngaged)
        {
            // NewlyEngaged ⟹ HaltNewEntries=true ⟹ AssessWithdrawal の契約で ProposedStage は非 null（Stage0Verification）。
            var assessment = outcome.Assessment;
            var bus = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
            await bus.Publish(
                new WithdrawalTriggered(
                    (int)assessment.ProposedStage!.Value,
                    assessment.Reason?.ToString() ?? string.Empty,
                    assessment.HaltNewEntries,
                    clock.UtcNow),
                cancellationToken).ConfigureAwait(false);
        }
    }
}
