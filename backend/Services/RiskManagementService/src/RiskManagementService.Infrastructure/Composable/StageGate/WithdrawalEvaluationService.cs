using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Events;
// IADR-0128: Web SDK（旧 Worker）の暗黙 using に頼っていた型を、ライブラリ SDK では明示する。
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;

namespace AiStockTrading.RiskManagement.Infrastructure.Composable.StageGate;

// FR-20, FR-11, FR-09, UC-06, ADR-0008, IADR-0070/0083, #166: 撤退（差し戻し）基準の定期評価ドライバ。
// StageGateService.EvaluateWithdrawal()（#20/IADR-0070 実装済み）を定時に叩き、撤退基準到達かつ HaltNewEntries なら
// kill switch を自動起動する（自動＝停止・承認＝段階変更。段階の実降格は提案に留める）。新規に停止したときだけ
// WithdrawalTriggered を発行して通知（FR-09）と中央監査（FR-11）へ連携する。
//
// 実績パターンは QuoteRefreshService（IADR-0066）に準拠する: PeriodicTimer で定時、巡回ごとに DI スコープを作り
// scoped な StageGateService/KillSwitchService/IMessageBus を解決する（EF ストアは scoped）。例外は捕捉して
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

    // 1 巡回。休場日はスキップし、営業日のみ撤退を評価する。停止経路（Stage 2/3・新規停止時）と非停止経路（Stage 1
    // ペーパー乖離・新規シグネチャ時）でそれぞれ WithdrawalTriggered を 1 回だけ発行する。単体テスト可能な単位として公開する。
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
        var assessment = outcome.Assessment;

        // 停止経路（Stage 2/3・#166/IADR-0083）: 新規に自動停止したときだけ通知する。判定はサービス側で確定済みのため、
        // ドライバ側で kill switch 状態を別読みして比較する必要がなく、手動評価エンドポイントとの同時起動での誤通知も避けられる。
        // kill switch 状態（DB 永続）が起動済みなら NewlyEngaged=false となり、撤退継続中・再起動後も再発行しない（冪等）。
        if (outcome.NewlyEngaged)
        {
            // NewlyEngaged ⟹ HaltNewEntries=true ⟹ AssessWithdrawal の契約で ProposedStage は非 null（Stage0Verification）。
            await PublishTriggeredAsync(scope, assessment, cancellationToken).ConfigureAwait(false);
        }

        // 非停止経路（Stage 1 ペーパー乖離・#189/IADR-0085）: kill switch を起動しないため、別の durable な「通知済み
        // シグネチャ」で重複排除する。新規シグネチャのときだけ 1 回発行し保存する（巡回ごとの通知スパムを避ける）。
        // 乖離が解消（該当しなくなった）ならシグネチャをクリアし、再乖離で再通知できるようにする。DB 永続のため再起動をまたいで冪等。
        var store = scope.ServiceProvider.GetRequiredService<IWithdrawalNotificationStore>();
        if (assessment is { Triggered: true, HaltNewEntries: false })
        {
            var signature = BuildSignature(assessment);
            if (store.GetNotifiedSignature() != signature)
            {
                // 発行を先に行い、成功後に「通知済み」を記録する。発行失敗時に降格提案を恒久的に握り潰さない（安全側）。
                // 記録失敗時のまれな重複は steady-state のスパムではなく、乖離継続中は一致で非発行に収束する。
                await PublishTriggeredAsync(scope, assessment, cancellationToken).ConfigureAwait(false);
                store.SaveNotifiedSignature(signature);
            }
        }
        else if (store.GetNotifiedSignature() is not null)
        {
            store.ClearNotifiedSignature();
        }
    }

    private Task PublishTriggeredAsync(
        IServiceScope scope, WithdrawalAssessment assessment, CancellationToken cancellationToken)
    {
        // ADR-0013, IADR-0129, #354: 発行は Wolverine の IMessageBus（scoped）。巡回ごとのスコープから解決する
        // （Wolverine の PublishAsync は CancellationToken を取らない）。
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        return bus.PublishAsync(
            new WithdrawalTriggered(
                (int)assessment.ProposedStage!.Value,
                assessment.Reason?.ToString() ?? string.Empty,
                assessment.HaltNewEntries,
                clock.UtcNow)).AsTask();
    }

    // 撤退提案の識別子。同一乖離が継続する間は不変＝再通知しない。理由と提案段階から算出し、将来 Stage 1 以外の
    // 非停止理由が増えても識別できる（Triggered な提案では Reason・ProposedStage とも非 null）。
    private static string BuildSignature(WithdrawalAssessment assessment) =>
        $"{assessment.Reason}:{(int)assessment.ProposedStage!.Value}";
}
