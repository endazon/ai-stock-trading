using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.EntityFrameworkCore;

namespace OrderExecutionService.Infrastructure.Persistence;

// FR-10, #331, IADR-0210 決定6: 保護逆指値レグ記録ストアの EF 実装（発注執行の専有 DB）。
// Save は EntryDecisionId で upsert（再発注＝試行の置き換え）。TrySave は版が一致するときだけ書く（#833 項目3, IADR-0396）。
public sealed class EfProtectiveStopOrderStore(OrderExecutionDbContext db) : IProtectiveStopOrderStore
{
    // 🔴 #833 項目3, IADR-0396: 無条件の上書き（版は保存先の値から 1 進める）。Version は EF の並行トークンなので、
    // このコンテキストが追跡している行が古い（別のスコープが先に書いた）と SaveChanges が 0 行で衝突する。
    // 無条件の保存はそこで失敗させず、追跡を外して保存先から読み直し、同じ値を当て直す（従来の last-writer-wins を保つ）。
    public void Save(ProtectiveStopOrder stop)
    {
        ArgumentNullException.ThrowIfNull(stop);

        for (var attempt = 1; ; attempt++)
        {
            var row = db.ProtectiveStopOrders.Find(stop.EntryDecisionId);
            if (row is null)
            {
                row = new ProtectiveStopOrderRow { EntryDecisionId = stop.EntryDecisionId, Version = stop.Version };
                Apply(row, stop);
                db.ProtectiveStopOrders.Add(row);
            }
            else
            {
                Apply(row, stop);
                row.Version++;
            }

            try
            {
                db.SaveChanges();
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < ProtectiveStopStoreUpdates.MaxAttempts)
            {
                Forget(row);
            }
        }
    }

    // 🔴 FR-10, #833 項目3, IADR-0396: 楽観並行の保存。「WHERE EntryDecisionId = @id AND Version = @読んだ時点の版」で書く
    // （元の値に stop.Version を置く）。0 行なら何も書かず false。追跡を外すので、次の Find は保存先の最新を読む
    // （同じコンテキストの他のストアの SaveChanges に、失敗した更新が混ざって再送されることも無い）。
    public bool TrySave(ProtectiveStopOrder stop)
    {
        ArgumentNullException.ThrowIfNull(stop);

        var row = db.ProtectiveStopOrders.Find(stop.EntryDecisionId);
        if (row is null || row.Version != stop.Version)
            return false;

        Apply(row, stop);
        var version = db.Entry(row).Property(r => r.Version);
        version.OriginalValue = stop.Version;
        version.CurrentValue = stop.Version + 1;
        try
        {
            db.SaveChanges();
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            Forget(row);
            return false;
        }
    }

    private void Forget(ProtectiveStopOrderRow row) => db.Entry(row).State = EntityState.Detached;

    private static void Apply(ProtectiveStopOrderRow row, ProtectiveStopOrder stop)
    {
        row.StopDecisionId = stop.StopDecisionId;
        row.StopOrderId = stop.StopOrderId;
        row.Symbol = stop.Symbol;
        row.Market = stop.Market;
        row.EntrySide = stop.EntrySide;
        row.ProductType = stop.ProductType;
        row.Mode = stop.Mode;
        row.Quantity = stop.Quantity;
        row.TriggerPrice = stop.TriggerPrice;
        row.FxRateToBase = stop.FxRateToBase;
        row.Attempt = stop.Attempt;
        row.State = stop.State;
        row.CreatedAt = stop.CreatedAt;
        row.UpdatedAt = stop.UpdatedAt;
        row.Mechanism = stop.Mechanism;
        row.TriggeredAt = stop.TriggeredAt;
        row.TriggeredPrice = stop.TriggeredPrice;
        row.RemainingProtected = stop.RemainingProtected;
        row.StalledNotifiedAt = stop.StalledNotifiedAt;
        row.PendingExternalReduction = stop.PendingExternalReduction;
        row.ExternalReductionObservations = stop.ExternalReductionObservations;
        row.ExternalReductionAbsences = stop.ExternalReductionAbsences;
        row.ProtectionSuspendedSince = stop.ProtectionSuspendedSince;
        row.ProtectionSuspendedNotifiedAt = stop.ProtectionSuspendedNotifiedAt;
        row.UnattributedNotifiedQuantity = stop.UnattributedNotifiedQuantity;
        row.UnattributedNotifiedAt = stop.UnattributedNotifiedAt;
        row.CloseFailures = stop.CloseFailures;
        row.NextCloseAttemptAt = stop.NextCloseAttemptAt;
        row.LastTriggerSeenAt = stop.LastTriggerSeenAt;
    }

    public ProtectiveStopOrder? Find(Guid entryDecisionId)
    {
        var row = db.ProtectiveStopOrders.Find(entryDecisionId);
        return row is null ? null : ToDomain(row);
    }

    public IReadOnlyList<ProtectiveStopOrder> FindActive(int batchSize) =>
        db.ProtectiveStopOrders
            .Where(r => r.State == ProtectiveStopState.Active)
            .OrderBy(r => r.CreatedAt)
            .Take(batchSize)
            .ToList()
            .Select(ToDomain)
            .ToList();

    // #820, IADR-0344 決定1・決定4: 損切りライン到達の突き合わせ対象（Active な S1 の同一銘柄・同一方向）。
    public IReadOnlyList<ProtectiveStopOrder> FindActiveSoftwareStops(string symbol, Market market, TradeSide entrySide) =>
        db.ProtectiveStopOrders
            .Where(r => r.State == ProtectiveStopState.Active
                && r.Mechanism == StopLossExecutionMethod.SoftwareStop
                && r.Symbol == symbol
                && r.Market == market
                && r.EntrySide == entrySide)
            .OrderBy(r => r.CreatedAt)
            .ToList()
            .Select(ToDomain)
            .ToList();

    // #820 の 6 巡目監査・7 巡目監査, IADR-0344 追記(6)・追記(7): 観測を数え続けてよいかの門が読む
    // 「完了済みの S1」（更新が新しい順・上限つき）。
    public IReadOnlyList<ProtectiveStopOrder> FindCompletedSoftwareStops(
        string symbol, Market market, TradeSide entrySide, int limit) =>
        db.ProtectiveStopOrders
            .Where(r => r.State == ProtectiveStopState.Completed
                && r.Mechanism == StopLossExecutionMethod.SoftwareStop
                && r.Symbol == symbol
                && r.Market == market
                && r.EntrySide == entrySide)
            .OrderByDescending(r => r.UpdatedAt)
            .Take(limit)
            .ToList()
            .Select(ToDomain)
            .ToList();

    private static ProtectiveStopOrder ToDomain(ProtectiveStopOrderRow r) =>
        new(r.EntryDecisionId, r.StopDecisionId, r.StopOrderId, r.Symbol, r.Market, r.EntrySide,
            r.ProductType, r.Mode, r.Quantity, r.TriggerPrice, r.FxRateToBase, r.Attempt, r.State,
            r.CreatedAt, r.UpdatedAt, r.Mechanism, r.TriggeredAt, r.TriggeredPrice,
            r.RemainingProtected, r.StalledNotifiedAt,
            r.PendingExternalReduction, r.ExternalReductionObservations,
            r.ExternalReductionAbsences, r.ProtectionSuspendedSince, r.ProtectionSuspendedNotifiedAt,
            r.UnattributedNotifiedQuantity, r.UnattributedNotifiedAt,
            r.CloseFailures, r.NextCloseAttemptAt, r.LastTriggerSeenAt, r.Version);
}
