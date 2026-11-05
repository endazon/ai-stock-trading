using Microsoft.EntityFrameworkCore;
using Npgsql;
using RiskManagementService.Features.RiskManagement;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-10, ADR-0003: kill switch 状態の EF 実装（単一行）。
// #1534, IADR-0620: 版（Version 列）は渡された値を使わず、書くたびにストアが 1 つ進める（単調増加・並行トークン）。
public sealed class EfKillSwitchStore(RiskManagementDbContext db) : IKillSwitchStore
{
    public KillSwitchState GetState()
    {
        var row = db.KillSwitch.Find(SingletonKeys.Id);
        return ToState(row);
    }

    public void SetState(KillSwitchState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var row = db.KillSwitch.Find(SingletonKeys.Id);
        if (row is null)
        {
            db.KillSwitch.Add(new KillSwitchRow
            {
                Id = SingletonKeys.Id,
                Engaged = state.Engaged,
                Actor = state.Actor,
                Reason = state.Reason,
                ChangedAt = state.ChangedAt,
                Version = 1,
            });
        }
        else
        {
            row.Engaged = state.Engaged;
            row.Actor = state.Actor;
            row.Reason = state.Reason;
            row.ChangedAt = state.ChangedAt;
            row.Version++;
        }

        db.SaveChanges();
    }

    // 🔴 #1534, IADR-0620: 比較と書き込みを 1 つの取引で行う（関係 DB では直列化可能。版の列は並行トークン）。
    // 「版が違う」（false＝409）として扱うのは、比較の不一致・直列化の失敗（40001）・デッドロック（40P01）・楽観排他の失敗だけ。
    // 接続断・タイムアウト・その他の DB の失敗は再送出する（5xx）—— DB の障害を「状態が変わった」と見せない。
    public bool TrySetState(KillSwitchState state, string expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var tx = db.Database.IsRelational()
            ? db.Database.BeginTransaction(System.Data.IsolationLevel.Serializable)
            : null;
        try
        {
            var current = ToState(db.KillSwitch.Find(SingletonKeys.Id));
            if (!string.Equals(current.Version, expectedVersion, StringComparison.Ordinal))
                return false;

            SetState(state);
            tx?.Commit();
            return true;
        }
        catch (Exception ex) when (IsConflict(ex))
        {
            // 失敗した書き込みの追跡を外す（DbContext はスコープ付きだが、同じスコープの後続の SaveChanges に持ち越さない）。
            foreach (var entry in db.ChangeTracker.Entries<KillSwitchRow>().ToList())
                entry.State = EntityState.Detached;
            return false;
        }
    }

    public static bool IsConflict(Exception ex)
    {
        if (ex is DbUpdateConcurrencyException)
            return true;
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected })
                return true;
        }

        return false;
    }

    private static KillSwitchState ToState(KillSwitchRow? row) =>
        row is null
            ? KillSwitchState.Disengaged
            : new KillSwitchState(row.Engaged, row.Actor, row.Reason, row.ChangedAt, row.Version);
}
