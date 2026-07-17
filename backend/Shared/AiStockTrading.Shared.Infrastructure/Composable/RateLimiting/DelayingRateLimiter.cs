namespace AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;

// FR-01, ADR-0004, IADR-0064: トークンバケット（純粋な状態機械）に基づき、消費できるまで待ってから通すレート制限。
// 待機は注入可能（既定は Task.Delay。テストは実時間を待たずフェイク待機で検証する）。
// TokenBucket はスレッド安全ではないため、ここでセマフォにより直列化する。
//
// IADR-0068: 情報収集・市況の両系統から使うため共有物へ移した（元は InformationCollection.Worker）。
// 時刻源は移動に伴い IClock（情報収集のポート）から TimeProvider へ替えた（Shared.Infrastructure の既存慣行に
// 揃え、サービス固有のポートを共有物へ持ち込まないため）。待機・補充のロジックは移動前から不変。
public sealed class DelayingRateLimiter(
    TokenBucket bucket,
    TimeProvider timeProvider,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IRateLimiter
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (!bucket.TryConsume(timeProvider.GetUtcNow(), out var retryAfter))
                await _delay(retryAfter, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
