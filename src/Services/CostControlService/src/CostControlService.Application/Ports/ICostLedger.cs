using AiStockTrading.CostControl.Domain;

namespace AiStockTrading.CostControl.Application.Ports;

// NFR（費用）, IADR-0027/0034: 月次費用台帳（追記・月×カテゴリ別集計）。Month は "yyyy-MM"。
public interface ICostLedger
{
    /// <summary>
    /// 費用を追記し、当該月の LLM 累計（計上前/計上後）を<b>原子的に</b>返す（IADR-0034）。
    /// 並行計上でもしきい値遷移を高々 1 回に保つため、実装は月単位で直列化する（EF=トランザクション＋アドバイザリロック / InMemory=ロック）。
    /// </summary>
    LlmCostRecordOutcome Record(string month, CostCategory category, decimal amount, DateTimeOffset at);

    /// <summary>指定月・カテゴリの累計費用。</summary>
    decimal GetMonthlyTotal(string month, CostCategory category);

    /// <summary>指定月の全カテゴリ累計費用。</summary>
    decimal GetMonthlyTotalAll(string month);
}

// NFR（費用）, IADR-0034: 1 回の計上における当該月 LLM 累計（計上前・計上後）。しきい値上方遷移の判定入力（計上と不可分）。
public readonly record struct LlmCostRecordOutcome(decimal LlmTotalBefore, decimal LlmTotalAfter);
