namespace OpendAuthGateway.Tests.Infrastructure;

/// <summary>
/// #722: 試験用の時刻源。実時間を待たずに流量制限の窓を跨ぐために使う。
/// <para>
/// <c>Microsoft.Extensions.TimeProvider.Testing</c> は入れない —— 本サイドカーは
/// 依存ゼロで保つ方針であり（IADR-0322）、必要なのは「進められる現在時刻」だけである。
/// </para>
/// </summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>現在時刻を進める。</summary>
    public void Advance(TimeSpan delta) => _now += delta;
}
