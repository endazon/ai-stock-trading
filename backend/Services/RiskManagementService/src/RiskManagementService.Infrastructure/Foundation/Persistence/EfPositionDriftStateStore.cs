using RiskManagementService.Application.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-05, FR-10, #305, IADR-0124: 建玉乖離の追跡状態ストアの EF 実装（単一行・楽観的排他）。
//
// 連続観測回数と報告済みシグネチャを DB へ永続することで、レプリカ間で状態が一貫する
// （インメモリでは replicas>1 で観測が Pod へ分散し、乖離が無言で未報告になり得た）。
// 未記録は PositionDriftState.Initial＝「数え直す」側へ倒す。DbContext は scoped のため本ストアも scoped。
internal sealed class EfPositionDriftStateStore(RiskManagementDbContext db) : IPositionDriftStateStore
{
    public PositionDriftState Get()
    {
        var row = db.PositionDriftStates.Find(SingletonKeys.Id);
        return row is null
            ? PositionDriftState.Initial
            : new PositionDriftState(row.ObservedSignature, row.ConsecutiveCount, row.ReportedSignature, row.Version);
    }

    public bool TrySave(PositionDriftState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var row = db.PositionDriftStates.Find(SingletonKeys.Id);

        // 読み取った版が現在版と違う＝別レプリカが先に進めた。何も書かずに負けを返す。
        if (row is not null && row.Version != state.Version)
        {
            return false;
        }

        EntityEntry<PositionDriftStateRow> entry;
        if (row is null)
        {
            entry = db.PositionDriftStates.Add(new PositionDriftStateRow
            {
                Id = SingletonKeys.Id,
                ObservedSignature = state.ObservedSignature,
                ConsecutiveCount = state.ConsecutiveCount,
                ReportedSignature = state.ReportedSignature,
                Version = state.Version + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            row.ObservedSignature = state.ObservedSignature;
            row.ConsecutiveCount = state.ConsecutiveCount;
            row.ReportedSignature = state.ReportedSignature;
            row.Version = state.Version + 1;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            entry = db.Entry(row);
        }

        try
        {
            db.SaveChanges();
            return true;
        }
        catch (DbUpdateException)
        {
            // 並行トークン不一致（別レプリカが読み取り後に先行保存した）／初回行の同時挿入（主キー衝突）。
            // どちらも「負けた」＝報告しない。追跡を外して、同一スコープの他の操作へ壊れた状態を持ち越さない。
            entry.State = EntityState.Detached;
            return false;
        }
    }
}
