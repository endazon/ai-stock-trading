using CostControlService.Features.CostControl;
using CostControlService.Domain;
using Microsoft.EntityFrameworkCore;

namespace CostControlService.Infrastructure.Persistence;

// NFR（費用）, IADR-0027/0034: 月次費用台帳の EF 実装（追記専用・専有 DB）。集計は月・カテゴリで絞って合算する。
// 計上と当該月 LLM 累計の before/after 読み取りを、月単位の PostgreSQL アドバイザリロック（トランザクション内）で
// 直列化し、並行計上でもしきい値遷移を高々 1 回に保つ（IADR-0034）。
public sealed class EfCostLedger(CostControlDbContext db) : ICostLedger
{
    // 非リレーショナル（テストの InMemory EF）専用のプロセス内直列化ロック。本番の Npgsql 経路はこのロックを通らず
    // pg_advisory_xact_lock で直列化する。テスト専用のため全インスタンス共有（無関係な test DB 間の待ち合わせは無害）。
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

    // NFR（費用）, #347: 月報へ供給するカテゴリ別内訳。対象外（LlmUncapped）も含めて返す。
    public IReadOnlyDictionary<CostCategory, decimal> GetMonthlyTotals(string month) =>
        db.CostEntries
            .Where(r => r.Month == month)
            .GroupBy(r => r.Category)
            .Select(g => new { g.Key, Total = g.Sum(r => r.Amount) })
            .ToDictionary(x => x.Key, x => x.Total);

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

    // 月キー "yyyy-MM"（常に CostControlAppService.MonthKey 由来）から決定的な bigint を導出する。プロセス跨ぎで安定
    // （複数レプリカでも同月は同キーで直列化）＝ string.GetHashCode（プロセス毎ランダム）は用いない。
    private static long AdvisoryKey(string month)
    {
        var parts = month.Split('-');
        return int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) * 100L
             + int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
    }
}
