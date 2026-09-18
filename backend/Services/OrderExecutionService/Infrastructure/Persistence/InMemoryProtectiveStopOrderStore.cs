using System.Collections.Concurrent;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Infrastructure.Persistence;

// FR-10, #331, IADR-0210: 保護逆指値レグ記録のインメモリ実装（paper 構成・単体テスト用）。
public sealed class InMemoryProtectiveStopOrderStore : IProtectiveStopOrderStore
{
    private readonly ConcurrentDictionary<Guid, ProtectiveStopOrder> _stops = new();

    public void Save(ProtectiveStopOrder stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        _stops[stop.EntryDecisionId] = stop;
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
