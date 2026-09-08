using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;

namespace TradeDecisionService.Hosted;

// FR-04, FR-15, NFR（費用）, ADR-0033 決定5, #632, IADR-0318 決定5: Stage 0 記録の起動口（**run-once**）。
//
// 🔴 **既定では走らない。** 常駐は常に登録するが、起動時に 1 回だけ `Stage0DecisionRecorder.RunAsync` を呼び、
// そこが無効・未構成・未承認のいずれかなら **LLM を 1 回も呼ばずに戻る**（見積りだけをログへ出す）。
// 巡回しないのは、記録が**一度きりの資材づくり**だからである —— 定時で回すと、承認した見積りを
// 何度も消費する（ADR-0033 決定5 が避けたい向き）。
//
// 起動口を HTTP エンドポイントにしなかった理由は IADR-0318 決定5 に記す（本サービスの HTTP 面は
// ヘルスチェックと自己申告のみで無認可であり、費用を発生させる口をそこへ足さない）。
public sealed class Stage0RecordingService(
    IServiceScopeFactory scopeFactory,
    IOptions<Stage0RecordingOptions> options,
    ILogger<Stage0RecordingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            // fail-safe: 有効化するまで 1 度も走らない。見積りだけは出す（ADR-0033 決定5 の「提示」）。
            using var previewScope = scopeFactory.CreateScope();
            var preview = previewScope.ServiceProvider.GetRequiredService<Stage0DecisionRecorder>()
                .Estimate(options.Value);
            logger.LogInformation(
                "Stage 0 記録は無効です（既定）。見積りのみ提示します: 呼び出し {Calls} 回・合計 {TotalJpy} 円。"
                + "実行するには {Section}:Enabled=true と承認値の設定が要ります。",
                preview.CallCount, preview.TotalJpy, Stage0RecordingOptions.SectionName);
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var recorder = scope.ServiceProvider.GetRequiredService<Stage0DecisionRecorder>();
            var outcome = await recorder.RunAsync(options.Value, stoppingToken).ConfigureAwait(false);

            logger.LogInformation(
                "Stage 0 記録の実行を終えました: {Status} —— {Reason}", outcome.Status, outcome.Reason);
        }
        catch (OperationCanceledException)
        {
            // 停止要求。記録は途中までで終わる（保存済みの分は残る）。
        }
        catch (Exception ex)
        {
            // 常駐の失敗でプロセスを落とさない（記録は本番取引の経路ではない）。
            logger.LogError(ex, "Stage 0 記録の実行でエラーが発生しました。");
        }
    }
}
