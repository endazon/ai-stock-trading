using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.EntityFrameworkCore;

namespace OrderExecutionService.Infrastructure.Persistence;

// FR-05, FR-16: 発注結果ストアの EF 実装（追記中心）。DecisionId は相関キー、OrderId は主キー。
public sealed class EfExecutedOrderStore(OrderExecutionDbContext db) : IExecutedOrderStore
{
    public void Save(ExecutionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        db.ExecutedOrders.Add(new ExecutedOrderRow
        {
            OrderId = record.OrderId,
            DecisionId = record.DecisionId,
            Symbol = record.Symbol,
            Market = record.Market,
            Side = record.Side,
            ProductType = record.ProductType,
            PositionEffect = record.PositionEffect,
            Quantity = record.Quantity,
            PlannedPrice = record.PlannedPrice,
            FilledQuantity = record.FilledQuantity,
            AveragePrice = record.AveragePrice,
            Status = record.Status,
            SlippageRatio = record.SlippageRatio,
            ExecutedAt = record.ExecutedAt,
            // #1122, IADR-0486 決定6: 下限を掛けてラインを引いた印（遡及が読む）。
            StopFloorSource = record.StopFloorSource,
            // #1222, IADR-0515 決定2: 承認の出どころ（S1 の決済の前の取消が読む）。
            ApprovalOrigin = record.ApprovalOrigin,
            // #1330, IADR-0534 決定2: 保存の時点では帰属させた株数は無い（呼び出し側が渡す値をそのまま書く。既定 0）。
            ProtectionReleasedQuantity = record.ProtectionReleasedQuantity,
            // #1339, IADR-0539: 保存の時点では系は取消を求めていない（呼び出し側が渡す値をそのまま書く。既定 null）。
            UnfilledCancelRequestedAt = record.UnfilledCancelRequestedAt,
            // #1395, IADR-0560: 保存の時点では S1 は取消を求めていない（呼び出し側が渡す値をそのまま書く。既定 null）。
            SoftwareStopCancelRequestedAt = record.SoftwareStopCancelRequestedAt,
        });
        db.SaveChanges();
    }

    public IReadOnlyList<ExecutionRecord> GetAll()
    {
        return [.. db.ExecutedOrders
            .OrderByDescending(r => r.ExecutedAt)
            .Select(r => ToRecord(r))];
    }

    public ExecutionRecord? FindByDecisionId(Guid decisionId)
    {
        var row = db.ExecutedOrders.FirstOrDefault(r => r.DecisionId == decisionId);
        return row is null ? null : ToRecord(row);
    }

    // #270, IADR-0113: 約定追跡の対象＝非終端かつ追跡上限内の記録を古い順に返す。
    // 終端判定は OrderStatusLifecycle と同一の集合を列挙する（EF が SQL へ翻訳できる形で書く）。
    public IReadOnlyList<ExecutionRecord> FindPendingSince(DateTimeOffset since, int batchSize)
    {
        return [.. db.ExecutedOrders
            .Where(r => (r.Status == OrderStatus.Accepted || r.Status == OrderStatus.PartiallyFilled)
                && r.ExecutedAt >= since)
            .OrderBy(r => r.ExecutedAt)
            .Take(batchSize)
            .Select(r => ToRecord(r))];
    }

    // FR-10, #958, IADR-0406 決定1: 指定した注文 ID の非終端の記録を古い順に返す（追跡上限は見ない）。
    public IReadOnlyList<ExecutionRecord> FindPendingByOrderIds(IReadOnlyCollection<string> orderIds)
    {
        ArgumentNullException.ThrowIfNull(orderIds);
        if (orderIds.Count == 0)
            return [];

        var ids = orderIds.ToList();
        return [.. db.ExecutedOrders
            .Where(r => (r.Status == OrderStatus.Accepted || r.Status == OrderStatus.PartiallyFilled)
                && ids.Contains(r.OrderId))
            .OrderBy(r => r.ExecutedAt)
            .Select(r => ToRecord(r))];
    }

    // FR-10, #1105, IADR-0461 決定1: 非終端の決済の行を銘柄・市場・決済の方向で絞って古い順に返す（追跡上限は見ない）。
    // 終端判定は FindPendingSince と同じ集合を列挙する（EF が SQL へ翻訳できる形）。
    public IReadOnlyList<ExecutionRecord> FindPendingCloses(string symbol, Market market, TradeSide closeSide)
    {
        ArgumentException.ThrowIfNullOrEmpty(symbol);

        return [.. db.ExecutedOrders
            .Where(r => r.PositionEffect == PositionEffect.Close
                && (r.Status == OrderStatus.Accepted || r.Status == OrderStatus.PartiallyFilled)
                && r.Symbol == symbol && r.Market == market && r.Side == closeSide)
            .OrderBy(r => r.ExecutedAt)
            .Select(r => ToRecord(r))];
    }

    // FR-10, #1121, IADR-0466 決定2: 新規建ての行を銘柄・市場・エントリーの方向で絞って新しい順に返す（状態を問わない・上限つき）。
    public IReadOnlyList<ExecutionRecord> FindRecentOpens(string symbol, Market market, TradeSide entrySide, int limit)
    {
        ArgumentException.ThrowIfNullOrEmpty(symbol);

        return [.. db.ExecutedOrders
            .Where(r => r.PositionEffect == PositionEffect.Open
                && r.Symbol == symbol && r.Market == market && r.Side == entrySide)
            .OrderByDescending(r => r.ExecutedAt)
            .Take(limit)
            .Select(r => ToRecord(r))];
    }

    // 🔴 FR-10, #1330, IADR-0534 決定2: 承認経路の決済（出どころあり）のうち、まだ保護の縮小へ帰属させていない約定を持つ行（古い順・上限つき）。
    public IReadOnlyList<ExecutionRecord> FindApprovedClosesWithUnreleasedFills(
        string symbol, Market market, TradeSide closeSide, int limit)
    {
        ArgumentException.ThrowIfNullOrEmpty(symbol);

        return [.. db.ExecutedOrders
            .Where(r => r.PositionEffect == PositionEffect.Close
                && r.ApprovalOrigin != null
                && r.FilledQuantity > r.ProtectionReleasedQuantity
                && r.Symbol == symbol && r.Market == market && r.Side == closeSide)
            .OrderBy(r => r.ExecutedAt)
            .Take(limit)
            .Select(r => ToRecord(r))];
    }

    // 🔴 FR-10, #1330, IADR-0534 決定2: 帰属させた株数を進める（**この 1 列だけ**を書く。単調・約定数量以下）。
    // 変更追跡により UPDATE は 1 列だけになり、並行に約定追跡が書いた状態・数量を古い値で上書きしない。
    public bool MarkProtectionReleased(string orderId, int releasedQuantity)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);

        var row = db.ExecutedOrders.Find(orderId);
        if (row is null)
            return false;

        var next = Math.Min(releasedQuantity, row.FilledQuantity);
        if (next <= row.ProtectionReleasedQuantity)
            return false;

        row.ProtectionReleasedQuantity = next;
        db.SaveChanges();
        return true;
    }

    // FR-10, #958, IADR-0406 決定3: 非終端の行の追跡の起点を進める。変更追跡により UPDATE は executed_at の 1 列だけになり、
    // 並行に約定追跡が書いた状態・数量を古い値で上書きしない。
    public bool RenewTracking(string orderId, DateTimeOffset trackedFrom)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);

        var row = db.ExecutedOrders.Find(orderId);
        if (row is null
            || !(row.Status == OrderStatus.Accepted || row.Status == OrderStatus.PartiallyFilled)
            || row.ExecutedAt >= trackedFrom)
        {
            return false;
        }

        row.ExecutedAt = trackedFrom;
        db.SaveChanges();
        return true;
    }

    // 🔴 FR-10, FR-11, #1048, IADR-0481 決定3: 追跡上限を過ぎた非終端の行のうち、その起点で打ち切りを記録していないもの（古い順）。
    // 除外（Active な S0 の逆指値レグ）は件数の上限の前に問い合わせの段で行う（#1048 独立監査）。
    public IReadOnlyList<ExecutionRecord> FindTrackingExpired(
        DateTimeOffset before, int batchSize, IReadOnlyCollection<string>? excludedOrderIds = null)
    {
        string[] excluded = [.. excludedOrderIds ?? []];
        return [.. db.ExecutedOrders
            .Where(r => (r.Status == OrderStatus.Accepted || r.Status == OrderStatus.PartiallyFilled)
                && r.ExecutedAt < before
                && (r.TrackingAbandonedFrom == null || r.TrackingAbandonedFrom != r.ExecutedAt)
                && !excluded.Contains(r.OrderId))
            .OrderBy(r => r.ExecutedAt)
            .Take(batchSize)
            .Select(r => ToRecord(r))];
    }

    // 🔴 FR-10, FR-11, #1048, IADR-0481 決定3: 打ち切りの印を書く（印の列だけ。起点が進んでいれば書かない）。
    public bool MarkTrackingAbandoned(string orderId, DateTimeOffset trackedFrom)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);

        var row = db.ExecutedOrders.Find(orderId);
        if (row is null || row.ExecutedAt != trackedFrom)
            return false;

        row.TrackingAbandonedFrom = trackedFrom;
        db.SaveChanges();
        return true;
    }

    // 🔴 FR-10, FR-05, #1339, IADR-0536 決定2: 追跡を打ち切った（今の起点で印がある）非終端の承認経路の決済（古い順・上限つき）。
    public IReadOnlyList<ExecutionRecord> FindAbandonedApprovedCloses(int batchSize)
    {
        return [.. db.ExecutedOrders
            .Where(r => r.PositionEffect == PositionEffect.Close
                && r.ApprovalOrigin != null
                && (r.Status == OrderStatus.Accepted || r.Status == OrderStatus.PartiallyFilled)
                && r.TrackingAbandonedFrom != null && r.TrackingAbandonedFrom == r.ExecutedAt)
            .OrderBy(r => r.ExecutedAt)
            .Take(batchSize)
            .Select(r => ToRecord(r))];
    }

    // FR-10, FR-11, #1339, IADR-0536 決定4: 発注から一定時間たった非終端の承認経路の決済（古い順・上限つき）。
    public IReadOnlyList<ExecutionRecord> FindStaleApprovedCloses(DateTimeOffset placedBefore, int limit)
    {
        return [.. db.ExecutedOrders
            .Where(r => r.PositionEffect == PositionEffect.Close
                && r.ApprovalOrigin != null
                && (r.Status == OrderStatus.Accepted || r.Status == OrderStatus.PartiallyFilled)
                && r.ExecutedAt < placedBefore)
            .OrderBy(r => r.ExecutedAt)
            .Take(limit)
            .Select(r => ToRecord(r))];
    }

    // 🔴 FR-10, ADR-0067 決定1・4, #1339, IADR-0539: 発注から N 分以上たって非終端の<b>判断由来の</b>決済で、系がまだ取消を求めていないもの（古い順・上限つき）。
    // 除外（回数の上限で取り消さないと決めた注文）は件数の上限の前に問い合わせの段で行う。
    public IReadOnlyList<ExecutionRecord> FindUnfilledDecisionCloses(
        DateTimeOffset placedAtOrBefore, int limit, IReadOnlyCollection<string>? excludedOrderIds = null,
        DateTimeOffset? placedOnOrAfter = null)
    {
        string[] excluded = [.. excludedOrderIds ?? []];
        var from = placedOnOrAfter ?? DateTimeOffset.MinValue;
        return [.. db.ExecutedOrders
            .Where(r => r.PositionEffect == PositionEffect.Close
                && r.ApprovalOrigin == OrderApprovalOrigin.TradeDecision
                && (r.Status == OrderStatus.Accepted || r.Status == OrderStatus.PartiallyFilled)
                && r.UnfilledCancelRequestedAt == null
                && r.ExecutedAt <= placedAtOrBefore
                && r.ExecutedAt >= from
                && !excluded.Contains(r.OrderId))
            .OrderBy(r => r.ExecutedAt)
            .Take(limit)
            .Select(r => ToRecord(r))];
    }

    // 🔴 FR-10, ADR-0067 決定2, #1339, IADR-0539: 同じ建玉（銘柄・市場・決済の方向）の判断由来の決済で、系が取消を求めた時刻が
    // [from, to) にある件数（1 建玉 1 日の回数の上限。範囲は呼び出し側が市場の現地取引日から作る）。
    public int CountUnfilledCancelRequests(
        string symbol, Market market, TradeSide closeSide, DateTimeOffset fromInclusive, DateTimeOffset toExclusive)
    {
        ArgumentException.ThrowIfNullOrEmpty(symbol);

        return db.ExecutedOrders.Count(r => r.PositionEffect == PositionEffect.Close
            && r.ApprovalOrigin == OrderApprovalOrigin.TradeDecision
            && r.Symbol == symbol && r.Market == market && r.Side == closeSide
            && r.UnfilledCancelRequestedAt != null
            && r.UnfilledCancelRequestedAt >= fromInclusive && r.UnfilledCancelRequestedAt < toExclusive);
    }

    // 🔴 FR-10, ADR-0067 決定1, #1339, IADR-0539: 取消を求めた印を書く（**この 1 列だけ**。非終端かつ未印のときだけ）。
    public bool MarkUnfilledCancelRequested(string orderId, DateTimeOffset requestedAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);

        var row = db.ExecutedOrders.Find(orderId);
        if (row is null
            || row.UnfilledCancelRequestedAt is not null
            || !(row.Status == OrderStatus.Accepted || row.Status == OrderStatus.PartiallyFilled))
        {
            return false;
        }

        row.UnfilledCancelRequestedAt = requestedAt;
        db.SaveChanges();
        return true;
    }

    // 🔴 FR-10, FR-09, #1395, IADR-0560: S1 が取消を求めた印を書く（**この 1 列だけ**。非終端のときだけ。既にあれば上書きしない）。
    public bool MarkSoftwareStopCancelRequested(string orderId, DateTimeOffset requestedAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);

        var row = db.ExecutedOrders.Find(orderId);
        if (row is null || !(row.Status == OrderStatus.Accepted || row.Status == OrderStatus.PartiallyFilled))
            return false;
        if (row.SoftwareStopCancelRequestedAt is not null)
            return true;

        row.SoftwareStopCancelRequestedAt = requestedAt;
        db.SaveChanges();
        return true;
    }

    // 🔴 #1395, IADR-0560: 印を保存先から読む（**追跡しない**問い合わせ。同じ文脈が先に読んだ行の古い値を返さない）。
    public DateTimeOffset? FindSoftwareStopCancelRequestedAt(string orderId)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);

        return db.ExecutedOrders.AsNoTracking()
            .Where(r => r.OrderId == orderId)
            .Select(r => r.SoftwareStopCancelRequestedAt)
            .FirstOrDefault();
    }

    // #270, IADR-0113: 観測した最新のブローカ状態を既存行へ反映する。行が無ければ何もしない
    // （新規に作らない＝DecisionId 1:1 の不変を壊さない）。
    public bool UpdateOutcome(
        string orderId,
        OrderStatus status,
        int filledQuantity,
        decimal averagePrice,
        decimal slippageRatio,
        DateTimeOffset executedAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);

        var row = db.ExecutedOrders.Find(orderId);
        if (row is null)
            return false;

        row.Status = status;
        row.FilledQuantity = filledQuantity;
        row.AveragePrice = averagePrice;
        row.SlippageRatio = slippageRatio;
        row.ExecutedAt = executedAt;
        db.SaveChanges();
        return true;
    }

    private static ExecutionRecord ToRecord(ExecutedOrderRow r) => new(
        r.DecisionId, r.OrderId, r.Symbol, r.Market, r.Side, r.ProductType, r.PositionEffect,
        r.Quantity, r.PlannedPrice, r.FilledQuantity, r.AveragePrice, r.Status, r.SlippageRatio, r.ExecutedAt,
        r.StopFloorSource, r.ApprovalOrigin, r.ProtectionReleasedQuantity, r.UnfilledCancelRequestedAt,
        r.SoftwareStopCancelRequestedAt);
}
