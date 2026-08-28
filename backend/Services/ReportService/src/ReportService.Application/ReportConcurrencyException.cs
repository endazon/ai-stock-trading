namespace ReportService.Application;

// FR-07, IADR-0024: 版番号付き冪等確定の楽観排他競合。読み込んだ版と現在版が一致しない更新/確定で送出する。
// ホストのエンドポイントはこれを 409 Conflict に写像する。
public sealed class ReportConcurrencyException(string periodKey, int expectedVersion, int actualVersion)
    : Exception($"報告書 {periodKey} が他の更新と競合しました（読み込み版 {expectedVersion} / 現在版 {actualVersion}）。最新を取得して再試行してください。")
{
    public string PeriodKey { get; } = periodKey;

    public int ExpectedVersion { get; } = expectedVersion;

    public int ActualVersion { get; } = actualVersion;
}
