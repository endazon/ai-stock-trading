using AiStockTrading.Shared.Contracts.Backtest;
using BacktestService.Features.Backtest.EvaluateStage0Gate;
using Microsoft.Extensions.Logging;

namespace BacktestService.Infrastructure.ExternalServices;

// FR-04, FR-15, ADR-0033 決定2, #632, IADR-0318: AI 判断の記録集合をファイル（JSON）から読むアダプタ。
//
// 記録は取引判断サービスが別プロセスで作り、**運用がファイルとして持ち込む**（サービス間の同期照会にしない
// ——記録は数百 KB〜数 MB になり得る一括の資材であり、判定の巡回ごとに取りに行くものではない）。
//
// 🔴 **失敗はすべて「記録なし」へ倒す**（例外を投げない）。パスが未設定・ファイルが無い・読めない・
// JSON が壊れている、のいずれも呼び出し元では同じ結論（合格 verdict を出さない）になる。
// ただし**理由はログへ残す**——「有効化したのに評価されない」を運用が追えるようにするためである。
public sealed class FileStage0DecisionRecordSource(string? path, ILogger<FileStage0DecisionRecordSource> logger)
    : IStage0DecisionRecordSource
{
    public async Task<Stage0DecisionRecordSet?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            logger.LogWarning("Stage 0: 記録の供給パスが未設定です（Backtest:Stage0:Recording:Path）。記録なしとして扱います。");
            return null;
        }

        if (!File.Exists(path))
        {
            logger.LogWarning("Stage 0: 記録ファイルが見つかりません（{Path}）。記録なしとして扱います。", path);
            return null;
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Stage 0: 記録ファイルを読めません（{Path}）。記録なしとして扱います。", path);
            return null;
        }

        var recordSet = Stage0DecisionRecordJson.TryDeserialize(json);
        if (recordSet is null)
        {
            logger.LogWarning("Stage 0: 記録ファイルを解釈できません（{Path}）。記録なしとして扱います。", path);
            return null;
        }

        logger.LogInformation(
            "Stage 0: 記録を読み込みました（{Path}・期間 {From}〜{To}・判断 {Records} 件・戦略 {StrategyId}）。",
            path, recordSet.From, recordSet.To, recordSet.Records?.Count ?? 0, recordSet.StrategyId);
        return recordSet;
    }
}
