using Microsoft.EntityFrameworkCore;
using ReportService.Features.Reports;

namespace ReportService.Infrastructure.Persistence;

// FR-14, ADR-0042 決定 3, #1024, IADR-0432 決定 1: `/policy` の試行の台帳（EF・report_svc の policy_revision_attempts 表）。
public sealed class EfPolicyRevisionLedger(ReportDbContext db) : IPolicyRevisionLedger
{
    // Postgres 以外（試験の InMemory）で TryBegin の排他区間を作るプロセス内の錠。DbContext はスコープごとに別なので静的に持つ。
    private static readonly Lock NonRelationalGate = new();

    // 勧告ロックの鍵の上位 32 ビット（"POLR"）。下位は JST の暦日の通し番号（DateOnly.DayNumber）。
    private const long AdvisoryLockNamespace = 0x504F4C52L << 32;

    // 🔴 **失敗した試行も数える**（結果を問わない）。応答が返らない・形式違反の呼び出しにも費用が掛かり得るため。
    public int CountOn(DateOnly jstDate) => db.PolicyRevisionAttempts.Count(a => a.JstDate == jstDate);

    public PolicyRevisionBeginResult TryBegin(PolicyRevisionAttempt attempt, int dailyLimit)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        if (db.Database.IsNpgsql())
        {
            using var tx = db.Database.BeginTransaction();
            var key = AdvisoryLockNamespace | (uint)attempt.JstDate.DayNumber;
            // トランザクションの終わり（commit / rollback）で自動的に外れる勧告ロック。同じ暦日の TryBegin を直列化する。
            db.Database.ExecuteSql($"SELECT pg_advisory_xact_lock({key})");
            var result = CountAndAdd(attempt, dailyLimit);
            tx.Commit();
            return result;
        }

        lock (NonRelationalGate)
            return CountAndAdd(attempt, dailyLimit);
    }

    private PolicyRevisionBeginResult CountAndAdd(PolicyRevisionAttempt attempt, int dailyLimit)
    {
        var used = CountOn(attempt.JstDate);
        if (used >= dailyLimit)
            return new PolicyRevisionBeginResult(false, used);

        var row = PolicyRevisionAttemptRow.From(attempt);
        db.PolicyRevisionAttempts.Add(row);
        SaveOrDetach(row);
        return new PolicyRevisionBeginResult(true, used);
    }

    public Guid Begin(PolicyRevisionAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var row = PolicyRevisionAttemptRow.From(attempt);
        db.PolicyRevisionAttempts.Add(row);
        SaveOrDetach(row);
        return attempt.Id;
    }

    public void Complete(Guid id, PolicyRevisionAttemptOutcome outcome, int? reportVersion, string? watchlistChangesJson)
    {
        var row = db.PolicyRevisionAttempts.Find(id)
            ?? throw new InvalidOperationException($"方針の改訂の試行 {id} がありません。");
        row.Outcome = outcome;
        row.ReportVersion = reportVersion;
        row.WatchlistChangesJson = watchlistChangesJson;
        SaveOrDetach(row);
    }

    // FR-14, #1024, IADR-0432（PR #1026 の再監査 F1）: 台帳の書き込みが失敗したら、その行を追跡から外してから例外を上げる。
    // 台帳は報告書のストアと DbContext（スコープ）を共有しており、失敗した行を Modified / Added のまま残すと、続く報告書の
    // 保存（提示の ApplyReview 等）がこの行をもう一度保存しようとして同じ失敗で落ちる（保存済みの案が提示されなくなる）。
    // #1029, IADR-0432（追記）: 台帳の書き込みはすべてこれを通す（Begin・MarkProposalConfirmed・RecordWatchlistApply も）。
    private void SaveOrDetach(PolicyRevisionAttemptRow row)
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

    public PolicyRevisionAttempt? Find(Guid id) => db.PolicyRevisionAttempts.Find(id)?.ToAttempt();

    // #1029, IADR-0432（追記）: WatchlistAppliedAt が同時実行のトークンのため、読んでから書くまでの間に内訳が記録されると
    // DbUpdateConcurrencyException を上げ得る（確定の遷移の直後に書くので実運用では起きない）。呼び手は best-effort として扱うこと
    // （現在の唯一の呼び手 ConfirmReport は例外を警告に留める）。
    public void MarkProposalConfirmed(Guid id, DateTimeOffset confirmedAt)
    {
        var row = db.PolicyRevisionAttempts.Find(id);
        if (row is null || row.ProposalConfirmedAt is not null)
            return;
        row.ProposalConfirmedAt = confirmedAt;
        SaveOrDetach(row);
    }

    public PolicyRevisionAttempt? FindProposed(string periodKey, int reportVersion) =>
        db.PolicyRevisionAttempts
            .Where(a => a.PeriodKey == periodKey && a.ReportVersion == reportVersion
                && a.Outcome == PolicyRevisionAttemptOutcome.Proposed)
            .OrderByDescending(a => a.AttemptedAt)
            .FirstOrDefault()?.ToAttempt();

    public bool RecordWatchlistApply(Guid id, string resultJson, DateTimeOffset recordedAt)
    {
        var row = db.PolicyRevisionAttempts.Find(id);
        if (row is null || row.WatchlistAppliedAt is not null)
            return false;
        row.WatchlistApplyJson = resultJson;
        row.WatchlistAppliedAt = recordedAt;
        try
        {
            SaveOrDetach(row);
        }
        catch (DbUpdateConcurrencyException)
        {
            // #1029, IADR-0432（追記）: 読んでから書くまでの間に、別の書き手（別の DbContext・別のプロセス）が先に記録した。
            // WatchlistAppliedAt は同時実行のトークンで、保存は「まだ記録が無い（NULL のまま）」行だけを更新する。
            // **先の内訳を上書きせず**「記録済み」として返す（行は SaveOrDetach が追跡から外した）。
            return false;
        }

        return true;
    }
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

    public PolicyRevisionBeginResult TryBegin(PolicyRevisionAttempt attempt, int dailyLimit)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        lock (_gate)
        {
            var used = _rows.Values.Count(a => a.JstDate == attempt.JstDate);
            if (used >= dailyLimit)
                return new PolicyRevisionBeginResult(false, used);
            _rows.Add(attempt.Id, attempt);
            return new PolicyRevisionBeginResult(true, used);
        }
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

    public PolicyRevisionAttempt? FindProposed(string periodKey, int reportVersion)
    {
        lock (_gate)
            return _rows.Values
                .Where(a => a.PeriodKey == periodKey && a.ReportVersion == reportVersion
                    && a.Outcome == PolicyRevisionAttemptOutcome.Proposed)
                .OrderByDescending(a => a.AttemptedAt)
                .FirstOrDefault();
    }

    public bool RecordWatchlistApply(Guid id, string resultJson, DateTimeOffset recordedAt)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(id, out var row) || row.WatchlistAppliedAt is not null)
                return false;
            _rows[id] = row with { WatchlistApplyJson = resultJson, WatchlistAppliedAt = recordedAt };
            return true;
        }
    }

    public void MarkProposalConfirmed(Guid id, DateTimeOffset confirmedAt)
    {
        lock (_gate)
        {
            if (_rows.TryGetValue(id, out var row) && row.ProposalConfirmedAt is null)
                _rows[id] = row with { ProposalConfirmedAt = confirmedAt };
        }
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

    // FR-13, ADR-0042 決定 1, #1025: 案を作った時点の監視銘柄（null＝照会できなかった）・適用の内訳・記録時刻。
    public string? WatchlistSnapshotJson { get; set; }

    public string? WatchlistApplyJson { get; set; }

    public DateTimeOffset? WatchlistAppliedAt { get; set; }

    // #1025（PR #1027 の監査 M1）: 報告書がこの試行の版で確定された時刻。
    public DateTimeOffset? ProposalConfirmedAt { get; set; }

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
        WatchlistSnapshotJson = a.WatchlistSnapshotJson,
        WatchlistApplyJson = a.WatchlistApplyJson,
        WatchlistAppliedAt = a.WatchlistAppliedAt,
        ProposalConfirmedAt = a.ProposalConfirmedAt,
    };

    internal PolicyRevisionAttempt ToAttempt() =>
        new(Id, AttemptedAt, JstDate, Actor, PeriodKey, Outcome, ReportVersion, WatchlistChangesJson,
            WatchlistSnapshotJson, WatchlistApplyJson, WatchlistAppliedAt, ProposalConfirmedAt);
}
