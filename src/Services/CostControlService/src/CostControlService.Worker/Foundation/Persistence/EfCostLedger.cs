using AiStockTrading.CostControl.Application.Ports;
using AiStockTrading.CostControl.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.CostControl.Worker.Foundation.Persistence;

// NFR（費用）, IADR-0027/0034: 月次費用台帳の EF 実装（追記専用・専有 DB）。集計は月・カテゴリで絞って合算する。
// 計上と当該月 LLM 累計の before/after 読み取りを、月単位の PostgreSQL アドバイザリロック（トランザクション内）で
// 直列化し、並行計上でもしきい値遷移を高々 1 回に保つ（IADR-0034）。
internal sealed class EfCostLedger(CostControlDbContext db) : ICostLedger
{
    // 非リレーショナル（テストの InMemory EF）はアドバイザリロック/実トランザクション非対応のためプロセス内ロックで代替直列化する。
    private static readonly Lock NonRelationalGate = new();

    public LlmCostRecordOutcome Record(string month, CostCategory category, decimal amount, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrEmpty(month);

        if (!db.Database.IsRelational())
        {
            lock (NonRelationalGate)
            {
                return AppendAndRead(month, category, amount, at);
            }
        }

        // 月単位のアドバイザリロックで並行計上を直列化する（トランザクション終了で自動解放）。
        using var tx = db.Database.BeginTransaction();
        db.Database.ExecuteSqlInterpolated($"SELECT pg_advisory_xact_lock({AdvisoryKey(month)})");
        var outcome = AppendAndRead(month, category, amount, at);
        tx.Commit();
        return outcome;
    }

    public decimal GetMonthlyTotal(string month, CostCategory category) =>
        db.CostEntries.Where(r => r.Month == month && r.Category == category).Sum(r => (decimal?)r.Amount) ?? 0m;

    public decimal GetMonthlyTotalAll(string month) =>
        db.CostEntries.Where(r => r.Month == month).Sum(r => (decimal?)r.Amount) ?? 0m;

    // ロック/トランザクション保持下で呼ぶこと。before 読み取り→追記→after 読み取りを不可分に行う。
    private LlmCostRecordOutcome AppendAndRead(string month, CostCategory category, decimal amount, DateTimeOffset at)
    {
        var before = GetMonthlyTotal(month, CostCategory.Llm);
        db.CostEntries.Add(new CostEntryRow
        {
            Id = Guid.NewGuid(),
            Month = month,
            Category = category,
            Amount = amount,
            RecordedAt = at,
        });
        db.SaveChanges();
        var after = GetMonthlyTotal(month, CostCategory.Llm);
        return new LlmCostRecordOutcome(before, after);
    }

    // "yyyy-MM" から決定的な bigint を導出する（プロセス跨ぎで安定＝複数レプリカでも同月は同キーで直列化。
    // string.GetHashCode はプロセス毎ランダムのため用いない）。想定外書式は FNV-1a 64bit にフォールバック。
    private static long AdvisoryKey(string month)
    {
        var parts = month.Split('-');
        if (parts.Length == 2 && int.TryParse(parts[0], out var year) && int.TryParse(parts[1], out var m))
            return year * 100L + m;
        return Fnv1a64(month);
    }

    private static long Fnv1a64(string s)
    {
        ulong h = 1469598103934665603UL;
        foreach (var c in s)
        {
            h ^= c;
            h *= 1099511628211UL;
        }

        return unchecked((long)h);
    }
}
