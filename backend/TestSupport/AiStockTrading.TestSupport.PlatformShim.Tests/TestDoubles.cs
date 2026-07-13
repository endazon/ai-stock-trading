using Microsoft.Extensions.Logging;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// IADR-0051: トークンのキャッシュ失効を決定的に検証するための可変時刻。TimeProvider を差し替える。
internal sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

// IADR-0051 決定 5: 取得失敗の可観測性（LogWarning）を検証するための記録ロガー。
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<string> Warnings { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning)
            Warnings.Add(formatter(state, exception));
    }
}
