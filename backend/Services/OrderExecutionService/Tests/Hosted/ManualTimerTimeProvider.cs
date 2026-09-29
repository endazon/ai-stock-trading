namespace OrderExecutionService.Tests;

// FR-10, #1093, IADR-0459: 常駐の初回の遅延（Task.Delay(delay, timeProvider, token)）を壁時計なしで観測する TimeProvider。
// 作られたタイマーの期限を記録し、試験が Fire() するまで発火しない。時刻は固定（観測時刻の供給元として使う）。
internal sealed class ManualTimerTimeProvider(DateTimeOffset now) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly TaskCompletionSource _created = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override DateTimeOffset GetUtcNow() => now;

    /// <summary>作られたタイマーの期限（作られた順）。</summary>
    public IReadOnlyList<TimeSpan> DueTimes
    {
        get
        {
            lock (_gate) return _timers.Select(t => t.DueTime).ToList();
        }
    }

    /// <summary>最初のタイマーが作られたら完了する。</summary>
    public Task FirstTimerCreated => _created.Task;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state, dueTime);
        lock (_gate) _timers.Add(timer);
        _created.TrySetResult();
        return timer;
    }

    /// <summary>最初に作られたタイマーを発火させる（初回の遅延が明けたことにする）。</summary>
    public void FireFirst()
    {
        ManualTimer first;
        lock (_gate) first = _timers[0];
        first.Fire();
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        public TimeSpan DueTime { get; } = dueTime;

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
