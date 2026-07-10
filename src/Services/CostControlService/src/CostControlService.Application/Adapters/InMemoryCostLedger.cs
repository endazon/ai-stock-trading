using System.Collections.Concurrent;
using AiStockTrading.CostControl.Application.Ports;
using AiStockTrading.CostControl.Domain;

namespace AiStockTrading.CostControl.Application.Adapters;

// NFR（費用）, IADR-0027: 月次費用台帳のインメモリ実装（テスト・単体実行用）。PostgreSQL 永続化は Worker の EfCostLedger で差し替える。
public sealed class InMemoryCostLedger : ICostLedger
{
    private readonly ConcurrentDictionary<(string Month, CostCategory Category), decimal> _totals = new();

    public void Record(string month, CostCategory category, decimal amount, DateTimeOffset at) =>
        _totals.AddOrUpdate((month, category), amount, (_, current) => current + amount);

    public decimal GetMonthlyTotal(string month, CostCategory category) =>
        _totals.TryGetValue((month, category), out var total) ? total : 0m;

    public decimal GetMonthlyTotalAll(string month) =>
        _totals.Where(kv => kv.Key.Month == month).Sum(kv => kv.Value);
}
