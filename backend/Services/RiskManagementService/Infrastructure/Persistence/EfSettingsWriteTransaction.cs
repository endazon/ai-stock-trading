using RiskManagementService.Features.RiskManagement;
using Microsoft.EntityFrameworkCore;

namespace RiskManagementService.Infrastructure.Persistence;

// 🔴 FR-11, FR-10, ADR-0007, #1291, IADR-0532 決定1: 設定の保存と変更履歴の記録を 1 つの DB トランザクションにまとめる EF 実装。
//
// 設定のストア（EfRiskSettingsStore）と履歴（EfSettingsChangeLog）は同じ scoped の DbContext を使い、それぞれ SaveChanges を呼ぶ。
// 明示のトランザクションが無いと 2 つの暗黙のトランザクションに割れ、履歴だけが失敗すると設定の変更だけが残る（応答は 500）。
// ここで包むと、両方の SaveChanges が同じトランザクションに入り、例外なら破棄（ロールバック）される。
//
// 関係 DB でないプロバイダ（テストの EF InMemory）はトランザクションを持たないため、本体をそのまま走らせる
// （EfEntryReservationStore と同じ縮退。本番の配線は常に Npgsql）。外側で既にトランザクションが張られていれば、それに乗る。
public sealed class EfSettingsWriteTransaction(RiskManagementDbContext db) : ISettingsWriteTransaction
{
    public void Execute(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null)
        {
            body();
            return;
        }

        using var transaction = db.Database.BeginTransaction();
        try
        {
            body();
            transaction.Commit();
        }
        catch
        {
            // ロールバックは using の破棄が行う。変更追跡には DB に残らなかった値（版の加算・履歴の行）が載ったままなので捨てる
            // （同じスコープで続けて読んだときに、取り消された値を現在値と取り違えない）。
            db.ChangeTracker.Clear();
            throw;
        }
    }
}
