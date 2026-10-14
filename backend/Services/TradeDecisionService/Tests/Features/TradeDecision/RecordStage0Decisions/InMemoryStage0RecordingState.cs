using AiStockTrading.Shared.Contracts.Backtest;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;

namespace TradeDecisionService.Tests.Features.TradeDecision.RecordStage0Decisions;

// FR-15, ADR-0033 決定5, #1342, IADR-0537: 記録器の試験用の状態の置き場（メモリ上）。
// 同じインスタンスを 2 つの記録器へ渡すと「再起動をまたいで残る永続領域」を表せる。
// 印・チェックポイント・台帳は別々の辞書であり、片方だけを壊す・消す・書けなくすることができる。
internal sealed class InMemoryStage0RecordingState : IStage0RecordingStateStore, IStage0RecordingCostLedger
{
    public Dictionary<string, Stage0RunState> Runs { get; } = [];

    public Dictionary<string, decimal?> Ledger { get; } = [];

    /// <summary>台帳へ書いた値の履歴（予約と実績の順序を見る）。</summary>
    public List<decimal> LedgerWrites { get; } = [];

    /// <summary>ロックを持っているか（ほかの実行を表す）。</summary>
    public bool Locked { get; set; }

    /// <summary>true にすると開始の印を書けない。</summary>
    public bool FailMarkStarted { get; set; }

    /// <summary>n 回目（1 始まり）のチェックポイントを**書く前に**落ちる（LLM の応答の後・チェックポイントの前＝最悪の瞬間）。</summary>
    public int? CrashBeforeCheckpoint { get; set; }

    /// <summary>n 回目（1 始まり）のチェックポイントを**書いた後に**落ちる（次の判断時点の前）。</summary>
    public int? CrashAfterCheckpoint { get; set; }

    private int _checkpoints;

    public IDisposable? TryAcquireRunLock()
    {
        if (Locked)
            return null;
        Locked = true;
        return new Release(this);
    }

    public Task<Stage0RunState> LoadAsync(string runId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Runs.TryGetValue(runId, out var run) ? run : new Stage0RunState(Stage0RunPhase.NotStarted));

    public Task<bool> MarkStartedAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (FailMarkStarted)
            return Task.FromResult(false);
        Runs[runId] = new Stage0RunState(Stage0RunPhase.InProgress);
        return Task.FromResult(true);
    }

    public Task<bool> SaveCheckpointAsync(
        string runId, Stage0DecisionRecordSet checkpoint, CancellationToken cancellationToken = default)
    {
        _checkpoints++;
        if (CrashBeforeCheckpoint == _checkpoints)
            throw new SimulatedCrashException();

        // 永続領域の往復（直列化）を通す。
        Runs[runId] = new Stage0RunState(
            Stage0RunPhase.InProgress, Stage0DecisionRecordJson.TryDeserialize(Stage0DecisionRecordJson.Serialize(checkpoint)));
        if (CrashAfterCheckpoint == _checkpoints)
            throw new SimulatedCrashException();
        return Task.FromResult(true);
    }

    public Task<bool> MarkFinishedAsync(
        string runId, Stage0RunPhase phase, string detail, CancellationToken cancellationToken = default)
    {
        Runs[runId] = new Stage0RunState(phase, Detail: detail);
        return Task.FromResult(true);
    }

    public Task<decimal?> ReadAsync(string ledgerId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Ledger.TryGetValue(ledgerId, out var value) ? value : 0m);

    public Task<bool> WriteAsync(string ledgerId, decimal cumulativeJpy, CancellationToken cancellationToken = default)
    {
        Ledger[ledgerId] = cumulativeJpy;
        LedgerWrites.Add(cumulativeJpy);
        return Task.FromResult(true);
    }

    /// <summary>ロックを外す（落ちたプロセスのロックは OS が外す）。</summary>
    public void ReleaseLock() => Locked = false;

    private sealed class Release(InMemoryStage0RecordingState owner) : IDisposable
    {
        public void Dispose() => owner.Locked = false;
    }
}

/// <summary>プロセスが落ちたことを表す（チェックポイントを書く直前）。</summary>
internal sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException()
        : base("simulated crash")
    {
    }

    public SimulatedCrashException(string message)
        : base(message)
    {
    }

    public SimulatedCrashException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
