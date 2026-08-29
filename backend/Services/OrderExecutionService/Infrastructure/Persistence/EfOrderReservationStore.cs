using OrderExecutionService.Features.OrderExecution;
using Microsoft.EntityFrameworkCore;

namespace OrderExecutionService.Infrastructure.Persistence;

// #131, FR-05, IADR-0057: 発注前 DecisionId 予約ストアの EF 実装。
// TryReserve はブローカ発注より前に呼ばれ、SaveChanges で「コミットしてから」true を返す（発注前予約の要）。
public sealed class EfOrderReservationStore(OrderExecutionDbContext db) : IOrderReservationStore
{
    public bool TryReserve(Guid decisionId, DateTimeOffset reservedAt)
    {
        // 先読みは高速路（再配送の大半はここで false）。並行配送の実際の排他は主キーの一意制約が担う。
        if (db.DispatchReservations.Any(r => r.DecisionId == decisionId))
            return false;

        db.DispatchReservations.Add(new OrderDispatchReservationRow
        {
            DecisionId = decisionId,
            State = OrderDispatchState.Reserved,
            ReservedAt = reservedAt,
        });

        try
        {
            db.SaveChanges();
            return true;
        }
        catch (DbUpdateException)
        {
            // 一意制約違反＝他プロセスが先に予約を確保した。書き込み失敗一般もここに落ちるが、いずれも
            // 「予約を確保できていない」ことに変わりはなく、false（＝発注しない）が安全側（IADR-0057）。
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public void MarkCompleted(Guid decisionId, string brokerOrderId, DateTimeOffset completedAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(brokerOrderId);

        var row = db.DispatchReservations.FirstOrDefault(r => r.DecisionId == decisionId)
            ?? throw new InvalidOperationException($"DecisionId={decisionId} の予約がありません。");

        row.State = OrderDispatchState.Completed;
        row.BrokerOrderId = brokerOrderId;
        row.CompletedAt = completedAt;
        db.SaveChanges();
    }

    public OrderDispatchReservation? Find(Guid decisionId)
    {
        var row = db.DispatchReservations.AsNoTracking().FirstOrDefault(r => r.DecisionId == decisionId);
        return row is null
            ? null
            : new OrderDispatchReservation(row.DecisionId, row.State, row.ReservedAt, row.BrokerOrderId, row.CompletedAt);
    }

    // #141, IADR-0074: 滞留 Reserved（State=Reserved AND ReservedAt < reservedBefore）を ReservedAt 昇順で
    // 最大 batchSize 件返す。#131 の State インデックス（「滞留 Reserved を洗い出すための検索用」）で洗い出せる。
    public IReadOnlyList<OrderDispatchReservation> FindStalledReserved(DateTimeOffset reservedBefore, int batchSize)
    {
        return db.DispatchReservations.AsNoTracking()
            .Where(r => r.State == OrderDispatchState.Reserved && r.ReservedAt < reservedBefore)
            .OrderBy(r => r.ReservedAt)
            .Take(batchSize)
            .Select(r => new OrderDispatchReservation(
                r.DecisionId, r.State, r.ReservedAt, r.BrokerOrderId, r.CompletedAt))
            .ToList();
    }

    // #141, IADR-0074: 未発注と確定した Reserved 予約のみ削除する。
    // 述語で State=Reserved を明示し、終端行（Completed）は決して消さない（消せば再配送で二重発注・実弾では実損）。
    public bool Release(Guid decisionId)
    {
        var row = db.DispatchReservations.FirstOrDefault(r => r.DecisionId == decisionId);
        if (row is null || row.State != OrderDispatchState.Reserved)
            return false;

        db.DispatchReservations.Remove(row);
        try
        {
            db.SaveChanges();
            return true;
        }
        catch (DbUpdateException)
        {
            // 並行で既に解放/確定された等。安全側＝「解放していない」に倒す。
            db.ChangeTracker.Clear();
            return false;
        }
    }

    // NFR（運用）, #137, IADR-0059: 終端（Completed）かつ cutoff より古い行のみをバッチ削除する。
    //
    // **Reserved は述語で明示的に除外する**。Reserved＝「ブローカへ発注済みか不明」であり、どれだけ古くても
    // 消してはならない（消せば再配送で二重発注＝実弾では実損）。滞留 Reserved の解消は #141 の自動
    // リコンサイルか人手の判断であって、時間経過ではない。
    // ExecuteDelete ではなく Where + RemoveRange を採る理由は IADR-0059 決定5（述語を CI で守るため）。
    public int PurgeCompletedBefore(DateTimeOffset cutoff, int batchSize)
    {
        // CompletedAt != null は State の冗長な裏付け（Completed なら必ず設定される）。述語を単独で読んで
        // 安全と分かるようにするため、あえて明示する。
        var expired = db.DispatchReservations
            .Where(r => r.State == OrderDispatchState.Completed
                        && r.CompletedAt != null
                        && r.CompletedAt < cutoff)
            .OrderBy(r => r.CompletedAt)
            .Take(batchSize)
            .ToList();

        if (expired.Count == 0)
            return 0;

        db.DispatchReservations.RemoveRange(expired);
        db.SaveChanges();
        return expired.Count;
    }
}
