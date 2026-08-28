using ConfigurationService.Client.Ports;
using ConfigurationService.Domain;

namespace ConfigurationService.Client.Tests;

// IADR-0063 決定 4: キャッシュの TTL 失効を決定的に検証するための可変時刻（PlatformShim.Tests の同名テストダブルに倣う）。
internal sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

// 取得回数を数える差し替え可能な取得元。Next = null は取得不可（非 2xx・不達・タイムアウト・不正応答）を表す。
internal sealed class FakeAssumptionsSource : IAssumptionsSource
{
    public int FetchCount { get; private set; }

    public VersionedAssumptions? Next { get; set; }

    /// <summary>取得の最中に割り込む処理（取得中に AssumptionsChanged が届く状況の再現）。</summary>
    public Action? OnFetching { get; set; }

    public Task<VersionedAssumptions?> FetchAsync(CancellationToken cancellationToken = default)
    {
        FetchCount++;
        OnFetching?.Invoke();
        return Task.FromResult(Next);
    }
}
