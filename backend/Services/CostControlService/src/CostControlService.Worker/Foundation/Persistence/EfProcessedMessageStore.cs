using AiStockTrading.CostControl.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.CostControl.Worker.Foundation.Persistence;

// NFR（費用）, IADR-0055 決定5: 重複排除ストアの PostgreSQL 実装（専有 DB cost_control_svc）。
// MessageId を主キーに持つ行の挿入可否で「未処理か」を判定する。同時到達は PK 衝突（DbUpdateException）で
// 検出し、既処理として false を返す。ICostLedger のトランザクションとは独立（決定4: 入れ子にしない）。
internal sealed class EfProcessedMessageStore(CostControlDbContext db) : IProcessedMessageStore
{
    public bool TryMarkProcessed(Guid messageId, DateTimeOffset at)
    {
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
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public void Unmark(Guid messageId)
    {
        var row = db.ProcessedMessages.Find(messageId);
        if (row is null)
        {
            return;
        }

        db.ProcessedMessages.Remove(row);
        db.SaveChanges();
    }
}
