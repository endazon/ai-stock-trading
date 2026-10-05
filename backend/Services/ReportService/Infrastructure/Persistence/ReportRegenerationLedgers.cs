using Microsoft.EntityFrameworkCore;
using ReportService.Domain;
using ReportService.Features.Reports;

namespace ReportService.Infrastructure.Persistence;

// FR-06, FR-14, 計画 ADR-0052 決定 1・4・5, #1156, IADR-0491 決定 3・5: `/report regenerate` の試行の台帳
// （EF・report_svc の report_regeneration_attempts 表）。形と排他の作り方は `/policy` の台帳（EfPolicyRevisionLedger）に揃える。
public sealed class EfReportRegenerationLedger(ReportDbContext db) : IReportRegenerationLedger
{
    // Postgres 以外（試験の InMemory）で TryBegin の排他区間を作るプロセス内の錠。DbContext はスコープごとに別なので静的に持つ。
    private static readonly Lock NonRelationalGate = new();

    // 勧告ロックの鍵の上位 32 ビット（"RGEN"）。下位は JST の暦日の通し番号。`/policy` の "POLR" とは別の鍵＝別枠で直列化する。
    private const long AdvisoryLockNamespace = 0x5247454EL << 32;

    // 🔴 断った行（上限・中核の入力の取得失敗）は数えない（ADR-0052 決定 4「回数は消費しない」）。
    public int CountOn(DateOnly jstDate) =>
        db.ReportRegenerationAttempts.Count(a => a.JstDate == jstDate
            && a.Outcome != ReportRegenerationOutcome.RefusedCoreUnsupplied
            && a.Outcome != ReportRegenerationOutcome.LimitReached);

    public ReportRegenerationBeginResult TryBegin(ReportRegenerationAttempt attempt, int dailyLimit)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        if (db.Database.IsNpgsql())
        {
            using var tx = db.Database.BeginTransaction();
            var key = AdvisoryLockNamespace | (uint)attempt.JstDate.DayNumber;
            db.Database.ExecuteSql($"SELECT pg_advisory_xact_lock({key})");
            var result = CountAndAdd(attempt, dailyLimit);
            tx.Commit();
            return result;
        }

        lock (NonRelationalGate)
            return CountAndAdd(attempt, dailyLimit);
    }

    private ReportRegenerationBeginResult CountAndAdd(ReportRegenerationAttempt attempt, int dailyLimit)
    {
        var used = CountOn(attempt.JstDate);
        if (used >= dailyLimit)
            return new ReportRegenerationBeginResult(false, used);

        var row = ReportRegenerationAttemptRow.From(attempt with { Outcome = ReportRegenerationOutcome.Pending });
        db.ReportRegenerationAttempts.Add(row);
        SaveOrDetach(row);
        return new ReportRegenerationBeginResult(true, used);
    }

    public void RecordRefusal(ReportRegenerationAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (ReportRegenerationOutcomes.Counts(attempt.Outcome))
            throw new ArgumentException($"断った試行の結果ではありません（{attempt.Outcome}）。", nameof(attempt));

        var row = ReportRegenerationAttemptRow.From(attempt);
        db.ReportRegenerationAttempts.Add(row);
        SaveOrDetach(row);
    }

    public void Complete(
        Guid id, ReportRegenerationOutcome outcome, int? reportVersion, string? unsuppliedInputs, string? notRestorableInputs)
    {
        var row = db.ReportRegenerationAttempts.Find(id)
            ?? throw new InvalidOperationException($"報告書の作り直しの試行 {id} がありません。");
        row.Outcome = outcome;
        row.ReportVersion = reportVersion;
        row.UnsuppliedInputs = unsuppliedInputs;
        row.NotRestorableInputs = notRestorableInputs;
        SaveOrDetach(row);
    }

    public ReportRegenerationAttempt? Find(Guid id) => db.ReportRegenerationAttempts.Find(id)?.ToAttempt();

    public ReportRegenerationTally Tally(DateOnly from, DateOnly to, int dailyLimit)
    {
        var rows = db.ReportRegenerationAttempts
            .Where(a => a.JstDate >= from && a.JstDate <= to)
            .Select(a => new { a.JstDate, a.Outcome })
            .ToList();
        return ReportRegenerationTallies.Of(rows.Select(r => (r.JstDate, r.Outcome)), dailyLimit);
    }

    // `/policy` の台帳と同じ規律: 書き込みが失敗したら、その行を追跡から外してから例外を上げる（共有の DbContext に失敗した行を残すと、
    // 続く報告書の保存がこの行をもう一度保存しようとして同じ失敗で落ちる。IADR-0432 再監査 F1）。
    private void SaveOrDetach(ReportRegenerationAttemptRow row)
    {
        try
        {
            db.SaveChanges();
        }
        catch
        {
            db.Entry(row).State = EntityState.Detached;
            throw;
        }
    }
}

// FR-06, 計画 ADR-0052 決定 1, IADR-0491 決定 6: 集計の規則（EF・InMemory の両方が使う純関数）。
public static class ReportRegenerationTallies
{
    public static ReportRegenerationTally Of(IEnumerable<(DateOnly JstDate, ReportRegenerationOutcome Outcome)> rows, int dailyLimit)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var list = rows.ToList();

        var limitDays = list
            .Where(r => r.Outcome == ReportRegenerationOutcome.LimitReached)
            .Select(r => r.JstDate)
            .Concat(list
                .Where(r => ReportRegenerationOutcomes.Counts(r.Outcome))
                .GroupBy(r => r.JstDate)
                .Where(g => g.Count() >= dailyLimit)
                .Select(g => g.Key))
            .Distinct()
            .Count();

        return new ReportRegenerationTally(
            list.Count(r => r.Outcome == ReportRegenerationOutcome.Regenerated),
            list.Count(r => r.Outcome == ReportRegenerationOutcome.RefusedCoreUnsupplied),
            limitDays);
    }
}

// 単体テスト用の台帳（プロセス内）。
public sealed class InMemoryReportRegenerationLedger : IReportRegenerationLedger
{
    private readonly Dictionary<Guid, ReportRegenerationAttempt> _rows = [];
    private readonly Lock _gate = new();

    public int CountOn(DateOnly jstDate)
    {
        lock (_gate)
            return _rows.Values.Count(a => a.JstDate == jstDate && ReportRegenerationOutcomes.Counts(a.Outcome));
    }

    public ReportRegenerationBeginResult TryBegin(ReportRegenerationAttempt attempt, int dailyLimit)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        lock (_gate)
        {
            var used = _rows.Values.Count(a => a.JstDate == attempt.JstDate && ReportRegenerationOutcomes.Counts(a.Outcome));
            if (used >= dailyLimit)
                return new ReportRegenerationBeginResult(false, used);
            _rows.Add(attempt.Id, attempt with { Outcome = ReportRegenerationOutcome.Pending });
            return new ReportRegenerationBeginResult(true, used);
        }
    }

    public void RecordRefusal(ReportRegenerationAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (ReportRegenerationOutcomes.Counts(attempt.Outcome))
            throw new ArgumentException($"断った試行の結果ではありません（{attempt.Outcome}）。", nameof(attempt));
        lock (_gate)
            _rows.Add(attempt.Id, attempt);
    }

    public void Complete(
        Guid id, ReportRegenerationOutcome outcome, int? reportVersion, string? unsuppliedInputs, string? notRestorableInputs)
    {
        lock (_gate)
            _rows[id] = _rows[id] with
            {
                Outcome = outcome,
                ReportVersion = reportVersion,
                UnsuppliedInputs = unsuppliedInputs,
                NotRestorableInputs = notRestorableInputs,
            };
    }

    public ReportRegenerationAttempt? Find(Guid id)
    {
        lock (_gate)
            return _rows.GetValueOrDefault(id);
    }

    public ReportRegenerationTally Tally(DateOnly from, DateOnly to, int dailyLimit)
    {
        lock (_gate)
            return ReportRegenerationTallies.Of(
                _rows.Values.Where(a => a.JstDate >= from && a.JstDate <= to).Select(a => (a.JstDate, a.Outcome)), dailyLimit);
    }

    // 試験が記録の中身を見るための一覧。
    public IReadOnlyList<ReportRegenerationAttempt> Attempts
    {
        get
        {
            lock (_gate)
                return [.. _rows.Values];
        }
    }
}

// 行モデル。結果は列挙名の文字列で持つ（序数に結合しない。DbContext の変換）。
public sealed class ReportRegenerationAttemptRow
{
    public Guid Id { get; set; }

    public DateTimeOffset AttemptedAt { get; set; }

    public DateOnly JstDate { get; set; }

    public string Actor { get; set; } = string.Empty;

    public string PeriodKey { get; set; } = string.Empty;

    public int PreviousVersion { get; set; }

    public ReportRegenerationOutcome Outcome { get; set; }

    public int? ReportVersion { get; set; }

    public string? UnsuppliedInputs { get; set; }

    public string? NotRestorableInputs { get; set; }

    internal static ReportRegenerationAttemptRow From(ReportRegenerationAttempt a) => new()
    {
        Id = a.Id,
        AttemptedAt = a.AttemptedAt,
        JstDate = a.JstDate,
        Actor = a.Actor,
        PeriodKey = a.PeriodKey,
        PreviousVersion = a.PreviousVersion,
        Outcome = a.Outcome,
        ReportVersion = a.ReportVersion,
        UnsuppliedInputs = a.UnsuppliedInputs,
        NotRestorableInputs = a.NotRestorableInputs,
    };

    internal ReportRegenerationAttempt ToAttempt() =>
        new(Id, AttemptedAt, JstDate, Actor, PeriodKey, PreviousVersion, Outcome, ReportVersion, UnsuppliedInputs, NotRestorableInputs);
}
