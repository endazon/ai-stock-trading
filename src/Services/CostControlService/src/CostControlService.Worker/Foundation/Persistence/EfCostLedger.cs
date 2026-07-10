using AiStockTrading.CostControl.Application.Ports;
using AiStockTrading.CostControl.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.CostControl.Worker.Foundation.Persistence;

// NFR（費用）, IADR-0027: 月次費用台帳の EF 実装（追記専用・専有 DB）。集計は月・カテゴリで絞って合算する。
internal sealed class EfCostLedger(CostControlDbContext db) : ICostLedger
{
    public void Record(string month, CostCategory category, decimal amount, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrEmpty(month);
        db.CostEntries.Add(new CostEntryRow
        {
            Id = Guid.NewGuid(),
            Month = month,
            Category = category,
            Amount = amount,
            RecordedAt = at,
        });
        db.SaveChanges();
    }

    public decimal GetMonthlyTotal(string month, CostCategory category) =>
        db.CostEntries.Where(r => r.Month == month && r.Category == category).Sum(r => (decimal?)r.Amount) ?? 0m;

    public decimal GetMonthlyTotalAll(string month) =>
        db.CostEntries.Where(r => r.Month == month).Sum(r => (decimal?)r.Amount) ?? 0m;
}
