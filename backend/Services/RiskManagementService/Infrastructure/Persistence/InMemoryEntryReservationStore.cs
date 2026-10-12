using System.Collections.Concurrent;
using RiskManagementService.Features.RiskManagement;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-10, #1319, IADR-0528: 新規建ての審査の直列化と予約（インメモリ・テスト／単体実行用）。本番配線は EfEntryReservationStore。
// 直列化はインスタンスのセマフォで行う（同じインスタンスを共有する審査どうしが直列になる）。
// **例外で終わった臨界区間の予約は取り消す**（EF 実装のロールバックと同じ意味論）。
public sealed class InMemoryEntryReservationStore : IEntryReservationStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, EntryReservation> _reservations = new();
    private readonly AsyncLocal<List<Guid>?> _pending = new();

    public async Task<T> RunSerializedAsync<T>(Func<CancellationToken, Task<T>> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var pending = new List<Guid>();
        _pending.Value = pending;
        try
        {
            return await body(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            foreach (var id in pending)
                _reservations.TryRemove(id, out _);
            throw;
        }
        finally
        {
            _pending.Value = null;
            _gate.Release();
        }
    }

    public EntryReservation? Find(Guid decisionId) =>
        _reservations.TryGetValue(decisionId, out var r) ? r : null;

    public void Reserve(EntryReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        if (_reservations.TryAdd(reservation.DecisionId, reservation))
            _pending.Value?.Add(reservation.DecisionId);
    }

    public IReadOnlyList<EntryReservation> Snapshot() => _reservations.Values.ToList();
}
