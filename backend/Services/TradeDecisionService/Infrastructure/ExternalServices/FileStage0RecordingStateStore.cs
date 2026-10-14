using System.Globalization;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Backtest;
using Microsoft.Extensions.Logging;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-15, NFR（費用）, ADR-0033 決定5, #1342, IADR-0537 決定4: Stage 0 の記録の実行の状態を、永続ボリューム上のファイルに置く。
//
// 置き方（`Stage0Recording:StateDirectory` の下）:
//   recorder.lock                    実行のロック（排他で開く。プロセスが落ちれば OS が外す）
//   runs/<印の鍵>.json               開始・終わりの印
//   runs/<印の鍵>.checkpoint.json    ここまでの記録（記録集合と同じ直列化）
//   ledger/<台帳の鍵>.json           費用の累計（円）
//
// 🔴 **印と台帳は別のファイル・別の鍵**である（片方が壊れても他方で止まる）。
// 🔴 書き込みは一時ファイルへ書いて flush してから置き換える（途中で落ちても半端なファイルを残さない）。
// 🔴 読めないファイル（壊れた JSON・権限）は「無い」とは扱わず「読めない」と答える（記録器は走らせない）。
public sealed class FileStage0RecordingStateStore(
    string directory, TimeProvider timeProvider, ILogger<FileStage0RecordingStateStore> logger)
    : IStage0RecordingStateStore, IStage0RecordingCostLedger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private string RunPath(string runKey) => Path.Combine(directory, "runs", $"{runKey}.json");

    private string CheckpointPath(string runKey) => Path.Combine(directory, "runs", $"{runKey}.checkpoint.json");

    private string LedgerPath(string ledgerKey) => Path.Combine(directory, "ledger", $"{ledgerKey}.json");

    public IDisposable? TryAcquireRunLock()
    {
        try
        {
            Directory.CreateDirectory(directory);
            // FileShare.None は Unix では flock(LOCK_EX|LOCK_NB) になる（同じノードの別プロセス・別の Pod でも排他）。
            return new FileStream(
                Path.Combine(directory, "recorder.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Stage 0 記録の実行のロックを取れません（{Directory}）。", directory);
            return null;
        }
    }

    public async Task<Stage0RunState> LoadAsync(string runKey, CancellationToken cancellationToken = default)
    {
        var path = RunPath(runKey);
        if (!File.Exists(path))
            return new Stage0RunState(Stage0RunPhase.NotStarted);

        RunMark? mark;
        try
        {
            mark = JsonSerializer.Deserialize<RunMark>(
                await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogError(ex, "Stage 0 記録の印が読めません（{Path}）。", path);
            return new Stage0RunState(Stage0RunPhase.Unreadable, Detail: $"印が読めない: {path}");
        }

        switch (mark?.Phase)
        {
            case nameof(Stage0RunPhase.Completed):
                return new Stage0RunState(Stage0RunPhase.Completed, Detail: mark.Detail);
            case nameof(Stage0RunPhase.Stopped):
                return new Stage0RunState(Stage0RunPhase.Stopped, Detail: mark.Detail);
            case nameof(Stage0RunPhase.InProgress):
                break;
            default:
                return new Stage0RunState(Stage0RunPhase.Unreadable, Detail: $"印の段階が不明: {path}");
        }

        var checkpointPath = CheckpointPath(runKey);
        if (!File.Exists(checkpointPath))
            return new Stage0RunState(Stage0RunPhase.InProgress); // 1 件目の前に落ちた

        string json;
        try
        {
            json = await File.ReadAllTextAsync(checkpointPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Stage 0 記録のチェックポイントが読めません（{Path}）。", checkpointPath);
            return new Stage0RunState(Stage0RunPhase.Unreadable, Detail: $"チェックポイントが読めない: {checkpointPath}");
        }

        return Stage0DecisionRecordJson.TryDeserialize(json) is { } checkpoint
            ? new Stage0RunState(Stage0RunPhase.InProgress, checkpoint)
            : new Stage0RunState(Stage0RunPhase.Unreadable, Detail: $"チェックポイントが読めない: {checkpointPath}");
    }

    public Task<bool> MarkStartedAsync(string runKey, CancellationToken cancellationToken = default) =>
        WriteMarkAsync(runKey, new RunMark(nameof(Stage0RunPhase.InProgress), timeProvider.GetUtcNow(), null, null), cancellationToken);

    public Task<bool> SaveCheckpointAsync(
        string runKey, Stage0DecisionRecordSet checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return WriteAtomicAsync(CheckpointPath(runKey), Stage0DecisionRecordJson.Serialize(checkpoint), cancellationToken);
    }

    public Task<bool> MarkFinishedAsync(
        string runKey, Stage0RunPhase phase, string detail, CancellationToken cancellationToken = default)
    {
        if (phase is not (Stage0RunPhase.Completed or Stage0RunPhase.Stopped))
            throw new ArgumentOutOfRangeException(nameof(phase), phase, "終わりの印は Completed か Stopped だけです。");

        return WriteMarkAsync(runKey, new RunMark(phase.ToString(), null, timeProvider.GetUtcNow(), detail), cancellationToken);
    }

    public async Task<decimal?> ReadAsync(string ledgerKey, CancellationToken cancellationToken = default)
    {
        var path = LedgerPath(ledgerKey);
        if (!File.Exists(path))
            return 0m;

        try
        {
            var entry = JsonSerializer.Deserialize<LedgerEntry>(
                await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions);
            if (entry is { CumulativeJpy: >= 0m })
                return entry.CumulativeJpy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogError(ex, "Stage 0 記録の費用の累計が読めません（{Path}）。", path);
            return null;
        }

        logger.LogError("Stage 0 記録の費用の累計が不正です（{Path}）。", path);
        return null;
    }

    public Task<bool> WriteAsync(string ledgerKey, decimal cumulativeJpy, CancellationToken cancellationToken = default) =>
        WriteAtomicAsync(
            LedgerPath(ledgerKey),
            JsonSerializer.Serialize(new LedgerEntry(cumulativeJpy, timeProvider.GetUtcNow()), JsonOptions),
            cancellationToken);

    private async Task<bool> WriteMarkAsync(string runKey, RunMark mark, CancellationToken cancellationToken)
    {
        // 開始の時刻は終わりの印にも引き継ぐ（読み戻せたときだけ）。
        if (mark.StartedAt is null && File.Exists(RunPath(runKey)))
        {
            try
            {
                var previous = JsonSerializer.Deserialize<RunMark>(
                    await File.ReadAllTextAsync(RunPath(runKey), cancellationToken).ConfigureAwait(false), JsonOptions);
                mark = mark with { StartedAt = previous?.StartedAt };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // 終わりの印は書く（開始の時刻を失うだけ）。
            }
        }

        return await WriteAtomicAsync(RunPath(runKey), JsonSerializer.Serialize(mark, JsonOptions), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temp = $"{path}.tmp-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogError(ex, "Stage 0 記録の状態を書けません（{Path}）。", path);
            TryDelete(temp);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 一時ファイルが残っても読み手は見ない（印・台帳の名前と別）。
        }
    }

    private sealed record RunMark(string Phase, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? Detail);

    private sealed record LedgerEntry(decimal CumulativeJpy, DateTimeOffset UpdatedAt);
}
