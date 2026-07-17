using AiStockTrading.InformationCollection.Application.Ports;
using AiStockTrading.InformationCollection.Domain.RateLimiting;

namespace AiStockTrading.InformationCollection.Worker.Composable.RateLimiting;

// FR-01, ADR-0004, IADR-0065: トークンバケット（ドメイン純関数）に基づき、消費できるまで待ってから通すレート制限。
// 待機は注入可能（既定は Task.Delay。テストは実時間を待たずフェイク待機で検証する）。
// TokenBucket はスレッド安全ではないため、ここでセマフォにより直列化する。
internal sealed class DelayingRateLimiter(
    TokenBucket bucket,
    IClock clock,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IRateLimiter
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (!bucket.TryConsume(clock.UtcNow, out var retryAfter))
                await _delay(retryAfter, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
