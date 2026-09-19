using System.Text.Json;
using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.EntityFrameworkCore;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-05, FR-10, FR-11, #849, IADR-0350 決定 1: 最新の建玉観測の EF 実装（単一行）。
//
// **永続でなければならない。** `BrokerPositionsObserved` は単一キューで受ける（IADR-0106）ため、replicas>1 では
// 観測を受けた Pod と取り込み API を受けた Pod が違い得る。プロセス内に持つと、取り込みが「観測が無い」で
// 拒否されるか、古い観測へ合わせるかのどちらかになる。DbContext は scoped のため本ストアも scoped。
public sealed class EfBrokerPositionObservationStore(RiskManagementDbContext db) : IBrokerPositionObservationStore
{
    public void Record(IReadOnlyList<BrokerPositionSnapshot> positions, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(positions);

        var row = db.BrokerPositionObservations.Find(SingletonKeys.Id);
        var json = JsonSerializer.Serialize(positions);

        if (row is null)
        {
            db.BrokerPositionObservations.Add(new BrokerPositionObservationRow
            {
                Id = SingletonKeys.Id,
                PositionsJson = json,
                ObservedAtUtc = observedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else if (observedAt > row.ObservedAtUtc)
        {
            row.PositionsJson = json;
            row.ObservedAtUtc = observedAt;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            // 逆行する観測（再送・順序前後）で新しい観測を古い値へ戻さない。書き込みも起こさない。
            return;
        }

        try
        {
            db.SaveChanges();
        }
        // #714, IADR-0317, IADR-0319: 競合の判定は例外の型ではなく**行の実在**で行う（初回行の同時挿入の例外型は
        // プロバイダごとに違う）。
        catch (Exception ex) when (ex is DbUpdateException or ArgumentException)
        {
            // 🔴 ChangeTracker.Clear() は使わない —— 本ストアの DbContext は scoped で、同じ観測ハンドラの他の
            // 作業単位（到達の記録・乖離の追跡状態）と共有される。自分が追跡させた行だけを外す。
            foreach (var entry in db.ChangeTracker.Entries<BrokerPositionObservationRow>().ToList())
                entry.State = EntityState.Detached;

            // 行が在る＝別レプリカが先に初回行を作った。こちらの観測は次の巡回が上書きするため捨ててよい
            // （古い側が残っても、取り込みサービスの鮮度判定が拒否側へ倒す）。
            if (db.BrokerPositionObservations.AsNoTracking().Any(r => r.Id == SingletonKeys.Id))
                return;

            // 行が生まれていない＝競合ではなく本物の書き込み失敗である。握り潰さない。
            throw;
        }
    }

    public BrokerPositionObservation? GetLatest()
    {
        var row = db.BrokerPositionObservations.AsNoTracking()
            .FirstOrDefault(r => r.Id == SingletonKeys.Id);
        if (row is null)
            return null;

        // 直列化は本ストアが書いた形だけを読む。壊れた JSON は**不明**として扱う（空列＝建玉なしへ倒さない
        // ——空列は「全建玉が消えた」という観測事実であり、取り込めば台帳の全建玉が消える）。
        IReadOnlyList<BrokerPositionSnapshot>? positions;
        try
        {
            positions = JsonSerializer.Deserialize<List<BrokerPositionSnapshot>>(row.PositionsJson);
        }
        catch (JsonException)
        {
            return null;
        }

        return positions is null ? null : new BrokerPositionObservation(positions, row.ObservedAtUtc);
    }
}
