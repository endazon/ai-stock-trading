using ReportService.Features.Reports;

namespace ReportService.Infrastructure.Persistence;

// FR-14, ADR-0042 決定 3, #1024, IADR-0432 決定 1: `/policy` の試行の台帳（EF・report_svc の policy_revision_attempts 表）。
public sealed class EfPolicyRevisionLedger(ReportDbContext db) : IPolicyRevisionLedger
{
    public int CountOn(DateOnly jstDate) => db.PolicyRevisionAttempts.Count(a => a.JstDate == jstDate);

    public Guid Begin(PolicyRevisionAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        db.PolicyRevisionAttempts.Add(PolicyRevisionAttemptRow.From(attempt));
        db.SaveChanges();
        return attempt.Id;
    }

    public void Complete(Guid id, PolicyRevisionAttemptOutcome outcome, int? reportVersion, string? watchlistChangesJson)
    {
        var row = db.PolicyRevisionAttempts.Find(id)
            ?? throw new InvalidOperationException($"方針の改訂の試行 {id} がありません。");
        row.Outcome = outcome;
        row.ReportVersion = reportVersion;
        row.WatchlistChangesJson = watchlistChangesJson;
        db.SaveChanges();
    }

    public PolicyRevisionAttempt? Find(Guid id) => db.PolicyRevisionAttempts.Find(id)?.ToAttempt();
}

// 単体テスト用の台帳（プロセス内）。
public sealed class InMemoryPolicyRevisionLedger : IPolicyRevisionLedger
{
    private readonly Dictionary<Guid, PolicyRevisionAttempt> _rows = [];
    private readonly Lock _gate = new();

    public int CountOn(DateOnly jstDate)
    {
        lock (_gate)
            return _rows.Values.Count(a => a.JstDate == jstDate);
    }

    public Guid Begin(PolicyRevisionAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        lock (_gate)
            _rows.Add(attempt.Id, attempt);
        return attempt.Id;
    }

    public void Complete(Guid id, PolicyRevisionAttemptOutcome outcome, int? reportVersion, string? watchlistChangesJson)
    {
        lock (_gate)
            _rows[id] = _rows[id] with { Outcome = outcome, ReportVersion = reportVersion, WatchlistChangesJson = watchlistChangesJson };
    }

    public PolicyRevisionAttempt? Find(Guid id)
    {
        lock (_gate)
            return _rows.GetValueOrDefault(id);
    }

    // 試験が記録の中身を見るための一覧。
    public IReadOnlyList<PolicyRevisionAttempt> Attempts
    {
        get
        {
            lock (_gate)
                return [.. _rows.Values];
        }
    }
}

// 行モデル。
public sealed class PolicyRevisionAttemptRow
{
    public Guid Id { get; set; }

    public DateTimeOffset AttemptedAt { get; set; }

    public DateOnly JstDate { get; set; }

    public string Actor { get; set; } = string.Empty;

    public string PeriodKey { get; set; } = string.Empty;

    public PolicyRevisionAttemptOutcome Outcome { get; set; }

    public int? ReportVersion { get; set; }

    public string? WatchlistChangesJson { get; set; }

    internal static PolicyRevisionAttemptRow From(PolicyRevisionAttempt a) => new()
    {
        Id = a.Id,
        AttemptedAt = a.AttemptedAt,
        JstDate = a.JstDate,
        Actor = a.Actor,
        PeriodKey = a.PeriodKey,
        Outcome = a.Outcome,
        ReportVersion = a.ReportVersion,
        WatchlistChangesJson = a.WatchlistChangesJson,
    };

    internal PolicyRevisionAttempt ToAttempt() =>
        new(Id, AttemptedAt, JstDate, Actor, PeriodKey, Outcome, ReportVersion, WatchlistChangesJson);
}
