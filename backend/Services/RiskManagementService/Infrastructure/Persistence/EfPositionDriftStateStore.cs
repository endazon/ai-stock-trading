using System.Data.Common;
using RiskManagementService.Features.RiskManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-05, FR-10, #305, IADR-0124: 建玉乖離の追跡状態ストアの EF 実装（単一行・楽観的排他）。
//
// 連続観測回数と報告済みシグネチャを DB へ永続することで、レプリカ間で状態が一貫する
// （インメモリでは replicas>1 で観測が Pod へ分散し、乖離が無言で未報告になり得た）。
// 未記録は PositionDriftState.Initial＝「数え直す」側へ倒す。DbContext は scoped のため本ストアも scoped。
public sealed class EfPositionDriftStateStore(RiskManagementDbContext db) : IPositionDriftStateStore
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
        // FR-05, FR-10, #714, IADR-0317, IADR-0319: **競合の判定は例外の型ではなく「行の実在と版」で行う。**
        // 並行トークン不一致（別レプリカが読み取り後に先行保存した）／初回行の同時挿入（主キー衝突）は、
        // 例外型がプロバイダごとに違う（relational は DbUpdateException、InMemory は ArgumentException）。
        catch (Exception ex) when (ex is DbUpdateException or ArgumentException)
        {
            // 追跡を外して、同一スコープの他の操作へ壊れた状態を持ち越さない。
            // 🔴 ChangeTracker.Clear() は使わない —— 本ストアの DbContext は scoped で他の作業単位と
            // 共有されうるため、無関係な追跡状態まで捨ててしまう。読み直しは AsNoTracking で行う。
            entry.State = EntityState.Detached;

            // #719, IADR-0319 追記: **読み直しは、呼び出し側の明示トランザクションの中では証拠にならない。**
            // REPEATABLE READ 以上のスナップショットは他方のコミット済み行を隠し（「行が無い」と読めてしまう）、
            // Postgres は失敗した文でトランザクション自体が abort する。実 DB の E2E
            // （PositionDriftStateConcurrencyE2ETests）はまさにその形で初回行の同時挿入を決定的に再現しており、
            // 読み直しだけに頼った版は「行が無い＝本物の失敗」と誤って再送出した（2026-09-09・develop 後段 E2E）。
            // そこで、EF／DB が既に確定させた事実を先に見る:
            //   - DbUpdateConcurrencyException ＝ 並行トークン不一致（更新対象の行が他方に先に進められた）。
            //   - SQLSTATE 23505（unique_violation。SQL 標準。プロバイダ固有の例外型ではなく
            //     DbException.SqlState という .NET の抽象で読む）＝ 固定キーの単一行 INSERT が制約で失敗する
            //     唯一の理由であり、他方が先に行を作ったことと同値である。
            // どちらでもないとき（InMemory の ArgumentException・SQLSTATE を持たない失敗）は従来どおり
            // 「行の実在」で判定する。
            if (ex is DbUpdateConcurrencyException || IsUniqueViolation(ex))
            {
                return false;
            }

            var current = db.PositionDriftStates.AsNoTracking()
                .FirstOrDefault(r => r.Id == SingletonKeys.Id);

            // **行が在り、かつ版が自分の読んだ版から動いている＝他方が先に進めた（負けた）。** 報告しない。
            if (current is not null && current.Version != state.Version)
            {
                return false;
            }

            // 行が生まれていない／版が動いていない＝競合ではなく本物の保存失敗である。**握り潰さない**
            // （「負けた」に化けると、乖離が無言で未報告のまま残り、検知が働いていないことに気づけない）。
            throw;
        }
    }

    // SQL 標準の SQLSTATE 23505（unique_violation）。DbException.SqlState は .NET 標準の抽象であり、
    // Npgsql の PostgresException 型へ依存しない（IADR-0317 決定 1 の「型を列挙しない」を保つ）。
    private static bool IsUniqueViolation(Exception ex) =>
        ex.InnerException is DbException { SqlState: "23505" };
}
