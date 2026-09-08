using AiStockTrading.Shared.Contracts.Backtest;
using Microsoft.Extensions.Logging;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-15, ADR-0033 決定2/決定5, #632, IADR-0318: 記録集合をファイル（JSON）へ書き出すアダプタ。
// 再生側（BacktestService の `FileStage0DecisionRecordSource`）が同じ直列化で読む。
//
// 🔴 **失敗を例外にしない。** 記録の実行は費用を伴い、書き出しの失敗で例外を投げると
// 「消費したのに何が起きたか分からない」状態になる。false を返して呼び出し元に報告させる。
public sealed class FileStage0DecisionRecordSink(string path, ILogger<FileStage0DecisionRecordSink> logger)
    : IStage0DecisionRecordSink
{
    public async Task<bool> SaveAsync(
        Stage0DecisionRecordSet recordSet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recordSet);

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            await File.WriteAllTextAsync(path, Stage0DecisionRecordJson.Serialize(recordSet), cancellationToken)
                .ConfigureAwait(false);

            logger.LogInformation(
                "Stage 0 記録を書き出しました（{Path}・判断 {Records} 件・戦略 {StrategyId}）。",
                path, recordSet.Records.Count, recordSet.StrategyId);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogError(ex, "Stage 0 記録の書き出しに失敗しました（{Path}）。", path);
            return false;
        }
    }
}
