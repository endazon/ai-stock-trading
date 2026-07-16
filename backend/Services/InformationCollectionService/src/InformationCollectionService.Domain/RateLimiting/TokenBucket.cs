namespace AiStockTrading.InformationCollection.Domain.RateLimiting;

// FR-01, ADR-0004, IADR-0061: 情報源のレート制限を「送信前に自制する」ためのトークンバケット。
// 時計を持たず現在時刻を引数で受ける純粋な状態機械（決定的に単体検証できる）。実際の待機は Worker 側の
// アダプタ（IRateLimiter 実装）が担う。429 を受けてから対処するのでは規約違反そのものを防げないため、
// 消費可否と待機時間をここで決める。
//
// 補充は連続量（refillInterval あたり capacity トークン）。単一の巡回スレッドから使う前提で、
// スレッド安全ではない（並行呼び出しの直列化は呼び出し側＝IRateLimiter アダプタの責務）。
public sealed class TokenBucket
{
    // 浮動小数の累積誤差で「ちょうど補充された瞬間」を取りこぼさないための許容差。
    private const double Epsilon = 1e-9;

    private readonly int _capacity;
    private readonly TimeSpan _refillInterval;

    private double _tokens;
    private DateTimeOffset? _lastRefill;

    public TokenBucket(int capacity, TimeSpan refillInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(refillInterval, TimeSpan.Zero);

        _capacity = capacity;
        _refillInterval = refillInterval;
        _tokens = capacity;
    }

    // トークンを 1 つ消費する。消費できなければ false を返し、次に 1 トークン貯まるまでの待機時間を retryAfter に返す。
    public bool TryConsume(DateTimeOffset now, out TimeSpan retryAfter)
    {
        Refill(now);

        if (_tokens + Epsilon >= 1)
        {
            _tokens -= 1;
            retryAfter = TimeSpan.Zero;
            return true;
        }

        var deficit = 1 - _tokens;
        retryAfter = TimeSpan.FromSeconds(deficit * _refillInterval.TotalSeconds / _capacity);
        return false;
    }

    private void Refill(DateTimeOffset now)
    {
        if (_lastRefill is null)
        {
            _lastRefill = now;
            return;
        }

        var elapsed = now - _lastRefill.Value;

        // 時刻の巻き戻り（NTP 補正等）ではトークンを増やさず、基準時刻も戻さない（安全側＝拒否に倒す）。
        if (elapsed <= TimeSpan.Zero)
            return;

        _tokens = Math.Min(_capacity, _tokens + (elapsed.TotalSeconds * _capacity / _refillInterval.TotalSeconds));
        _lastRefill = now;
    }
}
