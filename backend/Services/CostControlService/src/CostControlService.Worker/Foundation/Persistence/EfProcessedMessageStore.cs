using AiStockTrading.CostControl.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.CostControl.Worker.Foundation.Persistence;

// NFR（費用）, IADR-0055 決定5: 重複排除ストアの PostgreSQL 実装（専有 DB cost_control_svc）。
// MessageId を主キーに持つ行の挿入可否で「未処理か」を判定する。同時到達は PK 衝突（DbUpdateException）で
// 検出し、既処理として false を返す。
//
// IADR-0034（「Record を他のユニットオブワークと組み合わせない」）/ IADR-0055 決定4: 本ストアは
// **操作ごとに専用の短命 DbContext** を生成し、`EfCostLedger`（scoped DbContext・自前トランザクション）とは
// ChangeTracker を一切共有しない。共有した場合、計上の SaveChanges が失敗して CostEntryRow が Added のまま
// 残留した状態で Unmark の SaveChanges を呼ぶと、ロールバックされるべき計上行が一緒に INSERT される／
// 削除まで巻き込んで失敗する（マークが戻らず計上も欠落する）不具合が起き得る。
internal sealed class EfProcessedMessageStore(DbContextOptions<CostControlDbContext> options) : IProcessedMessageStore
{
    public bool TryMarkProcessed(Guid messageId, DateTimeOffset at)
    {
        using var db = new CostControlDbContext(options);

        if (db.ProcessedMessages.Any(r => r.MessageId == messageId))
        {
            return false;
        }

        db.ProcessedMessages.Add(new ProcessedMessageRow { MessageId = messageId, ProcessedAt = at });
        try
        {
            db.SaveChanges();
            return true;
        }
        catch (DbUpdateException)
        {
            // 同時到達で主キー衝突＝他方が先に処理済み。二重計上を避けるため false（no-op）に倒す。
            return false;
        }
    }

    public void Unmark(Guid messageId)
    {
        using var db = new CostControlDbContext(options);

        var row = db.ProcessedMessages.Find(messageId);
        if (row is null)
        {
            return;
        }

        db.ProcessedMessages.Remove(row);
        db.SaveChanges();
    }
}
