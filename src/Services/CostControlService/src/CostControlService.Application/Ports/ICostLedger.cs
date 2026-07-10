using AiStockTrading.CostControl.Domain;

namespace AiStockTrading.CostControl.Application.Ports;

// NFR（費用）, IADR-0027: 月次費用台帳（追記・月×カテゴリ別集計）。Month は "yyyy-MM"。
public interface ICostLedger
{
    void Record(string month, CostCategory category, decimal amount, DateTimeOffset at);

    /// <summary>指定月・カテゴリの累計費用。</summary>
    decimal GetMonthlyTotal(string month, CostCategory category);

    /// <summary>指定月の全カテゴリ累計費用。</summary>
    decimal GetMonthlyTotalAll(string month);
}
