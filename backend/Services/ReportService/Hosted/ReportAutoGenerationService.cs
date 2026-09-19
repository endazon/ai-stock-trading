using ReportService.Domain;
using ReportService.Features.Reports;
// IADR-0128: Web SDK（旧 Worker）の暗黙 using に頼っていた型を、ライブラリ SDK では明示する。
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReportService.Hosted;

// FR-06/07, UC-03〜05, 04_workflows/03_reporting-cycle, IADR-0115, #280: 報告書自動生成の常駐ドライバ。
// 閉場後の日報・週報・月報のドラフトを生成し、提示（PendingApproval）まで進める。**確定はしない**（ADR-0003）。
//
// 実装作法は ObservedDrawdownRefreshService（IADR-0103）・WithdrawalEvaluationService（IADR-0083）に準拠する:
// PeriodicTimer で定時、巡回ごとに DI スコープを作って scoped な ReportAutoGenerator（EF ストア）を解決し、
// 例外は捕捉して次周期へ縮退する（1 巡回の失敗で常駐を落とさない）。多重起動は逐次 await で防ぐ。
//
// 生成対象の判定は「境界時刻を過ぎていて未生成」であり、巡回時刻の一致を要求しない。したがって巡回の遅延・
// プロセス再起動があっても当期ぶんは次の巡回で回収される（IADR-0115 決定2/3）。
//
// #840, IADR-0352 決定 3・4: 依存先が一過性に落ちていて生成を見送った期間があるときは、**次の巡回を早める**
// （30 秒 → 60 → 120 → …。上限は通常の巡回間隔）。再起動の直後に依存先が立ち上がるまでの数分を、
// 通常の 5 分間隔より細かく拾うためである。見送りが無くなれば通常の間隔へ戻す。
public sealed class ReportAutoGenerationService(
    IServiceScopeFactory scopeFactory,
    IOptions<ReportAutoGenerationOptions> options,
    ILogger<ReportAutoGenerationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.Interval;
        using var timer = new PeriodicTimer(interval);

        do
        {
            var next = interval;
            try
            {
                next = await RunOnceAsync(stoppingToken).ConfigureAwait(false) ?? interval;
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

            // 変わったときだけ設定する（設定のたびにタイマーが張り直されるため、通常時の定時性を崩さない）。
            if (timer.Period != next)
                timer.Period = next;
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    // 1 巡回。単体テスト可能な単位として公開する。
    // 戻り値は**次の巡回を早めたい待ち時間**（見送った期間があるときだけ非 null。#840 / IADR-0352 決定 3）。
    public async Task<TimeSpan?> RunOnceAsync(CancellationToken cancellationToken)
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

        foreach (var deferral in result.Deferred)
        {
            // #840, IADR-0352 決定 3: 見送り。報告書は保存も提示もしていない（承認待ちに並んでいない）。
            logger.LogWarning(
                "報告書ドラフト {PeriodKey} の生成を見送りました。依存先が一過性に応答していません（未供給になる入力: {Inputs}・原因: {Causes}）。"
                + "約 {RetryAfterSeconds} 秒後に再試行します（見送り {Attempt}/{MaxDeferrals} 回目）。",
                deferral.PeriodKey,
                string.Join("、", ReportInputs.Labels(deferral.WaitingFor)),
                string.Join(" / ", deferral.Causes),
                (int)deferral.RetryAfter.TotalSeconds,
                deferral.Attempt,
                deferral.MaxDeferrals);
        }

        foreach (var degradation in result.Degraded)
        {
            // #840, IADR-0352 決定 4: 縮退した報告書を**黙って通さない**。恒常的な失敗（401/403・未設定）でも、
            // 見送りの上限に達した場合でも、どの入力が欠けたまま提示されたかを警告として残す。
            var inputs = string.Join("、", ReportInputs.Labels(degradation.UnsuppliedInputs));
            if (degradation.RetriesExhausted)
                logger.LogWarning(
                    "報告書ドラフト {PeriodKey} は、依存先が回復しないまま見送りの上限に達したため、入力が未供給のまま生成しました: {Inputs}。"
                    + "提示の通知と /report show に同じ内容を表示しています。確定の前に本文を確認してください。",
                    degradation.PeriodKey, inputs);
            else
                logger.LogWarning(
                    "報告書ドラフト {PeriodKey} は、入力が未供給のまま生成しました: {Inputs}。"
                    + "提示の通知と /report show に同じ内容を表示しています。確定の前に本文を確認してください。",
                    degradation.PeriodKey, inputs);
        }

        foreach (var failure in result.Failed)
        {
            // 期間単位の失敗。他の期間の生成は継続しており、この期間は次周期で再試行される。
            logger.LogWarning(failure.Error, "報告書ドラフトの自動生成に失敗しました: {PeriodKey}。次回巡回で再試行します。",
                failure.PeriodKey);
        }

        // 見送った期間が複数あれば、最も早い再試行に合わせる（他方も同じ巡回で再び対象になる）。
        return result.Deferred.Count == 0 ? null : result.Deferred.Min(d => d.RetryAfter);
    }
}
