using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;

// FR-10, #1093, IADR-0458: 保護逆指値ガードの建玉照会（巡回に 1 回）を、**分類できた一時的な失敗に限って**照会し直す。
//
// 🔴 何のためか: 1 回の照会失敗で、ガードは巡回（30 秒）ごと全件を据え置く（IADR-0210 決定 4(c)。これは正しい fail-safe）。
//    一時的な失敗（返信待ちの打ち切り・再起動直後の頻度制限）で 30 秒の保護の穴を作らないよう、巡回の中で 1 回だけ照会し直す。
// 🔴 何をしないか:
//    - 使い切ったら従来どおり null（据え置き）を返す。空列・推定値で代用しない（IADR-0118）。
//    - 分類できない失敗・業務上の失敗は照会し直さない。失敗も頻度制限の枠を消費し、素朴な再試行は連鎖失敗を招く（IADR-0144 決定 5）。
//    - 待ちの合計は予算（巡回間隔の半分）を超えない。超えるなら照会し直さずに据え置く（次の巡回が遅れない）。
//    - 読み取りだけに使う。発注・取消・成行手仕舞いには使わない（IADR-0211 決定 3・IADR-0117）。
public sealed class PositionQueryRetry
{
    private readonly int _maxRetries;
    private readonly TimeSpan _transientDelay;
    private readonly TimeSpan _rateLimitedDelay;
    private readonly double _jitter;
    private readonly TimeSpan _budget;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double> _random;
    private readonly ILogger _logger;

    public PositionQueryRetry(
        ProtectiveStopGuardOptions options,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<double>? random = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _maxRetries = options.PositionQueryMaxRetriesClamped;
        _transientDelay = options.PositionQueryTransientRetryDelay;
        _rateLimitedDelay = options.PositionQueryRateLimitedRetryDelay;
        _jitter = options.PositionQueryRetryJitterClamped;
        _budget = options.PositionQueryRetryBudget;
        _delay = delay ?? Task.Delay;
        _random = random ?? Random.Shared.NextDouble;
        _logger = logger ?? NullLogger<PositionQueryRetry>.Instance;
    }

    /// <summary>
    /// 照会し、分類できた一時的な失敗なら予算の範囲で照会し直す。戻り値が null なら照会不能（呼び出し側は据え置く）。
    /// </summary>
    public async Task<IReadOnlyList<BrokerPositionSnapshot>?> QueryAsync(
        IClassifiedPositionSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        var result = await source.QueryPositionsAsync(cancellationToken).ConfigureAwait(false);
        var waited = TimeSpan.Zero;
        var retries = 0;
        while (result.Positions is null && IsRetryable(result.Failure) && retries < _maxRetries)
        {
            var wait = Jittered(result.Failure == PositionQueryFailure.RateLimited ? _rateLimitedDelay : _transientDelay);
            if (waited + wait > _budget)
            {
                _logger.LogWarning(
                    "建玉照会が一時的に失敗しました（{Failure}）。待ち {Wait} は予算 {Budget} を超えるため照会し直さず、この巡回は据え置きます。",
                    result.Failure, wait, _budget);
                return null;
            }

            _logger.LogInformation(
                "建玉照会が一時的に失敗しました（{Failure}）。{Wait} 待って照会し直します（{Attempt}/{Max} 回目）。",
                result.Failure, wait, retries + 1, _maxRetries);
            await _delay(wait, cancellationToken).ConfigureAwait(false);
            waited += wait;
            retries++;
            result = await source.QueryPositionsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (retries > 0)
        {
            if (result.Positions is not null)
                _logger.LogInformation("照会し直した建玉照会は成功しました（{Retries} 回目）。", retries);
            else
                _logger.LogWarning(
                    "照会し直した建玉照会も失敗しました（{Failure}）。この巡回は据え置きます（fail-safe）。", result.Failure);
        }

        return result.Positions;
    }

    private static bool IsRetryable(PositionQueryFailure failure) =>
        failure is PositionQueryFailure.Transient or PositionQueryFailure.RateLimited;

    // 基準の待ちに ±jitter の揺らぎを掛ける（再起動の直後に複数の呼び手が同じ時刻へ揃わないように）。
    private TimeSpan Jittered(TimeSpan baseDelay)
    {
        var factor = 1.0 + (_jitter * ((2.0 * _random()) - 1.0));
        return TimeSpan.FromTicks((long)(baseDelay.Ticks * Math.Max(0.0, factor)));
    }
}
