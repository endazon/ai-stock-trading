using System.Collections.Concurrent;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Infrastructure.Persistence;

// FR-10, #331, IADR-0210: 保護逆指値レグ記録のインメモリ実装（paper 構成・単体テスト用）。
public sealed class InMemoryProtectiveStopOrderStore : IProtectiveStopOrderStore
{
    private readonly ConcurrentDictionary<Guid, ProtectiveStopOrder> _stops = new();

    // #833 項目3, IADR-0396: 版の比較と書き込みを 1 つの区間で行う（TrySave を原子的にする）。
    private readonly Lock _gate = new();

    // 無条件の上書き。新規は写しの版のまま、既存は保存先の版から 1 進める（EF 実装と同じ）。
    public void Save(ProtectiveStopOrder stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        lock (_gate)
        {
            _stops[stop.EntryDecisionId] = _stops.TryGetValue(stop.EntryDecisionId, out var current)
                ? stop with { Version = current.Version + 1 }
                : stop;
        }
    }

    // 🔴 #833 項目3, IADR-0396: 保存先の版が写しの版と一致するときだけ書き、版を 1 進める。
    public bool TrySave(ProtectiveStopOrder stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        lock (_gate)
        {
            if (!_stops.TryGetValue(stop.EntryDecisionId, out var current) || current.Version != stop.Version)
                return false;

            _stops[stop.EntryDecisionId] = stop with { Version = stop.Version + 1 };
            return true;
        }
    }

    public ProtectiveStopOrder? Find(Guid entryDecisionId) =>
        _stops.TryGetValue(entryDecisionId, out var stop) ? stop : null;

    public IReadOnlyList<ProtectiveStopOrder> FindActive(int batchSize) =>
        _stops.Values
            .Where(s => s.State == ProtectiveStopState.Active)
            .OrderBy(s => s.CreatedAt)
            .Take(batchSize)
            .ToList();

    // #820, IADR-0344 決定1・決定4。
    public IReadOnlyList<ProtectiveStopOrder> FindActiveSoftwareStops(string symbol, Market market, TradeSide entrySide) =>
        _stops.Values
            .Where(s => s.State == ProtectiveStopState.Active
                && s.IsSoftwareStop
                && s.Symbol == symbol
                && s.Market == market
                && s.EntrySide == entrySide)
            .OrderBy(s => s.CreatedAt)
            .ToList();

    // #820 の 6 巡目監査・7 巡目監査, IADR-0344 追記(6)・追記(7): 観測を数え続けてよいかの門が読む
    // 「完了済みの S1」（更新が新しい順）。
    public IReadOnlyList<ProtectiveStopOrder> FindCompletedSoftwareStops(
        string symbol, Market market, TradeSide entrySide, int limit) =>
        _stops.Values
            .Where(s => s.State == ProtectiveStopState.Completed
                && s.IsSoftwareStop
                && s.Symbol == symbol
                && s.Market == market
                && s.EntrySide == entrySide)
            .OrderByDescending(s => s.UpdatedAt)
            .Take(limit)
            .ToList();
}
