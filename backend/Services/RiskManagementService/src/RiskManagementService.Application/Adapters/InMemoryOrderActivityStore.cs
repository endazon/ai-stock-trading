using System.Collections.Concurrent;
using RiskManagementService.Application.Ports;
using RiskManagementService.Domain.Manipulation;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Application.Adapters;

// FR-19, #154, IADR-0067: 注文アクティビティ射影ストアのインメモリ実装（射影と読み取りの両方）。
// 単体テスト・ローカル実行のための供給で、本番配線は EfOrderActivityStore / EfOrderActivitySource（IADR-0067）。
// DecisionId をキーに 1 注文のライフサイクルを 1 行として保持し、（銘柄, 市場, 発注時刻）で窓を切り出す。
public sealed class InMemoryOrderActivityStore : IOrderActivityStore, IOrderActivitySource
{
    private sealed class Entry
    {
        public required string Symbol { get; init; }

        public required Market Market { get; init; }

        public required TradeSide Side { get; init; }

        public required DateTimeOffset PlacedAt { get; init; }

        public int Quantity { get; set; }

        public int FilledQuantity { get; set; }

        public OrderStatus Status { get; set; }

        public int AmendmentCount { get; set; }

        public DateTimeOffset? TerminalAt { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, Entry> _byDecision = new();

    public void RecordPlacement(
        Guid decisionId, string symbol, Market market, TradeSide side, int quantity, DateTimeOffset placedAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(symbol);
        // 冪等: 既存の承認は無視する（再送）。
        _byDecision.TryAdd(decisionId, new Entry
        {
            Symbol = symbol,
            Market = market,
            Side = side,
            PlacedAt = placedAt,
            Quantity = quantity,
            FilledQuantity = 0,
            Status = OrderStatus.Accepted,
            AmendmentCount = 0,
            TerminalAt = null,
        });
    }

    public void RecordExecution(Guid decisionId, OrderStatus status, int filledQuantity, DateTimeOffset executedAt)
    {
        if (!_byDecision.TryGetValue(decisionId, out var e))
            return;

        lock (e)
        {
            e.Status = status;
            e.FilledQuantity = filledQuantity;
            if (OrderActivityProjection.IsTerminal(status))
                e.TerminalAt = executedAt;
        }
    }

    public void RecordModification(Guid decisionId, int quantity, DateTimeOffset modifiedAt)
    {
        if (!_byDecision.TryGetValue(decisionId, out var e))
            return;

        lock (e)
        {
            e.AmendmentCount++;
            e.Quantity = quantity;
        }
    }

    public void RecordCancellation(Guid decisionId, DateTimeOffset cancelledAt)
    {
        if (!_byDecision.TryGetValue(decisionId, out var e))
            return;

        lock (e)
        {
            e.Status = OrderStatus.Cancelled;
            e.TerminalAt = cancelledAt;
        }
    }

    public OrderActivityWindow GetRecentActivity(
        string symbol, Market market, DateTimeOffset asOf, TimeSpan lookback)
    {
        var from = asOf - lookback;

        var records = _byDecision.Values
            .Where(e => e.Symbol == symbol && e.Market == market && e.PlacedAt >= from && e.PlacedAt <= asOf)
            .Select(e => new OrderActivityRecord
            {
                PlacedAt = e.PlacedAt,
                Side = e.Side,
                Quantity = e.Quantity,
                FilledQuantity = e.FilledQuantity,
                Status = e.Status,
                AmendmentCount = e.AmendmentCount,
                TerminalAt = e.TerminalAt,
            })
            .ToList();

        return records.Count == 0
            ? OrderActivityWindow.Empty(symbol, market, asOf)
            : new OrderActivityWindow { Symbol = symbol, Market = market, AsOf = asOf, Records = records };
    }
}
