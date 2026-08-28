using CostControlService.Application.Ports;
using CostControlService.Domain;

namespace CostControlService.Application.Adapters;

// NFR（費用）, IADR-0027/0034: 月次費用台帳のインメモリ実装（テスト・単体実行用）。PostgreSQL 永続化は Worker の EfCostLedger で差し替える。
// 計上（追記）と当該月 LLM 累計の before/after 読み取りを Lock で直列化し、並行計上でもしきい値遷移を高々 1 回に保つ。
public sealed class InMemoryCostLedger : ICostLedger
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Month, CostCategory Category), decimal> _totals = new();

    public LlmCostRecordOutcome Record(string month, CostCategory category, decimal amount, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrEmpty(month);
        lock (_gate)
        {
            var before = LlmTotal(month);
            var key = (month, category);
            _totals[key] = (_totals.TryGetValue(key, out var current) ? current : 0m) + amount;
            var after = LlmTotal(month);
            return new LlmCostRecordOutcome(before, after);
        }
    }

    public decimal GetMonthlyTotal(string month, CostCategory category)
    {
        lock (_gate)
        {
            return _totals.TryGetValue((month, category), out var total) ? total : 0m;
        }
    }

    public decimal GetMonthlyTotalAll(string month)
    {
        lock (_gate)
        {
            return _totals.Where(kv => kv.Key.Month == month).Sum(kv => kv.Value);
        }
    }

    public IReadOnlyDictionary<CostCategory, decimal> GetMonthlyTotals(string month)
    {
        lock (_gate)
        {
            return _totals
                .Where(kv => kv.Key.Month == month)
                .GroupBy(kv => kv.Key.Category)
                .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));
        }
    }

    // ロック保持下で呼ぶこと。当該月の LLM 累計。
    private decimal LlmTotal(string month) =>
        _totals.TryGetValue((month, CostCategory.Llm), out var total) ? total : 0m;
}
