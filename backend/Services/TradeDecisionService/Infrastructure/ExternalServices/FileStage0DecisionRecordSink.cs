using System.Globalization;
using AiStockTrading.Shared.Contracts.Backtest;
using Microsoft.Extensions.Logging;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-15, ADR-0033 決定2/決定5, #632, IADR-0318: 記録集合をファイル（JSON）へ書き出すアダプタ。
// 再生側（BacktestService の `FileStage0DecisionRecordSource`）が同じ直列化で読む。
//
// 🔴 **失敗を例外にしない。** 記録の実行は費用を伴い、書き出しの失敗で例外を投げると
// 「消費したのに何が起きたか分からない」状態になる。false を返して呼び出し元に報告させる。
//
// 🔴 #1342, IADR-0537 決定3: 書き出し先は永続ボリュームにあり、課金済みの記録が残る。
//   - **件数が減る上書きを拒む**（既存の記録の判断の件数より少ない集合では置き換えない。false を返す）。
//     置き換えたいときは、人が既存のファイルを退避してから記録し直す。読めない既存のファイルは守る対象にしない。
//   - 一時ファイルへ書いて flush してから置き換える（書き出しの途中で落ちても、既存の記録を半端なファイルにしない）。
public sealed class FileStage0DecisionRecordSink(string path, ILogger<FileStage0DecisionRecordSink> logger)
    : IStage0DecisionRecordSink
{
    public async Task<bool> SaveAsync(
        Stage0DecisionRecordSet recordSet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recordSet);

        var temp = $"{path}.tmp-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}";
        try
        {
            if (File.Exists(path)
                && Stage0DecisionRecordJson.TryDeserialize(
                    await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)) is { } existing
                && existing.Records.Count > recordSet.Records.Count)
            {
                logger.LogError(
                    "Stage 0 記録: 既存の記録（{Path}・判断 {Existing} 件・戦略 {ExistingStrategy}）を、より少ない判断 {Records} 件の記録で上書きしません。"
                    + "置き換えるには既存のファイルを退避してください。",
                    path, existing.Records.Count, existing.StrategyId, recordSet.Records.Count);
                return false;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(Stage0DecisionRecordJson.Serialize(recordSet).AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);

            logger.LogInformation(
                "Stage 0 記録を書き出しました（{Path}・判断 {Records} 件・戦略 {StrategyId}）。",
                path, recordSet.Records.Count, recordSet.StrategyId);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogError(ex, "Stage 0 記録の書き出しに失敗しました（{Path}）。", path);
            TryDelete(temp);
            return false;
        }
    }

    private static void TryDelete(string temp)
    {
        try
        {
            File.Delete(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 一時ファイルが残っても読み手は見ない（書き出し先と名前が違う）。
        }
    }
}
