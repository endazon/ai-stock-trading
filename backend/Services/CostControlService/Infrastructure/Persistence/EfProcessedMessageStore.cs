using CostControlService.Features.CostControl;
using Microsoft.EntityFrameworkCore;

namespace CostControlService.Infrastructure.Persistence;

// NFR（費用）, IADR-0055 決定5: 重複排除ストアの PostgreSQL 実装（専有 DB cost_control_svc）。
// MessageId を主キーに持つ行の挿入可否で「未処理か」を判定する。同時到達は PK 衝突（DbUpdateException）で
// 検出し、既処理として false を返す。
//
// IADR-0034（「Record を他のユニットオブワークと組み合わせない」）/ IADR-0055 決定4: 本ストアは
// **操作ごとに専用の短命 DbContext** を生成し、`EfCostLedger`（scoped DbContext・自前トランザクション）とは
// ChangeTracker を一切共有しない。共有した場合、計上の SaveChanges が失敗して CostEntryRow が Added のまま
// 残留した状態で Unmark の SaveChanges を呼ぶと、ロールバックされるべき計上行が一緒に INSERT される／
// 削除まで巻き込んで失敗する（マークが戻らず計上も欠落する）不具合が起き得る。
public sealed class EfProcessedMessageStore(DbContextOptions<CostControlDbContext> options) : IProcessedMessageStore
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
        // NFR（費用）, #714, IADR-0317, IADR-0319: **重複の判定は例外の型ではなく「行が実在するか」で行う。**
        // 一意キー違反の例外型はプロバイダごとに違う（relational は DbUpdateException、InMemory は
        // ArgumentException）ため、型を列挙すると取りこぼした側だけが素通りする。
        catch (Exception ex) when (ex is DbUpdateException or ArgumentException)
        {
            db.ChangeTracker.Clear();
            if (db.ProcessedMessages.AsNoTracking().Any(r => r.MessageId == messageId))
            {
                // 同時到達で主キー衝突＝他方が先に処理済み。二重計上を避けるため false（no-op）に倒す。
                return false;
            }

            // 🔴 **マーク行が生まれていない＝重複ではなく本物の書き込み失敗である。握り潰さない。**
            // 従来は false に化け、呼び出し側（LlmCostIncurredHandler）が「処理済み」とみなして
            // return していた —— **費用が 1 円も計上されないままメッセージが消える**（fail-open）。
            // 再送出すれば再配送で再試行される。
            throw;
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

    // NFR（運用）, #137, IADR-0059: cutoff より古い行のみをバッチ削除する。
    // ExecuteDelete（set-based）はリレーショナル専用で InMemory プロバイダのテストから外れるため、
    // 述語を CI で守れる Where + RemoveRange を採る（IADR-0059 決定5）。batchSize がメモリ使用の上限になる。
    public int PurgeProcessedBefore(DateTimeOffset cutoff, int batchSize)
    {
        using var db = new CostControlDbContext(options);

        // 「< cutoff」＝境界ちょうどは残す（消し過ぎない側へ倒す）。古い順に消し、残りは次回の巡回に回す。
        var expired = db.ProcessedMessages
            .Where(r => r.ProcessedAt < cutoff)
            .OrderBy(r => r.ProcessedAt)
            .Take(batchSize)
            .ToList();

        if (expired.Count == 0)
            return 0;

        db.ProcessedMessages.RemoveRange(expired);
        db.SaveChanges();
        return expired.Count;
    }
}
