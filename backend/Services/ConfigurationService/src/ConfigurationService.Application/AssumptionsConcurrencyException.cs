namespace AiStockTrading.Configuration.Application;

// FR-17, IADR-0021: 楽観的排他制御の競合。読み込んだ版と現在の版が一致しない更新で送出する。
// ホストのエンドポイントはこれを 409 Conflict に写像する（ロストアップデート防止）。
public sealed class AssumptionsConcurrencyException(int expectedVersion, int actualVersion)
    : Exception($"前提条件が他の更新と競合しました（読み込み版 {expectedVersion} / 現在版 {actualVersion}）。最新を取得して再試行してください。")
{
    public int ExpectedVersion { get; } = expectedVersion;

    public int ActualVersion { get; } = actualVersion;
}
