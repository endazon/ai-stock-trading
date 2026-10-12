using RiskManagementService.Features.RiskManagement;
using Microsoft.EntityFrameworkCore;

namespace RiskManagementService.Infrastructure.Persistence;

// 🔴 FR-10, FR-20, ADR-0016 決定9, #1319, IADR-0528 決定1・2: 新規建ての審査の直列化と予約の EF 実装（Risk 専有 DB）。
//
// 直列化は **DB のトランザクション境界の advisory lock**（`pg_advisory_xact_lock`）で行う。単一レプリカの前提に頼らない
// ——ロックは DB が持つので、プロセス・レプリカをまたいで新規建ての審査が 1 本ずつになる。ロックはコミット・ロールバックで
// 自動的に外れる（取り忘れ・外し忘れが無い）。審査の読み取り（snapshot）・判定・予約の書き込みは同じ DbContext
// （scoped）の上で走るため、すべてこのトランザクションに入る。READ COMMITTED の各文は文の開始時点のコミット済みを読むので、
// ロックを取った後の読み取りは**前の審査がコミットした予約を必ず見る**。
//
// 関係 DB でないプロバイダ（テストの EF InMemory）はトランザクションも advisory lock も持たないため、
// プロセス内のセマフォへ縮退する（本番の配線は常に Npgsql）。
public sealed class EfEntryReservationStore(RiskManagementDbContext db) : IEntryReservationStore
{
    // advisory lock の鍵（64bit の定数。"AST:entry-screening" の識別に用いるだけで、意味は持たない）。
    // 🔴 変えると、旧版と新版が並走する切り替えの間だけ直列化が 2 系統に割れる。変えない。
    internal const long AdvisoryLockKey = 0x4153_5445_4E54_5259; // "ASTENTRY"

    private static readonly SemaphoreSlim NonRelationalGate = new(1, 1);

    public async Task<T> RunSerializedAsync<T>(Func<CancellationToken, Task<T>> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (!db.Database.IsRelational())
        {
            await NonRelationalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await body(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                NonRelationalGate.Release();
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.Database
            .ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockKey})", cancellationToken)
            .ConfigureAwait(false);

        // 例外で抜けたら await using の破棄がロールバックし、ロックも外れる（予約は残らない）。
        var result = await body(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public EntryReservation? Find(Guid decisionId) =>
        db.EntryReservations.AsNoTracking().FirstOrDefault(r => r.DecisionId == decisionId) is { } r
            ? ToReservation(r)
            : null;

    public void Reserve(EntryReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);

        // 冪等（直列化の中で呼ばれるため、同じ判断の予約が並行に 2 本書かれることはない）。
        // 古い行は消さない（統制の状態であり自動パージの対象外・切替でも保全する。scripts/cutover-count-reconcile.sh の
        // manifest が state として持つ）。注文源は承認時刻の下限と当日の判定で切り出すので、古い行は算入されない。
        if (db.EntryReservations.Any(r => r.DecisionId == reservation.DecisionId))
            return;

        db.EntryReservations.Add(new EntryReservationRow
        {
            DecisionId = reservation.DecisionId,
            Symbol = reservation.Symbol,
            Market = reservation.Market,
            Side = reservation.Side,
            Quantity = reservation.Quantity,
            Price = reservation.Price,
            FxRateToBase = reservation.FxRateToBase,
            ApprovedQuantity = reservation.ApprovedQuantity,
            ApprovedAt = reservation.ApprovedAt,
            StopLossMethod = reservation.StopLossMethod,
        });
        db.SaveChanges();
    }

    internal static EntryReservation ToReservation(EntryReservationRow r) =>
        new(r.DecisionId, r.Symbol, r.Market, r.Side, r.Quantity, r.Price, r.FxRateToBase,
            r.ApprovedQuantity, r.ApprovedAt, r.StopLossMethod);
}
