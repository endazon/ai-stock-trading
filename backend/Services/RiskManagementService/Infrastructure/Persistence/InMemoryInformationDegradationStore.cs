using RiskManagementService.Features.RiskManagement;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-01, FR-02, FR-10, ADR-0020, #337, IADR-0249: 縮退状態のプロセス内保持（カテゴリ集合）。
//
// 永続化しないのは発行側（収集サービスの DegradationStateTracker）も同じくプロセス内であるためで、
// **リスク管理側だけを永続化しても再起動時の取りこぼし（縮退継続中に本サービスが再起動すると、
// 次の遷移まで状態が届かない）は解消しない**。この残余リスク（fail-open 側）は IADR-0249 に記録し、
// 解消（再送 or 定期スナップショット）は別作業とする。
public sealed class InMemoryInformationDegradationStore : IInformationDegradationStore
{
    private readonly object _gate = new();
    private readonly HashSet<string> _degraded = new(StringComparer.Ordinal);

    public void MarkDegraded(string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        lock (_gate)
        {
            _degraded.Add(category);
        }
    }

    public void MarkRecovered(string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        lock (_gate)
        {
            _degraded.Remove(category);
        }
    }

    public bool BlocksNewEntries
    {
        get
        {
            lock (_gate)
            {
                return _degraded.Count > 0;
            }
        }
    }
}
